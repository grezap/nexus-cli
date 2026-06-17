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
/// Vitess-sharded MySQL/Percona cluster adapter for Phase 0.O (nexus-cli
/// v0.7.2). Implements <see cref="IClusterAdapter"/> via SSH-shell-out to the
/// on-node <c>vtctldclient</c> (mTLS gRPC control plane) + the <c>mysql</c>
/// client against a vtgate MySQL listener (mTLS wire) -- no managed MySql / gRPC
/// driver is linked (NetArchTest-enforced). ADR-0020.
/// <para>
/// Topology per vms.yaml (cluster <c>vitess</c>, ADR-0041): 12 VMs, tier
/// 07-vitess -- 3 etcd topo (<c>vitess-etcd-1/2/3</c> @ .190-.192,
/// <c>nexus-etcd.service</c>, global+local cell <c>nexus</c>), 1 control
/// (<c>vitess-control-1</c> @ .193, <c>nexus-vtctld</c> + <c>nexus-vtorc</c>),
/// 2 vtgate routers (<c>vitess-vtgate-1/2</c> @ .194/.195, <c>nexus-vtgate</c>,
/// MySQL <c>:15306</c>), and 2 shards x 3 tablets (keyspace <c>commerce</c> split
/// <c>-80</c> @ .196-.198 / <c>80-</c> @ .199-.201; each tablet =
/// <c>nexus-vttablet</c> + local Percona Server 8.4 under <c>nexus-mysqlctld</c>).
/// Each shard runs 1 PRIMARY + 2 REPLICA; a row's shard is chosen by a hash
/// vindex on <c>customer_id</c> (the sharding proof). Durability <c>none</c>
/// (async repl; semi_sync is the 0.O.1 hardening). VTOrc auto-reparents a shard
/// when its PRIMARY dies.
/// </para>
/// <para>
/// Connection contract (LIVE-PROBED 2026-06-17 against the running cluster):
/// <list type="bullet">
///   <item><b>Control plane</b> (status / topology / failover / scale-out /
///   backup-orchestration) -- the mTLS-preloaded wrapper
///   <c>sudo /usr/local/sbin/nexus-vtctldclient</c> on the control node (dials
///   vtctld :15999 with the node's per-host PKI leaf; no password). Tablets
///   register in the topo by their <b>VMnet10 backplane</b> IP (.10.x), mapped
///   back to a node via vms.yaml's vmnet10.</item>
///   <item><b>SQL plane</b> (health write-probe / sharding proof) -- the
///   vtgate MySQL listener <c>:15306</c> over mTLS as the static-auth user
///   <c>nexus</c>; the listener requires a CLIENT cert (the node's own leaf
///   doubles as the client cert, 0.O fix O13). Run from a tablet node (it has
///   the <c>mysql</c> client + the TLS leaf), connecting to a vtgate's vmnet11.</item>
///   <item><b>Backup</b> -- the live 0.O infra has NO Vitess BackupStorage
///   backend configured (<c>GetBackups</c> -> "no registered implementation of
///   BackupStorage"; no xtrabackup installed), so the backup verb takes a
///   sharding-aware <b>logical mysqldump</b> per shard from the shard PRIMARY's
///   mysqld socket (as <c>vt_dba</c>) and proves a real round-trip by reloading
///   into a throwaway verify schema and counting rows. Engine-native
///   <c>vtctldclient Backup</c> (builtin/xtrabackup engine on a NFS file repo)
///   is the 0.O.1 infra enhancement.</item>
/// </list>
/// </para>
/// <para>
/// Operator identity (ADR-0020, the LOCKED Vault-KV model -- identical to
/// mongo/percona/patroni/clickhouse/starrocks): the SQL plane authenticates as
/// the <c>nexus</c> vtgate user, whose password = the app password held ONLY in
/// Vault KV (<c>nexus/vitess/mysql-app-password</c>, field <c>content</c>);
/// mysqldump uses <c>vt_dba</c> (<c>nexus/vitess/mysql-allprivs-password</c>).
/// Both fetched at runtime via <see cref="INexusVaultClient"/>; creds transit,
/// never persist. The gRPC control plane needs no password (mTLS only).
/// </para>
/// <para>
/// Verb surface (v0.7.2): status / health / topology (Shards POPULATED -- the
/// sharded showcase: <c>-80</c> / <c>80-</c> hash-vindex ranges) / failover
/// (graceful PlannedReparentShard to a healthy replica; VTOrc auto-reparent on a
/// PRIMARY kill is the chaos path) / scale-out add+remove (tablet membership via
/// DeleteTablets + service start) / backup take+restore (logical mysqldump
/// round-trip per shard) / cert-rotate (per-node Vault PKI, gRPC + vtgate
/// listener reload; mysqld-wire reload deferred to avoid an unplanned reparent)
/// / acl (the vtgate static-auth users in <c>vtgate_creds.json</c> -- the real
/// MySQL credentials at the :15306 front door; vtgate does not proxy CREATE USER
/// DDL) / chaos (process-kill a tablet + VTOrc/replication rejoin).
/// </para>
/// </summary>
public sealed class VitessAdapter : IClusterAdapter
{
    private const string ClusterName = "vitess";
    private const string DisplayNameConst = "Vitess Sharded MySQL Cluster";
    private const string Cell = "nexus";
    private const string Keyspace = "commerce";
    // The underlying mysqld database name = the keyspace prefixed with "vt_" (Vitess
    // convention). vtgate translates the keyspace name; DIRECT mysqld access
    // (mysqldump over the socket) must use the vt_-prefixed name.
    private const string MysqlDb = "vt_commerce";
    private const string ShardTable = "customer";
    private const string PkiRole = "vitess-server";

    private const string VtctldWrapper = "/usr/local/sbin/nexus-vtctldclient";
    private const string EtcdctlWrapper = "/usr/local/sbin/nexus-etcdctl";
    private const string TlsDir = "/etc/nexus-vitess/tls";
    private const string EtcdTlsDir = "/etc/nexus-etcd/tls";
    private const string SplitScript = "/usr/local/sbin/nexus-vitess-tls-split.sh";
    private const string DataRoot = "/var/lib/nexus-vitess";
    private const string BackupRoot = "/var/backups/nexus-vitess";
    private const string AgentToken = "/run/nexus-vault-agent/token";
    private const string VaultAddr = "https://192.168.70.121:8200";

    private const int VtgatePort = 15306;
    private const int VttabletStatusPort = 15101;
    private const int VtorcPort = 16000;

    // Service units.
    private const string EtcdSvc = "nexus-etcd";
    private const string VtctldSvc = "nexus-vtctld";
    private const string VtorcSvc = "nexus-vtorc";
    private const string VtgateSvc = "nexus-vtgate";
    private const string VttabletSvc = "nexus-vttablet";
    private const string MysqlctldSvc = "nexus-mysqlctld";

    // Vault KV (mount nexus/, KV-v2). Seeded by the 0.O security overlay
    // role-overlay-vault-vitess-cluster-creds-seed.tf; field is `content`.
    private const string VaultMount = "nexus";
    private const string AppPwdPath = "vitess/mysql-app-password";
    private const string DbaPwdPath = "vitess/mysql-allprivs-password";
    private const string PwdField = "content";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromMinutes(2);
    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly string[] DefaultGrantPrivs = ["SELECT"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    private string? _appPwd;
    private string? _dbaPwd;
    private ClusterStatus? _lastStatus;

    public VitessAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
        _vault = vault;
    }

