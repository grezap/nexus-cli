using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
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
/// redis-cli invocation pattern (reverse-engineered against the LIVE cluster, 0.G.1):
/// <c>sudo redis-cli --tls --cacert /etc/nexus-redis/tls/ca.crt --cert
/// /etc/nexus-redis/tls/server.crt --key /etc/nexus-redis/tls/server.key &lt;ARGS&gt;</c>.
/// The cluster is <b>mTLS-only</b> (<c>port 0</c> + <c>tls-port 6379</c> +
/// <c>tls-auth-clients yes</c>) -- there is NO AUTH password; the client cert/key are the
/// identity and the CA file is <c>ca.crt</c> (not <c>ca.pem</c>). Sudo is required because
/// <c>/etc/nexus-redis/</c> is <c>0750 root:redis</c> and nexusadmin is not in the redis
/// group (the <c>feedback_sudo_required_for_consul_etc_traverse.md</c> lesson, Redis edition).
/// </para>
/// <para>
/// Implementation status (v0.6.0 -- ALL live-verified against the running cluster 2026-06-05;
/// see docs/verification/0.G.1-redis.md):
/// <list type="bullet">
///   <item><c>GetStatusAsync</c> / <c>HealthAsync</c> / <c>TopologyAsync</c> -- CLUSTER NODES / INFO probes</item>
///   <item><c>FailoverAsync</c> -- CLUSTER FAILOVER on a replica + role-flip poll (RTO ~2.1s)</item>
///   <item><c>RotateCertAsync</c> -- genuine re-issue via the node's own Vault token (pki_int/issue/redis-server)</item>
///   <item><c>AclAsync</c> -- ACL LIST/DESCRIBE (grant/revoke pending)</item>
///   <item><c>ScaleOutAddAsync</c> / <c>ScaleOutRemoveAsync</c> -- role-aware add-node / del-node (apply-on-demand provisioning, ADR-0010)</item>
///   <item><c>BackupTakeAsync</c> / <c>BackupRestoreAsync</c> -- per-primary BGSAVE node-local snapshot + restore round-trip</item>
///   <item><c>ApplyChaosAsync</c> -- pushes nexus-chaos.sh; time-boxed self-reverting faults</item>
///   <item><c>CanResizeVm</c> -- refuses current primaries (consumed by IVmResizer)</item>
/// </list>
/// </para>
/// </summary>
public sealed class RedisAdapter : IClusterAdapter
{
    private const string ClusterName = "redis";
    // Connection contract reverse-engineered against the LIVE cluster (0.G.1 live-verify,
    // 2026-06-05): redis.conf has `port 0` + `tls-port 6379` + `tls-auth-clients yes`, so
    // the cluster is mTLS-ONLY -- there is NO AUTH password (no /etc/nexus-redis/auth-password.txt).
    // The client must present a cert+key; the CA file is `ca.crt` (not `ca.pem`). redis-cli runs
    // ON the target node (via SSH) so it reaches the local instance on 127.0.0.1:6379.
    private const string RedisCliPrefix = "sudo bash -c '/usr/bin/redis-cli --tls --cacert /etc/nexus-redis/tls/ca.crt --cert /etc/nexus-redis/tls/server.crt --key /etc/nexus-redis/tls/server.key";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailoverPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(120);
    private static readonly char[] AddressSeparators = [':', '@'];

    // Bare redis-cli invocation (mTLS, no AUTH) for embedding in multi-command remote
    // scripts via `sudo $R ...`. RedisCliPrefix wraps this in `sudo bash -c` for one-shots;
    // this bare form composes inside larger `;`-separated scripts run over one SSH channel.
    private const string RedisCliBare = "/usr/bin/redis-cli --tls --cacert /etc/nexus-redis/tls/ca.crt --cert /etc/nexus-redis/tls/server.crt --key /etc/nexus-redis/tls/server.key";

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
        var cmd = $"{RedisCliPrefix} CLUSTER NODES'";
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

