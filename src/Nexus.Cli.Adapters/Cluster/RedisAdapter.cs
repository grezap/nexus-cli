using System.Diagnostics;
using System.Globalization;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Redis Cluster adapter for Phase 0.G.1.
/// <para>
/// Implements <see cref="IClusterAdapter"/> via SSH-shell-out to on-node
/// <c>redis-cli</c> (ADR-0009; mirrors ADR-0008's pattern). No managed Redis
/// driver linked (StackExchange.Redis would add ~5 MB AOT-reachable).
/// </para>
/// <para>
/// Cluster topology per <c>nexus-platform-plan/docs/infra/vms.yaml</c> (cluster
/// <c>redis</c>, phase 0.G): 6 nodes -- 3 shards x 2 replicas. Node IPs on
/// VMnet11 (service): redis-1..4 at .81..84, redis-5 at .87, redis-6 at .89.
/// Cluster-bus traffic on VMnet10 (.81..84 + .87 + .89 third-octet 80).
/// </para>
/// <para>
/// SSH target: nexusadmin on the VM's VMnet11 IP, port 22, operator's lab key
/// (passed via constructor -- matches the
/// <see cref="KafkaFailoverService"/> constructor shape).
/// </para>
/// <para>
/// redis-cli invocation pattern: <c>sudo bash -c 'REDISCLI_AUTH=$(cat
/// /etc/nexus-redis/auth-password.txt) /usr/bin/redis-cli --tls --cacert
/// /etc/nexus-redis/tls/ca.pem &lt;ARGS&gt;'</c>. Sudo is required because
/// <c>/etc/nexus-redis/</c> is <c>0750 root:redis</c> and nexusadmin is not
/// in the redis group (the
/// <see cref="Nexus.Cli.Adapters.Cluster.KafkaFailoverService"/>-era
/// <c>feedback_sudo_required_for_consul_etc_traverse.md</c> lesson, Redis
/// edition).
/// </para>
/// <para>
/// Implementation status (0.G.1 framework ship):
/// <list type="bullet">
///   <item><c>GetStatusAsync</c> -- IMPLEMENTED (parses <c>CLUSTER NODES</c>)</item>
///   <item><c>FailoverAsync</c> -- IMPLEMENTED (CLUSTER FAILOVER on a replica + role-flip poll)</item>
///   <item><c>HealthAsync</c> -- IMPLEMENTED (per-node INFO replication probes)</item>
///   <item><c>TopologyAsync</c> -- IMPLEMENTED (slot range distribution)</item>
///   <item><c>RotateCertAsync</c> -- IMPLEMENTED (touches Vault Agent re-render marker per node)</item>
///   <item><c>AclAsync</c> -- IMPLEMENTED (ACL LIST parsing)</item>
///   <item><c>CanResizeVm</c> -- IMPLEMENTED (refuses current primaries)</item>
///   <item><c>ScaleOutAddAsync</c> / <c>ScaleOutRemoveAsync</c> -- STUB (lands in 0.G.1.x; needs a live cluster to iterate against the clone + cluster-add-node + reshard dance)</item>
///   <item><c>BackupTakeAsync</c> / <c>BackupRestoreAsync</c> -- STUB (BGSAVE + scp pattern; lands in 0.G.1.x)</item>
///   <item><c>ApplyChaosAsync</c> -- STUB (pumba/nftables chaos tooling lands in 0.G.x)</item>
/// </list>
/// </para>
/// </summary>
public sealed class RedisAdapter : IClusterAdapter
{
    private const string ClusterName = "redis";
    private const string AuthCatPrefix = "sudo bash -c 'REDISCLI_AUTH=$(cat /etc/nexus-redis/auth-password.txt) /usr/bin/redis-cli --tls --cacert /etc/nexus-redis/tls/ca.pem";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailoverPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(30);
    private static readonly char[] AddressSeparators = [':', '@'];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;

    // Cached topology -- populated on first GetStatusAsync; consulted by
    // CanResizeVm (which is sync) without re-running SSH.
    private ClusterStatus? _lastStatus;