    public string ClusterId => ClusterName;
    public string DisplayName => DisplayNameConst;

    // === Node classification (deterministic, from the vms.yaml name) =========
    // vitess-etcd-N        -> ("etcd",    shardIdx 0)
    // vitess-control-N     -> ("control", 0)
    // vitess-vtgate-N      -> ("vtgate",  0)
    // vitess-shard<K>-tablet-M -> ("tablet", K)   (K = 1-based shard index)
    internal static (string Role, int ShardIndex) Classify(string nodeName)
    {
        var n = nodeName.ToLowerInvariant();
        if (n.StartsWith("vitess-etcd", StringComparison.Ordinal)) return ("etcd", 0);
        if (n.StartsWith("vitess-control", StringComparison.Ordinal)) return ("control", 0);
        if (n.StartsWith("vitess-vtgate", StringComparison.Ordinal)) return ("vtgate", 0);
        var m = Regex.Match(n, @"vitess-shard(\d+)-tablet");
        if (m.Success) return ("tablet", int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        return ("unknown", 0);
    }

    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    private Result<IReadOnlyList<NodeRecord>> Nodes()
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<IReadOnlyList<NodeRecord>>(cluster.Error!);
        if (cluster.Value!.Nodes.Count == 0) return Result.Fail<IReadOnlyList<NodeRecord>>($"cluster '{ClusterName}' has no nodes in vms.yaml");
        return Result.Ok(cluster.Value.Nodes);
    }

    private static List<NodeRecord> ByRole(IReadOnlyList<NodeRecord> all, string role) =>
        all.Where(n => Classify(n.Name).Role == role).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

    private static NodeRecord? ByVmnet10(IReadOnlyList<NodeRecord> all, string ip) =>
        all.FirstOrDefault(n => n.Vmnet10 == ip || n.Vmnet11 == ip);

    // === Vault passwords ====================================================
    private async Task<Result<string>> AppPwdAsync(CancellationToken ct) => await PwdAsync(AppPwdPath, "nexus (vtgate app)", cache: v => _appPwd = v, cached: _appPwd, ct).ConfigureAwait(false);
    private async Task<Result<string>> DbaPwdAsync(CancellationToken ct) => await PwdAsync(DbaPwdPath, "vt_dba", cache: v => _dbaPwd = v, cached: _dbaPwd, ct).ConfigureAwait(false);