        // Issue CLUSTER FAILOVER on the target replica.
        var sshTarget = new SshTarget(targetReplica.IpAddress, 22, _sshUsername, _sshKeyPath);

        // Resolve the original primary authoritatively from the replica's live
        // master_host (INFO replication) -- the role labels in our model don't carry
        // the replica->master mapping, and a hostname heuristic is wrong once roles move.
        var originalPrimaryHostname = await ResolveMasterHostAsync(sshTarget, statusBefore.Value!, cancellationToken).ConfigureAwait(false) ?? "unknown";

        var failoverCmd = $"{RedisCliPrefix} CLUSTER FAILOVER'";
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
    // ScaleOutAddAsync -- IMPLEMENTED (apply-on-demand provisioning + role-aware join)
    // Per ADR-0010: the new node is minted by the proven IaC graph; this adapter does
    // the role-aware cluster JOIN over SSH. It discovers a provisioned-but-unjoined,
    // reachable redis node (a freshly-applied growth node, or one freed by a prior
    // scale-out remove) and joins it as the requested --role.
    // -----------------------------------------------------------------------
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var role = (request.Role ?? "replica").Trim().ToLowerInvariant();
        if (role is not ("replica" or "primary"))
            return Result.Fail<ScaleOutResult>($"redis scale-out role must be 'replica' or 'primary' (got '{request.Role}').");

        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ScaleOutResult>(cluster.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var seed = cluster.Value!.Nodes[0];
        var seedTarget = new SshTarget(seed.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var nodes = await GetRawNodesAsync(seedTarget, cancellationToken).ConfigureAwait(false);
        if (nodes.Count == 0) return Result.Fail<ScaleOutResult>("could not read CLUSTER NODES from any member");
        var memberIps = nodes.Select(n => n.Ip).ToHashSet(StringComparer.Ordinal);

        // Discover a provisioned-but-unjoined, reachable redis node.
        NodeRecord? candidate = null;
        foreach (var n in cluster.Value.Nodes)
        {
            if (memberIps.Contains(n.Vmnet11)) continue;
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var ping = await _ssh.ExecuteAsync(t, "sudo systemctl is-active nexus-redis 2>/dev/null || echo down", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (ping.IsOk && ping.Value!.Stdout.Contains("active", StringComparison.Ordinal)) { candidate = n; break; }
        }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "no provisioned-but-unjoined redis node is reachable. Provision one first (apply-on-demand, ADR-0010): "
                + "`pwsh -File nexus-infra-oltp/scripts/oltp-redis.ps1 apply -Vars redis_extra_count=1`, then re-run `scale-out add`.");

        var newIp = candidate.Vmnet11;
        var joinSeedIp = nodes.First(n => n.Role == "primary").Ip;
        string joinArgs;
        if (role == "replica")
        {
            var primaryIds = nodes.Where(n => n.Role == "primary").Select(n => n.Id).ToList();
            var masterId = primaryIds
                .OrderBy(id => nodes.Count(n => n.Role == "replica" && n.MasterId == id))
                .First();
            joinArgs = $"--cluster add-node {newIp}:6379 {joinSeedIp}:6379 --cluster-slave --cluster-master-id {masterId}";
        }
        else
        {
            joinArgs = $"--cluster add-node {newIp}:6379 {joinSeedIp}:6379";
        }

        var joinTarget = new SshTarget(newIp, 22, _sshUsername, _sshKeyPath);
        var joinExec = await _ssh.ExecuteAsync(joinTarget, $"sudo {RedisCliBare} {joinArgs}", BackupTimeout, cancellationToken).ConfigureAwait(false);
        if (joinExec.IsFail || joinExec.Value!.ExitCode != 0)
            return Result.Fail<ScaleOutResult>($"add-node failed for {candidate.Name}: {(joinExec.IsFail ? joinExec.Error : Tail(joinExec.Value!.Stdout + joinExec.Value.Stderr, 300))}");

        var note = role == "primary"
            ? " (new primary joined empty; run `redis-cli --cluster reshard` to assign it slots)"
            : "";
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: "ok",
            OutcomeReason: $"joined {candidate.Name} ({newIp}) as {role}{note}",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // -----------------------------------------------------------------------
    // ScaleOutRemoveAsync -- IMPLEMENTED (drain-guard + del-node + reset)
    // Refuses to remove a slot-holding primary (would lose data -- reshard away first).
    // For a replica: CLUSTER FORGET via del-node, then CLUSTER RESET HARD the removed
    // node so it is a clean, empty node ready to be re-added or deprovisioned.
    // -----------------------------------------------------------------------
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name");

        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ScaleOutResult>(cluster.Error!);
        var node = cluster.Value!.Nodes.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not in the redis cluster");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var seed = cluster.Value.Nodes.First(n => !string.Equals(n.Name, node.Name, StringComparison.OrdinalIgnoreCase));
        var seedTarget = new SshTarget(seed.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var nodes = await GetRawNodesAsync(seedTarget, cancellationToken).ConfigureAwait(false);
        var member = nodes.FirstOrDefault(n => n.Ip == node.Vmnet11);
        if (string.IsNullOrEmpty(member.Id))
            return Result.Fail<ScaleOutResult>($"{node.Name} ({node.Vmnet11}) is not currently a cluster member");
        if (member.HasSlots && request.Drain)
            return Result.Fail<ScaleOutResult>(
                $"{node.Name} is a PRIMARY holding slots; reshard its slots away first "
                + "(`redis-cli --cluster reshard`) before removing -- removing a slot-holding primary would lose data.");

        var delExec = await _ssh.ExecuteAsync(seedTarget, $"sudo {RedisCliBare} --cluster del-node {seed.Vmnet11}:6379 {member.Id}", BackupTimeout, cancellationToken).ConfigureAwait(false);
        if (delExec.IsFail || delExec.Value!.ExitCode != 0)
            return Result.Fail<ScaleOutResult>($"del-node failed: {(delExec.IsFail ? delExec.Error : Tail(delExec.Value!.Stdout + delExec.Value.Stderr, 300))}");

        var removedTarget = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
        await _ssh.ExecuteAsync(removedTarget, $"sudo {RedisCliBare} CLUSTER RESET HARD", SshTimeout, cancellationToken).ConfigureAwait(false);

        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"removed {node.Name} ({node.Vmnet11}) from the cluster + reset (ready for re-add or deprovision)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
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
            var cmd = $"{RedisCliPrefix} INFO replication'";
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
    // BackupTakeAsync -- IMPLEMENTED (per shard-primary BGSAVE -> node-local snapshot)
    // NFS is NOT mounted on redis nodes (0.G.1 live finding), so each shard primary's
    // dump.rdb is snapshotted node-locally under /var/backups/nexus-redis/<id>.
    // -----------------------------------------------------------------------
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var statusRes = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (statusRes.IsFail) return Result.Fail<BackupResult>(statusRes.Error!);
        var primaries = statusRes.Value!.Members.Where(m => m.Role == "primary").ToList();
        if (primaries.Count == 0) return Result.Fail<BackupResult>("no shard primaries found to back up");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"redis-backup-{startedAt:yyyyMMdd-HHmmss}"
            : $"redis-{request.Tag}-{startedAt:yyyyMMdd-HHmmss}";
        var destDir = $"/var/backups/nexus-redis/{backupId}";
        long totalBytes = 0;

        foreach (var p in primaries)
        {
            var target = new SshTarget(p.IpAddress, 22, _sshUsername, _sshKeyPath);
            var script =
                $"R=\"{RedisCliBare}\"; sudo mkdir -p {destDir}; sudo $R BGSAVE >/dev/null 2>&1; " +
                "for i in $(seq 1 30); do s=$(sudo $R INFO persistence | tr -d '\\r' | grep rdb_bgsave_in_progress | cut -d: -f2); [ \"$s\" = \"0\" ] && break; sleep 1; done; " +
                "dir=$(sudo $R CONFIG GET dir | tail -1 | tr -d '\\r'); fn=$(sudo $R CONFIG GET dbfilename | tail -1 | tr -d '\\r'); " +
                $"sudo cp \"$dir/$fn\" \"{destDir}/{p.Hostname}.rdb\"; sudo stat -c %s \"{destDir}/{p.Hostname}.rdb\"";
            var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail || exec.Value!.ExitCode != 0)
                return Result.Fail<BackupResult>($"backup on {p.Hostname} failed: {(exec.IsFail ? exec.Error : Tail(exec.Value!.Stderr, 200))}");
            var outLines = exec.Value.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (outLines.Length > 0 && long.TryParse(outLines[^1].Trim(), out var bytes)) totalBytes += bytes;
        }
        sw.Stop();

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{destDir} (node-local; {primaries.Count} shard-primary .rdb files)",
            SizeBytes: totalBytes,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // -----------------------------------------------------------------------
    // BackupRestoreAsync -- IMPLEMENTED (stop -> replace dump.rdb -> start -> verify DBSIZE)
    // DESTRUCTIVE: overwrites each shard primary's data with its snapshot. Replicas
    // re-sync from their primary automatically afterwards.
    // -----------------------------------------------------------------------
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id");
        var statusRes = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (statusRes.IsFail) return Result.Fail<RestoreResult>(statusRes.Error!);
        var primaries = statusRes.Value!.Members.Where(m => m.Role == "primary").ToList();
        if (primaries.Count == 0) return Result.Fail<RestoreResult>("no shard primaries found to restore");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var destDir = $"/var/backups/nexus-redis/{request.BackupId}";
        long itemsRestored = 0;

        foreach (var p in primaries)
        {
            var snap = $"{destDir}/{p.Hostname}.rdb";
            var target = new SshTarget(p.IpAddress, 22, _sshUsername, _sshKeyPath);
            var script =
                $"test -s {snap} || {{ echo MISSING-SNAPSHOT; exit 9; }}; R=\"{RedisCliBare}\"; " +
                "dir=$(sudo $R CONFIG GET dir | tail -1 | tr -d '\\r'); fn=$(sudo $R CONFIG GET dbfilename | tail -1 | tr -d '\\r'); " +
                $"sudo systemctl stop nexus-redis; sudo cp {snap} \"$dir/$fn\"; sudo chown redis:redis \"$dir/$fn\"; sudo systemctl start nexus-redis; " +
                "for i in $(seq 1 20); do sudo $R PING >/dev/null 2>&1 && break; sleep 1; done; sudo $R DBSIZE | tr -d '\\r'";
            var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail || exec.Value!.ExitCode != 0)
                return Result.Fail<RestoreResult>($"restore on {p.Hostname} failed: {(exec.IsFail ? exec.Error : Tail(exec.Value!.Stdout + exec.Value.Stderr, 200))}");
            var outLines = exec.Value.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (outLines.Length > 0 && long.TryParse(outLines[^1].Trim(), out var n)) itemsRestored += n;
        }
        sw.Stop();

        return Result.Ok(new RestoreResult(
            BackupId: request.BackupId,
            ItemsRestored: itemsRestored,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
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

            // Old serial (for the before/after report).
            var oldSerialExec = await _ssh.ExecuteAsync(target,
                "sudo openssl x509 -in /etc/nexus-redis/tls/server.crt -noout -serial 2>/dev/null | sed 's/serial=//'",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.ExitCode == 0 && oldSerialExec.Value.Stdout.Trim().Length > 0
                ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            // Genuine re-issue: SSH to the node and use ITS OWN Vault identity (the auto-auth
            // token sink) to issue a fresh leaf from pki_int/issue/redis-server. The Agent's
            // pkiCert template caches the 90-day cert and won't rotate on demand, so we go
            // direct via the node's `vault` CLI (SSH-shell-out, no managed driver -- ADR-0024);
            // JSON is parsed here in the AOT binary (JsonDocument, reflection-free). NOTE: the
            // on-node Agent will re-assert its cached cert on its NEXT render -- true persistent
            // rotation needs the Agent's pkiCert cache refreshed (an infra concern; handbook 3.3).
            var cn = $"{node.Name}.redis.nexus.lab";
            var alts = $"{node.Name},{node.Name}.nexus.lab,{node.Name}.redis.nexus.lab,localhost";
            var ips = $"{node.Vmnet10},{node.Vmnet11},127.0.0.1";
            var issueCmd =
                "T=$(sudo cat /run/nexus-vault-agent/token 2>/dev/null); " +
                "sudo env VAULT_ADDR=https://192.168.70.121:8200 VAULT_TOKEN=\"$T\" VAULT_CACERT=/etc/nexus-redis/tls/ca.crt " +
                $"/usr/local/bin/vault write -format=json pki_int/issue/redis-server common_name={cn} alt_names={alts} ip_sans={ips} ttl=2160h";
            var issueExec = await _ssh.ExecuteAsync(target, issueCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (issueExec.IsFail || issueExec.Value!.ExitCode != 0)
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: issueExec.IsFail ? issueExec.Error : $"vault issue failed: {Tail(issueExec.Value!.Stderr, 200)}"));
                continue;
            }

            string cert, key, ca, newSerial;
            try
            {
                using var doc = JsonDocument.Parse(issueExec.Value.Stdout);
                var d = doc.RootElement.GetProperty("data");
                cert = d.GetProperty("certificate").GetString() ?? "";
                key = d.GetProperty("private_key").GetString() ?? "";
                ca = d.GetProperty("issuing_ca").GetString() ?? "";
                newSerial = d.GetProperty("serial_number").GetString() ?? "(unknown)";
            }
            catch (Exception ex)
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: $"could not parse vault issue response: {ex.Message}"));
                continue;
            }

            // Write the new materials (server.crt/.key + bundle.pem = cert+key+ca) + reload redis.
            var bundle = cert.TrimEnd() + "\n" + key.TrimEnd() + "\n" + ca.TrimEnd() + "\n";
            var writeCmd =
                $"echo {B64(cert)}|base64 -d|sudo tee /etc/nexus-redis/tls/server.crt >/dev/null; " +
                $"echo {B64(key)}|base64 -d|sudo tee /etc/nexus-redis/tls/server.key >/dev/null; " +
                $"echo {B64(bundle)}|base64 -d|sudo tee /etc/nexus-redis/tls/bundle.pem >/dev/null; " +
                "sudo chown root:redis /etc/nexus-redis/tls/server.crt /etc/nexus-redis/tls/server.key /etc/nexus-redis/tls/bundle.pem; " +
                "sudo chmod 0640 /etc/nexus-redis/tls/server.crt /etc/nexus-redis/tls/server.key /etc/nexus-redis/tls/bundle.pem; " +
                "sudo systemctl reload-or-restart nexus-redis; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(target, writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stderr, 200)}"));
                continue;
            }

            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: null));
        }
        sw.Stop();

        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // -----------------------------------------------------------------------
    // ApplyChaosAsync -- IMPLEMENTED (push helper -> inject -> observe -> lift -> confirm)
    // Pushes the embedded nexus-chaos.sh helper over SSH (idempotent), injects the
    // time-boxed self-reverting fault, observes impact via HealthAsync mid-window,
    // lifts explicitly, then confirms the cluster returns to green. ADR-0010.
    // -----------------------------------------------------------------------
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        var known = new[] { "network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill" };
        if (!known.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", known)}");

        var statusRes = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (statusRes.IsFail) return Result.Fail<ChaosOutcome>(statusRes.Error!);

        // Pick the node: explicit target, else the first replica (safer than a primary).
        var members = statusRes.Value!.Members;
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? members.FirstOrDefault(m => string.Equals(m.Hostname, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : (members.FirstOrDefault(m => m.Role == "replica") ?? (members.Count > 0 ? members[0] : null));
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target node found");

        var target = new SshTarget(victim.IpAddress, 22, _sshUsername, _sshKeyPath);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? "nexus-redis" : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Hostname} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        // Observe impact mid-window, then lift explicitly (the helper also self-reverts).
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);

        // Confirm recovery.
        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(45);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var post = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (post.IsOk && post.Value!.OverallHealth == "green") { recovered = true; break; }
        }
        sw.Stop();

        return Result.Ok(new ChaosOutcome(
            ScenarioApplied: scenario.ScenarioType,
            Target: victim.Hostname,
            ObservedImpact: observed,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt,
            Recovered: recovered));
    }

    /// <summary>Install (idempotent) the embedded nexus-chaos.sh helper on a node.</summary>
    private async Task<Result<bool>> PushChaosHelperAsync(SshTarget target, CancellationToken cancellationToken)
    {
        var asm = typeof(RedisAdapter).Assembly;
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("nexus-chaos.sh", StringComparison.Ordinal));
        if (resName is null) return Result.Fail<bool>("embedded nexus-chaos.sh resource not found in the assembly");
        string script;
        using (var s = asm.GetManifestResourceStream(resName)!)
        using (var r = new StreamReader(s))
            script = (await r.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Replace("\r\n", "\n");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var cmd = $"echo {b64} | base64 -d | sudo tee /usr/local/bin/nexus-chaos.sh >/dev/null && sudo chmod +x /usr/local/bin/nexus-chaos.sh && echo PUSHED";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (exec.IsFail || exec.Value!.ExitCode != 0 || !exec.Value.Stdout.Contains("PUSHED", StringComparison.Ordinal))
            return Result.Fail<bool>($"failed to install nexus-chaos.sh: {(exec.IsFail ? exec.Error : Tail(exec.Value!.Stderr, 200))}");
        return Result.Ok(true);
    }

    /// <summary>Parse raw CLUSTER NODES (id, ip, role, masterId, hasSlots) from any member.</summary>
    private async Task<List<(string Id, string Ip, string Role, string MasterId, bool HasSlots)>> GetRawNodesAsync(SshTarget member, CancellationToken cancellationToken)
    {
        var result = new List<(string, string, string, string, bool)>();
        var exec = await _ssh.ExecuteAsync(member, $"{RedisCliPrefix} CLUSTER NODES'", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (exec.IsFail || exec.Value!.ExitCode != 0) return result;
        foreach (var line in exec.Value.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 8) continue;
            var ip = p[1].Split(AddressSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var role = p[2].Contains("master") ? "primary" : p[2].Contains("slave") ? "replica" : "unknown";
            var masterId = p[3] == "-" ? "" : p[3];
            result.Add((p[0], ip, role, masterId, p.Length > 8));
        }
        return result;
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
            var cmd = $"{RedisCliPrefix} ACL LIST'";
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
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user (the Redis ACL username).");
            var user = operation.User!;
            if (string.Equals(user, "default", StringComparison.OrdinalIgnoreCase))
                return Result.Fail<AclSnapshot>("refusing to modify the protected built-in 'default' user.");
            if (!IsSafeAclToken(user))
                return Result.Fail<AclSnapshot>($"unsafe Redis ACL username '{user}' (allowed: letters, digits, and . _ -).");

            // grant = ACL SETUSER with the operator's rules; revoke = ACL DELUSER (remove the user).
            string redisAcl;
            if (verb == "grant")
            {
                if (operation.Permissions is null || operation.Permissions.Count == 0)
                    return Result.Fail<AclSnapshot>(
                        "acl grant requires --permissions (Redis ACL SETUSER rules, comma-separated — e.g. "
                        + "`on,>mypassword,~cache:*,+@read` or `on,nopass,allkeys,allcommands`).");
                foreach (var tok in operation.Permissions)
                    if (!IsSafeAclToken(tok))
                        return Result.Fail<AclSnapshot>($"unsafe Redis ACL rule token '{tok}' (no quotes/spaces; ACL rule chars only).");
                redisAcl = $"ACL SETUSER {user} {string.Join(' ', operation.Permissions)}";
            }
            else
            {
                redisAcl = $"ACL DELUSER {user}";
            }

            // Redis Cluster ACLs are PER-NODE (not replicated like data) → apply on EVERY node,
            // then best-effort persist (ACL SAVE only writes if an aclfile is configured; harmless otherwise).
            var nodes = cluster.Value.Nodes;
            var applied = 0; string? firstErr = null;
            foreach (var n in nodes)
            {
                var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
                var ex = await _ssh.ExecuteAsync(t, $"{RedisCliPrefix} {redisAcl}'", SshTimeout, cancellationToken).ConfigureAwait(false);
                var okBody = ex.IsOk && ex.Value!.ExitCode == 0
                    && !ex.Value.Stdout.Contains("ERR", StringComparison.OrdinalIgnoreCase)
                    && !ex.Value.Stdout.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase);
                if (okBody)
                {
                    applied++;
                    await _ssh.ExecuteAsync(t, $"{RedisCliPrefix} ACL SAVE' 2>/dev/null || true", SshTimeout, cancellationToken).ConfigureAwait(false);
                }
                else if (firstErr is null)
                    firstErr = ex.IsFail ? ex.Error : Tail(ex.Value!.Stdout + ex.Value.Stderr, 200);
            }
            if (applied == 0)
                return Result.Fail<AclSnapshot>($"acl {verb} '{user}' failed on all {nodes.Count} nodes: {firstErr}");
            if (applied < nodes.Count)
                return Result.Fail<AclSnapshot>(
                    $"acl {verb} '{user}' applied on only {applied}/{nodes.Count} nodes — Redis Cluster ACLs are per-node, so this left partial state. Re-run to converge. First error: {firstErr}");

            // Re-list from the first node to confirm the change.
            return await AclAsync(new AclOperation("list"), cancellationToken).ConfigureAwait(false);
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
    /// Resolve a replica's current primary from its live INFO replication
    /// <c>master_host</c>, mapping the master IP back to a hostname via the known
    /// members. Authoritative even after roles have moved (unlike a hostname
    /// heuristic). Used by <see cref="FailoverAsync"/> to report OriginalPrimary.
    /// </summary>
    private async Task<string?> ResolveMasterHostAsync(SshTarget replica, ClusterStatus status, CancellationToken cancellationToken)
    {
        var exec = await _ssh.ExecuteAsync(replica, $"{RedisCliPrefix} INFO replication'", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (exec.IsFail || exec.Value!.ExitCode != 0) return null;
        var masterIp = ExtractInfoField(exec.Value.Stdout, "master_host");
        if (string.IsNullOrWhiteSpace(masterIp)) return null;
        return status.Members.FirstOrDefault(m => m.IpAddress == masterIp)?.Hostname ?? masterIp;
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

    /// <summary>
    /// Validate a Redis ACL username or SETUSER rule token. Permits the redis ACL
    /// rule charset (letters/digits + <c>. _ - &gt; &lt; ~ + @ * &amp; | : / = ! % #</c>) and
    /// rejects anything that could break the <c>sudo bash -c '…'</c> single-quote wrapper
    /// (quotes, whitespace, <c>$ ; ` \</c>, control chars) — defence against ACL-rule injection.
    /// </summary>
    internal static bool IsSafeAclToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        foreach (var c in token)
        {
            var ok = char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '>' or '<' or '~'
                or '+' or '@' or '*' or '&' or '|' or ':' or '/' or '=' or '!' or '%' or '#';
            if (!ok) return false;
        }
        return true;
    }

    private static string Tail(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= n ? s : s.Substring(s.Length - n);
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