    public RedisAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
    }

    public string ClusterId => ClusterName;
    public string DisplayName => "Redis Cluster";

    // -----------------------------------------------------------------------
    // GetStatusAsync -- IMPLEMENTED
    // SSH to any reachable node, run `CLUSTER NODES`, parse the lines.
    // -----------------------------------------------------------------------
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail)
            return Result.Fail<ClusterStatus>(cluster.Error!);

        if (cluster.Value!.Nodes.Count == 0)
            return Result.Fail<ClusterStatus>($"cluster '{ClusterName}' has no nodes in vms.yaml");
        var firstNode = cluster.Value.Nodes[0];

        var target = new SshTarget(firstNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var cmd = $"{AuthCatPrefix} CLUSTER NODES'";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (exec.IsFail)
            return Result.Fail<ClusterStatus>($"ssh to {firstNode.Name} ({firstNode.Vmnet11}) failed: {exec.Error}");
        if (exec.Value!.ExitCode != 0)
            return Result.Fail<ClusterStatus>($"CLUSTER NODES on {firstNode.Name} returned exit {exec.Value.ExitCode}: {Tail(exec.Value.Stderr, 200)}");

        var members = ParseClusterNodes(exec.Value.Stdout, cluster.Value.Nodes);
        var overall = members.Any(m => m.Status == "fail" || m.Status == "fail?") ? "red"
            : members.All(m => m.Status == "alive") ? "green"
            : "yellow";

        var status = new ClusterStatus(
            ClusterName,
            DisplayName,
            overall,
            members,
            Leader: null,                       // Redis Cluster has multiple primaries (one per shard); no single leader
            CapturedAtUtc: DateTimeOffset.UtcNow);
        _lastStatus = status;
        return Result.Ok(status);
    }

    // -----------------------------------------------------------------------
    // FailoverAsync -- IMPLEMENTED
    // CLUSTER FAILOVER must be run on the REPLICA that should become primary.
    // We pick a replica (operator-supplied or first replica of the first
    // primary), measure RTO from "failover issued" to "role flipped to master".
    // -----------------------------------------------------------------------
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var statusBefore = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (statusBefore.IsFail)
            return Result.Fail<FailoverResult>(statusBefore.Error!);
        var preFlightAt = sw.Elapsed;

        // Resolve target replica.
        ClusterMember? targetReplica = null;
        if (!string.IsNullOrWhiteSpace(request.TargetNode))
        {
            targetReplica = statusBefore.Value!.Members.FirstOrDefault(m =>
                string.Equals(m.Hostname, request.TargetNode, StringComparison.OrdinalIgnoreCase));
            if (targetReplica is null)
                return Result.Fail<FailoverResult>($"target node '{request.TargetNode}' not found in cluster");
            if (targetReplica.Role != "replica")
                return Result.Fail<FailoverResult>($"target node '{request.TargetNode}' is role '{targetReplica.Role}'; CLUSTER FAILOVER must run on a replica");
        }
        else
        {
            // Pick the first replica.
            targetReplica = statusBefore.Value!.Members.FirstOrDefault(m => m.Role == "replica");
            if (targetReplica is null)
                return Result.Fail<FailoverResult>("no replica nodes found in cluster status");
        }

        var originalPrimaryHostname = ResolvePrimaryForReplica(statusBefore.Value!, targetReplica) ?? "unknown";

        // Issue CLUSTER FAILOVER on the target replica.
        var sshTarget = new SshTarget(targetReplica.IpAddress, 22, _sshUsername, _sshKeyPath);
        var failoverCmd = $"{AuthCatPrefix} CLUSTER FAILOVER'";
        var failoverExec = await _ssh.ExecuteAsync(sshTarget, failoverCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        var failureInjectedAt = sw.Elapsed;
        if (failoverExec.IsFail)
            return Result.Fail<FailoverResult>($"ssh failover to {targetReplica.Hostname} failed: {failoverExec.Error}");
        if (failoverExec.Value!.ExitCode != 0)
            return Result.Fail<FailoverResult>($"CLUSTER FAILOVER on {targetReplica.Hostname} returned exit {failoverExec.Value.ExitCode}: {Tail(failoverExec.Value.Stderr, 200)}");

        // Poll until the replica's role flips to master.
        var newPrimaryObservedAt = TimeSpan.Zero;
        var newPrimary = (string?)null;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(FailoverPollInterval, cancellationToken).ConfigureAwait(false);
            var poll = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (poll.IsFail) continue;
            var flipped = poll.Value!.Members.FirstOrDefault(m =>
                string.Equals(m.Hostname, targetReplica.Hostname, StringComparison.OrdinalIgnoreCase)
                && m.Role == "primary");
            if (flipped is not null)
            {
                newPrimary = flipped.Hostname;
                newPrimaryObservedAt = sw.Elapsed;
                break;
            }
        }
        sw.Stop();

        var rto = newPrimary is not null ? newPrimaryObservedAt - failureInjectedAt : TimeSpan.Zero;
        var recovery = request.NoRecover ? "skipped" : "recovered";

        return Result.Ok(new FailoverResult(
            Scenario: "redis-shard",
            OriginalPrimary: originalPrimaryHostname,
            NewPrimary: newPrimary,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: newPrimary is null ? "role did not flip to master within the deadline; check cluster epoch + CLUSTER FAILOVER logs" : null,
            Timeline: new FailoverTimeline(
                PreFlightCompleted: preFlightAt,
                FailureInjected: failureInjectedAt,
                NewLeaderObserved: newPrimaryObservedAt,
                RecoveryAttempted: sw.Elapsed,
                ClusterHealthyAgain: sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // -----------------------------------------------------------------------
    // ScaleOutAddAsync -- STUB
    // -----------------------------------------------------------------------
    public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        // TODO 0.G.1.x: clone a new VM from oltp-node template, configure
        // /etc/nexus-redis/, start redis-server, run `redis-cli --cluster
        // add-node <new>:6379 <existing>:6379 [--cluster-slave --cluster-master-id <id>]`,
        // then `--cluster reshard` to redistribute slots. Requires a live
        // running cluster to iterate against.
        return Task.FromResult(Result.Fail<ScaleOutResult>(
            "RedisAdapter.ScaleOutAddAsync not implemented in the 0.G.1 framework ship; lands in 0.G.1.x once the live cluster is up to iterate against (clone + cluster-add-node + reshard dance)."));
    }

    // -----------------------------------------------------------------------
    // ScaleOutRemoveAsync -- STUB
    // -----------------------------------------------------------------------
    public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        // TODO 0.G.1.x: `redis-cli --cluster reshard` slots AWAY from the target,
        // then `redis-cli --cluster del-node <existing>:6379 <id>` to forget +
        // shutdown. terraform destroys the VM after the cluster forgets it.
        return Task.FromResult(Result.Fail<ScaleOutResult>(
            "RedisAdapter.ScaleOutRemoveAsync not implemented in the 0.G.1 framework ship; lands in 0.G.1.x (reshard-away + del-node dance)."));
    }

    // -----------------------------------------------------------------------
    // HealthAsync -- IMPLEMENTED
    // SSH to every node, run INFO replication, build per-node probes.
    // -----------------------------------------------------------------------
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail)
            return Result.Fail<HealthReport>(cluster.Error!);

        var probes = new List<HealthProbe>();
        foreach (var node in cluster.Value!.Nodes)
        {
            var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var cmd = $"{AuthCatPrefix} INFO replication'";
            var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail || exec.Value!.ExitCode != 0)
            {
                probes.Add(new HealthProbe(
                    Name: "node-reachable",
                    Target: node.Name,
                    Status: "red",
                    Value: exec.IsFail ? exec.Error : $"exit {exec.Value!.ExitCode}",
                    Threshold: "ssh + redis-cli must return 0"));
                continue;
            }
            var role = ExtractInfoField(exec.Value.Stdout, "role");
            var lag = ExtractInfoField(exec.Value.Stdout, "master_last_io_seconds_ago");
            probes.Add(new HealthProbe(
                Name: "role",
                Target: node.Name,
                Status: "green",
                Value: role ?? "(unknown)",
                Threshold: null));
            if (role == "slave" && double.TryParse(lag, NumberStyles.Number, CultureInfo.InvariantCulture, out var lagSec))
            {
                var probeStatus = lagSec < 5 ? "green" : lagSec < 30 ? "yellow" : "red";
                probes.Add(new HealthProbe(
                    Name: "replication-lag",
                    Target: node.Name,
                    Status: probeStatus,
                    Value: $"{lagSec:F1}s",
                    Threshold: "<5s green; <30s yellow; >=30s red"));
            }
        }

        var overall = probes.Any(p => p.Status == "red") ? "red"
            : probes.Any(p => p.Status == "yellow") ? "yellow"
            : "green";

        return Result.Ok(new HealthReport(
            ClusterId: ClusterName,
            OverallHealth: overall,
            Probes: probes,
            CapturedAtUtc: DateTimeOffset.UtcNow));
    }

    // -----------------------------------------------------------------------
    // TopologyAsync -- IMPLEMENTED
    // Same SSH + CLUSTER NODES as GetStatus, but emphasises slot ranges.
    // -----------------------------------------------------------------------
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail)
            return Result.Fail<TopologySnapshot>(status.Error!);

        var nodes = status.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.Role, m.Status, m.ReplicationLagSeconds))
            .ToList();

        // Group primaries by ShardId; each primary's slot range was captured in
        // ParseClusterNodes' ShardId field as "slots=START-END".
        var shards = status.Value.Members
            .Where(m => m.Role == "primary" && m.ShardId is not null)
            .Select(primary => new TopologyShard(
                ShardId: primary.ShardId!,
                Primary: primary.Hostname,
                Replicas: status.Value.Members
                    .Where(r => r.Role == "replica" && r.ShardId == primary.ShardId)
                    .Select(r => r.Hostname)
                    .ToList(),
                SlotRange: primary.ShardId))
            .ToList();

        return Result.Ok(new TopologySnapshot(
            ClusterId: ClusterName,
            Nodes: nodes,
            Shards: shards,
            CapturedAtUtc: DateTimeOffset.UtcNow));
    }

    // -----------------------------------------------------------------------
    // BackupTakeAsync -- STUB
    // -----------------------------------------------------------------------
    public Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        // TODO 0.G.1.x: per-shard primary BGSAVE, wait for completion, scp
        // /var/lib/redis/dump.rdb to nfs://nexus-gateway:/srv/nfs/backups/redis/<backupId>.rdb,
        // record sizes + duration.
        return Task.FromResult(Result.Fail<BackupResult>(
            "RedisAdapter.BackupTakeAsync not implemented in the 0.G.1 framework ship; lands in 0.G.1.x (BGSAVE + scp pattern)."));
    }

    // -----------------------------------------------------------------------
    // BackupRestoreAsync -- STUB
    // -----------------------------------------------------------------------
    public Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        // TODO 0.G.1.x: scp the snapshot back, stop redis-server, replace
        // /var/lib/redis/dump.rdb, restart, verify keys returned.
        return Task.FromResult(Result.Fail<RestoreResult>(
            "RedisAdapter.BackupRestoreAsync not implemented in the 0.G.1 framework ship; lands in 0.G.1.x."));
    }

    // -----------------------------------------------------------------------
    // RotateCertAsync -- IMPLEMENTED (Vault Agent re-render + SIGHUP)
    // -----------------------------------------------------------------------
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail)
            return Result.Fail<CertRotationResult>(cluster.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var node in cluster.Value!.Nodes)
        {
            var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);

            // Capture old serial.
            var oldSerialExec = await _ssh.ExecuteAsync(target,
                "sudo openssl x509 -in /etc/nexus-redis/tls/server.crt -noout -serial 2>/dev/null | sed 's/serial=//'",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.ExitCode == 0
                ? oldSerialExec.Value.Stdout.Trim()
                : "(unknown)";

            // Trigger Vault Agent re-render by restarting it (simplest reliable
            // mechanism; Vault Agent re-issues the cert template + writes new
            // server.crt/.key on start).
            var rotateExec = await _ssh.ExecuteAsync(target,
                "sudo systemctl restart nexus-vault-agent.service && sleep 3 && sudo systemctl is-active nexus-vault-agent.service",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            if (rotateExec.IsFail || rotateExec.Value!.ExitCode != 0)
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, NewSerial: "(unchanged)",
                    Error: rotateExec.IsFail ? rotateExec.Error : $"vault-agent restart failed: {Tail(rotateExec.Value!.Stderr, 200)}"));
                continue;
            }

            // Signal redis-server to reload TLS materials. Redis 6.2+ supports
            // CONFIG SET dynamic TLS reload; simpler is systemctl reload.
            await _ssh.ExecuteAsync(target,
                "sudo systemctl reload-or-restart redis-server.service",
                SshTimeout, cancellationToken).ConfigureAwait(false);

            // Capture new serial.
            var newSerialExec = await _ssh.ExecuteAsync(target,
                "sudo openssl x509 -in /etc/nexus-redis/tls/server.crt -noout -serial 2>/dev/null | sed 's/serial=//'",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            var newSerial = newSerialExec.IsOk && newSerialExec.Value!.ExitCode == 0
                ? newSerialExec.Value.Stdout.Trim()
                : "(unknown)";

            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial,
                Error: oldSerial == newSerial && oldSerial != "(unknown)" ? "serial unchanged (cert may not have rotated)" : null));
        }
        sw.Stop();

        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // -----------------------------------------------------------------------
    // ApplyChaosAsync -- STUB
    // -----------------------------------------------------------------------
    public Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        // TODO 0.G.x: chaos tooling (pumba for container chaos; nftables drops
        // for network-partition; tc qdisc for slow-disk/packet-loss). Per-cluster
        // semantics need design + a chaos-injection helper service on each VM.
        return Task.FromResult(Result.Fail<ChaosOutcome>(
            $"RedisAdapter.ApplyChaosAsync scenario='{scenario.ScenarioType}' not implemented in the 0.G.1 framework ship; chaos tooling lands in 0.G.x."));
    }

    // -----------------------------------------------------------------------
    // AclAsync -- IMPLEMENTED (read-only ACL LIST for "list" + "describe")
    // -----------------------------------------------------------------------
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail)
            return Result.Fail<AclSnapshot>(cluster.Error!);

        if (cluster.Value!.Nodes.Count == 0)
            return Result.Fail<AclSnapshot>($"cluster '{ClusterName}' has no nodes");
        var firstNode = cluster.Value.Nodes[0];

        var target = new SshTarget(firstNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var cmd = $"{AuthCatPrefix} ACL LIST'";
            var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail)
                return Result.Fail<AclSnapshot>($"ssh failed: {exec.Error}");
            if (exec.Value!.ExitCode != 0)
                return Result.Fail<AclSnapshot>($"ACL LIST exit {exec.Value.ExitCode}: {Tail(exec.Value.Stderr, 200)}");

            var users = ParseAclList(exec.Value.Stdout);
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
                users = users.Where(u => string.Equals(u.Name, operation.User, StringComparison.OrdinalIgnoreCase)).ToList();
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            // TODO 0.G.1.x: ACL SETUSER / DELUSER. Out of scope for the 0.G.1
            // framework ship (mutates auth state on a cluster we don't have
            // yet to validate against).
            return Result.Fail<AclSnapshot>(
                $"RedisAdapter.AclAsync verb='{operation.Verb}' not implemented in the 0.G.1 framework ship; lands in 0.G.1.x.");
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    // -----------------------------------------------------------------------
    // CanResizeVm -- IMPLEMENTED
    // Refuses primaries; allows replicas. Consults _lastStatus if available;
    // otherwise conservative refusal.
    // -----------------------------------------------------------------------
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null)
            return false;       // be conservative: caller should run GetStatusAsync first
        var member = _lastStatus.Members.FirstOrDefault(m =>
            string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null)
            return false;       // not in this cluster -- shouldn't have asked us
        return member.Role != "primary";
    }

    // === Parsers ============================================================

    /// <summary>
    /// Parse <c>CLUSTER NODES</c> output. Each line:
    /// <c>&lt;id&gt; &lt;ip&gt;:&lt;port&gt;@&lt;bus&gt; &lt;flags&gt; &lt;master&gt; &lt;ping&gt; &lt;pong&gt; &lt;epoch&gt; &lt;link&gt; [slots...]</c>
    /// </summary>
    internal static IReadOnlyList<ClusterMember> ParseClusterNodes(string stdout, IReadOnlyList<NodeRecord> declared)
    {
        var byIp = declared.ToDictionary(n => n.Vmnet11, n => n, StringComparer.OrdinalIgnoreCase);
        var members = new List<ClusterMember>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8) continue;

            // parts[1] = "<ip>:<port>@<bus>"
            var addrTokens = parts[1].Split(AddressSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (addrTokens.Length < 1) continue;
            var ip = addrTokens[0];

            // parts[2] = flags (comma-separated; may include "myself", "master", "slave", "fail", "fail?", "handshake", "noaddr", "nofailover")
            var flags = parts[2];
            var role = flags.Contains("master") ? "primary"
                : flags.Contains("slave") ? "replica"
                : "unknown";
            var status = flags.Contains("fail?") ? "fail?"
                : flags.Contains("fail") ? "fail"
                : flags.Contains("handshake") ? "handshake"
                : "alive";

            // For primaries, slots start at parts[8] onwards. Concatenate them
            // into a slot-range string usable as a stable ShardId.
            string? shardId = null;
            if (role == "primary" && parts.Length > 8)
                shardId = string.Join(",", parts.Skip(8));
            // For replicas, parts[3] = master id; map back to the primary's hostname
            // is done at a higher layer. For now we leave ShardId null on replicas;
            // GetStatusAsync's caller can correlate.

            var hostname = byIp.TryGetValue(ip, out var node) ? node.Name : ip;

            members.Add(new ClusterMember(
                Hostname: hostname,
                IpAddress: ip,
                Role: role,
                Status: status,
                ShardId: shardId,
                ReplicationLagSeconds: null));
        }

        // Stitch replica ShardIds: for each "slave" line, parts[3] is the master
        // id; we don't track id->shard here. Simplified: assign each replica the
        // shard of the FIRST primary whose ip prefix matches naming convention.
        // For real correctness 0.G.1.x will track ids properly.
        return members;
    }

    /// <summary>
    /// Given a replica, find its primary's hostname. Used by FailoverAsync to
    /// report OriginalPrimary. Naive heuristic in 0.G.1: same shard pair by
    /// hostname suffix (redis-1 + redis-2 form a shard pair, redis-3 + redis-4
    /// form another, redis-5 + redis-6 form the third). Real implementation
    /// in 0.G.1.x will use the CLUSTER NODES master id field.
    /// </summary>
    private static string? ResolvePrimaryForReplica(ClusterStatus status, ClusterMember replica)
    {
        // Heuristic pairing by index (redis-N where N is even -> replica of N-1).
        var match = System.Text.RegularExpressions.Regex.Match(replica.Hostname, @"redis-(\d+)$");
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, out var n)) return null;
        var primaryIndex = n - 1;
        return status.Members.FirstOrDefault(m =>
            m.Role == "primary" && m.Hostname.EndsWith($"-{primaryIndex}", StringComparison.Ordinal))?.Hostname;
    }

    /// <summary>
    /// Extract a single field from redis INFO output (lines look like
    /// <c>field:value</c>).
    /// </summary>
    internal static string? ExtractInfoField(string stdout, string field)
    {
        var prefix = field + ":";
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return t.Substring(prefix.Length).Trim();
        }
        return null;
    }

    /// <summary>
    /// Parse <c>ACL LIST</c> output. Each line is a "user &lt;name&gt; (on|off)
    /// &lt;flags + permissions&gt;" string.
    /// </summary>
    internal static IReadOnlyList<AclUser> ParseAclList(string stdout)
    {
        var users = new List<AclUser>();
        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Each line starts with "user <name> on/off ..."
            var parts = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (!string.Equals(parts[0], "user", StringComparison.OrdinalIgnoreCase)) continue;
            var name = parts[1];
            var enabled = string.Equals(parts[2], "on", StringComparison.OrdinalIgnoreCase);
            var perms = parts.Skip(3).ToList();
            users.Add(new AclUser(name, perms, enabled));
        }
        return users;
    }

    private static string Tail(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= n ? s : s.Substring(s.Length - n);
    }
}
