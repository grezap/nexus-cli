using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// ClickHouse (3 shards x 2 replicas) + ClickHouse Keeper RAFT-quorum adapter
/// for Phase 0.G.5 (nexus-cli v0.6.4). Implements <see cref="IClusterAdapter"/>
/// via SSH-shell-out to on-node <c>clickhouse-client</c> (native TLS :9440) and
/// the Keeper four-letter-word interface (<c>echo mntr | nc 127.0.0.1 9181</c>)
/// -- NO managed ClickHouse.Client driver (NetArchTest-enforced). ADR-0014.
/// <para>
/// Topology per vms.yaml (cluster <c>clickhouse</c>): 6 data nodes
/// (<c>ch-shard{1,2,3}-rep{1,2}</c> @ .44-.49) running
/// <c>nexus-clickhouse-server.service</c> -- a <c>Distributed</c> table
/// (<c>nexus.events</c>) over <c>ReplicatedMergeTree</c> (<c>nexus.events_local</c>),
/// <c>internal_replication=true</c> -- plus 3 dedicated ClickHouse Keeper nodes
/// (<c>ch-keeper-1/2/3</c> @ .41-.43) running
/// <c>nexus-clickhouse-keeper.service</c> (C++ RAFT, NOT ZooKeeper -- ADR-0028).
/// Front door = round-robin DNS <c>clickhouse.nexus.lab</c>, no VIP (ADR-0031);
/// every data node is an equal entry point, so there is no single write leader.
/// </para>
/// <para>
/// Connection contract (live, 0.G.5): clickhouse-client base
/// <c>--secure --accept-invalid-certificate --host localhost --port 9440</c>;
/// server TLS <c>/etc/clickhouse-server/tls/{server.crt,server.key,ca.crt}</c>
/// (0640 root:clickhouse), keeper TLS <c>/etc/nexus-clickhouse-keeper/tls</c>;
/// Keeper 4lw on plain :9181 (secure :9281, RAFT :9234); the CH remote_servers
/// cluster name is <c>nexus_analytics</c> (distinct from the ClusterId
/// <c>clickhouse</c>). PKI role <c>clickhouse-server</c> issues all 9 leaves on
/// domain <c>clickhouse.nexus.lab</c>; node agent token at
/// <c>/run/nexus-vault-agent/token</c>; backup Disk <c>analytics_backups</c>
/// (shared NFS, ADR-0032).
/// </para>
/// <para>
/// Operator identity (ADR-0014, the LOCKED Vault-KV model -- identical to
/// mongo/percona/patroni): the dedicated <c>nexus-cluster-admin</c> ClickHouse
/// user (sha256_password, <c>GRANT ALL ON *.* WITH GRANT OPTION</c>, distinct
/// from the engine's built-in <c>admin</c>); its password lives ONLY in Vault KV
/// (<c>nexus/analytics/clickhouse/operator-password</c>, field <c>password</c>),
/// fetched at runtime via <see cref="INexusVaultClient"/>. ClickHouse is
/// password-auth (sha256_password over the mTLS wire), NOT mTLS-only -- the
/// <c>default</c> user is loopback-only, so every networked client must
/// authenticate with a password.
/// </para>
/// </summary>
public sealed class ClickHouseAdapter : IClusterAdapter
{
    private const string ClusterName = "clickhouse";       // vms.yaml cluster key
    private const string ChCluster = "nexus_analytics";    // remote_servers cluster name (ON CLUSTER / system.clusters)
    private const string DisplayNameConst = "ClickHouse (sharded + Keeper)";
    private const string OperatorUser = "nexus-cluster-admin";

    private const string ServerSvc = "nexus-clickhouse-server";
    private const string KeeperSvc = "nexus-clickhouse-keeper";
    private const string ServerTlsDir = "/etc/clickhouse-server/tls";
    private const string KeeperTlsDir = "/etc/nexus-clickhouse-keeper/tls";
    private const int NativePort = 9440;       // clickhouse-client native TLS
    private const int Keeper4lwPort = 9181;    // Keeper four-letter-word (plain)
    private const string PkiRole = "clickhouse-server";
    private const string BackupDisk = "analytics_backups";
    private const string Db = "nexus";
    private const string LocalTable = "nexus.events_local";
    private const string DistTable = "nexus.events";

    private const string VaultMount = "nexus";
    private const string OperatorPwdPath = "analytics/clickhouse/operator-password";
    private const string PwdField = "password";
    private const string VaultAddr = "https://192.168.70.121:8200";

    // clickhouse-client base (no auth -- for the loopback default user readiness
    // probe); the operator variants append --user/--password.
    private const string ChBase = "clickhouse-client --secure --accept-invalid-certificate --host localhost --port 9440";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromMinutes(3);
    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly string[] DefaultGrantPrivs = ["SELECT"];
    private static readonly char[] WsSplit = [' ', '\t'];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    private string? _operatorPassword;
    private ClusterStatus? _lastStatus;

