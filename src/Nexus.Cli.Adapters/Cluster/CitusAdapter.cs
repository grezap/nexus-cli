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
/// Citus-sharded PostgreSQL cluster with full Patroni HA, adapter for Phase 0.P
/// (nexus-cli v0.7.3). Implements <see cref="IClusterAdapter"/> via SSH-shell-out
/// to on-node <c>patronictl</c> / <c>psql</c> / <c>etcdctl</c> (no managed Npgsql
/// driver -- NetArchTest-enforced). ADR-0021.
/// <para>
/// Citus = <b>Patroni HA per node-group + Citus distribution</b>. The closest
/// precedent is <see cref="PatroniAdapter"/> (0.G.4 PostgreSQL Patroni HA); this
/// adapter runs that HA model THREE times (one coordinator group + two worker
/// groups) and adds the Citus distributed layer on top (topology Shards populated
/// like <see cref="VitessAdapter"/>).
/// </para>
/// <para>
/// Topology per vms.yaml (cluster <c>citus</c>, ADR-0042): 9 VMs + 3 VRRP VIPs,
/// tier 08-citus --
/// <list type="bullet">
///   <item>3 etcd DCS (<c>citus-etcd-1/2/3</c> @ .202-.204, <c>nexus-etcd</c>,
///   client-cert-auth mTLS, no RBAC password) -- the Patroni DCS.</item>
///   <item>coordinator group scope <c>citus-coord</c> (<c>citus-coord-1/2</c> @
///   .205/.206, VIP <c>.211</c> <c>coord.citus.nexus.lab</c> = pg_dist_node
///   groupid 0) -- holds <c>pg_dist_*</c> + reference tables.</item>
///   <item>worker group scope <c>citus-worker1</c> (@ .207/.208, VIP <c>.212</c>
///   = groupid 1) + <c>citus-worker2</c> (@ .209/.210, VIP <c>.213</c> = groupid
///   2).</item>
/// </list>
/// Each PG group = 1 Patroni leader + 1 streaming replica over the shared etcd
/// DCS (<c>/citus/&lt;scope&gt;</c>); a keepalived VRRP VIP follows the Patroni
/// leader. Leaders DRIFT -- always read from <c>patronictl</c>. PG 17 + Citus
/// 14.1; the distributed DB is <c>citus</c> (table <c>events</c> hash-distributed
/// on <c>tenant_id</c> into 32 shards spread across the two worker groups +
/// <c>event_tags</c> colocated + <c>tenants</c> reference).
/// </para>
/// <para>
/// Operator identity (ADR-0011, the LOCKED Vault-KV model -- Greg-approved
/// 2026-06-18): the dedicated <c>nexus-cluster-admin</c> role (LOGIN CREATEROLE
/// CREATEDB + pg_read_all_data/pg_write_all_data, NOT superuser); its password
/// lives ONLY in Vault KV (<c>nexus/citus/operator-password</c>, field
/// <c>content</c>), fetched at runtime via <see cref="INexusVaultClient"/>. The
/// role auto-propagates to the workers (citus.enable_create_role_propagation) and
/// a <c>~postgres/.pgpass</c> entry on the coordinator nodes lets the coordinator
/// dial workers AS the operator, so distributed queries run end-to-end as the
/// operator. SQL connects to the coordinator VIP over TLS+scram and presents the
/// node's own leaf as the required client cert (pg_hba clientcert=verify-ca).
/// Patroni-plane verbs (switchover) go via on-node <c>patronictl</c> + sudo.
/// </para>
/// </summary>
public sealed class CitusAdapter : IClusterAdapter
{
    private const string ClusterName = "citus";
    private const string DisplayNameConst = "Citus Sharded PostgreSQL (Patroni HA)";
    private const string OperatorUser = "nexus-cluster-admin";
    private const string Db = "citus";
    private const string DistTable = "events";
    private const string RefTable = "tenants";
    private const string ColoTable = "event_tags";

    private const string TlsDir = "/etc/nexus-citus/tls";
    private const string EtcdTlsDir = "/etc/nexus-etcd/tls";
    private const string CaFile = TlsDir + "/ca.pem";
    private const string Patronictl = "sudo /usr/local/sbin/nexus-patronictl";
    private const string Etcdctl = "sudo /usr/local/sbin/nexus-etcdctl";
    private const string SplitScript = "/usr/local/sbin/nexus-citus-tls-split.sh";
    private const string Sock = "/var/run/nexus-citus";
    private const string BackupRoot = "/var/backups/nexus-citus";
    private const string PgSvc = "nexus-patroni";
    private const string EtcdSvc = "nexus-etcd";
    private const string PkiRole = "citus-server";

    private const string AgentToken = "/run/nexus-vault-agent/token";
    private const string CaBundle = "/etc/vault-agent/ca-bundle.crt";
    private const string VaultAddr = "https://192.168.70.121:8200";
    private const string VaultMount = "nexus";
    private const string OperatorPwdPath = "citus/operator-password";
    private const string PwdField = "content";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromMinutes(3);
    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly string[] DefaultGrantPrivs = ["CONNECT"];

    // The 3 PG node-groups (scope, kind, Citus groupid, VRRP VIP, VIP DNS). The
    // catalog doesn't surface virtual_ips, so the VIPs are infra canon (ADR-0042).
    internal sealed record PgGroup(string Scope, string Kind, int GroupId, string Vip, string Dns);

    private static readonly PgGroup[] Groups =
    [
        new("citus-coord", "coord", 0, "192.168.70.211", "coord.citus.nexus.lab"),
        new("citus-worker1", "worker", 1, "192.168.70.212", "worker1.citus.nexus.lab"),
        new("citus-worker2", "worker", 2, "192.168.70.213", "worker2.citus.nexus.lab"),
    ];

