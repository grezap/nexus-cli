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
/// PostgreSQL Patroni HA + etcd DCS + HAProxy LB adapter for Phase 0.G.4
/// (nexus-cli v0.6.3). Implements <see cref="IClusterAdapter"/> via SSH-shell-out
/// to on-node <c>patronictl</c> / <c>psql</c> / <c>pg_dump</c> / <c>etcdctl</c>
/// (no managed Npgsql driver). ADR-0013.
/// <para>
/// Topology per vms.yaml (cluster <c>postgres</c>): 3 Patroni PG nodes
/// (<c>pg-primary</c> + <c>pg-replica-1/2</c> @ .61/.62/.63, PG 17 streaming
/// replication on TLS :5432) + 3 etcd nodes (<c>etcd-1/2/3</c> @ .64/.65/.66,
/// the Patroni DCS, RBAC-enabled) + 2 HAProxy LB nodes (<c>haproxy-pg-1/2</c>
/// @ .67/.68) fronted by a keepalived VRRP VIP <c>.60</c>. HAProxy routes
/// <c>:5432</c> to the CURRENT Patroni leader only (<c>option httpchk GET
/// /leader</c> against each node's Patroni REST :8008).
/// </para>
/// <para>
/// Connection contract (live, 0.G.4): unit <c>nexus-patroni.service</c>;
/// patronictl wrapper <c>/usr/local/sbin/nexus-patronictl</c> (= <c>patronictl
/// -c /etc/nexus-patroni/patroni.yml</c>); etcdctl wrapper
/// <c>/usr/local/sbin/nexus-etcdctl</c>; PG certs
/// <c>/etc/nexus-patroni/tls/{server-cert,server-key,ca}.pem</c> (0640
/// root:postgres -- sudo to read); PG reached as the operator over TLS+scram via
/// <c>sudo env PGPASSWORD=&lt;kv&gt; psql "host=&lt;ip&gt; sslmode=verify-ca
/// sslrootcert=ca.pem user=nexus-cluster-admin"</c>. Writes target the VIP
/// <c>.60</c> (always the leader).
/// </para>
/// <para>
/// Operator identity (ADR-0013 model, identical to mongo + percona): the
/// dedicated <c>nexus-cluster-admin</c> role (LOGIN CREATEROLE CREATEDB
/// REPLICATION + pg_monitor/pg_read_all_data/pg_write_all_data, NOT superuser);
/// its password lives ONLY in Vault KV (<c>nexus/oltp/patroni/operator-password</c>),
/// fetched at runtime via <see cref="INexusVaultClient"/>. Patroni-plane verbs
/// (switchover/failover) go via on-node <c>patronictl</c> + sudo, not this role.
/// </para>
/// </summary>
public sealed class PatroniAdapter : IClusterAdapter
{
    private const string ClusterName = "postgres";
    private const string DisplayNameConst = "PostgreSQL Patroni HA";
    private const string OperatorUser = "nexus-cluster-admin";

    private const string PgTlsDir = "/etc/nexus-patroni/tls";
    private const string PgCaFile = PgTlsDir + "/ca.pem";
    private const string Patronictl = "sudo /usr/local/sbin/nexus-patronictl";
    private const string Etcdctl = "sudo /usr/local/sbin/nexus-etcdctl";
    private const string PgSvc = "nexus-patroni";
    private const string SmokeDb = "postgres";
    private const string SmokeTable = "nexus_smoke";

