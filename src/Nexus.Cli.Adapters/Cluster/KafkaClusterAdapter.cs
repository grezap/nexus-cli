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
/// Per-cluster Kafka adapter for Phase 0.H.7 / nexus-cli v0.6.7. ONE instance
/// is registered per KRaft cluster -- <c>kafka-east</c> + <c>kafka-west</c>
/// (matching the vms.yaml cluster keys + every other adapter's ClusterId ==
/// vms.yaml-name convention). The existing <see cref="KafkaAdapter"/>
/// (ClusterId <c>kafka</c>) stays as the cross-region DR meta-cluster
/// (east&lt;-&gt;west MirrorMaker-2 failover); this adapter is the full
/// per-cluster verb surface (status/health/topology/failover/scale-out/
/// backup/cert-rotate/acl/chaos) against that cluster's 3 combined
/// broker+controller nodes.
/// <para>
/// Auth = mTLS-ONLY (ADR-0018). There is NO operator password and NO
/// <see cref="INexusVaultClient"/> -- like <see cref="RedisAdapter"/>. The
/// operator identity is the broker's own Vault-PKI keystore: every Kafka CLI
/// runs ON a broker over SSH via
/// <c>sudo /opt/kafka/bin/kafka-*.sh --command-config
/// /etc/nexus-kafka/client-ssl.properties</c> (the keystore.pem doubles as the
/// client cert; the kafka-broker PKI role has client_flag=true). Sudo is
/// required because <c>/etc/nexus-kafka/</c> is <c>0750 root:kafka</c> and
/// nexusadmin is not in the kafka group (the consul-/etc/-0750 lesson, Kafka
/// edition). NO managed Confluent.Kafka driver is linked (NetArchTest
/// no-managed-driver invariant; AOT footprint stays flat -- ADR-0009).
/// </para>
/// <para>
/// Live contract reverse-engineered against the running tier (v0.6.7 probe):
/// brokers are combined broker+controller (process.roles=broker,controller);
/// client + inter-broker listener SSL://0.0.0.0:9092, controller listener
/// CONTROLLER://&lt;vmnet10&gt;:9093. The CLI bootstrap is the broker's OWN
/// VMnet10 backplane IP (<c>SSL://&lt;vmnet10&gt;:9092</c>) because
/// <c>ssl.endpoint.identification.algorithm=https</c> requires the bootstrap
/// host to be in the broker cert SAN (the VMnet10 IP is). ACL enforcement
/// (the StandardAuthorizer) is enabled by
/// role-overlay-kafka-acl-authorizer.tf (Phase 0.H.7).
/// </para>
/// <para>
/// IMPORTANT CLI-flag note: the admin tools (kafka-topics / kafka-acls /
/// kafka-metadata-quorum / kafka-configs) take <c>--command-config</c>, but
/// kafka-console-producer takes <c>--producer.config</c> and
/// kafka-console-consumer takes <c>--consumer.config</c> -- passing
/// --command-config to the console tools silently prints usage and consumes
/// nothing (caught live during v0.6.7 backup-verb dev).
/// </para>
/// </summary>
public sealed class KafkaClusterAdapter : IClusterAdapter
{
    private const string KafkaBin = "/opt/kafka/bin";
    private const string ClientCfg = "/etc/nexus-kafka/client-ssl.properties";
    private const string ServerProps = "/etc/nexus-kafka/server.properties";
    private const string Keystore = "/etc/nexus-kafka/tls/keystore.pem";
    private const string PkiRole = "kafka-broker";
    private const string VaultAddr = "https://192.168.70.121:8200";
    private const string VaultCaCert = "/etc/ssl/certs/kafka-ca.pem"; // world-readable intermediate CA (chains the Vault listener cert too)
    private const string AgentTokenPath = "/run/nexus-vault-agent/token";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan FailoverPoll = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RejoinDeadline = TimeSpan.FromSeconds(120);

    private readonly string _clusterId;
    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;

    private ClusterStatus? _lastStatus;

    public KafkaClusterAdapter(string clusterId, IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath)
    {
        _clusterId = clusterId;
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
    }

    public string ClusterId => _clusterId;
    public string DisplayName =>
        $"Apache Kafka ({(_clusterId.EndsWith("east", StringComparison.OrdinalIgnoreCase) ? "primary east" : "DR west")} KRaft cluster)";

    // === Broker discovery ===================================================

    /// <summary>A broker: hostname + service IP (VMnet11) + backplane IP (VMnet10) + KRaft node.id.</summary>
    private sealed record Broker(string Hostname, string Vmnet11, string Vmnet10, int NodeId);

    /// <summary>
    /// Resolve the 3 brokers from vms.yaml + the live controller.quorum.voters
    /// (parsed from a seed broker's server.properties) to attach the KRaft
    /// node.id to each. One sudo SSH. Returns brokers ordered by node.id.
    /// </summary>
    private async Task<Result<List<Broker>>> ResolveBrokersAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(_clusterId);
        if (cluster.IsFail) return Result.Fail<List<Broker>>(cluster.Error!);
        var nodes = cluster.Value!.Nodes;
        if (nodes.Count == 0) return Result.Fail<List<Broker>>($"cluster '{_clusterId}' has no nodes in vms.yaml");