    private async Task<Result<string>> PwdAsync(string kvPath, string who, Action<string> cache, string? cached, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cached)) return Result.Ok(cached);
        if (_vault is null)
            return Result.Fail<string>(
                $"vitess SQL-plane verbs authenticate as {who}, whose password lives in Vault KV ({VaultMount}/{kvPath}). "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT (e.g. `$env:VAULT_ADDR='https://192.168.70.121:8200'; "
                + "$env:VAULT_TOKEN=<token>; $env:VAULT_CACERT=$HOME\\.nexus\\vault-ca-bundle.crt`) and retry.");
        var r = await _vault.ReadKvFieldAsync(VaultMount, kvPath, PwdField, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"could not read {who} password from Vault ({VaultMount}/{kvPath}): {r.Error}");
        var pwd = (r.Value ?? string.Empty).Trim();
        if (pwd.Length == 0) return Result.Fail<string>($"{who} password from Vault is empty");
        cache(pwd);
        return Result.Ok(pwd);
    }

    // === vtctldclient (control plane, mTLS, no password) ====================
    private async Task<Result<string>> VtctldAsync(IReadOnlyList<NodeRecord> all, string args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var control = ByRole(all, "control").FirstOrDefault();
        if (control is null) return Result.Fail<string>("no vitess-control node in vms.yaml");
        var cmd = $"sudo {VtctldWrapper} {args} 2>&1";
        var exec = await _ssh.ExecuteAsync(T(control.Vmnet11), cmd, timeout ?? SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {control.Name} ({control.Vmnet11}) failed: {exec.Error}");
        if (exec.Value!.ExitCode != 0)
            return Result.Fail<string>($"vtctldclient {args.Split(' ')[0]} exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout, 300)}");
        return Result.Ok(exec.Value.Stdout.Trim());
    }

    // A tablet record from `GetTablets --format json`.
    internal sealed record TabletInfo(int Uid, string Shard, string Role, string Vmnet10);

    /// <summary>Parse `GetTablets --format json` into tablet records (type 1=primary, 2=replica, 3=rdonly).</summary>
    internal static List<TabletInfo> ParseTabletsJson(string json)
    {
        var tablets = new List<TabletInfo>();
        var trimmed = ExtractJsonArray(json);
        if (trimmed is null) return tablets;
        using var doc = JsonDocument.Parse(trimmed);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return tablets;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var uid = t.TryGetProperty("alias", out var a) && a.TryGetProperty("uid", out var u) ? u.GetInt32() : -1;
            var shard = t.TryGetProperty("shard", out var sh) ? sh.GetString() ?? "" : "";
            var host = t.TryGetProperty("hostname", out var hh) ? hh.GetString() ?? "" : "";
            var typeNum = t.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.Number ? ty.GetInt32() : 0;
            var role = typeNum switch { 1 => "primary", 2 => "replica", 3 => "rdonly", _ => "unknown" };
            if (uid >= 0 && host.Length > 0) tablets.Add(new TabletInfo(uid, shard, role, host));
        }
        return tablets;
    }

    /// <summary>Parse `GetShard commerce/&lt;shard&gt;` JSON for the authoritative primary uid.</summary>
    internal static int? ParseShardPrimaryUid(string json)
    {
        var obj = ExtractJsonObject(json);
        if (obj is null) return null;
        using var doc = JsonDocument.Parse(obj);
        if (doc.RootElement.TryGetProperty("shard", out var sh)
            && sh.TryGetProperty("primary_alias", out var pa)
            && pa.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.Number)
            return u.GetInt32();
        return null;
    }

    private async Task<Result<List<TabletInfo>>> GetTabletsAsync(IReadOnlyList<NodeRecord> all, CancellationToken ct, string? shard = null)
    {
        var args = shard is null ? $"GetTablets --keyspace {Keyspace} --format json"
            : $"GetTablets --keyspace {Keyspace} --shard {shard} --format json";
        var r = await VtctldAsync(all, args, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<List<TabletInfo>>(r.Error!);
        return Result.Ok(ParseTabletsJson(r.Value!));
    }

    /// <summary>Discover the keyspace shards (sorted Ordinal, e.g. -80, 80-).</summary>
    private async Task<Result<List<string>>> GetShardsAsync(IReadOnlyList<NodeRecord> all, CancellationToken ct)
    {
        var t = await GetTabletsAsync(all, ct).ConfigureAwait(false);
        if (t.IsFail) return Result.Fail<List<string>>(t.Error!);
        var shards = t.Value!.Select(x => x.Shard).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (shards.Count == 0) return Result.Fail<List<string>>($"no shards found in keyspace {Keyspace}");
        return Result.Ok(shards);
    }

    // === vtgate SQL (mTLS, user nexus) ======================================
    /// <summary>
    /// Run SQL via a vtgate MySQL listener, from a tablet node (mysql client + TLS
    /// leaf present). targetKeyspace defaults to commerce; pass "commerce/-80" to
    /// scope to a shard. Returns trimmed stdout (-N tuples-only).
    /// </summary>
    private async Task<Result<string>> VtgateSqlAsync(IReadOnlyList<NodeRecord> all, string appPwd, string targetKeyspace, string sql, CancellationToken ct, bool tuplesOnly = true)
    {
        var tablet = ByRole(all, "tablet").FirstOrDefault();
        var vtgate = ByRole(all, "vtgate").FirstOrDefault(n => true);
        if (tablet is null || vtgate is null) return Result.Fail<string>("need at least one tablet (mysql client) + one vtgate to run SQL");
        var esc = sql.Replace("'", "'\\''");
        var flags = tuplesOnly ? "--batch --skip-column-names" : "--batch";
        // The vtgate listener requires a client cert (O13); the key is 0640
        // root:vitess so sudo is needed. MYSQL_PWD avoids argv exposure + warning.
        var cmd =
            $"sudo env MYSQL_PWD='{appPwd}' mysql --host={vtgate.Vmnet11} --port={VtgatePort} --user=nexus "
            + $"--ssl-mode=REQUIRED --ssl-cert={TlsDir}/server-cert.pem --ssl-key={TlsDir}/server-key.pem --ssl-ca={TlsDir}/ca.pem "
            + $"{flags} {ShellQuoteKeyspace(targetKeyspace)} -e '{esc}' 2>&1";
        var exec = await _ssh.ExecuteAsync(T(tablet.Vmnet11), cmd, SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {tablet.Name} failed: {exec.Error}");
        var outp = StripPwWarning(exec.Value!.Stdout);
        if (exec.Value.ExitCode != 0)
            return Result.Fail<string>($"mysql via vtgate {vtgate.Name} exit {exec.Value.ExitCode}: {Tail(outp, 300)}");
        return Result.Ok(outp.Trim());
    }

    // A shard-scoped keyspace ("commerce/80-") must be quoted for the shell.
    private static string ShellQuoteKeyspace(string ks) => ks.Length == 0 ? "" : $"'{ks}'";

    private static string StripPwWarning(string s) =>
        string.Join('\n', s.Split('\n').Where(l =>
            !l.Contains("Using a password on the command line", StringComparison.Ordinal)
            && !l.Contains("no verification of server certificate", StringComparison.Ordinal)));

    private async Task<bool> IsActiveAsync(string ip, string unit, CancellationToken ct)
    {
        var ping = await _ssh.ExecuteAsync(T(ip), $"systemctl is-active {unit} 2>/dev/null; true", SshTimeout, ct).ConfigureAwait(false);
        return ping.IsOk && ping.Value!.Stdout.Trim().StartsWith("active", StringComparison.Ordinal);
    }

    private async Task<bool> PortOpenAsync(string ip, int port, CancellationToken ct)
    {
        var p = await _ssh.ExecuteAsync(T(ip), $"(echo > /dev/tcp/127.0.0.1/{port}) 2>/dev/null && echo OPEN || echo SHUT", SshTimeout, ct).ConfigureAwait(false);
        return p.IsOk && p.Value!.Stdout.Contains("OPEN", StringComparison.Ordinal);
    }

    // === GetStatusAsync =====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ClusterStatus>(nodesR.Error!);
        var all = nodesR.Value!;

        var members = new List<ClusterMember>();

        // etcd topo nodes.
        foreach (var n in ByRole(all, "etcd"))
        {
            var alive = await IsActiveAsync(n.Vmnet11, EtcdSvc, cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, "etcd", alive ? "alive" : "failed"));
        }

        // control node (vtctld + vtorc).
        foreach (var n in ByRole(all, "control"))
        {
            var vtctld = await IsActiveAsync(n.Vmnet11, VtctldSvc, cancellationToken).ConfigureAwait(false);
            var vtorc = await IsActiveAsync(n.Vmnet11, VtorcSvc, cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, "control", vtctld && vtorc ? "alive" : vtctld ? "syncing" : "failed"));
        }

        // vtgate routers.
        foreach (var n in ByRole(all, "vtgate"))
        {
            var up = await IsActiveAsync(n.Vmnet11, VtgateSvc, cancellationToken).ConfigureAwait(false)
                     && await PortOpenAsync(n.Vmnet11, VtgatePort, cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, "router", up ? "alive" : "failed"));
        }

        // tablets via the topo (GetTablets) -> map uid host (vmnet10) back to node.
        var tablets = await GetTabletsAsync(all, cancellationToken).ConfigureAwait(false);
        if (tablets.IsOk)
        {
            foreach (var ti in tablets.Value!)
            {
                var node = ByVmnet10(all, ti.Vmnet10);
                members.Add(new ClusterMember(node?.Name ?? ti.Vmnet10, node?.Vmnet11 ?? ti.Vmnet10, ti.Role, "alive", ShardId: ti.Shard));
            }
            // Tablet nodes in vms.yaml that did NOT register in the topo = down.
            var seen = tablets.Value!.Select(t => ByVmnet10(all, t.Vmnet10)?.Name).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
            foreach (var n in ByRole(all, "tablet").Where(n => !seen.Contains(n.Name)))
            {
                var (_, k) = Classify(n.Name);
                members.Add(new ClusterMember(n.Name, n.Vmnet11, "replica", "failed", ShardId: $"shard{k}(unregistered)"));
            }
        }
        else
        {
            foreach (var n in ByRole(all, "tablet"))
                members.Add(new ClusterMember(n.Name, n.Vmnet11, "unknown", "failed", ShardId: null));
        }

        var shards = tablets.IsOk
            ? tablets.Value!.Select(t => t.Shard).Where(s => s.Length > 0).Distinct().ToList()
            : new List<string>();
        var overall = ComputeOverall(members, shards);
        var status = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, Leader: null, DateTimeOffset.UtcNow);
        _lastStatus = status;
        return Result.Ok(status);
    }

    private static string ComputeOverall(List<ClusterMember> members, IReadOnlyList<string> shards)
    {
        if (members.Count == 0) return "red";
        if (members.Any(m => m.Status == "failed")) return "red";
        // etcd quorum.
        var etcd = members.Where(m => m.Role == "etcd").ToList();
        if (etcd.Count == 0 || etcd.Count(e => e.Status == "alive") < (etcd.Count / 2 + 1)) return "red";
        // per shard: exactly 1 primary + >=2 replicas.
        foreach (var sh in shards)
        {
            var sm = members.Where(m => m.ShardId == sh).ToList();
            if (sm.Count(m => m.Role == "primary") != 1) return "red";
            if (sm.Count(m => m.Role is "replica" or "rdonly") < 2) return "yellow";
        }
        if (members.Any(m => m.Status == "syncing")) return "yellow";
        return "green";
    }

    // === HealthAsync ========================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<HealthReport>(nodesR.Error!);
        var all = nodesR.Value!;
        var probes = new List<HealthProbe>();

        // etcd quorum: the nexus-etcdctl wrapper carries all 3 endpoints, so a single
        // `endpoint health` from any etcd node reports the whole cluster. Count the
        // "is healthy" lines (NOT bare "healthy", which also matches "is unhealthy").
        var etcd = ByRole(all, "etcd");
        var etcdHealthy = 0;
        foreach (var e in etcd)
        {
            var h = await _ssh.ExecuteAsync(T(e.Vmnet11), $"sudo {EtcdctlWrapper} endpoint health 2>&1 | grep -c 'is healthy'; true", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (h.IsOk && int.TryParse(h.Value!.Stdout.Trim().Split('\n').LastOrDefault()?.Trim(), out var c) && c > 0) { etcdHealthy = c; break; }
        }
        probes.Add(new HealthProbe("etcd-quorum", "topo", etcdHealthy >= (etcd.Count / 2 + 1) ? "green" : "red",
            $"{etcdHealthy}/{etcd.Count} healthy", $">= {etcd.Count / 2 + 1} (majority)"));

        // control: vtctld active + vtorc healthy (no problems).
        var control = ByRole(all, "control").FirstOrDefault();
        if (control is not null)
        {
            var vtctld = await IsActiveAsync(control.Vmnet11, VtctldSvc, cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("vtctld", control.Name, vtctld ? "green" : "red", vtctld ? "active" : "down", "active"));
            var vtorcH = await _ssh.ExecuteAsync(T(control.Vmnet11),
                $"curl -fsS http://127.0.0.1:{VtorcPort}/debug/health 2>/dev/null; echo; curl -fsS http://127.0.0.1:{VtorcPort}/api/problems 2>/dev/null", SshTimeout, cancellationToken).ConfigureAwait(false);
            var healthy = vtorcH.IsOk && Regex.IsMatch(vtorcH.Value!.Stdout, "\"Healthy\"\\s*:\\s*true");
            var noProblems = vtorcH.IsOk && (vtorcH.Value!.Stdout.Contains("null", StringComparison.Ordinal) || !vtorcH.Value.Stdout.Contains("Analysis", StringComparison.Ordinal));
            probes.Add(new HealthProbe("vtorc", control.Name, healthy && noProblems ? "green" : healthy ? "yellow" : "red",
                healthy ? (noProblems ? "healthy, no problems" : "healthy, problems reported") : "unhealthy", "healthy + no analysis problems"));
        }

        // vtgate routers: active + :15306.
        foreach (var v in ByRole(all, "vtgate"))
        {
            var up = await IsActiveAsync(v.Vmnet11, VtgateSvc, cancellationToken).ConfigureAwait(false)
                     && await PortOpenAsync(v.Vmnet11, VtgatePort, cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("vtgate", v.Name, up ? "green" : "red", up ? "active + :15306" : "down", "active + listener open"));
        }

        // per-shard 1 PRIMARY + 2 REPLICA (from the topo).
        var tablets = await GetTabletsAsync(all, cancellationToken).ConfigureAwait(false);
        if (tablets.IsFail) return Result.Fail<HealthReport>(tablets.Error!);
        var shards = tablets.Value!.Select(t => t.Shard).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        foreach (var sh in shards)
        {
            var sm = tablets.Value!.Where(t => t.Shard == sh).ToList();
            var primaries = sm.Count(t => t.Role == "primary");
            var replicas = sm.Count(t => t.Role is "replica" or "rdonly");
            probes.Add(new HealthProbe("shard-primary", $"{Keyspace}/{sh}", primaries == 1 ? "green" : "red", $"{primaries} PRIMARY", "exactly 1"));
            probes.Add(new HealthProbe("shard-replicas", $"{Keyspace}/{sh}", replicas >= 2 ? "green" : replicas == 1 ? "yellow" : "red", $"{replicas} REPLICA", ">= 2"));
        }

        // operator-auth + sharding proof (SQL via vtgate).
        var appPwd = await AppPwdAsync(cancellationToken).ConfigureAwait(false);
        if (appPwd.IsOk)
        {
            var who = await VtgateSqlAsync(all, appPwd.Value!, Keyspace, "SELECT 1", cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("operator-auth", "nexus@vtgate", who.IsOk && who.Value!.Trim() == "1" ? "green" : "red",
                who.IsOk ? "SELECT 1 = 1 over mTLS" : "unreachable", "vtgate :15306 mTLS round-trip"));

            // Sharding proof: each shard non-empty (a single logical table split).
            var perShard = new List<string>();
            var shardOk = true;
            foreach (var sh in shards)
            {
                var c = await VtgateSqlAsync(all, appPwd.Value!, $"{Keyspace}/{sh}", $"SELECT COUNT(*) FROM {ShardTable}", cancellationToken).ConfigureAwait(false);
                var n = c.IsOk && long.TryParse(c.Value!.Trim(), out var cc) ? cc : -1;
                perShard.Add($"{sh}={(n < 0 ? "?" : n.ToString(CultureInfo.InvariantCulture))}");
                if (n <= 0) shardOk = false;
            }
            probes.Add(new HealthProbe("sharding", $"{Keyspace}.{ShardTable}", shardOk ? "green" : "yellow",
                string.Join(" ", perShard), "each shard > 0 rows (hash vindex split)"));
        }
        else
        {
            probes.Add(new HealthProbe("operator-auth", "nexus@vtgate", "yellow", "Vault unavailable", appPwd.Error));
        }

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync (Shards POPULATED -- the sharded showcase) ===========
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<TopologySnapshot>(nodesR.Error!);
        var all = nodesR.Value!;

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var members = status.Value!.Members;

        var nodes = members
            .Select(m => new TopologyNode(m.Hostname, m.ShardId is null ? m.Role : $"{m.ShardId}/{m.Role}", m.Status, m.ReplicationLagSeconds))
            .ToList();

        // One TopologyShard per keyspace shard (real topo shards only -- skip the
        // "(unregistered)" sentinel set for tablet nodes that aren't in the topo).
        var shardIds = members.Select(m => m.ShardId)
            .Where(s => s is not null && !s.Contains("unregistered", StringComparison.Ordinal))
            .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        var shards = new List<TopologyShard>();
        foreach (var sh in shardIds)
        {
            var sm = members.Where(m => m.ShardId == sh).ToList();
            var primary = sm.FirstOrDefault(m => m.Role == "primary")?.Hostname ?? "(none)";
            var replicas = sm.Where(m => m.Role is "replica" or "rdonly").Select(m => m.Hostname).ToList();
            shards.Add(new TopologyShard(sh!, primary, replicas, SlotRange: HumanRange(sh!)));
        }

        return Result.Ok(new TopologySnapshot(ClusterName, nodes, shards, DateTimeOffset.UtcNow));
    }

    // Human-friendly key-range description for the hash vindex shard boundaries.
    private static string HumanRange(string shard) => shard switch
    {
        "-80" => "keyrange [00, 80) -- hash(customer_id) < 0x80",
        "80-" => "keyrange [80, ff] -- hash(customer_id) >= 0x80",
        _ => $"keyrange {shard}"
    };

    // === FailoverAsync (graceful PlannedReparentShard to a healthy replica) ==
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<FailoverResult>(nodesR.Error!);
        var all = nodesR.Value!;

        var shardsR = await GetShardsAsync(all, cancellationToken).ConfigureAwait(false);
        if (shardsR.IsFail) return Result.Fail<FailoverResult>(shardsR.Error!);
        var shards = shardsR.Value!;

        // Resolve target shard: --target may name a shard ("-80"/"80-") or a tablet node.
        string targetShard;
        if (!string.IsNullOrWhiteSpace(request.TargetNode))
        {
            if (shards.Contains(request.TargetNode)) targetShard = request.TargetNode!;
            else
            {
                var named = all.FirstOrDefault(n => string.Equals(n.Name, request.TargetNode, StringComparison.OrdinalIgnoreCase));
                if (named is null || Classify(named.Name).Role != "tablet")
                    return Result.Fail<FailoverResult>($"--target '{request.TargetNode}' is neither a shard ({string.Join("/", shards)}) nor a tablet node");
                var (_, k) = Classify(named.Name);
                if (k < 1 || k > shards.Count) return Result.Fail<FailoverResult>($"could not map {named.Name} to a shard");
                targetShard = shards[k - 1];
            }
        }
        else
        {
            targetShard = shards[0];
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Pre-flight: read the current primary (authoritative via GetShard).
        var before = await GetTabletsAsync(all, cancellationToken, targetShard).ConfigureAwait(false);
        if (before.IsFail) return Result.Fail<FailoverResult>(before.Error!);
        var oldPrimaryUidR = await ShardPrimaryUidAsync(all, targetShard, cancellationToken).ConfigureAwait(false);
        if (oldPrimaryUidR.IsFail) return Result.Fail<FailoverResult>(oldPrimaryUidR.Error!);
        var oldPrimaryUid = oldPrimaryUidR.Value;
        var oldPrimary = before.Value!.FirstOrDefault(t => t.Uid == oldPrimaryUid);
        if (oldPrimary is null) return Result.Fail<FailoverResult>($"no current PRIMARY found in {Keyspace}/{targetShard}");

        // Choose a healthy REPLICA in the shard as the new primary.
        var newReplica = before.Value!.FirstOrDefault(t => t.Uid != oldPrimaryUid && t.Role is "replica");
        if (newReplica is null) return Result.Fail<FailoverResult>($"no healthy REPLICA in {Keyspace}/{targetShard} to promote");
        var newAlias = $"{Cell}-{newReplica.Uid}";
        var oldNode = ByVmnet10(all, oldPrimary.Vmnet10);
        var newNode = ByVmnet10(all, newReplica.Vmnet10);
        var preFlightAt = sw.Elapsed;

        // Inject: PlannedReparentShard (graceful -- demotes old primary, promotes the chosen replica).
        var prs = await VtctldAsync(all, $"PlannedReparentShard {Keyspace}/{targetShard} --new-primary {newAlias}", cancellationToken, BackupTimeout).ConfigureAwait(false);
        var injectedAt = sw.Elapsed;
        if (prs.IsFail)
            return Result.Fail<FailoverResult>($"PlannedReparentShard {Keyspace}/{targetShard} -> {newAlias} failed: {prs.Error}");

        // Confirm the new primary is authoritative in the shard record.
        string? confirmed = null;
        var newPrimaryAt = TimeSpan.Zero;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            var cur = await ShardPrimaryUidAsync(all, targetShard, cancellationToken).ConfigureAwait(false);
            if (cur.IsOk && cur.Value == newReplica.Uid) { confirmed = newAlias; newPrimaryAt = sw.Elapsed; break; }
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();

        var rto = confirmed is not null ? newPrimaryAt - injectedAt : TimeSpan.Zero;
        return Result.Ok(new FailoverResult(
            Scenario: $"vitess-planned-reparent ({Keyspace}/{targetShard})",
            OriginalPrimary: $"{targetShard}/{oldNode?.Name ?? oldPrimary.Vmnet10} (nexus-{oldPrimaryUid})",
            NewPrimary: confirmed is not null ? $"{targetShard}/{newNode?.Name ?? newReplica.Vmnet10} ({newAlias})" : null,
            Rto: rto,
            Recovery: confirmed is not null ? "recovered" : "failed",
            RecoveryHint: confirmed is null
                ? $"PlannedReparentShard issued but the new primary was not confirmed in the shard record within the deadline; check `vtctldclient GetShard {Keyspace}/{targetShard}` + VTOrc (:16000)"
                : "old PRIMARY was demoted to REPLICA in place (no recovery step needed; re-run with --target to fail back)",
            Timeline: new FailoverTimeline(preFlightAt, injectedAt, newPrimaryAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    private async Task<Result<int>> ShardPrimaryUidAsync(IReadOnlyList<NodeRecord> all, string shard, CancellationToken ct)
    {
        var r = await VtctldAsync(all, $"GetShard {Keyspace}/{shard}", ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<int>(r.Error!);
        var uid = ParseShardPrimaryUid(r.Value!);
        if (uid is null) return Result.Fail<int>($"could not read primary_alias from GetShard {Keyspace}/{shard}");
        return Result.Ok(uid.Value);
    }

    // === ScaleOutAddAsync (re-join a removed tablet to its shard) ============
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var all = nodesR.Value!;

        var shardsR = await GetShardsAsync(all, cancellationToken).ConfigureAwait(false);
        if (shardsR.IsFail) return Result.Fail<ScaleOutResult>(shardsR.Error!);
        var shards = shardsR.Value!;
        var targetShard = !string.IsNullOrWhiteSpace(request.ShardId) && shards.Contains(request.ShardId) ? request.ShardId! : shards[0];
        var shardIdx = shards.IndexOf(targetShard) + 1; // 1-based, matches vitess-shard<K>-tablet

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Tablet nodes belonging to the target shard, by name (vitess-shard<K>-tablet-*).
        var shardNodes = ByRole(all, "tablet").Where(n => Classify(n.Name).ShardIndex == shardIdx).ToList();
        if (shardNodes.Count == 0) return Result.Fail<ScaleOutResult>($"no tablet nodes map to shard {targetShard} (index {shardIdx})");

        // Candidate = a shard node whose vttablet is NOT active (i.e. previously removed).
        NodeRecord? candidate = null;
        foreach (var n in shardNodes)
            if (!await IsActiveAsync(n.Vmnet11, VttabletSvc, cancellationToken).ConfigureAwait(false)) { candidate = n; break; }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                $"all provisioned tablets in shard {targetShard} are already serving. To grow the shard, provision a new tablet "
                + "(apply-on-demand, ADR-0020): add a VM + overlays in nexus-infra-vitess/terraform/envs/vitess, "
                + "`pwsh -File scripts/vitess.ps1 apply`, then re-run `scale-out add`.");

        // Bring mysqld + vttablet back up -> vttablet re-registers in the topo.
        var start = await _ssh.ExecuteAsync(T(candidate.Vmnet11),
            $"sudo systemctl start {MysqlctldSvc}.service && sleep 3 && sudo systemctl start {VttabletSvc}.service && echo STARTED",
            BackupTimeout, cancellationToken).ConfigureAwait(false);
        if (start.IsFail || !start.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to start mysqlctld+vttablet on {candidate.Name}: {(start.IsFail ? start.Error : Tail(start.Value!.Stdout + start.Value.Stderr, 220))}");

        // Wait for the tablet to re-appear in the shard topo as a REPLICA.
        var joined = false;
        var deadline = sw.Elapsed + JoinDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var t = await GetTabletsAsync(all, cancellationToken, targetShard).ConfigureAwait(false);
            if (t.IsOk && t.Value!.Any(x => x.Vmnet10 == candidate.Vmnet10)) { joined = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: joined ? "ok" : "partial",
            OutcomeReason: joined
                ? $"{candidate.Name} re-joined shard {targetShard} as a REPLICA (vttablet re-registered in topo; mysqld resumes replication)"
                : $"{candidate.Name} started but has not re-registered in {Keyspace}/{targetShard} yet (check vttablet :{VttabletStatusPort})",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === ScaleOutRemoveAsync (DeleteTablets + stop services) ================
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var node = all.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not in the vitess cluster");
        var (role, _) = Classify(node.Name);
        if (role != "tablet")
            return Result.Fail<ScaleOutResult>($"{node.Name} is a {role} node, not a tablet; remove etcd/vtgate/control by deprovisioning the VM (terraform), not via scale-out.");

        // Find the tablet record (uid + shard) for this node.
        var tablets = await GetTabletsAsync(all, cancellationToken).ConfigureAwait(false);
        if (tablets.IsFail) return Result.Fail<ScaleOutResult>(tablets.Error!);
        var ti = tablets.Value!.FirstOrDefault(t => t.Vmnet10 == node.Vmnet10);
        if (ti is null) return Result.Fail<ScaleOutResult>($"{node.Name} ({node.Vmnet10}) is not currently registered in the topo");
        if (ti.Role == "primary" && request.Drain)
            return Result.Fail<ScaleOutResult>(
                $"{node.Name} is the PRIMARY of {Keyspace}/{ti.Shard}; fail it over first "
                + $"(`nexus failover-test cluster vitess --target {node.Name}`) before removing -- removing the PRIMARY would force an unplanned reparent.");
        // Guard: keep >= 2 surviving tablets in the shard (so 1 primary + >=1 replica remain).
        var survivors = tablets.Value!.Count(t => t.Shard == ti.Shard && t.Uid != ti.Uid);
        if (request.Drain && survivors < 2)
            return Result.Fail<ScaleOutResult>($"removing {node.Name} would leave {survivors} tablet(s) in {Keyspace}/{ti.Shard}; need >= 2 surviving. Bring another tablet up first.");

        var alias = $"{Cell}-{ti.Uid}";
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Stop the tablet's services first (vttablet then mysqld), then DeleteTablets
        // from the topo so the shard record drops the member cleanly.
        await _ssh.ExecuteAsync(T(node.Vmnet11), $"sudo systemctl stop {VttabletSvc}.service {MysqlctldSvc}.service 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);
        var del = await VtctldAsync(all, $"DeleteTablets --allow-primary=false {alias}", cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (del.IsFail)
            return Result.Fail<ScaleOutResult>($"DeleteTablets {alias} failed (services stopped; topo record may remain): {del.Error}");

        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"removed {node.Name} ({alias}) from {Keyspace}/{ti.Shard} (vttablet+mysqld stopped, topo record deleted; re-add via `scale-out add --shard {ti.Shard}`)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupTakeAsync (logical mysqldump per shard, from the shard primary) =
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<BackupResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var dbaPwd = await DbaPwdAsync(cancellationToken).ConfigureAwait(false);
        if (dbaPwd.IsFail) return Result.Fail<BackupResult>(dbaPwd.Error!);
        var shardsR = await GetShardsAsync(all, cancellationToken).ConfigureAwait(false);
        if (shardsR.IsFail) return Result.Fail<BackupResult>(shardsR.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"vitess-backup-{startedAt:yyyyMMdd-HHmmss}"
            : $"vitess-{Sanitize(request.Tag)}-{startedAt:yyyyMMdd-HHmmss}";

        long totalSize = 0;
        var perShard = new List<string>();
        foreach (var sh in shardsR.Value!)
        {
            var primary = await PrimaryNodeAsync(all, sh, cancellationToken).ConfigureAwait(false);
            if (primary.IsFail) { sw.Stop(); return Result.Fail<BackupResult>(primary.Error!); }
            var (node, uid) = primary.Value;
            var sock = $"{DataRoot}/vt_{uid:D10}/mysql.sock";
            var dir = $"{BackupRoot}/{backupId}";
            var file = $"{dir}/{ShardFile(sh)}.sql.gz";
            // mysqldump the shard's portion of `commerce` (the customer table) as vt_dba
            // over the local socket. --single-transaction = consistent, no locks. Dump
            // to a temp file first so a failed dump (or empty gz) can't masquerade as a
            // valid backup -- require the DDL marker + non-zero size before sealing.
            var script =
                $"sudo mkdir -p {dir}; TMP=$(mktemp); "
                + $"sudo env MYSQL_PWD='{dbaPwd.Value}' mysqldump --socket={sock} -u vt_dba "
                + $"--single-transaction --no-tablespaces --skip-add-locks --set-gtid-purged=OFF {MysqlDb} {ShardTable} 2>/tmp/vdump.err > \"$TMP\"; RC=$?; "
                + $"if [ $RC -ne 0 ] || ! grep -q 'CREATE TABLE' \"$TMP\"; then echo DUMP_FAIL; cat /tmp/vdump.err; rm -f \"$TMP\" /tmp/vdump.err; exit 1; fi; "
                + $"gzip -c \"$TMP\" | sudo tee {file} >/dev/null; rm -f \"$TMP\" /tmp/vdump.err; "
                + $"sudo stat -c %s {file}";
            var exec = await _ssh.ExecuteAsync(T(node.Vmnet11), script, BackupTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail) { sw.Stop(); return Result.Fail<BackupResult>($"mysqldump for shard {sh} on {node.Name} failed: {exec.Error}"); }
            if (exec.Value!.Stdout.Contains("DUMP_FAIL", StringComparison.Ordinal))
            { sw.Stop(); return Result.Fail<BackupResult>($"mysqldump for shard {sh} on {node.Name} failed: {Tail(exec.Value.Stdout + exec.Value.Stderr, 280)}"); }
            var sizeLine = exec.Value.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (!long.TryParse(sizeLine.Trim(), out var size) || size <= 0)
            { sw.Stop(); return Result.Fail<BackupResult>($"mysqldump for shard {sh} produced no archive: {Tail(exec.Value.Stdout + exec.Value.Stderr, 240)}"); }
            totalSize += size;
            perShard.Add($"{sh}@{node.Name} ({size}B)");
        }
        sw.Stop();
        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{BackupRoot}/{backupId}/<shard>.sql.gz node-local on each shard primary: {string.Join(", ", perShard)} (logical mysqldump; engine-native Backup = 0.O.1)",
            SizeBytes: totalSize,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupRestoreAsync (reload each shard dump into a verify schema) =====
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<RestoreResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var dbaPwd = await DbaPwdAsync(cancellationToken).ConfigureAwait(false);
        if (dbaPwd.IsFail) return Result.Fail<RestoreResult>(dbaPwd.Error!);
        var shardsR = await GetShardsAsync(all, cancellationToken).ConfigureAwait(false);
        if (shardsR.IsFail) return Result.Fail<RestoreResult>(shardsR.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        const string verifyDb = "commerce_restore_verify";
        long totalRows = 0;
        foreach (var sh in shardsR.Value!)
        {
            var primary = await PrimaryNodeAsync(all, sh, cancellationToken).ConfigureAwait(false);
            if (primary.IsFail) { sw.Stop(); return Result.Fail<RestoreResult>(primary.Error!); }
            var (node, uid) = primary.Value;
            var sock = $"{DataRoot}/vt_{uid:D10}/mysql.sock";
            var file = $"{BackupRoot}/{request.BackupId}/{ShardFile(sh)}.sql.gz";
            var my = $"sudo env MYSQL_PWD='{dbaPwd.Value}' mysql --socket={sock} -u vt_dba";
            // Reload the dump into a throwaway verify DB (sql_log_bin=0 so it never
            // replicates to the shard's replicas), count rows, then drop it.
            var script =
                $"sudo test -s {file} || {{ echo MISSING-ARCHIVE; exit 9; }}; "
                + $"{my} -e \"SET sql_log_bin=0; DROP DATABASE IF EXISTS {verifyDb}; CREATE DATABASE {verifyDb};\" && "
                + $"( echo 'SET sql_log_bin=0;'; sudo gunzip -c {file} ) | {my} {verifyDb} && "
                + $"{my} -N -e \"SELECT COUNT(*) FROM {verifyDb}.{ShardTable}\"; "
                + $"{my} -e \"SET sql_log_bin=0; DROP DATABASE IF EXISTS {verifyDb};\" >/dev/null 2>&1";
            var exec = await _ssh.ExecuteAsync(T(node.Vmnet11), script, BackupTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail) { sw.Stop(); return Result.Fail<RestoreResult>($"restore-verify for shard {sh} on {node.Name} failed: {exec.Error}"); }
            var outp = StripPwWarning(exec.Value!.Stdout);
            if (outp.Contains("MISSING-ARCHIVE", StringComparison.Ordinal))
            { sw.Stop(); return Result.Fail<RestoreResult>($"backup '{request.BackupId}' shard {sh} archive not found on its current primary {node.Name} ({file}). It may have been taken before a failover; re-take, or restore on the node that holds it."); }
            var rowsLine = outp.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => long.TryParse(l.Trim(), out _));
            if (rowsLine is null) { sw.Stop(); return Result.Fail<RestoreResult>($"restore-verify for shard {sh} did not confirm a row count: {Tail(outp, 240)}"); }
            totalRows += long.Parse(rowsLine.Trim(), CultureInfo.InvariantCulture);
        }
        sw.Stop();
        return Result.Ok(new RestoreResult(request.BackupId, totalRows, sw.Elapsed, startedAt));
    }

    private async Task<Result<(NodeRecord Node, int Uid)>> PrimaryNodeAsync(IReadOnlyList<NodeRecord> all, string shard, CancellationToken ct)
    {
        var uidR = await ShardPrimaryUidAsync(all, shard, ct).ConfigureAwait(false);
        if (uidR.IsFail) return Result.Fail<(NodeRecord, int)>(uidR.Error!);
        var tablets = await GetTabletsAsync(all, ct, shard).ConfigureAwait(false);
        if (tablets.IsFail) return Result.Fail<(NodeRecord, int)>(tablets.Error!);
        var ti = tablets.Value!.FirstOrDefault(t => t.Uid == uidR.Value);
        if (ti is null) return Result.Fail<(NodeRecord, int)>($"primary uid {uidR.Value} not in topo for shard {shard}");
        var node = ByVmnet10(all, ti.Vmnet10);
        if (node is null) return Result.Fail<(NodeRecord, int)>($"primary {ti.Vmnet10} of shard {shard} not in vms.yaml");
        return Result.Ok((node, ti.Uid));
    }

    private static string ShardFile(string shard) => Regex.Replace(shard, "[^A-Za-z0-9]", "_");

    // === RotateCertAsync (per-node Vault PKI; gRPC + vtgate listener reload) ==
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<CertRotationResult>(nodesR.Error!);
        var all = nodesR.Value!;

        // Order to minimise disruption: etcd (one at a time, quorum-tolerant) ->
        // tablet replicas -> tablet primaries (last) -> vtgate -> control.
        var tablets = await GetTabletsAsync(all, cancellationToken).ConfigureAwait(false);
        var primaryVmnet10 = tablets.IsOk
            ? tablets.Value!.Where(t => t.Role == "primary").Select(t => t.Vmnet10).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var order = new List<NodeRecord>();
        order.AddRange(ByRole(all, "etcd"));
        order.AddRange(ByRole(all, "tablet").Where(n => !primaryVmnet10.Contains(n.Vmnet10)));
        order.AddRange(ByRole(all, "tablet").Where(n => primaryVmnet10.Contains(n.Vmnet10)));
        order.AddRange(ByRole(all, "vtgate"));
        order.AddRange(ByRole(all, "control"));

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var node in order)
        {
            var (role, _) = Classify(node.Name);
            var dir = role == "etcd" ? EtcdTlsDir : TlsDir;
            var group = role == "etcd" ? "etcd" : "vitess";

            var oldSerialExec = await _ssh.ExecuteAsync(T(node.Vmnet11),
                $"sudo openssl x509 -in {dir}/server-cert.pem -noout -serial 2>/dev/null | sed 's/serial=//'", SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.Stdout.Trim().Length > 0 ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            var cn = $"{node.Name}.vitess.nexus.lab";
            var alts = role == "vtgate"
                ? $"{node.Name},{node.Name}.nexus.lab,{cn},vtgate.nexus.lab,localhost"
                : $"{node.Name},{node.Name}.nexus.lab,{cn},localhost";
            var ips = $"{node.Vmnet10},{node.Vmnet11},127.0.0.1";

            // Re-issue via the node's own Vault Agent token -> assemble bundle.pem ->
            // run the infra's split script (writes server-cert/server-key(PKCS#8)/ca)
            // -> restart the serving units. Reuses /usr/local/sbin/nexus-vitess-tls-split.sh.
            var issueCmd =
                $"T=$(sudo cat {AgentToken} 2>/dev/null); "
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

            // Assemble cert + key + ca into bundle.pem (the split-script input shape:
            // leaf cert, then key, then CA), then split + restart.
            var bundle = cert.TrimEnd() + "\n" + key.TrimEnd() + "\n" + ca.TrimEnd() + "\n";
            var restartUnits = role switch
            {
                "etcd" => $"{EtcdSvc}.service",
                "control" => $"{VtctldSvc}.service {VtorcSvc}.service",
                "vtgate" => $"{VtgateSvc}.service",
                // tablet: restart vttablet only (gRPC + db-client certs); mysqld stays
                // up so the PRIMARY is never demoted (mysqld-wire cert reload deferred).
                _ => $"{VttabletSvc}.service"
            };
            var writeCmd =
                $"echo {B64(bundle)}|base64 -d|sudo tee {dir}/bundle.pem >/dev/null; "
                + $"sudo {SplitScript} {dir} {group} >/dev/null 2>&1; "
                + $"sudo systemctl restart {restartUnits}; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(T(node.Vmnet11), writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stdout + writeExec.Value.Stderr, 220)}"));
                continue;
            }
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: null));
            await Task.Delay(TimeSpan.FromSeconds(role == "etcd" ? 6 : 4), cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === AclAsync (vtgate static-auth users in vtgate_creds.json) ===========
    private const string VtgateCreds = "/etc/nexus-vitess/vtgate_creds.json";

    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<AclSnapshot>(nodesR.Error!);
        var all = nodesR.Value!;
        var vtgates = ByRole(all, "vtgate");
        if (vtgates.Count == 0) return Result.Fail<AclSnapshot>("no vtgate node found");
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var read = await _ssh.ExecuteAsync(T(vtgates[0].Vmnet11), $"sudo cat {VtgateCreds} 2>/dev/null", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (read.IsFail || read.Value!.ExitCode != 0)
                return Result.Fail<AclSnapshot>($"could not read {VtgateCreds} on {vtgates[0].Name}: {(read.IsFail ? read.Error : Tail(read.Value!.Stderr, 200))}");
            var users = ParseVtgateCreds(read.Value!.Stdout);
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
                users = users.Where(u => string.Equals(u.Name, operation.User, StringComparison.OrdinalIgnoreCase)).ToList();
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user.");
            if (string.Equals(operation.User, "nexus", StringComparison.OrdinalIgnoreCase) && verb == "revoke")
                return Result.Fail<AclSnapshot>("refusing to revoke the built-in `nexus` operator user (it is the cluster's app/operator identity).");

            // Read current creds from the first vtgate (authoritative), mutate, then
            // write to BOTH vtgate nodes + reload (restart) so the front door is
            // consistent across the RR-DNS pair.
            var read = await _ssh.ExecuteAsync(T(vtgates[0].Vmnet11), $"sudo cat {VtgateCreds} 2>/dev/null", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (read.IsFail || read.Value!.ExitCode != 0)
                return Result.Fail<AclSnapshot>($"could not read {VtgateCreds}: {(read.IsFail ? read.Error : Tail(read.Value!.Stderr, 200))}");

            string newJson;
            try { newJson = MutateVtgateCreds(read.Value!.Stdout, operation.User!, verb == "grant"); }
            catch (Exception ex) { return Result.Fail<AclSnapshot>($"could not edit {VtgateCreds}: {ex.Message}"); }

            var b64 = B64(newJson);
            foreach (var v in vtgates)
            {
                var write = await _ssh.ExecuteAsync(T(v.Vmnet11),
                    $"echo {b64}|base64 -d|sudo tee {VtgateCreds} >/dev/null && sudo chown root:vitess {VtgateCreds} && sudo chmod 0640 {VtgateCreds} "
                    + $"&& sudo systemctl restart {VtgateSvc}.service && echo WROTE", SshTimeout, cancellationToken).ConfigureAwait(false);
                if (write.IsFail || !write.Value!.Stdout.Contains("WROTE", StringComparison.Ordinal))
                    return Result.Fail<AclSnapshot>($"acl {verb} write to {v.Name} failed: {(write.IsFail ? write.Error : Tail(write.Value!.Stdout + write.Value.Stderr, 200))}");
            }
            // vtgate needs a moment after restart before the listener accepts again.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return await AclAsync(new AclOperation("list", operation.User), cancellationToken).ConfigureAwait(false);
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    /// <summary>Parse vtgate_creds.json -> the configured MySQL users (the :15306 front door).</summary>
    internal static List<AclUser> ParseVtgateCreds(string json)
    {
        var users = new List<AclUser>();
        var obj = ExtractJsonObject(json);
        if (obj is null) return users;
        using var doc = JsonDocument.Parse(obj);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return users;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var userData = prop.Name;
            if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0)
            {
                var first = prop.Value[0];
                if (first.TryGetProperty("UserData", out var ud) && ud.GetString() is { Length: > 0 } u) userData = u;
            }
            users.Add(new AclUser(prop.Name, [$"vtgate MySQL listener login (UserData={userData})"], Enabled: true));
        }
        return users;
    }

    /// <summary>Add or remove a vtgate static-auth user, returning the new creds JSON.</summary>
    internal static string MutateVtgateCreds(string json, string user, bool add)
    {
        // Extract existing (username -> (password, userdata)) from the file.
        var entries = new Dictionary<string, (string Pwd, string UserData)>(StringComparer.Ordinal);
        var obj = ExtractJsonObject(json);
        if (obj is not null)
        {
            using var doc = JsonDocument.Parse(obj);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var pwd = ""; var ud = p.Name;
                    if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                    {
                        var f = p.Value[0];
                        if (f.TryGetProperty("Password", out var pw)) pwd = pw.GetString() ?? "";
                        if (f.TryGetProperty("UserData", out var u)) ud = u.GetString() ?? p.Name;
                    }
                    entries[p.Name] = (pwd, ud);
                }
        }

        if (add)
        {
            // Deterministic 32-hex placeholder password (the operator resets it out of band).
            var pwd = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(user + ":nexus-vitess")))[..32].ToLowerInvariant();
            entries[user] = (pwd, user);
        }
        else
        {
            entries.Remove(user);
        }

        // Re-emit the static-auth file shape: {"user":[{"Password":"..","UserData":".."}],..}.
        var sb = new StringBuilder();
        sb.Append("{\n");
        var i = 0;
        foreach (var kv in entries)
        {
            sb.Append("  ").Append(JsonStr(kv.Key)).Append(": [\n    { \"Password\": ")
              .Append(JsonStr(kv.Value.Pwd)).Append(", \"UserData\": ")
              .Append(JsonStr(kv.Value.UserData)).Append(" }\n  ]");
            sb.Append(++i < entries.Count ? ",\n" : "\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    // JSON string literal (quoted + minimally escaped) for the small creds file.
    private static string JsonStr(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // === ApplyChaosAsync (process-kill a tablet + rejoin) ===================
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ChaosOutcome>(nodesR.Error!);
        var all = nodesR.Value!;

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<ChaosOutcome>(status.Error!);
        var members = status.Value!.Members;

        // Default victim: a tablet REPLICA (safe -- the shard stays writable). An
        // explicit --target may name a PRIMARY to exercise VTOrc auto-reparent.
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? members.FirstOrDefault(m => string.Equals(m.Hostname, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : members.FirstOrDefault(m => m.Role == "replica");
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target tablet found");
        var (vrole, _) = Classify(victim.Hostname);
        if (vrole != "tablet")
            return Result.Fail<ChaosOutcome>($"{victim.Hostname} is a {vrole} node; chaos targets tablets (run against a replica or primary).");

        var target = T(victim.IpAddress);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        // process-kill SIGSTOPs ONE unit (nexus-chaos.sh). For a primary, freeze its
        // mysqld (nexus-mysqlctld) -> the tablet stops responding -> VTOrc auto-
        // reparents the shard to a replica. For a replica, freeze its vttablet.
        var killTarget = victim.Role == "primary" ? MysqlctldSvc : VttabletSvc;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? killTarget : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Hostname} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);
        // Ensure services are back (process-kill stops them).
        await _ssh.ExecuteAsync(target, $"sudo systemctl start {MysqlctldSvc}.service {VttabletSvc}.service 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(120);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
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

    // === CanResizeVm ========================================================
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false;
        var member = _lastStatus.Members.FirstOrDefault(m => string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        return member.Role != "primary"; // a shard PRIMARY resize would force a reparent
    }

    // === JSON + string helpers ==============================================
    internal static string? ExtractJsonArray(string s)
    {
        var i = s.IndexOf('[');
        var j = s.LastIndexOf(']');
        return (i >= 0 && j > i) ? s.Substring(i, j - i + 1) : null;
    }

    internal static string? ExtractJsonObject(string s)
    {
        var i = s.IndexOf('{');
        var j = s.LastIndexOf('}');
        return (i >= 0 && j > i) ? s.Substring(i, j - i + 1) : null;
    }

    private static string Sanitize(string s) => Regex.Replace(s, "[^A-Za-z0-9_]", "_");
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