    private const string VaultMount = "nexus";
    private const string OperatorPwdPath = "oltp/patroni/operator-password";
    private const string PwdField = "content";
    private const string VaultAddr = "https://192.168.70.121:8200";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FailoverPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromMinutes(3);
    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly string[] DefaultGrantPrivs = ["CONNECT"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    private string? _operatorPassword;
    private ClusterStatus? _lastStatus;

    /// <summary>
    /// Constructs the Patroni adapter over the vms.yaml catalog + SSH client (operator username/key
    /// used for every on-node <c>patronictl</c>/<c>psql</c>/<c>etcdctl</c> call) and an OPTIONAL
    /// Vault client — Vault is consulted lazily for the <c>nexus-cluster-admin</c> password, so the
    /// Patroni-plane verbs (status/topology/failover) work without an operator token.
    /// </summary>
    public PatroniAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
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
    /// <summary>True when the node is a Patroni PG node (<c>pg-*</c> by vms.yaml name convention).</summary>
    private static bool IsPg(NodeRecord n) => n.Name.StartsWith("pg-", StringComparison.OrdinalIgnoreCase);
    /// <summary>True when the node is an etcd DCS node (<c>etcd-*</c>).</summary>
    private static bool IsEtcd(NodeRecord n) => n.Name.StartsWith("etcd-", StringComparison.OrdinalIgnoreCase);
    /// <summary>True when the node is an HAProxy LB node (<c>haproxy-*</c>).</summary>
    private static bool IsHaproxy(NodeRecord n) => n.Name.StartsWith("haproxy-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Split the vms.yaml <c>postgres</c> cluster into its PG / etcd / HAProxy tiers; fails if no <c>pg-*</c> node exists.</summary>
    private Result<(IReadOnlyList<NodeRecord> Pg, IReadOnlyList<NodeRecord> Etcd, IReadOnlyList<NodeRecord> Haproxy)> Split()
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>(cluster.Error!);
        var pg = cluster.Value!.Nodes.Where(IsPg).ToList();
        var etcd = cluster.Value.Nodes.Where(IsEtcd).ToList();
        var ha = cluster.Value.Nodes.Where(IsHaproxy).ToList();
        if (pg.Count == 0) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>("no pg-* nodes in vms.yaml cluster 'postgres'");
        return Result.Ok(((IReadOnlyList<NodeRecord>)pg, (IReadOnlyList<NodeRecord>)etcd, (IReadOnlyList<NodeRecord>)ha));
    }

    /// <summary>The keepalived VRRP VIP fronting HAProxy → the current Patroni leader (writes always target this).</summary>
    private static string Vip()
    {
        // HAProxy VRRP VIP fronting the leader. vms.yaml cluster 'postgres'
        // virtual_ips.haproxy_pg_vip = 192.168.70.60. Hard-default mirrors the
        // infra canon; the catalog doesn't surface virtual_ips today.
        return "192.168.70.60";
    }

    // === Vault password ====================================================
    /// <summary>Lazily fetch + cache the <c>nexus-cluster-admin</c> password from Vault KV; fails fast with an actionable hint when no operator token is configured.</summary>
    private async Task<Result<string>> OperatorPwdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_operatorPassword)) return Result.Ok(_operatorPassword);
        if (_vault is null)
            return Result.Fail<string>(
                "postgres verbs authenticate as nexus-cluster-admin, whose password lives in Vault KV. "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var r = await _vault.ReadKvFieldAsync(VaultMount, OperatorPwdPath, PwdField, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"could not read operator password from Vault ({VaultMount}/{OperatorPwdPath}): {r.Error}");
        _operatorPassword = r.Value;
        return Result.Ok(_operatorPassword!);
    }

    // === psql / patronictl helpers =========================================
    /// <summary>Run a SQL on a PG node as the operator over TLS+scram; returns tab-separated rows.</summary>
    private async Task<Result<string>> PgQueryAsync(string nodeIp, string pwd, string sql, CancellationToken ct, string db = SmokeDb, string? hostOverride = null)
    {
        var target = new SshTarget(nodeIp, 22, _sshUsername, _sshKeyPath);
        var host = hostOverride ?? nodeIp;
        // -tA = tuples-only, unaligned (tab field sep). sudo so root can read the
        // 0640 root:postgres ca.pem. Single-quote the SQL for the remote shell.
        var conn = $"host={host} port=5432 sslmode=verify-ca sslrootcert={PgCaFile} user={OperatorUser} dbname={db}";
        var cmd = $"sudo env PGPASSWORD='{pwd}' psql \"{conn}\" -tAF $'\\t' -c '{sql.Replace("'", "'\\''")}' 2>&1";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {nodeIp} failed: {exec.Error}");
        if (exec.Value!.ExitCode != 0)
            return Result.Fail<string>($"psql on {nodeIp} exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout, 300)}");
        return Result.Ok(exec.Value.Stdout.Trim());
    }

    /// <summary>Run nexus-patronictl on a PG node, returning stdout.</summary>
    private async Task<Result<string>> PatronictlAsync(string nodeIp, string args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var target = new SshTarget(nodeIp, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, $"{Patronictl} {args} 2>&1", timeout ?? SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {nodeIp} failed: {exec.Error}");
        if (exec.Value!.ExitCode != 0)
            return Result.Fail<string>($"patronictl on {nodeIp} exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout, 400)}");
        return Result.Ok(exec.Value.Stdout.Trim());
    }

    /// <summary><c>systemctl is-active</c> on a node → true only on an exact <c>active</c> prefix (avoids the <c>inactive</c> substring trap).</summary>
    private async Task<bool> IsActiveAsync(string nodeIp, string unit, CancellationToken ct)
    {
        var t = new SshTarget(nodeIp, 22, _sshUsername, _sshKeyPath);
        // `; true` so the ssh client always reports success; exact-prefix match
        // avoids the "inactive".Contains("active") substring trap.
        var ping = await _ssh.ExecuteAsync(t, $"systemctl is-active {unit} 2>/dev/null; true", SshTimeout, ct).ConfigureAwait(false);
        return ping.IsOk && ping.Value!.Stdout.Trim().StartsWith("active", StringComparison.Ordinal);
    }

    // === patronictl list --format json parsing =============================
    /// <summary>One row of <c>patronictl list --format json</c> (a PG cluster member as Patroni sees it).</summary>
    private sealed record PgMember(string Cluster, string Member, string Host, string Role, string State, double? LagMb);

    /// <summary>Parse `patronictl list --format json` from the first reachable PG node.</summary>
    private async Task<Result<(string Scope, IReadOnlyList<PgMember> Members, string SourceIp)>> PatroniListAsync(IReadOnlyList<NodeRecord> pg, CancellationToken ct)
    {
        foreach (var n in pg)
        {
            var r = await PatronictlAsync(n.Vmnet11, "list --format json", ct).ConfigureAwait(false);
            if (r.IsFail) continue;
            var json = r.Value!.Trim();
            var lb = json.IndexOf('[');
            if (lb < 0) continue;
            json = json.Substring(lb);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var members = new List<PgMember>();
                string scope = "";
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string GetS(string k) => el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                    double? lag = el.TryGetProperty("Lag in MB", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetDouble() : null;
                    scope = GetS("Cluster");
                    members.Add(new PgMember(scope, GetS("Member"), GetS("Host"), GetS("Role"), GetS("State"), lag));
                }
                if (members.Count > 0)
                    return Result.Ok((scope, (IReadOnlyList<PgMember>)members, n.Vmnet11));
            }
            catch { /* bad JSON during a transient; try the next node */ }
        }
        return Result.Fail<(string, IReadOnlyList<PgMember>, string)>("could not read patronictl list from any pg node");
    }

    /// <summary>Normalize a Patroni role (<c>Leader</c>/<c>Standby Leader</c>/<c>Replica</c>) to the adapter's <c>primary</c>/<c>replica</c> vocabulary.</summary>
    private static string RoleOf(PgMember m) =>
        m.Role.Equals("Leader", StringComparison.OrdinalIgnoreCase) ? "primary"
        : m.Role.Contains("Standby", StringComparison.OrdinalIgnoreCase) ? "replica"
        : m.Role.Equals("Replica", StringComparison.OrdinalIgnoreCase) ? "replica"
        : m.Role.ToLowerInvariant();

    /// <summary>Map a Patroni member <c>State</c> to the adapter's health vocabulary (<c>alive</c>/<c>syncing</c>/<c>failed</c>).</summary>
    private static string StatusOf(PgMember m) =>
        m.State is "running" or "streaming" ? "alive"
        : m.State is "starting" or "stopping" or "creating replica" or "in archive recovery" ? "syncing"
        : "failed";

    // === GetStatusAsync ====================================================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<ClusterStatus>(split.Error!);
        var (pg, etcd, ha) = split.Value;

        var list = await PatroniListAsync(pg, cancellationToken).ConfigureAwait(false);
        if (list.IsFail) return Result.Fail<ClusterStatus>(list.Error!);

        var members = new List<ClusterMember>();
        string? leader = null;
        foreach (var m in list.Value.Members)
        {
            var node = pg.FirstOrDefault(n => n.Vmnet11 == m.Host || string.Equals(n.Name, m.Member, StringComparison.OrdinalIgnoreCase));
            var role = RoleOf(m);
            if (role == "primary") leader = m.Member;
            members.Add(new ClusterMember(m.Member, node?.Vmnet11 ?? m.Host, role, StatusOf(m),
                ShardId: null, ReplicationLagSeconds: m.LagMb is { } mb ? mb : null));
        }
        // etcd quorum members (the Patroni DCS).
        foreach (var n in etcd)
        {
            var alive = await IsActiveAsync(n.Vmnet11, "nexus-etcd", cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, "dcs", alive ? "alive" : "failed"));
        }
        // HAProxy LB members + which holds the VIP.
        var vip = Vip();
        foreach (var n in ha)
        {
            var alive = await IsActiveAsync(n.Vmnet11, "nexus-haproxy", cancellationToken).ConfigureAwait(false);
            var holdsVip = await HoldsVipAsync(n.Vmnet11, vip, cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, holdsVip ? "router*" : "router", alive ? "alive" : "failed"));
        }

        var pgAlive = members.Where(m => m.Role is "primary" or "replica").Count(m => m.Status == "alive");
        var etcdAlive = members.Where(m => m.Role == "dcs").Count(m => m.Status == "alive");
        var haAlive = members.Where(m => m.Role.StartsWith("router", StringComparison.Ordinal)).Count(m => m.Status == "alive");
        var etcdQuorum = etcd.Count == 0 || etcdAlive >= (etcd.Count / 2 + 1);
        var overall = (leader is not null && pgAlive == pg.Count && etcdQuorum && haAlive >= 1) ? "green"
            : (leader is not null && pgAlive >= (pg.Count / 2 + 1) && etcdQuorum) ? "yellow" : "red";

        var s = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, leader, DateTimeOffset.UtcNow);
        _lastStatus = s;
        return Result.Ok(s);
    }

    /// <summary>True if the given HAProxy node currently owns the VRRP VIP (the <c>/</c> suffix anchors the CIDR match so <c>.6</c> can't match <c>.60</c>).</summary>
    private async Task<bool> HoldsVipAsync(string nodeIp, string vip, CancellationToken ct)
    {
        var t = new SshTarget(nodeIp, 22, _sshUsername, _sshKeyPath);
        var r = await _ssh.ExecuteAsync(t, $"ip -4 addr show | grep -q '{vip}/' && echo YES || echo NO", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Contains("YES", StringComparison.Ordinal);
    }

    // === HealthAsync =======================================================
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<HealthReport>(split.Error!);
        var (pg, etcd, ha) = split.Value;

        var list = await PatroniListAsync(pg, cancellationToken).ConfigureAwait(false);
        if (list.IsFail) return Result.Fail<HealthReport>(list.Error!);

        var probes = new List<HealthProbe>();
        var leaders = list.Value.Members.Count(m => RoleOf(m) == "primary");
        probes.Add(new HealthProbe("single-leader", ClusterName, leaders == 1 ? "green" : "red",
            leaders.ToString(CultureInfo.InvariantCulture), "exactly 1"));

        foreach (var m in list.Value.Members)
        {
            var role = RoleOf(m);
            var st = StatusOf(m);
            probes.Add(new HealthProbe("patroni-state", m.Member, st == "alive" ? "green" : st == "syncing" ? "yellow" : "red",
                $"{m.Role}/{m.State}", "Leader/running or Replica/streaming"));
            if (role == "replica")
            {
                var lag = m.LagMb ?? -1;
                probes.Add(new HealthProbe("replication-lag", m.Member, lag >= 0 && lag <= 10 ? "green" : lag < 0 ? "red" : "yellow",
                    lag < 0 ? "(unknown)" : $"{lag} MB", "<=10 MB"));
            }
        }

        // Operator-auth probe: a genuine TLS+scram round-trip via the VIP proves
        // the nexus-cluster-admin credential + HAProxy->leader routing end-to-end.
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsOk)
        {
            var vq = await PgQueryAsync(pg[0].Vmnet11, pwd.Value!, "SELECT pg_is_in_recovery()", cancellationToken, hostOverride: Vip()).ConfigureAwait(false);
            var writable = vq.IsOk && vq.Value!.Trim().StartsWith("f", StringComparison.OrdinalIgnoreCase);
            probes.Add(new HealthProbe("vip-writable", $"vip:{Vip()}", writable ? "green" : "red",
                vq.IsOk ? (writable ? "leader (recovery=false)" : vq.Value!.Trim()) : "unreachable", "leader serving writes"));
        }

        // etcd quorum -- authed endpoint health from one etcd node (reads the
        // etcd-root password on-node via that node's own Vault Agent token).
        if (etcd.Count > 0)
        {
            var eq = await EtcdQuorumAsync(etcd, cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("etcd-quorum", "etcd", eq.Healthy >= (etcd.Count / 2 + 1) ? "green" : "red",
                $"{eq.Healthy}/{etcd.Count} healthy", $">={etcd.Count / 2 + 1} (quorum)"));
        }

        foreach (var n in ha)
        {
            var ok = await IsActiveAsync(n.Vmnet11, "nexus-haproxy", cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("haproxy", n.Name, ok ? "green" : "red", ok ? "active" : "down", "active"));
        }

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    /// <summary>Authed etcd endpoint health from one etcd node (password read on-node via agent token).</summary>
    private async Task<(int Healthy, int Total)> EtcdQuorumAsync(IReadOnlyList<NodeRecord> etcd, CancellationToken ct)
    {
        foreach (var n in etcd)
        {
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var script =
                "T=$(sudo cat /run/nexus-vault-agent/token 2>/dev/null); "
                + $"PW=$(sudo env VAULT_ADDR={VaultAddr} VAULT_TOKEN=\"$T\" VAULT_CACERT=/etc/nexus-etcd/tls/ca.pem /usr/local/bin/vault kv get -field=content nexus/oltp/patroni/etcd-root-password 2>/dev/null); "
                + $"{Etcdctl} --user root:\"$PW\" endpoint health --cluster 2>&1";
            var r = await _ssh.ExecuteAsync(t, script, SshTimeout, ct).ConfigureAwait(false);
            if (r.IsFail || string.IsNullOrWhiteSpace(r.Value!.Stdout)) continue;
            var healthy = r.Value.Stdout.Split('\n').Count(l => l.Contains("is healthy", StringComparison.Ordinal));
            if (healthy > 0) return (healthy, etcd.Count);
        }
        return (0, etcd.Count);
    }

    // === TopologyAsync =====================================================
    /// <inheritdoc />
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var nodes = status.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.Role, m.Status, m.ReplicationLagSeconds))
            .ToList();
        // Patroni = single-leader streaming replication, not sharded → Shards=null.
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, CapturedAtUtc: DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (patronictl switchover, RTO via VIP) ================
    /// <inheritdoc />
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<FailoverResult>(split.Error!);
        var (pg, _, _) = split.Value;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var list = await PatroniListAsync(pg, cancellationToken).ConfigureAwait(false);
        if (list.IsFail) return Result.Fail<FailoverResult>(list.Error!);
        var preFlightAt = sw.Elapsed;

        var scope = list.Value.Scope;
        var current = list.Value.Members.FirstOrDefault(m => RoleOf(m) == "primary");
        if (current is null) return Result.Fail<FailoverResult>("no current Patroni leader found to switch over");
        var leaderNode = pg.First(n => n.Vmnet11 == current.Host || string.Equals(n.Name, current.Member, StringComparison.OrdinalIgnoreCase));

        // Pick the candidate: an explicit target, else a streaming replica.
        var candidates = list.Value.Members.Where(m => RoleOf(m) == "replica" && m.State == "streaming").ToList();
        var candidate = !string.IsNullOrWhiteSpace(request.TargetNode)
            ? candidates.FirstOrDefault(m => string.Equals(m.Member, request.TargetNode, StringComparison.OrdinalIgnoreCase))
            : candidates.FirstOrDefault();
        if (candidate is null) return Result.Fail<FailoverResult>("no streaming replica available to switch over to");
        var candidateNode = pg.First(n => n.Vmnet11 == candidate.Host || string.Equals(n.Name, candidate.Member, StringComparison.OrdinalIgnoreCase));
        var oldLeaderIp = leaderNode.Vmnet11;

        // Issue a planned switchover (graceful, repeatable). Run from a stable
        // node (the candidate -- it stays up + becomes the new leader).
        var swArgs = $"switchover {scope} --leader {current.Member} --candidate {candidate.Member} --force";
        var swRes = await PatronictlAsync(candidateNode.Vmnet11, swArgs, cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        // patronictl exits 0 even when the switchover is REFUSED (e.g. "403,
        // client certificate required") -- so validate the success banner in
        // stdout, not just the exit code (0.G.4 live-caught).
        if (swRes.IsFail) return Result.Fail<FailoverResult>($"patronictl switchover failed: {swRes.Error}");
        if (!swRes.Value!.Contains("Successfully switched over", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<FailoverResult>($"patronictl switchover did not succeed: {Tail(swRes.Value!, 300)}");
        var failureInjectedAt = sw.Elapsed;

        // Poll the VIP until it serves a DIFFERENT, writable leader.
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<FailoverResult>(pwd.Error!);
        string? newLeader = null;
        var newLeaderAt = TimeSpan.Zero;
        var vip = Vip();
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(FailoverPollInterval, cancellationToken).ConfigureAwait(false);
            var q = await PgQueryAsync(candidateNode.Vmnet11, pwd.Value!,
                "SELECT inet_server_addr() || '|' || pg_is_in_recovery()::text", cancellationToken, hostOverride: vip).ConfigureAwait(false);
            if (q.IsFail) continue;
            var parts = q.Value!.Trim().Split('|');
            if (parts.Length < 2) continue;
            var servingIp = parts[0].Trim().Split('/')[0];
            var inRecovery = parts[1].Trim().StartsWith("t", StringComparison.OrdinalIgnoreCase);
            if (!inRecovery && servingIp != oldLeaderIp)
            {
                newLeader = pg.FirstOrDefault(n => n.Vmnet11 == servingIp)?.Name ?? servingIp;
                newLeaderAt = sw.Elapsed;
                break;
            }
        }

        var rto = newLeader is not null ? newLeaderAt - failureInjectedAt : TimeSpan.Zero;

        // Recovery: switch back to the original leader (unless NoRecover).
        var recovery = "skipped";
        if (!request.NoRecover && newLeader is not null)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var back = await PatronictlAsync(candidateNode.Vmnet11,
                $"switchover {scope} --leader {candidate.Member} --candidate {current.Member} --force", cancellationToken, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            recovery = back.IsOk && back.Value!.Contains("Successfully switched over", StringComparison.OrdinalIgnoreCase) ? "recovered" : "failed";
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "patroni-switchover",
            OriginalPrimary: leaderNode.Name,
            NewPrimary: newLeader,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: newLeader is null ? "VIP did not serve a new writable leader within the deadline; check HAProxy /leader httpchk + Patroni REST :8008" : null,
            Timeline: new FailoverTimeline(preFlightAt, failureInjectedAt, newLeaderAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOutAddAsync / RemoveAsync (Patroni replica join/leave) =======
    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<ScaleOutResult>(split.Error!);
        var (pg, _, _) = split.Value;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // A "joined" node = nexus-patroni active. Find a provisioned PG node that is NOT active.
        NodeRecord? candidate = null;
        foreach (var n in pg)
        {
            if (!await IsActiveAsync(n.Vmnet11, PgSvc, cancellationToken).ConfigureAwait(false)) { candidate = n; break; }
        }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "all provisioned pg nodes are already joined. Provision a new node first (apply-on-demand, ADR-0013): "
                + "add a pg-replica-N + overlays in oltp-patroni, `pwsh -File scripts/oltp-patroni.ps1 apply`, then re-run `scale-out add`.");

        var joinTarget = new SshTarget(candidate.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var join = await _ssh.ExecuteAsync(joinTarget, $"sudo systemctl start {PgSvc}.service && echo STARTED", BackupTimeout, cancellationToken).ConfigureAwait(false);
        if (join.IsFail || !join.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to start {PgSvc} on {candidate.Name}: {(join.IsFail ? join.Error : Tail(join.Value!.Stderr, 200))}");

        // Wait for the rejoined node to reach streaming.
        var deadline = sw.Elapsed + JoinDeadline;
        var streaming = false;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var list = await PatroniListAsync(pg, cancellationToken).ConfigureAwait(false);
            if (list.IsOk)
            {
                var m = list.Value.Members.FirstOrDefault(x => string.Equals(x.Member, candidate.Name, StringComparison.OrdinalIgnoreCase));
                if (m is not null && m.State == "streaming") { streaming = true; break; }
            }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: streaming ? "ok" : "partial",
            OutcomeReason: streaming ? $"{candidate.Name} rejoined the Patroni cluster (streaming)" : $"{candidate.Name} started but not yet streaming (pg_basebackup/rewind may still be running)",
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
        var (pg, _, _) = split.Value;
        var node = pg.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not a pg node in the postgres cluster");

        // Refuse removing the current leader (would force a failover).
        var list = await PatroniListAsync(pg, cancellationToken).ConfigureAwait(false);
        if (list.IsOk)
        {
            var m = list.Value.Members.FirstOrDefault(x => string.Equals(x.Member, node.Name, StringComparison.OrdinalIgnoreCase));
            if (request.Drain && m is not null && RoleOf(m) == "primary")
                return Result.Fail<ScaleOutResult>(
                    $"{node.Name} is the current Patroni leader; fail it over first (`nexus failover-test cluster postgres`) before removing.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var t = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
        // Graceful leave = stop nexus-patroni; the remaining members keep quorum, Patroni shows it stopped.
        var stop = await _ssh.ExecuteAsync(t, $"sudo systemctl stop {PgSvc}.service && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to stop {PgSvc} on {node.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 200))}");
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"gracefully left {node.Name} from the Patroni cluster (service stopped; ready for re-add via `scale-out add` or deprovision)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupTakeAsync / RestoreAsync (pg_dump round-trip) ===============
    /// <inheritdoc />
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<BackupResult>(split.Error!);
        var (pg, _, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<BackupResult>(pwd.Error!);

        // Dump from a streaming replica where possible (offload the leader); read-only.
        var statusRes = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (statusRes.IsFail) return Result.Fail<BackupResult>(statusRes.Error!);
        var runMember = statusRes.Value!.Members.FirstOrDefault(m => m.Role == "replica" && m.Status == "alive")
            ?? statusRes.Value.Members.First(m => m.Role is "primary" or "replica" && m.Status == "alive");
        var runNode = pg.First(n => n.Vmnet11 == runMember.IpAddress);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"patroni-backup-{startedAt:yyyyMMdd-HHmmss}"
            : $"patroni-{request.Tag}-{startedAt:yyyyMMdd-HHmmss}";
        var dir = "/var/backups/nexus-patroni";
        var file = $"{dir}/{backupId}.sql.gz";

        // pg_dump the nexus_smoke table over TLS+scram as the operator (connect to
        // the node's own VMnet11 so pg_hba's hostssl scram rule applies); gzip;
        // node-local file; report the byte size. --no-owner/--no-privileges so the
        // dump replays cleanly under the operator role (which is not the table
        // owner nexusops) -- otherwise the ALTER TABLE ... OWNER TO lines fail on
        // restore (0.G.4 live-caught: "must be owner of table nexus_smoke").
        var conn = $"host={runNode.Vmnet11} port=5432 sslmode=verify-ca sslrootcert={PgCaFile} user={OperatorUser} dbname={SmokeDb}";
        var script =
            $"sudo mkdir -p {dir}; "
            + $"sudo env PGPASSWORD='{pwd.Value}' pg_dump \"{conn}\" -t {SmokeTable} --no-owner --no-privileges 2>/dev/null | gzip | sudo tee {file} >/dev/null; "
            + $"sudo stat -c %s {file}";
        var target = new SshTarget(runNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<BackupResult>($"backup on {runNode.Name} failed: {exec.Error}");
        var lines = exec.Value!.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        long size = 0;
        if (lines.Length == 0 || !long.TryParse(lines[^1].Trim(), out size) || size <= 0)
            return Result.Fail<BackupResult>($"pg_dump did not produce a non-empty archive: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{file} (node-local on {runNode.Name}; pg_dump -t {SmokeTable})",
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
        var (pg, _, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<RestoreResult>(pwd.Error!);

        var dir = "/var/backups/nexus-patroni";
        var file = $"{dir}/{request.BackupId}.sql.gz";

        // Find the node holding the (node-local) dump.
        NodeRecord? runNode = null;
        foreach (var n in pg)
        {
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var probe = await _ssh.ExecuteAsync(t, $"test -s {file} && echo FOUND || echo NO", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (probe.IsOk && probe.Value!.Stdout.Contains("FOUND", StringComparison.Ordinal)) { runNode = n; break; }
        }
        if (runNode is null)
            return Result.Fail<RestoreResult>($"backup '{request.BackupId}' not found on any pg node (looked for {file}).");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        // Non-destructive restore into a throwaway DATABASE the operator OWNS
        // (it has CREATEDB but no CREATE on the postgres db, so a schema-in-postgres
        // restore is denied -- 0.G.4 live-caught). Writes go via the VIP (leader).
        // Separate -c flags so CREATE DATABASE runs outside a transaction block.
        var vip = Vip();
        var connPg = $"host={vip} port=5432 sslmode=verify-ca sslrootcert={PgCaFile} user={OperatorUser} dbname={SmokeDb}";
        var connVerify = $"host={vip} port=5432 sslmode=verify-ca sslrootcert={PgCaFile} user={OperatorUser} dbname=nexus_restore_verify";
        var script =
            $"sudo env PGPASSWORD='{pwd.Value}' psql \"{connPg}\" -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS nexus_restore_verify' -c 'CREATE DATABASE nexus_restore_verify' 2>&1; "
            + $"zcat {file} | sudo env PGPASSWORD='{pwd.Value}' psql \"{connVerify}\" 2>&1 | tail -2; "
            + $"sudo env PGPASSWORD='{pwd.Value}' psql \"{connVerify}\" -tAc 'SELECT '\\''RESTORED='\\'' || count(*) FROM {SmokeTable};' 2>&1";
        var target = new SshTarget(runNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<RestoreResult>($"restore on {runNode.Name} failed: {exec.Error}");
        var m = System.Text.RegularExpressions.Regex.Match(exec.Value!.Stdout, @"RESTORED=(\d+)");
        if (!m.Success)
            return Result.Fail<RestoreResult>($"restore round-trip did not confirm restored rows: {Tail(exec.Value.Stdout, 400)}");

        return Result.Ok(new RestoreResult(
            BackupId: request.BackupId,
            ItemsRestored: long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === RotateCertAsync (Vault re-issue per node, rolling reload/restart) ==
    /// <summary>Per-tier cert facts: the TLS dir, file-owner group, systemd unit, whether a reload (vs restart) picks up the new leaf, and the allowed PKI domain.</summary>
    private sealed record CertRole(string TlsDir, string Group, string Svc, bool Reload, string Domain);

    /// <summary>Resolve the <see cref="CertRole"/> for a node from its tier (all tiers share the single <c>patroni-server</c> PKI role + <c>patroni.nexus.lab</c> domain).</summary>
    private static CertRole RoleDescriptor(NodeRecord n) =>
        // All 8 nodes share the single PKI role 'patroni-server', whose
        // allowed_domains is 'patroni.nexus.lab' (the original etcd/haproxy certs
        // are <node>.patroni.nexus.lab too -- a foreign domain 500s "common name
        // not allowed by this role", 0.G.4 live-caught).
        IsEtcd(n) ? new CertRole("/etc/nexus-etcd/tls", "etcd", "nexus-etcd", Reload: false, "patroni.nexus.lab")
        : IsHaproxy(n) ? new CertRole("/etc/nexus-haproxy/tls", "haproxy", "nexus-haproxy", Reload: true, "patroni.nexus.lab")
        : new CertRole(PgTlsDir, "postgres", "nexus-patroni", Reload: true, "patroni.nexus.lab");

    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<CertRotationResult>(split.Error!);
        var (pg, etcd, ha) = split.Value;
        // Rotate replicas + etcd + haproxy first, the PG leader LAST (so the
        // write endpoint reloads only after the rest are healthy).
        var statusForOrder = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var leader = statusForOrder.IsOk ? statusForOrder.Value!.Leader : null;
        var pgOrdered = pg.OrderBy(n => string.Equals(n.Name, leader, StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
        var all = etcd.Concat(ha).Concat(pgOrdered).ToList();

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var node in all)
        {
            var rd = RoleDescriptor(node);
            var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var oldSerialExec = await _ssh.ExecuteAsync(target,
                $"sudo openssl x509 -in {rd.TlsDir}/server-cert.pem -noout -serial 2>/dev/null | sed 's/serial=//'",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.ExitCode == 0 && oldSerialExec.Value.Stdout.Trim().Length > 0
                ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            var cn = $"{node.Name}.{rd.Domain}";
            var alts = $"{node.Name},{node.Name}.nexus.lab,{cn},localhost";
            var ips = $"{node.Vmnet10},{node.Vmnet11},127.0.0.1";
            // HAProxy nodes carry the VIP in their cert IP-SANs (clients verify the VIP host).
            if (IsHaproxy(node)) ips += $",{Vip()}";
            var issueCmd =
                "T=$(sudo cat /run/nexus-vault-agent/token 2>/dev/null); "
                + $"sudo env VAULT_ADDR={VaultAddr} VAULT_TOKEN=\"$T\" VAULT_CACERT={rd.TlsDir}/ca.pem "
                + $"/usr/local/bin/vault write -format=json pki_int/issue/patroni-server common_name={cn} alt_names={alts} ip_sans={ips} ttl=2160h";
            var issueExec = await _ssh.ExecuteAsync(target, issueCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
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

            // PG reloads ssl_cert_file on SIGHUP (systemctl reload); etcd needs a
            // restart; haproxy reloads gracefully.
            var apply = rd.Reload ? $"sudo systemctl reload {rd.Svc}" : $"sudo systemctl restart {rd.Svc}";
            var writeCmd =
                $"echo {B64(cert.TrimEnd() + "\n")}|base64 -d|sudo tee {rd.TlsDir}/server-cert.pem >/dev/null; "
                + $"echo {B64(key.TrimEnd() + "\n")}|base64 -d|sudo tee {rd.TlsDir}/server-key.pem >/dev/null; "
                + $"echo {B64(ca.TrimEnd() + "\n")}|base64 -d|sudo tee /tmp/_ica.pem >/dev/null; "
                + $"sudo bash -c 'cat /tmp/_ica.pem $(ls /etc/vault-agent/ca-bundle.crt 2>/dev/null) > {rd.TlsDir}/ca.pem 2>/dev/null || cp /tmp/_ica.pem {rd.TlsDir}/ca.pem'; "
                + $"sudo rm -f /tmp/_ica.pem; sudo chown root:{rd.Group} {rd.TlsDir}/server-cert.pem {rd.TlsDir}/server-key.pem {rd.TlsDir}/ca.pem; "
                + $"sudo chmod 0640 {rd.TlsDir}/server-cert.pem {rd.TlsDir}/server-key.pem {rd.TlsDir}/ca.pem; "
                + $"{apply}; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(target, writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stdout, 200)}"));
                continue;
            }
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: null));
            // Settle so the reloaded node is healthy before the next rotates.
            await Task.Delay(TimeSpan.FromSeconds(IsPg(node) ? 6 : 3), cancellationToken).ConfigureAwait(false);
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
        var (pg, _, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<AclSnapshot>(pwd.Error!);
        var verb = operation.Verb.ToLowerInvariant();
        // ACL ops go to the leader (CREATE ROLE is a write); read via VIP too.
        var vip = Vip();

        if (verb is "list" or "describe")
        {
            // \du equivalent: rolname + the LOGIN/SUPERUSER/CREATEROLE/CREATEDB/REPLICATION attribute flags.
            var sql = "SELECT rolname || '|' || "
                + "concat_ws(',', CASE WHEN rolcanlogin THEN 'LOGIN' END, CASE WHEN rolsuper THEN 'SUPERUSER' END, "
                + "CASE WHEN rolcreaterole THEN 'CREATEROLE' END, CASE WHEN rolcreatedb THEN 'CREATEDB' END, "
                + "CASE WHEN rolreplication THEN 'REPLICATION' END) FROM pg_roles WHERE rolname NOT LIKE 'pg\\_%' ORDER BY rolname";
            var r = await PgQueryAsync(pg[0].Vmnet11, pwd.Value!, sql, cancellationToken, hostOverride: vip).ConfigureAwait(false);
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
            var privs = operation.Permissions is { Count: > 0 } ? operation.Permissions : DefaultGrantPrivs;
            // PG privilege grants are on objects; for a portfolio "acl grant" we
            // CREATE the LOGIN role (idempotent) + GRANT CONNECT on the smoke db.
            var roleId = "\"" + operation.User.Replace("\"", "\"\"") + "\"";
            string sql = verb == "grant"
                ? $"DO $do$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{operation.User.Replace("'", "''")}') THEN CREATE ROLE {roleId} LOGIN PASSWORD '{operation.User}-ChangeMe-{DateTime.UtcNow.Ticks}'; END IF; END $do$; "
                  + $"GRANT {string.Join(", ", privs)} ON DATABASE {SmokeDb} TO {roleId}; SELECT 'GRANT_OK'"
                : $"REVOKE {string.Join(", ", privs)} ON DATABASE {SmokeDb} FROM {roleId}; SELECT 'REVOKE_OK'";
            var r = await PgQueryAsync(pg[0].Vmnet11, pwd.Value!, sql, cancellationToken, hostOverride: vip).ConfigureAwait(false);
            if (r.IsFail || !(r.Value!.Contains("GRANT_OK") || r.Value.Contains("REVOKE_OK")))
                return Result.Fail<AclSnapshot>($"acl {verb} failed: {(r.IsFail ? r.Error : Tail(r.Value ?? "", 200))}");
            return await AclAsync(new AclOperation("describe", operation.User), cancellationToken).ConfigureAwait(false);
        }
        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    /// <summary>Parse the pipe-delimited <c>rolname|FLAG,FLAG</c> rows from the <c>\du</c>-equivalent query into <see cref="AclUser"/>s.</summary>
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

    // === ApplyChaosAsync ===================================================
    /// <inheritdoc />
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");

        var split = Split();
        if (split.IsFail) return Result.Fail<ChaosOutcome>(split.Error!);
        var (pg, _, _) = split.Value;

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<ChaosOutcome>(status.Error!);

        // Default target: a replica (safer than the leader).
        var members = status.Value!.Members.Where(m => m.Role is "primary" or "replica").ToList();
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? members.FirstOrDefault(m => string.Equals(m.Hostname, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : (members.FirstOrDefault(m => m.Role == "replica") ?? (members.Count > 0 ? members[0] : null));
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target node found");
        var victimNode = pg.First(n => n.Vmnet11 == victim.IpAddress || string.Equals(n.Name, victim.Hostname, StringComparison.OrdinalIgnoreCase));

        var target = new SshTarget(victimNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
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

        // For process-kill, Patroni itself is the killed process → restart it so PG rejoins.
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

        return Result.Ok(new ChaosOutcome(
            ScenarioApplied: scenario.ScenarioType,
            Target: victim.Hostname,
            ObservedImpact: observed,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt,
            Recovered: recovered));
    }

    /// <summary>Base64-stream the embedded <c>nexus-chaos.sh</c> helper onto the victim node and mark it executable (idempotent).</summary>
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
        return member.Role != "primary"; // refuse the current Patroni leader
    }

    /// <summary>Keep only the last <paramref name="n"/> characters of a string (trims noisy stdout/stderr in error messages).</summary>
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
    /// <summary>UTF-8 base64-encode a string for safe transport through the remote shell (cert/key material carries newlines + PEM markers).</summary>
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