        // node.id <- controller.quorum.voters (id@backplane:9093,...). Try each
        // node until one answers (a single broker may be down mid-failover).
        Dictionary<string, int>? byBackplane = null;
        foreach (var n in nodes)
        {
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var exec = await _ssh.ExecuteAsync(t, $"sudo grep '^controller.quorum.voters=' {ServerProps}", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsOk && exec.Value!.ExitCode == 0 && exec.Value.Stdout.Contains('@', StringComparison.Ordinal))
            {
                byBackplane = ParseVoters(exec.Value.Stdout);
                break;
            }
        }
        if (byBackplane is null)
            return Result.Fail<List<Broker>>($"could not read controller.quorum.voters from any {_clusterId} broker (cluster down?)");

        var brokers = new List<Broker>();
        foreach (var n in nodes)
        {
            if (!byBackplane.TryGetValue(n.Vmnet10, out var id)) continue;
            brokers.Add(new Broker(n.Name, n.Vmnet11, n.Vmnet10, id));
        }
        if (brokers.Count == 0)
            return Result.Fail<List<Broker>>($"vms.yaml {_clusterId} nodes do not match the live quorum voters by backplane IP");
        return Result.Ok(brokers.OrderBy(b => b.NodeId).ToList());
    }

    /// <summary>Parse <c>controller.quorum.voters=1@ip:9093,2@ip:9093,...</c> into backplane-IP -&gt; node.id.</summary>
    internal static Dictionary<string, int> ParseVoters(string line)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var eq = line.IndexOf('=');
        var rhs = eq >= 0 ? line[(eq + 1)..] : line;
        foreach (var entry in rhs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = entry.Split('@', 2);
            if (at.Length != 2) continue;
            if (!int.TryParse(at[0].Trim(), out var id)) continue;
            var ip = at[1].Split(':', 2)[0].Trim();
            map[ip] = id;
        }
        return map;
    }

    private SshTarget Ssh(Broker b) => new(b.Vmnet11, 22, _sshUsername, _sshKeyPath);

    /// <summary>An admin-tool invocation bootstrapped against <paramref name="b"/>'s own backplane listener.</summary>
    private static string Admin(Broker b, string tool, string args) =>
        $"sudo {KafkaBin}/{tool} --bootstrap-server SSL://{b.Vmnet10}:9092 --command-config {ClientCfg} {args}";

    // === GetStatusAsync =====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<ClusterStatus>(brokersRes.Error!);
        var brokers = brokersRes.Value!;

        // Probe from a seed (first node that answers). One SSH returns every section.
        QuorumView? q = null;
        Broker? seed = null;
        foreach (var b in brokers)
        {
            var script =
                $"echo '===QSTATUS==='; {Admin(b, "kafka-metadata-quorum.sh", "describe --status")} 2>/dev/null; " +
                $"echo '===QREPL==='; {Admin(b, "kafka-metadata-quorum.sh", "describe --replication")} 2>/dev/null; " +
                $"echo '===UNDERREP==='; {Admin(b, "kafka-topics.sh", "--describe --under-replicated-partitions")} 2>/dev/null | grep -c Partition; " +
                $"echo '===OFFLINE==='; {Admin(b, "kafka-topics.sh", "--describe --unavailable-partitions")} 2>/dev/null | grep -c Partition; " +
                "echo '===END==='";
            var exec = await _ssh.ExecuteAsync(Ssh(b), script, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsOk && exec.Value!.Stdout.Contains("LeaderId", StringComparison.Ordinal))
            {
                q = ParseQuorum(exec.Value.Stdout);
                seed = b;
                break;
            }
        }
        if (q is null || seed is null)
            return Result.Fail<ClusterStatus>($"could not read KRaft quorum from any {_clusterId} broker");

        var byId = brokers.ToDictionary(b => b.NodeId, b => b);
        var members = new List<ClusterMember>();
        foreach (var b in brokers)
        {
            var repl = q.Replicas.FirstOrDefault(r => r.NodeId == b.NodeId);
            var isLeader = b.NodeId == q.LeaderId;
            var inVoters = q.Voters.Contains(b.NodeId);
            // A voter whose fetch lag is huge / missing is treated as down.
            var status = !inVoters ? "down"
                : repl is null ? "unknown"
                : repl.Lag <= 0 ? "alive"
                : repl.Lag < 5000 ? "lagging"
                : "down";
            members.Add(new ClusterMember(
                Hostname: b.Hostname,
                IpAddress: b.Vmnet11,
                Role: isLeader ? "controller-leader" : "controller-follower",
                Status: status,
                ShardId: null,
                ReplicationLagSeconds: null));
        }

        var aliveVoters = members.Count(m => m.Status == "alive");
        var overall = (q.LeaderId >= 0 && aliveVoters == brokers.Count && q.UnderReplicated == 0 && q.Offline == 0) ? "green"
            : (q.LeaderId >= 0 && aliveVoters >= (brokers.Count / 2 + 1) && q.Offline == 0) ? "yellow"
            : "red";

        var leaderHost = byId.TryGetValue(q.LeaderId, out var lb) ? lb.Hostname : null;
        var status2 = new ClusterStatus(_clusterId, DisplayName, overall, members, leaderHost, DateTimeOffset.UtcNow);
        _lastStatus = status2;
        return Result.Ok(status2);
    }

    // === HealthAsync ========================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<HealthReport>(brokersRes.Error!);
        var brokers = brokersRes.Value!;
        var probes = new List<HealthProbe>();

        // Per-broker kafka.service liveness.
        var liveCount = 0;
        Broker? up = null;
        foreach (var b in brokers)
        {
            var exec = await _ssh.ExecuteAsync(Ssh(b), "systemctl is-active kafka.service", SshTimeout, cancellationToken).ConfigureAwait(false);
            var active = exec.IsOk && exec.Value!.Stdout.Trim() == "active";
            if (active) { liveCount++; up ??= b; }
            probes.Add(new HealthProbe("kafka-service", b.Hostname, active ? "green" : "red",
                active ? "active" : (exec.IsOk ? exec.Value!.Stdout.Trim() : exec.Error), "kafka.service active"));
        }
        if (up is null)
            return Result.Ok(new HealthReport(_clusterId, "red", probes, DateTimeOffset.UtcNow));

        // Quorum view from a live broker.
        var script =
            $"echo '===QSTATUS==='; {Admin(up, "kafka-metadata-quorum.sh", "describe --status")} 2>/dev/null; " +
            $"echo '===QREPL==='; {Admin(up, "kafka-metadata-quorum.sh", "describe --replication")} 2>/dev/null; " +
            $"echo '===UNDERREP==='; {Admin(up, "kafka-topics.sh", "--describe --under-replicated-partitions")} 2>/dev/null | grep -c Partition; " +
            $"echo '===OFFLINE==='; {Admin(up, "kafka-topics.sh", "--describe --unavailable-partitions")} 2>/dev/null | grep -c Partition; " +
            "echo '===END==='";
        var qexec = await _ssh.ExecuteAsync(Ssh(up), script, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (qexec.IsFail || !qexec.Value!.Stdout.Contains("LeaderId", StringComparison.Ordinal))
            return Result.Fail<HealthReport>($"could not read KRaft quorum on {up.Hostname}: {(qexec.IsFail ? qexec.Error : "no LeaderId")}");
        var q = ParseQuorum(qexec.Value.Stdout);

        probes.Add(new HealthProbe("quorum-has-leader", _clusterId, q.LeaderId >= 0 ? "green" : "red",
            q.LeaderId >= 0 ? $"node {q.LeaderId}" : "no leader", "a controller-quorum leader exists"));
        probes.Add(new HealthProbe("quorum-voters", _clusterId, q.Voters.Count == brokers.Count ? "green" : q.Voters.Count >= 2 ? "yellow" : "red",
            $"{q.Voters.Count}/{brokers.Count}", $"{brokers.Count} voters present"));
        var maxLag = q.Replicas.Count > 0 ? q.Replicas.Max(r => r.Lag) : 0;
        probes.Add(new HealthProbe("voter-fetch-lag", _clusterId, maxLag == 0 ? "green" : maxLag < 5000 ? "yellow" : "red",
            $"max {maxLag}", "0 records green; <5000 yellow"));
        probes.Add(new HealthProbe("under-replicated-partitions", _clusterId, q.UnderReplicated == 0 ? "green" : "red",
            q.UnderReplicated.ToString(CultureInfo.InvariantCulture), "0 under-replicated"));
        probes.Add(new HealthProbe("offline-partitions", _clusterId, q.Offline == 0 ? "green" : "red",
            q.Offline.ToString(CultureInfo.InvariantCulture), "0 offline/unavailable"));

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(_clusterId, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync ======================================================
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var statusRes = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (statusRes.IsFail) return Result.Fail<TopologySnapshot>(statusRes.Error!);
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<TopologySnapshot>(brokersRes.Error!);
        var brokers = brokersRes.Value!;
        var byId = brokers.ToDictionary(b => b.NodeId, b => b.Hostname);

        var seed = brokers.FirstOrDefault(b => statusRes.Value!.Members.Any(m => m.Hostname == b.Hostname && m.Status != "down")) ?? brokers[0];
        var exec = await _ssh.ExecuteAsync(Ssh(seed), Admin(seed, "kafka-topics.sh", "--describe") + " 2>/dev/null", SshTimeout, cancellationToken).ConfigureAwait(false);
        var shards = new List<TopologyShard>();
        if (exec.IsOk && exec.Value!.ExitCode == 0)
            shards = ParseTopics(exec.Value.Stdout, byId);

        var nodes = statusRes.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.Role, m.Status, null))
            .ToList();

        return Result.Ok(new TopologySnapshot(_clusterId, nodes, shards, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (controlled controller-leader move) ==================
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var before = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (before.IsFail) return Result.Fail<FailoverResult>(before.Error!);
        var preFlightAt = sw.Elapsed;

        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<FailoverResult>(brokersRes.Error!);
        var brokers = brokersRes.Value!;

        var leaderHost = before.Value!.Leader;
        var leader = brokers.FirstOrDefault(b => b.Hostname == leaderHost);
        if (leader is null) return Result.Fail<FailoverResult>("could not identify the current quorum leader");
        var survivor = brokers.FirstOrDefault(b => b.NodeId != leader.NodeId);
        if (survivor is null) return Result.Fail<FailoverResult>("no surviving broker to observe re-election");

        // Stop kafka.service on the leader -> forces a controller re-election.
        var stop = await _ssh.ExecuteAsync(Ssh(leader), "sudo systemctl stop kafka.service", SshTimeout, cancellationToken).ConfigureAwait(false);
        var failureInjectedAt = sw.Elapsed;
        if (stop.IsFail || stop.Value!.ExitCode != 0)
            return Result.Fail<FailoverResult>($"failed to stop kafka.service on {leader.Hostname}: {(stop.IsFail ? stop.Error : stop.Value!.Stderr)}");

        // Poll a survivor until a NEW leader (node.id != old) is elected.
        int? newLeaderId = null;
        var newLeaderAt = TimeSpan.Zero;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(FailoverPoll, cancellationToken).ConfigureAwait(false);
            var st = await _ssh.ExecuteAsync(Ssh(survivor), Admin(survivor, "kafka-metadata-quorum.sh", "describe --status") + " 2>/dev/null", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (st.IsFail || st.Value!.ExitCode != 0) continue;
            var lid = ExtractInt(st.Value.Stdout, "LeaderId:");
            if (lid is >= 0 && lid != leader.NodeId) { newLeaderId = lid; newLeaderAt = sw.Elapsed; break; }
        }

        var rto = newLeaderId is not null ? newLeaderAt - failureInjectedAt : TimeSpan.Zero;
        var newLeaderHost = newLeaderId is not null ? brokers.FirstOrDefault(b => b.NodeId == newLeaderId)?.Hostname : null;

        // Recover: restart the old leader, wait for it to rejoin as a voter.
        var recovery = "skipped";
        if (!request.NoRecover)
        {
            await _ssh.ExecuteAsync(Ssh(leader), "sudo systemctl reset-failed kafka.service 2>/dev/null; sudo systemctl start kafka.service", SshTimeout, cancellationToken).ConfigureAwait(false);
            recovery = await WaitRejoinAsync(survivor, leader.NodeId, cancellationToken).ConfigureAwait(false) ? "recovered" : "failed";
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: $"{_clusterId}-controller-leader-move",
            OriginalPrimary: leader.Hostname,
            NewPrimary: newLeaderHost,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: newLeaderId is null ? "no new controller leader observed within the deadline; check the surviving brokers' metadata.quorum" : null,
            Timeline: new FailoverTimeline(preFlightAt, failureInjectedAt, newLeaderAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOutRemoveAsync (drain a broker) ===============================
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name");
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<ScaleOutResult>(brokersRes.Error!);
        var brokers = brokersRes.Value!;
        var target = brokers.FirstOrDefault(b => string.Equals(b.Hostname, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (target is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not in the {_clusterId} cluster");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Guard: removing a broker drops a controller-quorum voter. With a fixed
        // 3-node combined quorum, only one can be down without losing majority.
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsOk)
        {
            var aliveOthers = status.Value!.Members.Count(m => m.Status == "alive" && !string.Equals(m.Hostname, target.Hostname, StringComparison.OrdinalIgnoreCase));
            if (aliveOthers < 2)
                return Result.Fail<ScaleOutResult>(
                    $"refusing to drain {target.Hostname}: only {aliveOthers} other broker(s) are alive -- stopping this one would lose the controller-quorum majority. Restore the cluster first (`scale-out add {_clusterId}`).");
        }

        var stop = await _ssh.ExecuteAsync(Ssh(target), "sudo systemctl stop kafka.service", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || stop.Value!.ExitCode != 0)
            return Result.Fail<ScaleOutResult>($"failed to stop kafka.service on {target.Hostname}: {(stop.IsFail ? stop.Error : stop.Value!.Stderr)}");
        sw.Stop();

        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [target.Hostname],
            Outcome: "ok",
            OutcomeReason: $"drained {target.Hostname} (kafka.service stopped); quorum now runs on the remaining {brokers.Count - 1} voters (degraded -- re-add with `scale-out add {_clusterId}` to restore RF/quorum resilience)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === ScaleOutAddAsync (broker rejoin) ===================================
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<ScaleOutResult>(brokersRes.Error!);
        var brokers = brokersRes.Value!;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Find a provisioned-but-stopped broker (e.g. one drained by scale-out
        // remove, the failover leader, or a chaos victim that exited) and rejoin
        // it. The KRaft controller quorum is FIXED at 3 combined nodes at format
        // time, so this is broker REJOIN, not new-controller expansion.
        Broker? stopped = null;
        Broker? live = null;
        foreach (var b in brokers)
        {
            var exec = await _ssh.ExecuteAsync(Ssh(b), "systemctl is-active kafka.service", SshTimeout, cancellationToken).ConfigureAwait(false);
            var active = exec.IsOk && exec.Value!.Stdout.Trim() == "active";
            if (active) live ??= b; else stopped ??= b;
        }
        if (stopped is null)
            return Result.Fail<ScaleOutResult>(
                $"all {brokers.Count} {_clusterId} brokers are already running. The KRaft controller quorum is fixed at {brokers.Count} combined broker+controller nodes at format time, so there is no stopped broker to rejoin. Growing the cluster (a 4th broker) is an apply-on-demand IaC operation: add a node to vms.yaml + the kafka env and `kafka.ps1 apply`, then it joins as a broker-only node.");
        if (live is null) return Result.Fail<ScaleOutResult>("no live broker to verify rejoin against");

        var start = await _ssh.ExecuteAsync(Ssh(stopped), "sudo systemctl reset-failed kafka.service 2>/dev/null; sudo systemctl start kafka.service", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (start.IsFail || start.Value!.ExitCode != 0)
            return Result.Fail<ScaleOutResult>($"failed to start kafka.service on {stopped.Hostname}: {(start.IsFail ? start.Error : start.Value!.Stderr)}");

        var rejoined = await WaitRejoinAsync(live, stopped.NodeId, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [stopped.Hostname],
            Outcome: rejoined ? "ok" : "partial",
            OutcomeReason: rejoined
                ? $"restarted {stopped.Hostname}; it rejoined the controller quorum (caught up, lag 0)"
                : $"started {stopped.Hostname} but it did not catch up within {RejoinDeadline.TotalSeconds:F0}s -- check its journal",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupTakeAsync (topic -> node-local file capture) =================
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<BackupResult>(brokersRes.Error!);
        var live = await FirstLiveAsync(brokersRes.Value!, cancellationToken).ConfigureAwait(false);
        if (live is null) return Result.Fail<BackupResult>($"no live {_clusterId} broker to back up from");

        var srcTopic = string.IsNullOrWhiteSpace(request.Tag) ? "dr-gate-test" : request.Tag!;
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = $"{_clusterId}-{srcTopic}-{startedAt:yyyyMMdd-HHmmss}";
        var dir = "/var/backups/nexus-kafka";
        var file = $"{dir}/{backupId}.jsonl";

        // Consume the topic from the beginning to a node-local file (NOTE the
        // --consumer.config flag -- console tools reject --command-config).
        var script =
            $"sudo mkdir -p {dir}; " +
            $"sudo {KafkaBin}/kafka-console-consumer.sh --bootstrap-server SSL://{live.Vmnet10}:9092 --consumer.config {ClientCfg} " +
            $"--topic {srcTopic} --from-beginning --timeout-ms 15000 2>/dev/null | sudo tee {file} >/dev/null; " +
            $"sudo wc -l < {file}; sudo stat -c %s {file}";
        var exec = await _ssh.ExecuteAsync(Ssh(live), script, LongTimeout, cancellationToken).ConfigureAwait(false);
        if (exec.IsFail || exec.Value!.ExitCode != 0)
            return Result.Fail<BackupResult>($"backup capture of '{srcTopic}' failed on {live.Hostname}: {(exec.IsFail ? exec.Error : Tail(exec.Value!.Stderr, 200))}");
        var lines = exec.Value.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long count = 0, bytes = 0;
        if (lines.Length >= 1 && long.TryParse(lines[0].Trim(), out var c0)) count = c0;
        if (lines.Length >= 2 && long.TryParse(lines[^1].Trim(), out var b0)) bytes = b0;
        sw.Stop();

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{file} (node-local on {live.Hostname}; {count} records from topic '{srcTopic}')",
            SizeBytes: bytes,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupRestoreAsync (replay file -> verify topic, count round-trip) =
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id");
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<RestoreResult>(brokersRes.Error!);
        var live = await FirstLiveAsync(brokersRes.Value!, cancellationToken).ConfigureAwait(false);
        if (live is null) return Result.Fail<RestoreResult>($"no live {_clusterId} broker to restore on");

        var file = $"/var/backups/nexus-kafka/{request.BackupId}.jsonl";
        var verifyTopic = $"{request.BackupId}-restore";
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Create the verify topic, replay the captured file into it, consume it
        // back, and count (the produce->consume round-trip proves the restore).
        var script =
            $"test -s {file} || {{ echo MISSING-BACKUP; exit 9; }}; " +
            $"sudo {KafkaBin}/kafka-topics.sh --bootstrap-server SSL://{live.Vmnet10}:9092 --command-config {ClientCfg} --create --if-not-exists --topic {verifyTopic} --partitions 3 --replication-factor 3 >/dev/null 2>&1; " +
            $"sudo {KafkaBin}/kafka-console-producer.sh --bootstrap-server SSL://{live.Vmnet10}:9092 --producer.config {ClientCfg} --topic {verifyTopic} < {file} >/dev/null 2>&1; " +
            $"sudo {KafkaBin}/kafka-console-consumer.sh --bootstrap-server SSL://{live.Vmnet10}:9092 --consumer.config {ClientCfg} --topic {verifyTopic} --from-beginning --timeout-ms 15000 2>/dev/null | wc -l";
        var exec = await _ssh.ExecuteAsync(Ssh(live), script, LongTimeout, cancellationToken).ConfigureAwait(false);
        if (exec.IsFail || exec.Value!.ExitCode != 0)
            return Result.Fail<RestoreResult>($"restore of '{request.BackupId}' failed on {live.Hostname}: {(exec.IsFail ? exec.Error : Tail(exec.Value!.Stdout + exec.Value.Stderr, 200))}");
        long restored = 0;
        var outLines = exec.Value.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (outLines.Length > 0 && long.TryParse(outLines[^1].Trim(), out var r0)) restored = r0;
        sw.Stop();

        return Result.Ok(new RestoreResult(
            BackupId: request.BackupId,
            ItemsRestored: restored,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === RotateCertAsync (per-broker reissue + split + rolling restart) =====
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<CertRotationResult>(brokersRes.Error!);
        var brokers = brokersRes.Value!;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var b in brokers)
        {
            var target = Ssh(b);
            // Old serial = the leaf cert in keystore.pem (key + leaf + ca; the
            // first CERTIFICATE block is the leaf).
            var oldExec = await _ssh.ExecuteAsync(target,
                $"sudo openssl x509 -in {Keystore} -noout -serial 2>/dev/null | sed 's/serial=//'", SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldExec.IsOk && oldExec.Value!.ExitCode == 0 && oldExec.Value.Stdout.Trim().Length > 0 ? oldExec.Value.Stdout.Trim() : "(unknown)";

            // Re-issue from the node's OWN Vault Agent token (mirrors the agent
            // template's pkiCert call exactly), assemble bundle.pem (Cert/Key/CA),
            // run kafka-tls-split.sh, restart kafka.service, then wait rejoin.
            var cn = $"{b.Hostname}.kafka.nexus.lab";
            var alts = $"{b.Hostname},{b.Hostname}.nexus.lab,{b.Hostname}.kafka.nexus.lab,localhost";
            var ips = $"{b.Vmnet10},{b.Vmnet11},127.0.0.1";
            var issueCmd =
                $"T=$(sudo cat {AgentTokenPath} 2>/dev/null); " +
                $"sudo env VAULT_ADDR={VaultAddr} VAULT_TOKEN=\"$T\" VAULT_CACERT={VaultCaCert} " +
                $"/usr/local/bin/vault write -format=json pki_int/issue/{PkiRole} common_name={cn} alt_names={alts} ip_sans={ips} ttl=2160h";
            var issue = await _ssh.ExecuteAsync(target, issueCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (issue.IsFail || issue.Value!.ExitCode != 0)
            {
                rotated.Add(new CertRotatedNode(b.Hostname, oldSerial, "(unchanged)", issue.IsFail ? issue.Error : $"vault issue failed: {Tail(issue.Value!.Stderr, 200)}"));
                continue;
            }

            string cert, key, ca, newSerial;
            try
            {
                using var doc = JsonDocument.Parse(issue.Value.Stdout);
                var d = doc.RootElement.GetProperty("data");
                cert = d.GetProperty("certificate").GetString() ?? "";
                key = d.GetProperty("private_key").GetString() ?? "";
                ca = d.GetProperty("issuing_ca").GetString() ?? "";
                newSerial = d.GetProperty("serial_number").GetString() ?? "(unknown)";
            }
            catch (Exception ex)
            {
                rotated.Add(new CertRotatedNode(b.Hostname, oldSerial, "(unchanged)", $"could not parse vault issue response: {ex.Message}"));
                continue;
            }

            // bundle.pem layout the split script expects: Cert, Key, CA (it sorts
            // by PEM header, so order is robust either way).
            var bundle = cert.TrimEnd() + "\n" + key.TrimEnd() + "\n" + ca.TrimEnd() + "\n";
            var writeCmd =
                $"echo {B64(bundle)} | base64 -d | sudo tee /etc/nexus-kafka/tls/bundle.pem >/dev/null; " +
                "sudo chown root:kafka /etc/nexus-kafka/tls/bundle.pem; sudo chmod 0640 /etc/nexus-kafka/tls/bundle.pem; " +
                "sudo /usr/local/sbin/kafka-tls-split.sh >/dev/null 2>&1; " +
                "sudo systemctl reset-failed kafka.service 2>/dev/null; sudo systemctl restart kafka.service; echo WROTE";
            var write = await _ssh.ExecuteAsync(target, writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (write.IsFail || write.Value!.ExitCode != 0 || !write.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(b.Hostname, oldSerial, "(unchanged)", write.IsFail ? write.Error : $"writing new cert / restart failed: {Tail(write.Value!.Stderr, 200)}"));
                continue;
            }

            // Rolling: wait for this broker to rejoin before rotating the next
            // (KRaft tolerates exactly 1 down at a time).
            var other = brokers.FirstOrDefault(x => x.NodeId != b.NodeId);
            if (other is not null) await WaitRejoinAsync(other, b.NodeId, cancellationToken).ConfigureAwait(false);

            rotated.Add(new CertRotatedNode(b.Hostname, oldSerial, newSerial, null));
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === AclAsync ===========================================================
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<AclSnapshot>(brokersRes.Error!);
        var live = await FirstLiveAsync(brokersRes.Value!, cancellationToken).ConfigureAwait(false);
        if (live is null) return Result.Fail<AclSnapshot>($"no live {_clusterId} broker for ACL ops");

        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var exec = await _ssh.ExecuteAsync(Ssh(live), Admin(live, "kafka-acls.sh", "--list") + " 2>&1", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail) return Result.Fail<AclSnapshot>($"ssh failed: {exec.Error}");
            if (exec.Value!.Stdout.Contains("SecurityDisabledException", StringComparison.Ordinal))
                return Result.Fail<AclSnapshot>("no authorizer is configured on the broker -- enable it with role-overlay-kafka-acl-authorizer.tf (var.enable_kafka_acl_authorizer) in nexus-infra-kafka.");
            if (exec.Value.ExitCode != 0)
                return Result.Fail<AclSnapshot>($"kafka-acls --list exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout, 300)}");
            var users = ParseAcls(exec.Value.Stdout);
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
                users = users.Where(u => u.Name.Contains(operation.User!, StringComparison.OrdinalIgnoreCase)).ToList();
            return Result.Ok(new AclSnapshot(_clusterId, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user (a principal, e.g. User:CN=my-app or just my-app)");
            var principal = NormalizePrincipal(operation.User!);
            // Operations: caller-supplied Permissions, else a sensible default.
            var ops = (operation.Permissions is { Count: > 0 } p ? p : ["Read", "Write", "Describe"])
                .Select(o => $"--operation {o}").ToList();
            var action = verb == "grant" ? "--add" : "--remove --force";
            // Resource: topic '*' (cluster-wide topic access) -- the demonstrable,
            // reversible grant. A specific topic can be targeted later via the
            // Permissions convention if needed.
            var cmd = Admin(live, "kafka-acls.sh", $"{action} --allow-principal {principal} {string.Join(" ", ops)} --topic '*'") + " 2>&1";
            var exec = await _ssh.ExecuteAsync(Ssh(live), cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail) return Result.Fail<AclSnapshot>($"ssh failed: {exec.Error}");
            if (exec.Value!.Stdout.Contains("SecurityDisabledException", StringComparison.Ordinal))
                return Result.Fail<AclSnapshot>("no authorizer is configured on the broker -- enable it with role-overlay-kafka-acl-authorizer.tf first.");
            if (exec.Value.ExitCode != 0)
                return Result.Fail<AclSnapshot>($"kafka-acls {action} exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout, 300)}");

            // Return the post-change ACL list scoped to the principal.
            var after = await _ssh.ExecuteAsync(Ssh(live), Admin(live, "kafka-acls.sh", "--list") + " 2>&1", SshTimeout, cancellationToken).ConfigureAwait(false);
            var users = after.IsOk ? ParseAcls(after.Value!.Stdout).Where(u => u.Name.Contains(NormalizeCn(operation.User!), StringComparison.OrdinalIgnoreCase)).ToList() : [];
            return Result.Ok(new AclSnapshot(_clusterId, verb, users, DateTimeOffset.UtcNow));
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    // === ApplyChaosAsync (process-kill kafka.service + rejoin) ==============
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        var known = new[] { "network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill" };
        if (!known.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", known)}");

        var statusRes = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (statusRes.IsFail) return Result.Fail<ChaosOutcome>(statusRes.Error!);
        var brokersRes = await ResolveBrokersAsync(cancellationToken).ConfigureAwait(false);
        if (brokersRes.IsFail) return Result.Fail<ChaosOutcome>(brokersRes.Error!);
        var brokers = brokersRes.Value!;

        // Victim: explicit target, else a FOLLOWER (never the quorum leader by default).
        Broker? victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? brokers.FirstOrDefault(b => string.Equals(b.Hostname, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : brokers.FirstOrDefault(b => b.Hostname != statusRes.Value!.Leader) ?? brokers.FirstOrDefault();
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target broker found");

        var target = Ssh(victim);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperUnit = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? "kafka.service" : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var inject = await _ssh.ExecuteAsync(target,
            $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperUnit}'", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (inject.IsFail || inject.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Hostname} failed: {(inject.IsFail ? inject.Error : Tail(inject.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(60);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
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

    // === CanResizeVm ========================================================
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false;
        var member = _lastStatus.Members.FirstOrDefault(m => string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        return member.Role != "controller-leader";
    }

    // === Helpers ============================================================

    private async Task<Broker?> FirstLiveAsync(List<Broker> brokers, CancellationToken cancellationToken)
    {
        foreach (var b in brokers)
        {
            var exec = await _ssh.ExecuteAsync(Ssh(b), "systemctl is-active kafka.service", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsOk && exec.Value!.Stdout.Trim() == "active") return b;
        }
        return null;
    }

    /// <summary>Poll a live broker until <paramref name="nodeId"/> is a caught-up voter (lag 0).</summary>
    private async Task<bool> WaitRejoinAsync(Broker probe, int nodeId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + RejoinDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var exec = await _ssh.ExecuteAsync(Ssh(probe), Admin(probe, "kafka-metadata-quorum.sh", "describe --replication") + " 2>/dev/null", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail || exec.Value!.ExitCode != 0) continue;
            var q = ParseReplication(exec.Value.Stdout);
            var r = q.FirstOrDefault(x => x.NodeId == nodeId);
            if (r is not null && r.Lag == 0) return true;
        }
        return false;
    }

    /// <summary>Install (idempotent) the embedded nexus-chaos.sh helper on a node.</summary>
    private async Task<Result<bool>> PushChaosHelperAsync(SshTarget target, CancellationToken cancellationToken)
    {
        var asm = typeof(KafkaClusterAdapter).Assembly;
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

    // === Parsers ============================================================

    internal sealed record ReplicaRow(int NodeId, long Leo, long Lag, string Status);
    internal sealed record QuorumView(int LeaderId, IReadOnlyList<int> Voters, IReadOnlyList<ReplicaRow> Replicas, int UnderReplicated, int Offline);

    /// <summary>Parse the combined status/replication/under-replicated probe output.</summary>
    internal static QuorumView ParseQuorum(string stdout)
    {
        var sections = SplitSections(stdout);
        var leaderId = ExtractInt(sections.GetValueOrDefault("QSTATUS", ""), "LeaderId:") ?? -1;
        var voters = ParseIntList(ExtractField(sections.GetValueOrDefault("QSTATUS", ""), "CurrentVoters:"));
        var repl = ParseReplication(sections.GetValueOrDefault("QREPL", ""));
        var under = ParseCount(sections.GetValueOrDefault("UNDERREP", ""));
        var offline = ParseCount(sections.GetValueOrDefault("OFFLINE", ""));
        return new QuorumView(leaderId, voters, repl, under, offline);
    }

    /// <summary>Parse <c>describe --replication</c> rows: NodeId LEO Lag ... Status.</summary>
    private static List<ReplicaRow> ParseReplication(string text)
    {
        var rows = new List<ReplicaRow>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("NodeId", StringComparison.OrdinalIgnoreCase)) continue;
            var p = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3 || !int.TryParse(p[0], out var id)) continue;
            if (!long.TryParse(p[1], out var leo)) leo = 0;
            if (!long.TryParse(p[2], out var lag)) lag = 0;
            var statusTok = p[^1];
            rows.Add(new ReplicaRow(id, leo, lag, statusTok));
        }
        return rows;
    }

    /// <summary>Parse <c>kafka-topics --describe</c> into one TopologyShard per topic.</summary>
    internal static List<TopologyShard> ParseTopics(string stdout, IReadOnlyDictionary<int, string> nodeIdToHost)
    {
        var shards = new List<TopologyShard>();
        string? curTopic = null; int partCount = 0, rf = 0; var replicaIds = new HashSet<int>(); string? p0Leader = null;
        void Flush()
        {
            if (curTopic is null) return;
            var replicas = replicaIds.OrderBy(x => x).Select(x => nodeIdToHost.TryGetValue(x, out var h) ? h : $"node-{x}").ToList();
            shards.Add(new TopologyShard(curTopic, p0Leader ?? "(n/a)", replicas, $"{partCount}p RF{rf}"));
        }
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("Topic:", StringComparison.Ordinal) && line.Contains("PartitionCount:", StringComparison.Ordinal))
            {
                Flush();
                replicaIds = new HashSet<int>(); p0Leader = null; partCount = 0; rf = 0;
                curTopic = ExtractToken(line, "Topic:");
                partCount = (int)(ExtractInt(line, "PartitionCount:") ?? 0);
                rf = (int)(ExtractInt(line, "ReplicationFactor:") ?? 0);
            }
            else if (line.StartsWith("Topic:", StringComparison.Ordinal) && line.Contains("Partition:", StringComparison.Ordinal))
            {
                var part = ExtractInt(line, "Partition:");
                var leader = ExtractInt(line, "Leader:");
                var reps = ParseIntList(ExtractToken(line, "Replicas:"));
                foreach (var r in reps) replicaIds.Add(r);
                if (part == 0 && leader is int lid)
                    p0Leader = nodeIdToHost.TryGetValue(lid, out var h) ? h : $"node-{lid}";
            }
        }
        Flush();
        return shards;
    }

    /// <summary>Parse <c>kafka-acls --list</c> output into per-principal AclUser rows.</summary>
    internal static IReadOnlyList<AclUser> ParseAcls(string stdout)
    {
        // Lines look like: (principal=User:CN=x, host=*, operation=READ, permissionType=ALLOW)
        var byPrincipal = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = raw.IndexOf("principal=", StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = raw[(idx + "principal=".Length)..];
            var principal = rest.Split(',')[0].Trim();
            var op = ExtractKv(raw, "operation=");
            var perm = ExtractKv(raw, "permissionType=");
            if (!byPrincipal.TryGetValue(principal, out var list)) { list = []; byPrincipal[principal] = list; }
            if (!string.IsNullOrEmpty(op)) list.Add($"{perm}:{op}".Trim(':'));
        }
        return byPrincipal.Select(kv => new AclUser(kv.Key, kv.Value, true)).ToList();
    }

    // === small text utilities ==============================================

    private static Dictionary<string, string> SplitSections(string stdout)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? cur = null; var sb = new StringBuilder();
        void Commit() { if (cur is not null) map[cur] = sb.ToString(); sb.Clear(); }
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("===", StringComparison.Ordinal) && t.EndsWith("===", StringComparison.Ordinal))
            {
                Commit();
                cur = t.Trim('=').Trim();
                if (cur == "END") { cur = null; }
                continue;
            }
            if (cur is not null) sb.Append(line).Append('\n');
        }
        Commit();
        return map;
    }

    private static int? ExtractInt(string text, string label)
    {
        var v = ExtractField(text, label);
        if (v is null) return null;
        var tok = v.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(tok, out var n) ? n : null;
    }

    /// <summary>Value following a "Label:" on the same line (whitespace-delimited remainder).</summary>
    private static string? ExtractField(string text, string label)
    {
        foreach (var line in text.Split('\n'))
        {
            var idx = line.IndexOf(label, StringComparison.Ordinal);
            if (idx >= 0) return line[(idx + label.Length)..].Trim();
        }
        return null;
    }

    /// <summary>The single token after a "Label:" appearing inline (for tab/space-columned lines).</summary>
    private static string ExtractToken(string line, string label)
    {
        var idx = line.IndexOf(label, StringComparison.Ordinal);
        if (idx < 0) return "";
        var rest = line[(idx + label.Length)..].TrimStart();
        var tok = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return tok;
    }

    private static string ExtractKv(string line, string key)
    {
        var idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return "";
        var rest = line[(idx + key.Length)..];
        var tok = rest.Split([',', ' ', '\t', ')'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return tok;
    }

    private static List<int> ParseIntList(string? s)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        foreach (var tok in s.Trim('[', ']', ' ').Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(tok.Trim(), out var n)) list.Add(n);
        return list;
    }

    private static int ParseCount(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var tok = s.Trim().Split('\n').LastOrDefault()?.Trim();
        return int.TryParse(tok, out var n) ? n : 0;
    }

    /// <summary>Accept "User:CN=x", "CN=x", or bare "x" -> a full "User:CN=x" principal string.</summary>
    internal static string NormalizePrincipal(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("User:", StringComparison.OrdinalIgnoreCase)) return s;
        if (s.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return $"User:{s}";
        return $"User:CN={s}";
    }

    private static string NormalizeCn(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("User:", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        if (s.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) s = s[3..];
        return s;
    }

    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s[^n..]);
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