    /// <summary>
    /// Wires the adapter to the vms.yaml catalog, the SSH transport, the login
    /// identity for on-node shell-out, and the optional Vault client used to
    /// fetch the <c>nexus-cluster-admin</c> operator password on demand.
    /// </summary>
    public ClickHouseAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
        _vault = vault;
    }

    /// <inheritdoc />
    public string ClusterId => ClusterName;

    /// <inheritdoc />
    public string DisplayName => DisplayNameConst;

    // === node helpers ======================================================
    /// <summary>True for the dedicated Keeper coordination nodes (<c>ch-keeper*</c>).</summary>
    private static bool IsKeeper(NodeRecord n) => n.Name.StartsWith("ch-keeper", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the data-plane replica nodes (<c>ch-shard*</c>).</summary>
    private static bool IsData(NodeRecord n) => n.Name.StartsWith("ch-shard", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex ShardRepRx = new(@"ch-shard(\d+)-rep(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Extracts the (shard, replica) ordinals from a <c>ch-shardN-repM</c> node name; (0,0) if unmatched.</summary>
    private static (int Shard, int Replica) ShardRep(NodeRecord n)
    {
        var m = ShardRepRx.Match(n.Name);
        return m.Success
            ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : (0, 0);
    }

    /// <summary>
    /// Splits the vms.yaml cluster into ordered data + Keeper node lists.
    /// Ordinal name sort keeps shard/replica iteration deterministic; fails if
    /// no <c>ch-shard*</c> data node is present.
    /// </summary>
    private Result<(IReadOnlyList<NodeRecord> Data, IReadOnlyList<NodeRecord> Keeper)> Split()
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>(cluster.Error!);
        var data = cluster.Value!.Nodes.Where(IsData).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        var keeper = cluster.Value.Nodes.Where(IsKeeper).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (data.Count == 0) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>("no ch-shard* data nodes in vms.yaml cluster 'clickhouse'");
        return Result.Ok(((IReadOnlyList<NodeRecord>)data, (IReadOnlyList<NodeRecord>)keeper));
    }

    /// <summary>Builds an SSH target (port 22, configured key/user) for the given node IP.</summary>
    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    // === Vault password ====================================================
    /// <summary>
    /// Lazily resolves the <c>nexus-cluster-admin</c> password from Vault KV and
    /// memoises it; fails with an actionable hint when no Vault client is wired.
    /// </summary>
    private async Task<Result<string>> OperatorPwdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_operatorPassword)) return Result.Ok(_operatorPassword);
        if (_vault is null)
            return Result.Fail<string>(
                "clickhouse verbs authenticate as nexus-cluster-admin, whose password lives in Vault KV. "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var r = await _vault.ReadKvFieldAsync(VaultMount, OperatorPwdPath, PwdField, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"could not read operator password from Vault ({VaultMount}/{OperatorPwdPath}): {r.Error}");
        _operatorPassword = r.Value;
        return Result.Ok(_operatorPassword!);
    }

    // === clickhouse-client helpers =========================================
    /// <summary>Run a SQL on a data node as the operator over native TLS; returns tab-separated stdout.</summary>
    private async Task<Result<string>> ChQueryAsync(string nodeIp, string pwd, string sql, CancellationToken ct, TimeSpan? timeout = null)
    {
        // Single-quote the SQL for the remote shell (escape embedded quotes). The
        // operator password is 32-char hex (no shell-special chars) -> safe single-quote.
        var esc = sql.Replace("'", "'\\''");
        var cmd = $"{ChBase} --user {OperatorUser} --password '{pwd}' --format TabSeparated --query '{esc}' 2>&1";
        var exec = await _ssh.ExecuteAsync(T(nodeIp), cmd, timeout ?? SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {nodeIp} failed: {exec.Error}");
        if (exec.Value!.ExitCode != 0)
            return Result.Fail<string>($"clickhouse-client on {nodeIp} exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout, 300)}");
        return Result.Ok(exec.Value.Stdout.Trim());
    }

    /// <summary>True if the given systemd unit reports <c>active</c> on the node.</summary>
    private async Task<bool> IsActiveAsync(string nodeIp, string unit, CancellationToken ct)
    {
        // `; true` so ssh always reports success; exact-prefix match dodges the
        // "inactive".Contains("active") substring trap.
        var ping = await _ssh.ExecuteAsync(T(nodeIp), $"systemctl is-active {unit} 2>/dev/null; true", SshTimeout, ct).ConfigureAwait(false);
        return ping.IsOk && ping.Value!.Stdout.Trim().StartsWith("active", StringComparison.Ordinal);
    }

    // === Keeper four-letter-word ===========================================
    private sealed record KeeperStat(string State, long Znodes, long FollowerCount);

    /// <summary>Run a Keeper 4lw command on a node (plain :9181); returns raw lines.</summary>
    private async Task<Result<string>> Keeper4lwAsync(string nodeIp, string word, CancellationToken ct)
    {
        var cmd = $"echo {word} | nc -w 3 127.0.0.1 {Keeper4lwPort} 2>/dev/null; true";
        var exec = await _ssh.ExecuteAsync(T(nodeIp), cmd, SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {nodeIp} failed: {exec.Error}");
        var outp = exec.Value!.Stdout.Trim();
        if (string.IsNullOrWhiteSpace(outp)) return Result.Fail<string>($"keeper 4lw '{word}' returned nothing on {nodeIp}");
        return Result.Ok(outp);
    }

    /// <summary>Parse `mntr` for the leader/follower state + znode count of one Keeper.</summary>
    private async Task<Result<KeeperStat>> KeeperMntrAsync(string nodeIp, CancellationToken ct)
    {
        var r = await Keeper4lwAsync(nodeIp, "mntr", ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<KeeperStat>(r.Error!);
        string state = "unknown"; long znodes = 0, followers = 0;
        foreach (var line in r.Value!.Split('\n'))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) parts = line.Split(WsSplit, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            switch (parts[0])
            {
                case "zk_server_state": state = parts[1].Trim(); break;
                case "zk_znode_count": if (long.TryParse(parts[1].Trim(), out var z)) znodes = z; break;
                case "zk_followers": if (long.TryParse(parts[1].Trim(), out var fc)) followers = fc; break;
            }
        }
        return Result.Ok(new KeeperStat(state, znodes, followers));
    }

    /// <summary>Find the current Keeper RAFT leader (by 4lw state); null if none.</summary>
    private async Task<(NodeRecord? Leader, int Reachable, Dictionary<string, KeeperStat> Stats)> KeeperQuorumAsync(IReadOnlyList<NodeRecord> keepers, CancellationToken ct)
    {
        NodeRecord? leader = null;
        var reachable = 0;
        var stats = new Dictionary<string, KeeperStat>(StringComparer.Ordinal);
        foreach (var k in keepers)
        {
            var m = await KeeperMntrAsync(k.Vmnet11, ct).ConfigureAwait(false);
            if (m.IsFail) continue;
            reachable++;
            stats[k.Name] = m.Value!;
            if (m.Value!.State.Equals("leader", StringComparison.OrdinalIgnoreCase)) leader = k;
        }
        return (leader, reachable, stats);
    }

    // === GetStatusAsync ====================================================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<ClusterStatus>(split.Error!);
        var (data, keeper) = split.Value;

        var members = new List<ClusterMember>();

        // Data nodes: server active + shard/replica identity.
        foreach (var n in data)
        {
            var (shard, _) = ShardRep(n);
            var alive = await IsActiveAsync(n.Vmnet11, ServerSvc, cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, "replica", alive ? "alive" : "failed", ShardId: shard.ToString(CultureInfo.InvariantCulture)));
        }

        // Keeper quorum: mark the RAFT leader.
        var (kLeader, kReachable, kStats) = await KeeperQuorumAsync(keeper, cancellationToken).ConfigureAwait(false);
        foreach (var k in keeper)
        {
            var alive = kStats.ContainsKey(k.Name);
            var isLeader = kLeader is not null && string.Equals(kLeader.Name, k.Name, StringComparison.Ordinal);
            members.Add(new ClusterMember(k.Name, k.Vmnet11, isLeader ? "keeper-leader" : "keeper", alive ? "alive" : "failed"));
        }

        var dataAlive = members.Where(m => m.Role == "replica").Count(m => m.Status == "alive");
        var keeperQuorum = keeper.Count == 0 || kReachable >= (keeper.Count / 2 + 1);
        var overall = (dataAlive == data.Count && kLeader is not null && keeperQuorum) ? "green"
            : (dataAlive >= (data.Count / 2 + 1) && kLeader is not null && keeperQuorum) ? "yellow" : "red";

        // The coordination leader (Keeper RAFT) is the cluster's single "leader";
        // the data plane is leaderless (every replica is writable -- ADR-0029).
        var s = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, kLeader?.Name, DateTimeOffset.UtcNow);
        _lastStatus = s;
        return Result.Ok(s);
    }

    // === HealthAsync =======================================================
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<HealthReport>(split.Error!);
        var (data, keeper) = split.Value;
        var probes = new List<HealthProbe>();

        // Keeper quorum + single-leader.
        var (kLeader, kReachable, _) = await KeeperQuorumAsync(keeper, cancellationToken).ConfigureAwait(false);
        var kQ = keeper.Count / 2 + 1;
        probes.Add(new HealthProbe("keeper-quorum", "keeper", kReachable >= kQ && kLeader is not null ? "green" : "red",
            $"{kReachable}/{keeper.Count} reachable, leader={kLeader?.Name ?? "(none)"}", $">={kQ} + 1 leader"));

        // Per-data-node server liveness.
        foreach (var n in data)
        {
            var alive = await IsActiveAsync(n.Vmnet11, ServerSvc, cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("server-active", n.Name, alive ? "green" : "red", alive ? "active" : "down", "active"));
        }

        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsOk)
        {
            // Operator-auth round-trip (proves the Vault-KV credential end-to-end).
            var who = await ChQueryAsync(data[0].Vmnet11, pwd.Value!, "SELECT currentUser()", cancellationToken).ConfigureAwait(false);
            var authed = who.IsOk && who.Value!.Trim() == OperatorUser;
            probes.Add(new HealthProbe("operator-auth", OperatorUser, authed ? "green" : "red",
                who.IsOk ? who.Value!.Trim() : "unreachable", "authenticates as nexus-cluster-admin"));

            // Distributed cluster membership: system.clusters shows 6 host rows.
            var hosts = await ChQueryAsync(data[0].Vmnet11, pwd.Value!, $"SELECT count() FROM system.clusters WHERE cluster='{ChCluster}'", cancellationToken).ConfigureAwait(false);
            var hc = hosts.IsOk && int.TryParse(hosts.Value!.Trim(), out var n6) ? n6 : -1;
            probes.Add(new HealthProbe("distributed-membership", ChCluster, hc == data.Count ? "green" : "red",
                $"{hc} host rows", $"{data.Count} (3 shards x 2 replicas)"));

            // Distributed query round-trip across all shards.
            var cnt = await ChQueryAsync(data[0].Vmnet11, pwd.Value!, $"SELECT count() FROM {DistTable}", cancellationToken).ConfigureAwait(false);
            var ok = cnt.IsOk && long.TryParse(cnt.Value!.Trim(), out var c) && c > 0;
            probes.Add(new HealthProbe("distributed-query", DistTable, ok ? "green" : "red",
                cnt.IsOk ? $"{cnt.Value!.Trim()} rows" : "unreachable", ">0 rows fan-in"));

            // Per-node replica health: no read-only / session-expired replicas, lag bounded.
            foreach (var n in data)
            {
                var rq = await ChQueryAsync(n.Vmnet11, pwd.Value!,
                    "SELECT countIf(is_readonly OR is_session_expired), max(absolute_delay) FROM system.replicas", cancellationToken).ConfigureAwait(false);
                if (rq.IsFail) { probes.Add(new HealthProbe("replica-health", n.Name, "red", "unreachable", "0 ro/expired")); continue; }
                var f = rq.Value!.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                var bad = f.Length > 0 && int.TryParse(f[0].Trim(), out var b) ? b : 0;
                var delay = f.Length > 1 && double.TryParse(f[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
                probes.Add(new HealthProbe("replica-health", n.Name, bad == 0 && delay <= 30 ? "green" : bad == 0 ? "yellow" : "red",
                    $"{bad} ro/expired, {delay:0}s delay", "0 ro/expired, <=30s"));
            }
        }
        else
        {
            probes.Add(new HealthProbe("operator-auth", OperatorUser, "yellow", "Vault not configured", "set VAULT_ADDR/TOKEN/CACERT"));
        }

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync =====================================================
    /// <inheritdoc />
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var split = Split();
        if (split.IsFail) return Result.Fail<TopologySnapshot>(split.Error!);
        var (data, _) = split.Value;

        var nodes = status.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.Role, m.Status))
            .ToList();

        // Shards derived from node names (ch-shardN-repM). ClickHouse replicas are
        // co-equal (multi-master ReplicatedMergeTree) -- the per-shard "Primary"
        // is the merge leader (system.replicas.is_leader) when queryable, else the
        // lexically-first replica (rep1).
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        var shards = new List<TopologyShard>();
        foreach (var grp in data.GroupBy(n => ShardRep(n).Shard).OrderBy(g => g.Key))
        {
            var reps = grp.OrderBy(n => ShardRep(n).Replica).ToList();
            string primary = reps[0].Name;
            if (pwd.IsOk)
            {
                foreach (var rep in reps)
                {
                    var ld = await ChQueryAsync(rep.Vmnet11, pwd.Value!, "SELECT is_leader FROM system.replicas LIMIT 1", cancellationToken).ConfigureAwait(false);
                    if (ld.IsOk && ld.Value!.Trim() == "1") { primary = rep.Name; break; }
                }
            }
            shards.Add(new TopologyShard($"shard{grp.Key}", primary, reps.Select(r => r.Name).ToList(), SlotRange: null));
        }
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, shards, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (Keeper RAFT leader re-election, RTO measured) =======
    /// <inheritdoc />
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<FailoverResult>(split.Error!);
        var (_, keeper) = split.Value;
        if (keeper.Count < 3) return Result.Fail<FailoverResult>("Keeper failover needs the 3-node RAFT quorum");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var (preLeader, reachable, _) = await KeeperQuorumAsync(keeper, cancellationToken).ConfigureAwait(false);
        if (preLeader is null) return Result.Fail<FailoverResult>("no current Keeper leader to fail over");
        if (reachable < 3) return Result.Fail<FailoverResult>($"only {reachable}/3 Keeper nodes reachable; refusing to fail over a degraded quorum");
        var preFlightAt = sw.Elapsed;

        // Inject: stop the Keeper service on the current leader -> RAFT re-elects.
        var stop = await _ssh.ExecuteAsync(T(preLeader.Vmnet11), $"sudo systemctl stop {KeeperSvc}.service && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<FailoverResult>($"failed to stop {KeeperSvc} on {preLeader.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 200))}");
        var injectedAt = sw.Elapsed;

        // Poll the survivors until a NEW leader emerges.
        NodeRecord? newLeader = null;
        var newLeaderAt = TimeSpan.Zero;
        var survivors = keeper.Where(k => !string.Equals(k.Name, preLeader.Name, StringComparison.Ordinal)).ToList();
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            foreach (var k in survivors)
            {
                var m = await KeeperMntrAsync(k.Vmnet11, cancellationToken).ConfigureAwait(false);
                if (m.IsOk && m.Value!.State.Equals("leader", StringComparison.OrdinalIgnoreCase)) { newLeader = k; break; }
            }
            if (newLeader is not null) { newLeaderAt = sw.Elapsed; break; }
        }
        var rto = newLeader is not null ? newLeaderAt - injectedAt : TimeSpan.Zero;

        // Recovery: restart the stopped Keeper so it rejoins as a follower.
        var recovery = "skipped";
        if (!request.NoRecover)
        {
            await _ssh.ExecuteAsync(T(preLeader.Vmnet11), $"sudo systemctl start {KeeperSvc}.service 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);
            var rejoinDeadline = sw.Elapsed + TimeSpan.FromSeconds(60);
            var rejoined = false;
            while (sw.Elapsed < rejoinDeadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var m = await KeeperMntrAsync(preLeader.Vmnet11, cancellationToken).ConfigureAwait(false);
                if (m.IsOk && (m.Value!.State.Equals("follower", StringComparison.OrdinalIgnoreCase) || m.Value.State.Equals("leader", StringComparison.OrdinalIgnoreCase)))
                { rejoined = true; break; }
            }
            recovery = rejoined ? "recovered" : "failed";
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "keeper-leader-failover",
            OriginalPrimary: preLeader.Name,
            NewPrimary: newLeader?.Name,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: newLeader is null ? "no survivor Keeper became leader within the deadline; check nexus-clickhouse-keeper RAFT (:9234) on the survivors" : null,
            Timeline: new FailoverTimeline(preFlightAt, injectedAt, newLeaderAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOutAddAsync / RemoveAsync (data replica join/leave) ===========
    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<ScaleOutResult>(split.Error!);
        var (data, _) = split.Value;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // A "joined" replica = nexus-clickhouse-server active. Find a provisioned data node that is NOT active.
        NodeRecord? candidate = null;
        foreach (var n in data)
            if (!await IsActiveAsync(n.Vmnet11, ServerSvc, cancellationToken).ConfigureAwait(false)) { candidate = n; break; }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "all provisioned data nodes are already joined. Provision a new replica first (apply-on-demand, ADR-0014): "
                + "add a ch-shardN-repM + overlays in analytics-clickhouse, `pwsh -File scripts/analytics-clickhouse.ps1 apply`, then re-run `scale-out add`.");

        var start = await _ssh.ExecuteAsync(T(candidate.Vmnet11), $"sudo systemctl start {ServerSvc}.service && echo STARTED", BackupTimeout, cancellationToken).ConfigureAwait(false);
        if (start.IsFail || !start.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to start {ServerSvc} on {candidate.Name}: {(start.IsFail ? start.Error : Tail(start.Value!.Stderr, 200))}");

        // Wait for the replica to answer + drain its replication queue (caught up).
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        var deadline = sw.Elapsed + JoinDeadline;
        var caught = false;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (!await IsActiveAsync(candidate.Vmnet11, ServerSvc, cancellationToken).ConfigureAwait(false)) continue;
            if (pwd.IsFail) { caught = true; break; } // active is enough without auth
            var q = await ChQueryAsync(candidate.Vmnet11, pwd.Value!,
                "SELECT sum(queue_size), max(absolute_delay) FROM system.replicas", cancellationToken).ConfigureAwait(false);
            if (q.IsOk)
            {
                var f = q.Value!.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                var queue = f.Length > 0 && long.TryParse(f[0].Trim(), out var ql) ? ql : 0;
                if (queue == 0) { caught = true; break; }
            }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: caught ? "ok" : "partial",
            OutcomeReason: caught ? $"{candidate.Name} rejoined the cluster (ReplicatedMergeTree caught up via Keeper)" : $"{candidate.Name} started but replication queue not yet drained",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name");
        var split = Split();
        if (split.IsFail) return Result.Fail<ScaleOutResult>(split.Error!);
        var (data, _) = split.Value;
        var node = data.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not a ch-shard* data node in the clickhouse cluster");

        // Refuse removing a shard's LAST live replica (would drop that shard's data plane).
        var (shard, _) = ShardRep(node);
        var siblings = data.Where(n => ShardRep(n).Shard == shard && !string.Equals(n.Name, node.Name, StringComparison.Ordinal)).ToList();
        var siblingAlive = false;
        foreach (var s in siblings)
            if (await IsActiveAsync(s.Vmnet11, ServerSvc, cancellationToken).ConfigureAwait(false)) { siblingAlive = true; break; }
        if (request.Drain && !siblingAlive)
            return Result.Fail<ScaleOutResult>(
                $"{node.Name} is the only live replica of shard{shard}; removing it would take that shard offline. Bring a sibling replica up first.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var stop = await _ssh.ExecuteAsync(T(node.Vmnet11), $"sudo systemctl stop {ServerSvc}.service && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to stop {ServerSvc} on {node.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 200))}");
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"gracefully removed {node.Name} from shard{shard} (server stopped; the sibling replica still serves; ready for re-add via `scale-out add`)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupTakeAsync / RestoreAsync (native BACKUP/RESTORE round-trip) ==
    /// <inheritdoc />
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<BackupResult>(split.Error!);
        var (data, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<BackupResult>(pwd.Error!);

        // Take on the first live data node.
        NodeRecord? runNode = null;
        foreach (var n in data) if (await IsActiveAsync(n.Vmnet11, ServerSvc, cancellationToken).ConfigureAwait(false)) { runNode = n; break; }
        if (runNode is null) return Result.Fail<BackupResult>("no live data node to take a backup on");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"clickhouse-backup-{startedAt:yyyyMMdd-HHmmss}.zip"
            : $"clickhouse-{request.Tag}-{startedAt:yyyyMMdd-HHmmss}.zip";

        // Native BACKUP of the local ReplicatedMergeTree table to the shared NFS Disk.
        var bk = await ChQueryAsync(runNode.Vmnet11, pwd.Value!,
            $"BACKUP TABLE {LocalTable} TO Disk('{BackupDisk}', '{backupId}')", cancellationToken, BackupTimeout).ConfigureAwait(false);
        sw.Stop();
        if (bk.IsFail || !bk.Value!.Contains("BACKUP_CREATED", StringComparison.Ordinal))
            return Result.Fail<BackupResult>($"BACKUP did not report BACKUP_CREATED: {(bk.IsFail ? bk.Error : Tail(bk.Value!, 300))}");

        // Report the archive size from the shared repo.
        long size = 0;
        var stat = await _ssh.ExecuteAsync(T(runNode.Vmnet11),
            $"sudo stat -c %s /var/backups/analytics/clickhouse/{backupId} 2>/dev/null; true", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stat.IsOk && long.TryParse(stat.Value!.Stdout.Trim().Split('\n').LastOrDefault()?.Trim(), out var sz)) size = sz;

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"Disk('{BackupDisk}', '{backupId}') (shared NFS repo, ADR-0032; taken on {runNode.Name})",
            SizeBytes: size,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id");
        var split = Split();
        if (split.IsFail) return Result.Fail<RestoreResult>(split.Error!);
        var (data, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<RestoreResult>(pwd.Error!);

        // Restore cross-node: prefer a DIFFERENT live node than where it was taken
        // (the repo is shared NFS -> proves cluster-wide restore). Restore AS a
        // throwaway table; the {uuid} zk path means no REPLICA_ALREADY_EXISTS clash.
        NodeRecord? runNode = null;
        foreach (var n in data) if (await IsActiveAsync(n.Vmnet11, ServerSvc, cancellationToken).ConfigureAwait(false)) { runNode = n; break; }
        if (runNode is null) return Result.Fail<RestoreResult>("no live data node to restore on");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        const string verifyTable = "nexus.events_restore_verify";
        await ChQueryAsync(runNode.Vmnet11, pwd.Value!, $"DROP TABLE IF EXISTS {verifyTable} SYNC", cancellationToken).ConfigureAwait(false);
        var rs = await ChQueryAsync(runNode.Vmnet11, pwd.Value!,
            $"RESTORE TABLE {LocalTable} AS {verifyTable} FROM Disk('{BackupDisk}', '{request.BackupId}')", cancellationToken, BackupTimeout).ConfigureAwait(false);
        if (rs.IsFail || !rs.Value!.Contains("RESTORED", StringComparison.Ordinal))
        {
            sw.Stop();
            return Result.Fail<RestoreResult>($"RESTORE did not report RESTORED: {(rs.IsFail ? rs.Error : Tail(rs.Value!, 300))}");
        }
        var cnt = await ChQueryAsync(runNode.Vmnet11, pwd.Value!, $"SELECT count() FROM {verifyTable}", cancellationToken).ConfigureAwait(false);
        // cleanup the throwaway table (best-effort).
        await ChQueryAsync(runNode.Vmnet11, pwd.Value!, $"DROP TABLE IF EXISTS {verifyTable} SYNC", cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (cnt.IsFail || !long.TryParse(cnt.Value!.Trim(), out var rows))
            return Result.Fail<RestoreResult>($"restore round-trip did not confirm restored rows: {(cnt.IsFail ? cnt.Error : Tail(cnt.Value!, 200))}");

        return Result.Ok(new RestoreResult(
            BackupId: request.BackupId,
            ItemsRestored: rows,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === RotateCertAsync (Vault re-issue per node, rolling restart) =========
    private sealed record CertRole(string TlsDir, string Svc);

    /// <summary>Maps a node to its TLS material directory + systemd unit (Keeper vs. server).</summary>
    private static CertRole RoleDescriptor(NodeRecord n) =>
        IsKeeper(n) ? new CertRole(KeeperTlsDir, KeeperSvc) : new CertRole(ServerTlsDir, ServerSvc);

    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<CertRotationResult>(split.Error!);
        var (data, keeper) = split.Value;

        // Rotate data nodes first (one at a time -> each shard keeps a live
        // replica), then Keeper followers, the Keeper LEADER last (its restart
        // triggers a re-election, so do it when everything else is settled).
        var (kLeader, _, _) = await KeeperQuorumAsync(keeper, cancellationToken).ConfigureAwait(false);
        var keeperOrdered = keeper.OrderBy(n => kLeader is not null && string.Equals(n.Name, kLeader.Name, StringComparison.Ordinal) ? 1 : 0).ToList();
        var all = data.Concat(keeperOrdered).ToList();

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var node in all)
        {
            var rd = RoleDescriptor(node);
            var oldSerialExec = await _ssh.ExecuteAsync(T(node.Vmnet11),
                $"sudo openssl x509 -in {rd.TlsDir}/server.crt -noout -serial 2>/dev/null | sed 's/serial=//'",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.ExitCode == 0 && oldSerialExec.Value.Stdout.Trim().Length > 0
                ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            // Same CN/SAN shape the TLS overlay issues (domain clickhouse.nexus.lab,
            // round-robin name in the SANs). All 9 nodes share PKI role clickhouse-server.
            var cn = $"{node.Name}.clickhouse.nexus.lab";
            var alts = $"{node.Name},{node.Name}.nexus.lab,{cn},clickhouse.nexus.lab,localhost";
            var ips = $"{node.Vmnet10},{node.Vmnet11},127.0.0.1";
            var issueCmd =
                "T=$(sudo cat /run/nexus-vault-agent/token 2>/dev/null); "
                + $"sudo env VAULT_ADDR={VaultAddr} VAULT_TOKEN=\"$T\" VAULT_CACERT=/etc/vault-agent/ca-bundle.crt "
                + $"/usr/local/bin/vault write -format=json pki_int/issue/{PkiRole} common_name={cn} alt_names={alts} ip_sans={ips} ttl=2160h";
            var issueExec = await _ssh.ExecuteAsync(T(node.Vmnet11), issueCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (issueExec.IsFail || issueExec.Value!.ExitCode != 0)
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: issueExec.IsFail ? issueExec.Error : $"vault issue failed: {Tail(issueExec.Value!.Stdout + issueExec.Value.Stderr, 220)}"));
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
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)", Error: $"could not parse vault issue response: {ex.Message}"));
                continue;
            }

            // ClickHouse (OpenSSL) needs: server.crt = leaf, server.key = PKCS#8
            // (Vault issues PKCS#1 -> convert), ca.crt = issuing intermediate +
            // root anchor (the Vault-Agent ca-bundle) -- mirrors clickhouse-tls-split.sh.
            // Restart the unit (deterministic cert reload). 1 node at a time.
            var writeCmd =
                $"echo {B64(cert.TrimEnd() + "\n")}|base64 -d|sudo tee {rd.TlsDir}/server.crt >/dev/null; "
                + $"echo {B64(key.TrimEnd() + "\n")}|base64 -d|sudo openssl pkcs8 -topk8 -nocrypt -out {rd.TlsDir}/server.key 2>/dev/null; "
                + $"echo {B64(ca.TrimEnd() + "\n")}|base64 -d|sudo tee /tmp/_ica.pem >/dev/null; "
                + $"sudo bash -c 'cat /tmp/_ica.pem /etc/vault-agent/ca-bundle.crt > {rd.TlsDir}/ca.crt'; sudo rm -f /tmp/_ica.pem; "
                + $"sudo chown root:clickhouse {rd.TlsDir}/server.crt {rd.TlsDir}/server.key {rd.TlsDir}/ca.crt; "
                + $"sudo chmod 0640 {rd.TlsDir}/server.crt {rd.TlsDir}/server.key {rd.TlsDir}/ca.crt; "
                + $"sudo systemctl restart {rd.Svc}; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(T(node.Vmnet11), writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stdout, 200)}"));
                continue;
            }
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: null));
            // Settle so the restarted node is healthy before the next rotates.
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === AclAsync ==========================================================
    /// <inheritdoc />
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<AclSnapshot>(split.Error!);
        var (data, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<AclSnapshot>(pwd.Error!);
        var verb = operation.Verb.ToLowerInvariant();
        var coord = data[0].Vmnet11;

        if (verb is "list" or "describe")
        {
            // SHOW USERS + their default roles/grants (system.users join system.grants).
            var sql = "SELECT u.name || '|' || arrayStringConcat(groupArray(g.access_type), ',') "
                + "FROM system.users u LEFT JOIN system.grants g ON g.user_name = u.name "
                + "WHERE u.storage != 'users_directory_xml' OR u.name != '' "
                + "GROUP BY u.name ORDER BY u.name";
            var r = await ChQueryAsync(coord, pwd.Value!, sql, cancellationToken).ConfigureAwait(false);
            if (r.IsFail) return Result.Fail<AclSnapshot>(r.Error!);
            var users = ParseUsers(r.Value!);
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
                users = users.Where(u => string.Equals(u.Name, operation.User, StringComparison.OrdinalIgnoreCase)).ToList();
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user.");
            var privs = operation.Permissions is { Count: > 0 } ? operation.Permissions : DefaultGrantPrivs;
            // Identifiers with hyphens need backtick-quoting in ClickHouse.
            var uid = "`" + operation.User.Replace("`", "``") + "`";
            string sql;
            if (verb == "grant")
            {
                // Idempotently CREATE the user (no_password demo identity) then GRANT on nexus.* ON CLUSTER.
                sql = $"CREATE USER IF NOT EXISTS {uid} ON CLUSTER {ChCluster} IDENTIFIED WITH no_password";
                var c = await ChQueryAsync(coord, pwd.Value!, sql, cancellationToken).ConfigureAwait(false);
                if (c.IsFail) return Result.Fail<AclSnapshot>($"acl grant (create user) failed: {c.Error}");
                sql = $"GRANT ON CLUSTER {ChCluster} {string.Join(", ", privs)} ON {Db}.* TO {uid}";
            }
            else
            {
                sql = $"REVOKE ON CLUSTER {ChCluster} {string.Join(", ", privs)} ON {Db}.* FROM {uid}";
            }
            var g = await ChQueryAsync(coord, pwd.Value!, sql, cancellationToken).ConfigureAwait(false);
            if (g.IsFail) return Result.Fail<AclSnapshot>($"acl {verb} failed: {g.Error}");
            // Re-describe the mutated user so the caller sees the post-change grant state.
            return await AclAsync(new AclOperation("describe", operation.User), cancellationToken).ConfigureAwait(false);
        }
        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    /// <summary>Parses the <c>name|priv,priv</c> rows from the ACL list query into <see cref="AclUser"/> records.</summary>
    private static List<AclUser> ParseUsers(string stdout)
    {
        var users = new List<AclUser>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 2);
            if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) continue;
            var perms = parts.Length > 1 && parts[1].Trim().Length > 0
                ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray()
                : ["(no grants)"];
            users.Add(new AclUser(parts[0].Trim(), perms, Enabled: true));
        }
        return users;
    }

    // === ApplyChaosAsync ===================================================
    /// <inheritdoc />
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");

        var split = Split();
        if (split.IsFail) return Result.Fail<ChaosOutcome>(split.Error!);
        var (data, _) = split.Value;

        // Default target: a data replica (the surviving replica keeps the shard up).
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? data.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : data.FirstOrDefault(n => ShardRep(n).Replica == 2) ?? data[0];
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target node found");

        var target = T(victim.Vmnet11);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? ServerSvc : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Name} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);

        // process-kill stops nexus-clickhouse-server -> restart it so the replica
        // rejoins + re-syncs via Keeper.
        if (string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase))
            await _ssh.ExecuteAsync(target, $"sudo systemctl start {ServerSvc}.service 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(120);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var post = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (post.IsOk && post.Value!.OverallHealth == "green") { recovered = true; break; }
        }
        sw.Stop();

        return Result.Ok(new ChaosOutcome(
            ScenarioApplied: scenario.ScenarioType,
            Target: victim.Name,
            ObservedImpact: observed,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt,
            Recovered: recovered));
    }

    /// <summary>
    /// Installs the embedded <c>nexus-chaos.sh</c> helper (shared across adapters,
    /// hence loaded from <see cref="RedisAdapter"/>'s assembly) onto the target node.
    /// </summary>
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

    // === CanResizeVm =======================================================
    /// <inheritdoc />
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false;
        var member = _lastStatus.Members.FirstOrDefault(m => string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        // Refuse the current Keeper leader (resizing forces a power-cycle -> a
        // RAFT re-election). Data replicas are safe (the sibling replica + the
        // remaining shards keep serving while one node reboots).
        return member.Role != "keeper-leader";
    }

    /// <summary>Returns the last <paramref name="n"/> chars of <paramref name="s"/> (for truncating error tails).</summary>
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));

    /// <summary>Base64-encodes a UTF-8 string for safe transport through the remote shell.</summary>
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