    private static PgGroup CoordGroup => Groups[0];
    private static IEnumerable<PgGroup> WorkerGroups => Groups.Where(g => g.Kind == "worker");

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    private string? _operatorPassword;
    private ClusterStatus? _lastStatus;

    /// <summary>
    /// Constructs the adapter over an <see cref="ISshClient"/> transport and the
    /// <see cref="IVmsCatalog"/> node inventory. <paramref name="vault"/> is optional
    /// -- null degrades operator-authenticated verbs (they surface a Vault-setup hint
    /// rather than throwing).
    /// </summary>
    public CitusAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
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

    // === node classification (deterministic, from the vms.yaml name) ==========
    internal static bool IsEtcd(string name) => name.StartsWith("citus-etcd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Map a PG node name to its group (null for etcd / unknown).</summary>
    internal static PgGroup? GroupOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("citus-coord", StringComparison.Ordinal)) return CoordGroup;
        if (n.StartsWith("citus-worker1", StringComparison.Ordinal)) return Groups[1];
        if (n.StartsWith("citus-worker2", StringComparison.Ordinal)) return Groups[2];
        return null;
    }

    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    private Result<IReadOnlyList<NodeRecord>> Nodes()
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<IReadOnlyList<NodeRecord>>(cluster.Error!);
        if (cluster.Value!.Nodes.Count == 0) return Result.Fail<IReadOnlyList<NodeRecord>>($"cluster '{ClusterName}' has no nodes in vms.yaml");
        return Result.Ok(cluster.Value.Nodes);
    }

    private static List<NodeRecord> EtcdNodes(IReadOnlyList<NodeRecord> all) =>
        all.Where(n => IsEtcd(n.Name)).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

    private static List<NodeRecord> PgNodes(IReadOnlyList<NodeRecord> all) =>
        all.Where(n => GroupOf(n.Name) is not null).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

    private static List<NodeRecord> GroupNodes(IReadOnlyList<NodeRecord> all, PgGroup g) =>
        all.Where(n => GroupOf(n.Name)?.Scope == g.Scope).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

    // === Vault operator password ===========================================
    private async Task<Result<string>> OperatorPwdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_operatorPassword)) return Result.Ok(_operatorPassword);
        if (_vault is null)
            return Result.Fail<string>(
                "citus verbs authenticate as nexus-cluster-admin, whose password lives in Vault KV "
                + $"({VaultMount}/{OperatorPwdPath}). Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var r = await _vault.ReadKvFieldAsync(VaultMount, OperatorPwdPath, PwdField, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"could not read operator password from Vault ({VaultMount}/{OperatorPwdPath}): {r.Error}");
        var pwd = (r.Value ?? string.Empty).Trim();
        if (pwd.Length == 0) return Result.Fail<string>("operator password from Vault is empty");
        _operatorPassword = pwd;
        return Result.Ok(_operatorPassword!);
    }

    // === patronictl list parsing (per scope) ===============================
    internal sealed record PgMember(string Scope, string Member, string Host, string Role, string State, double? LagMb);

    internal static List<PgMember> ParsePatroniList(string stdout)
    {
        var members = new List<PgMember>();
        var i = stdout.IndexOf('[');
        var j = stdout.LastIndexOf(']');
        if (i < 0 || j <= i) return members;
        using var doc = JsonDocument.Parse(stdout.Substring(i, j - i + 1));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return members;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string GetS(string k) => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            double? lag = el.TryGetProperty("Lag in MB", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetDouble() : null;
            members.Add(new PgMember(GetS("Cluster"), GetS("Member"), GetS("Host"), GetS("Role"), GetS("State"), lag));
        }
        return members;
    }

    internal static string RoleOf(PgMember m) =>
        m.Role.Equals("Leader", StringComparison.OrdinalIgnoreCase) ? "primary"
        : m.Role.Contains("Standby", StringComparison.OrdinalIgnoreCase) ? "replica"
        : m.Role.Equals("Replica", StringComparison.OrdinalIgnoreCase) ? "replica"
        : m.Role.ToLowerInvariant();

    internal static string StatusOf(PgMember m) =>
        m.State is "running" or "streaming" ? "alive"
        : m.State is "starting" or "stopping" or "creating replica" or "in archive recovery" ? "syncing"
        : "failed";

    /// <summary>Run patronictl on the first reachable node of a group; parse its scope's members.</summary>
    private async Task<Result<List<PgMember>>> GroupMembersAsync(IReadOnlyList<NodeRecord> all, PgGroup g, CancellationToken ct)
    {
        var nodes = GroupNodes(all, g);
        if (nodes.Count == 0) return Result.Fail<List<PgMember>>($"no nodes for group {g.Scope} in vms.yaml");
        foreach (var n in nodes)
        {
            var r = await _ssh.ExecuteAsync(T(n.Vmnet11), $"{Patronictl} list --format json 2>&1", SshTimeout, ct).ConfigureAwait(false);
            if (r.IsFail || r.Value!.ExitCode != 0) continue;
            var members = ParsePatroniList(r.Value.Stdout);
            if (members.Count > 0) return Result.Ok(members);
        }
        return Result.Fail<List<PgMember>>($"could not read patronictl list for group {g.Scope} from any node");
    }

    private async Task<bool> IsActiveAsync(string ip, string unit, CancellationToken ct)
    {
        var ping = await _ssh.ExecuteAsync(T(ip), $"systemctl is-active {unit} 2>/dev/null; true", SshTimeout, ct).ConfigureAwait(false);
        return ping.IsOk && ping.Value!.Stdout.Trim().StartsWith("active", StringComparison.Ordinal);
    }

    // === operator psql via the coordinator VIP =============================
    /// <summary>
    /// Run SQL as the operator over TLS+scram via the coordinator VIP, from the
    /// first reachable coordinator node (it has the mysql/psql client + the TLS
    /// leaf that doubles as the required client cert). Returns trimmed stdout.
    /// </summary>
    private async Task<Result<string>> OperatorPsqlAsync(IReadOnlyList<NodeRecord> all, string pwd, string sql, CancellationToken ct, string db = Db, string? host = null)
    {
        var coordNodes = GroupNodes(all, CoordGroup);
        if (coordNodes.Count == 0) return Result.Fail<string>("no coordinator nodes in vms.yaml");
        var h = host ?? CoordGroup.Vip;
        var conn = $"host={h} port=5432 dbname={db} user={OperatorUser} sslmode=verify-ca sslrootcert={CaFile} sslcert={TlsDir}/server-cert.pem sslkey={TlsDir}/server-key.pem";
        var cmd = $"sudo env PGPASSWORD='{pwd}' psql \"{conn}\" -tAF $'\\t' -v ON_ERROR_STOP=1 -c '{sql.Replace("'", "'\\''")}' 2>&1";
        var lastErr = "no coordinator node reachable";
        foreach (var n in coordNodes)
        {
            var exec = await _ssh.ExecuteAsync(T(n.Vmnet11), cmd, SshTimeout, ct).ConfigureAwait(false);
            if (exec.IsFail) { lastErr = exec.Error!; continue; }   // node unreachable -> try the other coord node
            if (exec.Value!.ExitCode != 0)
                return Result.Fail<string>($"psql via {h} on {n.Name} exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout, 300)}");
            return Result.Ok(exec.Value.Stdout.Trim());
        }
        return Result.Fail<string>($"could not reach any coordinator node to run SQL: {lastErr}");
    }

    // === etcd quorum (union healthy endpoints across nodes -- 127.0.1.1) =====
    private static readonly Regex EtcdHealthyLine = new(@"https://([A-Za-z0-9.\-]+):\d+ is healthy", RegexOptions.Compiled);

    /// <summary>
    /// etcd cluster health. Running <c>etcdctl endpoint health</c> ON an etcd node
    /// always reports that node's OWN endpoint unhealthy (its hostname maps to
    /// 127.0.1.1 in Debian's /etc/hosts but etcd listens on 127.0.0.1 + real IPs),
    /// so a single node only ever sees the OTHER members healthy. UNION the
    /// distinct healthy endpoint names across all etcd nodes for the true count.
    /// </summary>
    private async Task<(int Healthy, int Total)> EtcdQuorumAsync(List<NodeRecord> etcd, CancellationToken ct)
    {
        var healthy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in etcd)
        {
            var r = await _ssh.ExecuteAsync(T(n.Vmnet11), $"{Etcdctl} endpoint health 2>&1; true", SshTimeout, ct).ConfigureAwait(false);
            if (r.IsFail) continue;
            foreach (Match m in EtcdHealthyLine.Matches(r.Value!.Stdout))
                healthy.Add(m.Groups[1].Value);
            if (healthy.Count >= etcd.Count) break;     // all members seen healthy; stop early
        }
        return (healthy.Count, etcd.Count);
    }

    // === GetStatusAsync ====================================================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ClusterStatus>(nodesR.Error!);
        var all = nodesR.Value!;

        var members = new List<ClusterMember>();
        string? leader = null;
        var groupsHealthy = 0;

        foreach (var g in Groups)
        {
            var gm = await GroupMembersAsync(all, g, cancellationToken).ConfigureAwait(false);
            var gnodes = GroupNodes(all, g);
            if (gm.IsFail)
            {
                // group unreachable -> mark its provisioned nodes failed.
                foreach (var n in gnodes)
                    members.Add(new ClusterMember(n.Name, n.Vmnet11, "replica", "failed", ShardId: g.Scope));
                continue;
            }
            var leaders = 0;
            foreach (var m in gm.Value!)
            {
                var node = gnodes.FirstOrDefault(n => n.Vmnet11 == m.Host || string.Equals(n.Name, m.Member, StringComparison.OrdinalIgnoreCase));
                var role = RoleOf(m);
                if (role == "primary") { leaders++; if (g.Kind == "coord") leader = m.Member; }
                members.Add(new ClusterMember(m.Member, node?.Vmnet11 ?? m.Host, role, StatusOf(m),
                    ShardId: g.Scope, ReplicationLagSeconds: m.LagMb));
            }
            var alive = gm.Value!.Count(m => StatusOf(m) == "alive");
            if (leaders == 1 && alive == gnodes.Count) groupsHealthy++;
        }

        var etcd = EtcdNodes(all);
        foreach (var n in etcd)
        {
            var ok = await IsActiveAsync(n.Vmnet11, EtcdSvc, cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, "dcs", ok ? "alive" : "failed"));
        }
        var (etcdHealthy, etcdTotal) = await EtcdQuorumAsync(etcd, cancellationToken).ConfigureAwait(false);
        var etcdQuorum = etcdTotal == 0 || etcdHealthy >= (etcdTotal / 2 + 1);

        // Registered Citus workers in pg_dist_node (the distributed-layer membership).
        var workersRegistered = await ActiveWorkerCountAsync(all, cancellationToken).ConfigureAwait(false);

        var overall =
            (groupsHealthy == Groups.Length && etcdQuorum && workersRegistered == WorkerGroups.Count() && leader is not null) ? "green"
            : (leader is not null && etcdQuorum && groupsHealthy >= 1) ? "yellow" : "red";

        var s = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, leader, DateTimeOffset.UtcNow);
        _lastStatus = s;
        return Result.Ok(s);
    }

    /// <summary>Active worker count from pg_dist_node (groupid&lt;&gt;0, isactive, primary). -1 if unavailable.</summary>
    private async Task<int> ActiveWorkerCountAsync(IReadOnlyList<NodeRecord> all, CancellationToken ct)
    {
        var pwd = await OperatorPwdAsync(ct).ConfigureAwait(false);
        if (pwd.IsFail) return -1;
        var r = await OperatorPsqlAsync(all, pwd.Value!,
            "SELECT count(*) FROM pg_dist_node WHERE isactive AND noderole='primary' AND groupid <> 0", ct).ConfigureAwait(false);
        return r.IsOk && int.TryParse(r.Value!.Trim(), out var c) ? c : -1;
    }

    // === HealthAsync =======================================================
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<HealthReport>(nodesR.Error!);
        var all = nodesR.Value!;
        var probes = new List<HealthProbe>();

        // etcd quorum (union across nodes).
        var etcd = EtcdNodes(all);
        var (eh, et) = await EtcdQuorumAsync(etcd, cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("etcd-quorum", "dcs", eh >= (et / 2 + 1) ? "green" : "red",
            $"{eh}/{et} healthy", $">= {et / 2 + 1} (quorum)"));

        // per group: exactly 1 leader + replica streaming + lag.
        foreach (var g in Groups)
        {
            var gm = await GroupMembersAsync(all, g, cancellationToken).ConfigureAwait(false);
            if (gm.IsFail)
            {
                probes.Add(new HealthProbe("patroni-group", g.Scope, "red", "unreachable", "1 leader + 1 streaming replica"));
                continue;
            }
            var leaders = gm.Value!.Count(m => RoleOf(m) == "primary");
            probes.Add(new HealthProbe("single-leader", g.Scope, leaders == 1 ? "green" : "red",
                leaders.ToString(CultureInfo.InvariantCulture), "exactly 1"));
            foreach (var m in gm.Value!)
            {
                var st = StatusOf(m);
                probes.Add(new HealthProbe("patroni-state", $"{g.Scope}/{m.Member}", st == "alive" ? "green" : st == "syncing" ? "yellow" : "red",
                    $"{m.Role}/{m.State}", "Leader/running or Replica/streaming"));
                if (RoleOf(m) == "replica")
                {
                    var lag = m.LagMb ?? -1;
                    probes.Add(new HealthProbe("replication-lag", $"{g.Scope}/{m.Member}", lag >= 0 && lag <= 10 ? "green" : lag < 0 ? "red" : "yellow",
                        lag < 0 ? "(unknown)" : $"{lag} MB", "<= 10 MB"));
                }
            }
        }

        // Citus distributed layer: registered active workers + operator auth + sharding proof.
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsOk)
        {
            var who = await OperatorPsqlAsync(all, pwd.Value!, "SELECT current_user", cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("operator-auth", $"{OperatorUser}@coord-vip", who.IsOk && who.Value!.Trim() == OperatorUser ? "green" : "red",
                who.IsOk ? "scram+mTLS round-trip via VIP" : "unreachable", "coordinator VIP :5432 mTLS round-trip"));

            var wr = await OperatorPsqlAsync(all, pwd.Value!,
                "SELECT count(*) FROM pg_dist_node WHERE isactive AND noderole='primary' AND groupid <> 0", cancellationToken).ConfigureAwait(false);
            var nWorkers = wr.IsOk && int.TryParse(wr.Value!.Trim(), out var w) ? w : -1;
            probes.Add(new HealthProbe("citus-workers", "pg_dist_node", nWorkers == WorkerGroups.Count() ? "green" : "red",
                nWorkers < 0 ? "(unknown)" : $"{nWorkers} active workers", $"{WorkerGroups.Count()} (worker1 + worker2)"));

            // Sharding proof: events shards spread across BOTH worker groups (coordinator-local citus_shards).
            var sh = await OperatorPsqlAsync(all, pwd.Value!,
                $"SELECT nodename || '=' || count(*) FROM citus_shards WHERE table_name='{DistTable}'::regclass GROUP BY nodename ORDER BY nodename", cancellationToken).ConfigureAwait(false);
            var lines = sh.IsOk ? sh.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
            var spread = lines.Length >= WorkerGroups.Count() && lines.All(l => { var p = l.Split('='); return p.Length == 2 && long.TryParse(p[1], out var c) && c > 0; });
            probes.Add(new HealthProbe("sharding", $"{Db}.{DistTable}", spread ? "green" : "yellow",
                sh.IsOk ? string.Join(" ", lines.Select(l => l.Trim())) : "unreachable", $"{DistTable} shards span both worker groups"));

            // Distributed aggregate (fans out to workers as the operator via .pgpass).
            var cnt = await OperatorPsqlAsync(all, pwd.Value!, $"SELECT count(*) FROM {DistTable}", cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("distributed-query", $"{Db}.{DistTable}", cnt.IsOk && long.TryParse(cnt.Value!.Trim(), out var rc) && rc > 0 ? "green" : "red",
                cnt.IsOk ? $"{cnt.Value!.Trim()} rows" : "failed", "cross-shard aggregate routes through coordinator"));
        }
        else
        {
            probes.Add(new HealthProbe("operator-auth", $"{OperatorUser}@coord-vip", "yellow", "Vault unavailable", pwd.Error));
        }

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync (Shards POPULATED -- worker-group shard placements) ===
    /// <inheritdoc />
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<TopologySnapshot>(nodesR.Error!);
        var all = nodesR.Value!;

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var nodes = status.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.ShardId is null ? m.Role : $"{m.ShardId}/{m.Role}", m.Status, m.ReplicationLagSeconds))
            .ToList();

        // Per-worker-group shard placements of the distributed table `events`
        // (coordinator-local citus_shards). nodename = the worker group's VIP DNS.
        var shardCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsOk)
        {
            var sh = await OperatorPsqlAsync(all, pwd.Value!,
                $"SELECT nodename, count(*) FROM citus_shards WHERE table_name='{DistTable}'::regclass GROUP BY nodename", cancellationToken).ConfigureAwait(false);
            if (sh.IsOk)
                foreach (var line in sh.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = line.Split('\t');
                    if (p.Length == 2 && long.TryParse(p[1].Trim(), out var c)) shardCounts[p[0].Trim()] = c;
                }
        }

        var shards = new List<TopologyShard>();
        foreach (var g in WorkerGroups)
        {
            var gm = await GroupMembersAsync(all, g, cancellationToken).ConfigureAwait(false);
            var primary = "(none)"; var replicas = new List<string>();
            if (gm.IsOk)
            {
                primary = gm.Value!.FirstOrDefault(m => RoleOf(m) == "primary")?.Member ?? "(none)";
                replicas = gm.Value!.Where(m => RoleOf(m) == "replica").Select(m => m.Member).ToList();
            }
            var n = shardCounts.TryGetValue(g.Dns, out var c) ? c : 0;
            shards.Add(new TopologyShard(g.Scope, primary, replicas,
                SlotRange: $"{n} of {Db}.{DistTable} shards (hash on tenant_id) @ {g.Dns}"));
        }
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, shards, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (patronictl switchover on a chosen group) ============
    private static Result<PgGroup> ResolveGroup(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return Result.Ok(CoordGroup);
        var t = target!.ToLowerInvariant();
        // accept scope ("citus-worker1"), short name ("worker1"/"coord"), or a node name.
        var g = Groups.FirstOrDefault(x => x.Scope == t)
                ?? Groups.FirstOrDefault(x => x.Scope == "citus-" + t)
                ?? GroupOf(target!);
        return g is null
            ? Result.Fail<PgGroup>($"--target '{target}' is not a citus group (coord/worker1/worker2 or citus-coord/...) or PG node")
            : Result.Ok(g);
    }

    /// <inheritdoc />
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<FailoverResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var gr = ResolveGroup(request.TargetNode);
        if (gr.IsFail) return Result.Fail<FailoverResult>(gr.Error!);
        var g = gr.Value!;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var gm = await GroupMembersAsync(all, g, cancellationToken).ConfigureAwait(false);
        if (gm.IsFail) return Result.Fail<FailoverResult>(gm.Error!);
        var preFlightAt = sw.Elapsed;

        var leaderM = gm.Value!.FirstOrDefault(m => RoleOf(m) == "primary");
        if (leaderM is null) return Result.Fail<FailoverResult>($"no current Patroni leader in group {g.Scope}");
        var candM = gm.Value!.FirstOrDefault(m => RoleOf(m) == "replica" && m.State == "streaming");
        if (candM is null) return Result.Fail<FailoverResult>($"no streaming replica in group {g.Scope} to switch over to");

        // Run patronictl from the candidate node (it stays up and becomes the new leader).
        var candNode = GroupNodes(all, g).First(n => string.Equals(n.Name, candM.Member, StringComparison.OrdinalIgnoreCase) || n.Vmnet11 == candM.Host);
        var swArgs = $"switchover {g.Scope} --leader {leaderM.Member} --candidate {candM.Member} --force";
        var swRes = await _ssh.ExecuteAsync(T(candNode.Vmnet11), $"{Patronictl} {swArgs} 2>&1", SshTimeout, cancellationToken).ConfigureAwait(false);
        var injectedAt = sw.Elapsed;
        if (swRes.IsFail) return Result.Fail<FailoverResult>($"ssh switchover on {candNode.Name} failed: {swRes.Error}");
        // patronictl exits 0 even on a refused switchover -> validate the banner.
        if (!swRes.Value!.Stdout.Contains("Successfully switched over", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<FailoverResult>($"patronictl switchover did not succeed: {Tail(swRes.Value.Stdout, 320)}");

        // Poll until the candidate is the new leader.
        string? newLeader = null;
        var newLeaderAt = TimeSpan.Zero;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            var cur = await GroupMembersAsync(all, g, cancellationToken).ConfigureAwait(false);
            if (cur.IsOk)
            {
                var nl = cur.Value!.FirstOrDefault(m => RoleOf(m) == "primary");
                if (nl is not null && string.Equals(nl.Member, candM.Member, StringComparison.OrdinalIgnoreCase))
                { newLeader = nl.Member; newLeaderAt = sw.Elapsed; break; }
            }
        }
        var rto = newLeader is not null ? newLeaderAt - injectedAt : TimeSpan.Zero;

        // Recovery: switch back to the original leader unless NoRecover.
        var recovery = "skipped";
        if (!request.NoRecover && newLeader is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var back = await _ssh.ExecuteAsync(T(candNode.Vmnet11),
                $"{Patronictl} switchover {g.Scope} --leader {candM.Member} --candidate {leaderM.Member} --force 2>&1", SshTimeout, cancellationToken).ConfigureAwait(false);
            recovery = back.IsOk && back.Value!.Stdout.Contains("Successfully switched over", StringComparison.OrdinalIgnoreCase) ? "recovered" : "failed";
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: $"citus-patroni-switchover ({g.Scope})",
            OriginalPrimary: $"{g.Scope}/{leaderM.Member}",
            NewPrimary: newLeader is not null ? $"{g.Scope}/{newLeader}" : null,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: newLeader is null
                ? $"patronictl switchover issued but {candM.Member} was not confirmed leader within the deadline; check `{Patronictl} list` + Patroni REST :8008 (a missing patroni.yml `ctl:` block 403s state-changing calls)"
                : (g.Kind == "worker"
                    ? "worker-group failover: the VRRP VIP followed the new Patroni leader, so pg_dist_node needs no rewrite (registered by VIP)."
                    : "coordinator failover: the client VIP followed the new leader; distributed metadata is intact."),
            Timeline: new FailoverTimeline(preFlightAt, injectedAt, newLeaderAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOut (Patroni member add/remove within a group) ================
    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var pg = PgNodes(all);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // A removed member = a PG node whose nexus-patroni is not active. If a
        // --shard/group is given, restrict to that group.
        var candidates = pg;
        if (!string.IsNullOrWhiteSpace(request.ShardId))
        {
            var gr = ResolveGroup(request.ShardId);
            if (gr.IsOk) candidates = GroupNodes(all, gr.Value!);
        }
        NodeRecord? candidate = null;
        foreach (var n in candidates)
            if (!await IsActiveAsync(n.Vmnet11, PgSvc, cancellationToken).ConfigureAwait(false)) { candidate = n; break; }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "all provisioned PG members are already joined. To grow the SHARD topology (a 3rd worker group), provision a new "
                + "Patroni pair (apply-on-demand, ADR-0042): add the VMs + overlays in nexus-infra-citus/terraform/envs/citus, "
                + "`pwsh -File scripts/citus.ps1 apply`, then on the coordinator `SELECT citus_add_node('<vip>',5432)` + "
                + "`SELECT rebalance_table_shards()` to spread shards onto it.");

        var g = GroupOf(candidate.Name)!;
        var start = await _ssh.ExecuteAsync(T(candidate.Vmnet11), $"sudo systemctl start {PgSvc}.service && echo STARTED", BackupTimeout, cancellationToken).ConfigureAwait(false);
        if (start.IsFail || !start.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to start {PgSvc} on {candidate.Name}: {(start.IsFail ? start.Error : Tail(start.Value!.Stderr, 200))}");

        var streaming = false;
        var deadline = sw.Elapsed + JoinDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var gm = await GroupMembersAsync(all, g, cancellationToken).ConfigureAwait(false);
            if (gm.IsOk && gm.Value!.Any(m => string.Equals(m.Member, candidate.Name, StringComparison.OrdinalIgnoreCase) && m.State == "streaming")) { streaming = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: streaming ? "ok" : "partial",
            OutcomeReason: streaming ? $"{candidate.Name} rejoined Patroni group {g.Scope} (streaming)" : $"{candidate.Name} started but not yet streaming (pg_basebackup/rewind may still be running)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var node = all.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not in the citus cluster");
        var g = GroupOf(node.Name);
        if (g is null)
            return Result.Fail<ScaleOutResult>($"{node.Name} is an etcd DCS node, not a PG member; remove etcd by deprovisioning the VM (terraform), not via scale-out.");

        // Refuse removing the current leader (would force a failover).
        var gm = await GroupMembersAsync(all, g, cancellationToken).ConfigureAwait(false);
        if (gm.IsOk && request.Drain)
        {
            var m = gm.Value!.FirstOrDefault(x => string.Equals(x.Member, node.Name, StringComparison.OrdinalIgnoreCase));
            if (m is not null && RoleOf(m) == "primary")
                return Result.Fail<ScaleOutResult>(
                    $"{node.Name} is the current Patroni leader of {g.Scope}; fail it over first "
                    + $"(`nexus failover-test cluster citus --target {g.Scope}`) before removing.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var stop = await _ssh.ExecuteAsync(T(node.Vmnet11), $"sudo systemctl stop {PgSvc}.service && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to stop {PgSvc} on {node.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 200))}");
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"gracefully left {node.Name} from Patroni group {g.Scope} (service stopped; the group keeps quorum on its leader; re-add via `scale-out add` or deprovision)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === Backup (operator COPY round-trip of the distributed dataset) ========
    /// <inheritdoc />
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<BackupResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<BackupResult>(pwd.Error!);

        var coord = GroupNodes(all, CoordGroup).FirstOrDefault();
        if (coord is null) return Result.Fail<BackupResult>("no coordinator node to run the backup from");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"citus-backup-{startedAt:yyyyMMdd-HHmmss}"
            : $"citus-{Sanitize(request.Tag)}-{startedAt:yyyyMMdd-HHmmss}";
        var dir = $"{BackupRoot}/{backupId}";

        // Stream each table via the operator over the coordinator VIP with
        // `COPY (...) TO STDOUT WITH CSV` -- a client-side pull that fans the
        // distributed `events`/`event_tags` rows out to the workers (operator
        // dials them via .pgpass) and merges through the coordinator, plus the
        // coordinator-local reference `tenants`. gzip to a node-local file. A
        // server-side `COPY TO file` would need superuser; STDOUT does not.
        var psql = $"sudo env PGPASSWORD='{pwd.Value}' psql \"host={CoordGroup.Vip} port=5432 dbname={Db} user={OperatorUser} sslmode=verify-ca sslrootcert={CaFile} sslcert={TlsDir}/server-cert.pem sslkey={TlsDir}/server-key.pem\" -v ON_ERROR_STOP=1";
        var script = $"sudo mkdir -p {dir}; sudo chmod 777 {dir}; ";
        foreach (var (tbl, _) in new[] { (RefTable, 0), (DistTable, 0), (ColoTable, 0) })
            script += $"{psql} -c \"\\copy (SELECT * FROM {tbl}) TO STDOUT WITH CSV\" 2>/tmp/cdump.err | gzip > {dir}/{tbl}.csv.gz || {{ echo DUMP_FAIL_{tbl}; cat /tmp/cdump.err; exit 1; }}; ";
        script += $"du -bc {dir}/*.csv.gz | tail -1 | cut -f1";
        var exec = await _ssh.ExecuteAsync(T(coord.Vmnet11), script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<BackupResult>($"backup on {coord.Name} failed: {exec.Error}");
        if (exec.Value!.Stdout.Contains("DUMP_FAIL", StringComparison.Ordinal))
            return Result.Fail<BackupResult>($"backup COPY failed: {Tail(exec.Value.Stdout, 320)}");
        var sizeLine = exec.Value.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (!long.TryParse(sizeLine.Trim(), out var size) || size <= 0)
            return Result.Fail<BackupResult>($"backup produced no archive: {Tail(exec.Value.Stdout, 280)}");

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{dir}/{{{RefTable},{DistTable},{ColoTable}}}.csv.gz node-local on {coord.Name} (operator COPY round-trip; distributed `{DistTable}` pulled through the coordinator)",
            SizeBytes: size,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<RestoreResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<RestoreResult>(pwd.Error!);

        var dir = $"{BackupRoot}/{request.BackupId}";
        // Find the coordinator node holding the dump.
        NodeRecord? coord = null;
        foreach (var n in GroupNodes(all, CoordGroup))
        {
            var probe = await _ssh.ExecuteAsync(T(n.Vmnet11), $"test -s {dir}/{DistTable}.csv.gz && echo FOUND || echo NO", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (probe.IsOk && probe.Value!.Stdout.Contains("FOUND", StringComparison.Ordinal)) { coord = n; break; }
        }
        if (coord is null) return Result.Fail<RestoreResult>($"backup '{request.BackupId}' not found on any coordinator node (looked for {dir}/{DistTable}.csv.gz).");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        const string verifyDb = "citus_restore_verify";
        // Non-destructive: create a throwaway DB the operator OWNS (it has
        // CREATEDB), recreate PLAIN (non-distributed) tables, COPY the rows back
        // in, count events. The operator can CREATE/COPY in a DB it owns.
        var mk = $"sudo env PGPASSWORD='{pwd.Value}' psql \"host={CoordGroup.Vip} port=5432 dbname={Db} user={OperatorUser} sslmode=verify-ca sslrootcert={CaFile} sslcert={TlsDir}/server-cert.pem sslkey={TlsDir}/server-key.pem\" -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS {verifyDb}' -c 'CREATE DATABASE {verifyDb}'";
        var vp = $"sudo env PGPASSWORD='{pwd.Value}' psql \"host={CoordGroup.Vip} port=5432 dbname={verifyDb} user={OperatorUser} sslmode=verify-ca sslrootcert={CaFile} sslcert={TlsDir}/server-cert.pem sslkey={TlsDir}/server-key.pem\" -v ON_ERROR_STOP=1";
        var ddl =
            "CREATE TABLE tenants (tenant_id int PRIMARY KEY, name text); "
            + "CREATE TABLE events (event_id bigint, tenant_id int, payload text, created_at timestamptz, PRIMARY KEY (tenant_id, event_id)); "
            + "CREATE TABLE event_tags (event_id bigint, tenant_id int, tag text, PRIMARY KEY (tenant_id, event_id, tag));";
        var script =
            $"{mk} 2>&1; "
            + $"{vp} -c \"{ddl}\" 2>&1; "
            + $"for t in {RefTable} {DistTable} {ColoTable}; do gunzip -c {dir}/$t.csv.gz | {vp} -c \"\\copy $t FROM STDIN WITH CSV\" 2>&1; done; "
            + $"{vp} -tAc 'SELECT '\\''RESTORED='\\'' || count(*) FROM {DistTable}' 2>&1";
        var exec = await _ssh.ExecuteAsync(T(coord.Vmnet11), script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<RestoreResult>($"restore on {coord.Name} failed: {exec.Error}");
        var m = Regex.Match(exec.Value!.Stdout, @"RESTORED=(\d+)");
        if (!m.Success)
            return Result.Fail<RestoreResult>($"restore round-trip did not confirm restored rows: {Tail(exec.Value.Stdout, 400)}");
        return Result.Ok(new RestoreResult(request.BackupId, long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), sw.Elapsed, startedAt));
    }

    // === RotateCertAsync (per-node Vault PKI; PG reload, etcd restart) =======
    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<CertRotationResult>(nodesR.Error!);
        var all = nodesR.Value!;

        // Order: etcd -> worker replicas -> worker leaders -> coord replica ->
        // coord leader LAST. PG nodes RELOAD (SIGHUP, no failover); etcd RESTARTS.
        var order = new List<NodeRecord>();
        order.AddRange(EtcdNodes(all));
        var pgOrdered = new List<NodeRecord>();
        foreach (var g in WorkerGroups.Concat([CoordGroup]))   // workers first, coordinator last
        {
            var gm = await GroupMembersAsync(all, g, cancellationToken).ConfigureAwait(false);
            var leaderName = gm.IsOk ? gm.Value!.FirstOrDefault(m => RoleOf(m) == "primary")?.Member : null;
            var gnodes = GroupNodes(all, g).OrderBy(n => string.Equals(n.Name, leaderName, StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
            pgOrdered.AddRange(gnodes);
        }
        order.AddRange(pgOrdered);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var node in order)
        {
            var etcd = IsEtcd(node.Name);
            var g = GroupOf(node.Name);
            var dir = etcd ? EtcdTlsDir : TlsDir;
            var group = etcd ? "etcd" : "postgres";

            var oldSerialExec = await _ssh.ExecuteAsync(T(node.Vmnet11),
                $"sudo openssl x509 -in {dir}/server-cert.pem -noout -serial 2>/dev/null | sed 's/serial=//'", SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.Stdout.Trim().Length > 0 ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            var cn = $"{node.Name}.citus.nexus.lab";
            var alts = $"{node.Name},{node.Name}.nexus.lab,{cn},localhost";
            var ips = $"{node.Vmnet10},{node.Vmnet11},127.0.0.1";
            // PG nodes carry their group VIP (DNS + IP) so the cert covers the VIP endpoint.
            if (g is not null) { alts += $",{g.Dns}"; ips += $",{g.Vip}"; }

            var issueCmd =
                $"T=$(sudo cat {AgentToken} 2>/dev/null); "
                + $"sudo env VAULT_ADDR={VaultAddr} VAULT_TOKEN=\"$T\" VAULT_CACERT={CaBundle} "
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

            var bundle = cert.TrimEnd() + "\n" + key.TrimEnd() + "\n" + ca.TrimEnd() + "\n";
            // PG: SIGHUP via `systemctl reload nexus-patroni` (Patroni reloads PG ssl
            // files -> no restart, no failover). etcd: restart (it reads certs at boot).
            var apply = etcd ? $"sudo systemctl restart {EtcdSvc}" : $"sudo systemctl reload {PgSvc}";
            var writeCmd =
                $"echo {B64(bundle)}|base64 -d|sudo tee {dir}/bundle.pem >/dev/null; "
                + $"sudo {SplitScript} {dir} {group} >/dev/null 2>&1; "
                + $"{apply}; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(T(node.Vmnet11), writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stdout + writeExec.Value.Stderr, 220)}"));
                continue;
            }
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: null));
            await Task.Delay(TimeSpan.FromSeconds(etcd ? 6 : 4), cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === AclAsync (PG roles via the operator over the coordinator VIP) =======
    private static readonly string[] ProtectedRoles = ["nexus-cluster-admin", "postgres", "citus_app", "replicator", "rewind"];

    /// <inheritdoc />
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<AclSnapshot>(nodesR.Error!);
        var all = nodesR.Value!;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<AclSnapshot>(pwd.Error!);
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var sql = "SELECT rolname || '|' || concat_ws(',', CASE WHEN rolcanlogin THEN 'LOGIN' END, "
                + "CASE WHEN rolsuper THEN 'SUPERUSER' END, CASE WHEN rolcreaterole THEN 'CREATEROLE' END, "
                + "CASE WHEN rolcreatedb THEN 'CREATEDB' END, CASE WHEN rolreplication THEN 'REPLICATION' END) "
                + "FROM pg_roles WHERE rolname NOT LIKE 'pg\\_%' ORDER BY rolname";
            var r = await OperatorPsqlAsync(all, pwd.Value!, sql, cancellationToken).ConfigureAwait(false);
            if (r.IsFail) return Result.Fail<AclSnapshot>(r.Error!);
            var users = ParseRoles(r.Value!);
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
                users = users.Where(u => string.Equals(u.Name, operation.User, StringComparison.OrdinalIgnoreCase)).ToList();
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user.");
            if (verb == "revoke" && ProtectedRoles.Contains(operation.User, StringComparer.OrdinalIgnoreCase))
                return Result.Fail<AclSnapshot>($"refusing to revoke the built-in role '{operation.User}' (operator/system/app identity).");
            var privs = operation.Permissions is { Count: > 0 } ? operation.Permissions : DefaultGrantPrivs;
            var roleId = "\"" + operation.User.Replace("\"", "\"\"") + "\"";
            // CREATE ROLE on the coordinator auto-propagates to the workers (Citus).
            var sql = verb == "grant"
                ? $"DO $do$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{operation.User.Replace("'", "''")}') THEN CREATE ROLE {roleId} LOGIN PASSWORD '{operation.User}-ChangeMe-{DateTime.UtcNow.Ticks}'; END IF; END $do$; "
                  + $"GRANT {string.Join(", ", privs)} ON DATABASE {Db} TO {roleId}; SELECT 'GRANT_OK'"
                : $"REVOKE {string.Join(", ", privs)} ON DATABASE {Db} FROM {roleId}; SELECT 'REVOKE_OK'";
            var r = await OperatorPsqlAsync(all, pwd.Value!, sql, cancellationToken).ConfigureAwait(false);
            if (r.IsFail || !(r.Value!.Contains("GRANT_OK") || r.Value.Contains("REVOKE_OK")))
                return Result.Fail<AclSnapshot>($"acl {verb} failed: {(r.IsFail ? r.Error : Tail(r.Value ?? "", 220))}");
            return await AclAsync(new AclOperation("describe", operation.User), cancellationToken).ConfigureAwait(false);
        }
        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    private static List<AclUser> ParseRoles(string stdout)
    {
        var users = new List<AclUser>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 2);
            if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) continue;
            var attrs = parts.Length > 1 && parts[1].Length > 0
                ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                : ["(no attributes)"];
            users.Add(new AclUser(parts[0].Trim(), attrs, Enabled: true));
        }
        return users;
    }

    // === ApplyChaosAsync (process-kill a PG member + Patroni rejoin) =========
    /// <inheritdoc />
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ChaosOutcome>(nodesR.Error!);
        var all = nodesR.Value!;

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<ChaosOutcome>(status.Error!);
        var pgMembers = status.Value!.Members.Where(m => m.Role is "primary" or "replica").ToList();

        // Default victim: a worker-group REPLICA (safe -- its group stays writable).
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? pgMembers.FirstOrDefault(m => string.Equals(m.Hostname, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : (pgMembers.FirstOrDefault(m => m.Role == "replica" && m.ShardId != CoordGroup.Scope) ?? pgMembers.FirstOrDefault(m => m.Role == "replica"));
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target PG member found");
        var victimNode = all.FirstOrDefault(n => n.Vmnet11 == victim.IpAddress || string.Equals(n.Name, victim.Hostname, StringComparison.OrdinalIgnoreCase));
        if (victimNode is null || GroupOf(victimNode.Name) is null)
            return Result.Fail<ChaosOutcome>($"{victim.Hostname} is not a PG member; chaos targets PG nodes.");

        var target = T(victimNode.Vmnet11);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? PgSvc : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Hostname} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase))
            await _ssh.ExecuteAsync(target, $"sudo systemctl start {PgSvc}.service 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(120);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var post = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (post.IsOk && post.Value!.OverallHealth == "green") { recovered = true; break; }
        }
        sw.Stop();

        return Result.Ok(new ChaosOutcome(scenario.ScenarioType, victim.Hostname, observed, sw.Elapsed, startedAt, recovered));
    }

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
        return member.Role != "primary"; // refuse a Patroni leader (any group)
    }

    private static string Sanitize(string s) => Regex.Replace(s, "[^A-Za-z0-9_]", "_");
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
