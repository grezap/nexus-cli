using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Lakehouse-tier adapter (nexus-cli v0.8.4, Phase 0.L, ADR-0025) — the FOURTH
/// non-data-tier adapter and the LAST big multi-component one. ONE component-aware
/// adapter spanning all three lakehouse components + the ZooKeeper ensemble over 16
/// VMs + 1 VRRP VIP (the Greg-locked decision: the operator runs a single
/// <c>nexus status lakehouse</c>; the adapter classifies by node name-prefix and
/// dispatches per component internally).
/// <para>
/// Nodes (vms.yaml cluster <c>lakehouse</c>, tier 08-spark): minio-1/2/3/4
/// (distributed EC:2 object store, RR DNS minio.nexus.lab, no VIP), iceberg-rest-1/2
/// (Project Nessie Iceberg REST catalog HA, RR DNS), iceberg-pg-1/2 (PG17 streaming
/// replication for the Nessie catalog, VRRP VIP <c>.151</c> iceberg-db.nexus.lab),
/// spark-master-1/2 (Spark standalone, ZK-elected HA, master URL
/// <c>spark://...140:7077,...153:7077</c>), spark-worker-1/2/3, zookeeper-1/2/3
/// (Apache ZK ensemble; the one deliberate Apache-ZK exception per ADR-0035; it
/// coordinates the Spark master election).
/// </para>
/// <para>
/// Access posture (mirrors <see cref="ObservabilityAdapter"/>, even simpler in
/// places): SSH-local-curl for every service endpoint with the node's own ca
/// (always self-consistent across CA generations — Nessie's mgmt health + Spark's
/// UI are plain HTTP, MinIO is HTTPS validated against
/// <c>/etc/nexus-minio/certs/CAs/nexus-ca.crt</c>); build-host
/// <see cref="INexusVaultClient"/> for KV (mount <c>nexus</c>, EVERY lakehouse
/// secret field is <c>value</c>); node SSH for systemctl / mc / psql / VIP /
/// keepalived / ZK / chaos. MinIO admin ops go through the on-node <c>mc</c> alias
/// <c>nexuslocal</c> (sudo /usr/local/bin/mc ...). No managed MinIO/Spark/
/// Iceberg/Nessie driver — NetArchTest-clean.
/// </para>
/// <para>
/// Live-contract note (the v0.8.1 Vault-greenfield casualty class, the same one the
/// observability tier hit — see ADR-0024 + ADR-0025 §Access): the tier was offline
/// during the 2026-06-18/19 Vault greenfield, so MinIO was re-certed to the new root
/// in the v0.8.3 session but Nessie/iceberg-pg/Spark/ZK are STILL old-root with their
/// vault-agent token absent. That produces a CROSS-TIER CA SPLIT (old-root Nessie's
/// JVM truststore cannot validate the new-root MinIO S3 leaf → Nessie
/// <c>/q/health</c> reports "Warehouses Object Stores" DOWN) and an iceberg-pg
/// replication split (pg-2 never re-seeded as a standby). The adapter probes the as-is
/// tier and reports both honestly RED; the trust re-cert + pg re-seed is a
/// Greg-authorized infra repair (handbook §3), not an adapter responsibility.
/// </para>
/// <list type="bullet">
///   <item>status = MinIO EC online + Nessie up + Spark master/workers + ZK quorum (16 nodes + roles + VIP holder).</item>
///   <item>health = MinIO /health/{live,cluster} + mc drives; Nessie /q/health per-check + /iceberg/v1/config; Spark master ALIVE + aliveworkers + worker /json/; ZK quorum; iceberg-pg replication; S3+catalog reachable.</item>
///   <item>topology = 16 nodes + roles + VIP .151 holder + ZK ensemble (leader/followers) + Spark master/standby. Not sharded.</item>
///   <item>failover = --direction spark-master (stop the ALIVE master → ZK promotes the STANDBY, ~30s — the live-proven HA drill) OR --direction iceberg-pg (catalog-DB VRRP cutover .151: stop keepalived on the primary → standby promoted → fence + pg_basebackup re-seed the old primary as a standby; 0.L.2.1 fencing hardening made pg_hba-on-both + a guarded reseed helper, so no split-brain + Nessie stays served).</item>
///   <item>scale-out = graceful actionable N/A (MinIO EC fixed at 4; Spark worker count = terraform/Packer in nexus-infra-lakehouse).</item>
///   <item>cert-rotate = vault-agent force-rerender per node + reload (MinIO BIG-BANG restart all 4 — a rolling 1-node re-cert breaks distributed inter-node mTLS; Spark/Nessie/ZK restart; iceberg-pg deferred to the PG DR runbook).</item>
///   <item>acl = MinIO policies + users via <c>mc admin policy/user</c> (root + app user protected).</item>
///   <item>chaos = nexus-chaos.sh process-kill a MinIO node (EC tolerates 1) / Spark worker / Nessie node + recover-to-green.</item>
///   <item>backup = mc mirror s3://warehouse round-trip (the Iceberg/Spark data bucket).</item>
/// </list>
/// </summary>
public sealed class LakehouseAdapter : IClusterAdapter
{
    private const string ClusterName = "lakehouse";
    private const string DisplayNameConst = "Lakehouse tier (MinIO EC + Iceberg/Nessie REST catalog + Spark ZK-HA + ZooKeeper)";
    private const string VmsCluster = "lakehouse";

    // VRRP VIP (vms.yaml virtual_ips) — the Iceberg catalog Postgres front door.
    private const string IcebergPgVip = "192.168.70.151";

    // MinIO mc alias + binary (sudo; /etc/nexus-minio is 0750 root) + node CA bundle.
    private const string McAlias = "nexuslocal";
    private const string McBin = "/usr/local/bin/mc";
    private const string MinioCa = "/etc/nexus-minio/certs/CAs/nexus-ca.crt";
    private const string WarehouseBucket = "warehouse";

    // Vault KV (mount nexus); EVERY lakehouse secret uses the field name "value".
    private const string KvMinioRootUser = "lakehouse/minio/root-user";
    private const string KvMinioRootPw = "lakehouse/minio/root-password";

    // MinIO root user + app user that acl revoke must never strip (operator + service identities).
    private static readonly string[] ProtectedMinioUsers = ["nexus-minio-root", "nexus-lakehouse-app"];

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CurlTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan McTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReseedTimeout = TimeSpan.FromSeconds(180);   // pg_basebackup re-seed of the demoted old primary

    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly char[] WsChars = [' ', '\t'];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    // Cached holders (for CanResizeVm after a status/health call).
    private string? _icebergPgVipHolder;
    private string? _sparkAliveLeader;

    /// <summary>
    /// Construct the lakehouse adapter over an SSH client + the vms.yaml catalog.
    /// <paramref name="vault"/> is nullable — MinIO-root reads (mc / acl) need the
    /// operator token, so those verbs fail-soft when it is absent; every other verb
    /// works SSH-only.
    /// </summary>
    public LakehouseAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
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

    // === per-role service contract (from the live probe 2026-06-24) =========
    /// <summary>Static per-role facts: the systemd unit, HTTP(S) port, TLS dir, scheme.</summary>
    internal sealed record RoleSpec(string Role, string Unit, int Port, string TlsDir, bool Https);

    private static readonly RoleSpec MinioSpec = new("minio", "nexus-minio", 9000, "/etc/nexus-minio/certs", true);
    private static readonly RoleSpec NessieSpec = new("nessie", "nexus-nessie", 19120, "/etc/nexus-iceberg-rest/tls", true);   // app port (REST/Iceberg)
    private const int NessieMgmtPort = 9000;                                                                                    // Quarkus mgmt (/q/health), plain HTTP
    private static readonly RoleSpec IcebergPgSpec = new("iceberg-pg", "postgresql@17-main", 5432, "/etc/nexus-iceberg-pg/tls", true);
    private static readonly RoleSpec SparkMasterSpec = new("spark-master", "nexus-spark-master", 8080, "", false);              // UI/REST plain HTTP, RPC = shared-secret
    private static readonly RoleSpec SparkWorkerSpec = new("spark-worker", "nexus-spark-worker", 8081, "", false);
    private static readonly RoleSpec ZookeeperSpec = new("zookeeper", "nexus-zookeeper", 2181, "", false);

    /// <summary>Map a vms.yaml node name to its lakehouse role.</summary>
    internal static string ClassifyRole(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("minio-", StringComparison.Ordinal)) return "minio";
        if (n.StartsWith("iceberg-rest-", StringComparison.Ordinal)) return "nessie";
        if (n.StartsWith("iceberg-pg-", StringComparison.Ordinal)) return "iceberg-pg";
        if (n.StartsWith("spark-master-", StringComparison.Ordinal)) return "spark-master";
        if (n.StartsWith("spark-worker-", StringComparison.Ordinal)) return "spark-worker";
        if (n.StartsWith("zookeeper-", StringComparison.Ordinal)) return "zookeeper";
        return "other";
    }

    /// <summary>Resolve the <see cref="RoleSpec"/> for a role (unknown falls back to MinIO — never used at runtime).</summary>
    private static RoleSpec SpecFor(string role) => role switch
    {
        "minio" => MinioSpec,
        "nessie" => NessieSpec,
        "iceberg-pg" => IcebergPgSpec,
        "spark-master" => SparkMasterSpec,
        "spark-worker" => SparkWorkerSpec,
        "zookeeper" => ZookeeperSpec,
        _ => MinioSpec
    };

    /// <summary>Build an SSH target for a node IP with the adapter's operator key/user.</summary>
    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    /// <summary>Load the lakehouse cluster's nodes from vms.yaml, ordinal-sorted by name (stable role slicing).</summary>
    private Result<List<NodeRecord>> Nodes()
    {
        var cluster = _catalog.GetCluster(VmsCluster);
        if (cluster.IsFail) return Result.Fail<List<NodeRecord>>(cluster.Error!);
        var nodes = cluster.Value!.Nodes.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (nodes.Count == 0) return Result.Fail<List<NodeRecord>>($"vms.yaml cluster '{VmsCluster}' has no nodes");
        return Result.Ok(nodes);
    }

    /// <summary>Slice the nodes belonging to one role (<see cref="ClassifyRole"/>), ordinal-sorted by name.</summary>
    private static List<NodeRecord> Role(List<NodeRecord> all, string role) =>
        all.Where(n => ClassifyRole(n.Name) == role).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

    // === SSH-local-curl (the access posture) ===============================
    /// <summary>
    /// curl a service endpoint locally on its own node. <paramref name="https"/>
    /// selects scheme; <paramref name="caPath"/> validates the leaf with the node's
    /// own ca (null = -k for the plain-HTTP / self-signed loopback paths). Host
    /// defaults to 127.0.0.1 but Spark's UI binds to the node's vmnet11 IP, so the
    /// caller passes that for Spark. Returns (httpCode, body).
    /// </summary>
    private async Task<(int Code, string Body)> CurlAsync(string ip, string host, int port, bool https, string? caPath, string path, string? basicAuth, CancellationToken ct)
    {
        var scheme = https ? "https" : "http";
        var ca = https ? (caPath is not null ? $"--cacert {caPath} " : "-k ") : "";
        var auth = string.IsNullOrEmpty(basicAuth) ? "" : $"-u '{basicAuth}' ";
        var cmd = $"sudo curl -sS --max-time 8 {ca}{auth}{scheme}://{host}:{port}{path} -w '\\n__HTTP_%{{http_code}}__' 2>/dev/null";
        var r = await _ssh.ExecuteAsync(T(ip), cmd, CurlTimeout, ct).ConfigureAwait(false);
        if (r.IsFail || r.Value is null) return (0, "");
        var raw = r.Value.Stdout;
        var m = Regex.Match(raw, @"__HTTP_(\d+)__\s*$");
        var code = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var body = m.Success ? raw.Substring(0, m.Index) : raw;
        return (code, body.Trim());
    }

    /// <summary><c>systemctl is-active &lt;unit&gt;</c> on a node → true if "active".</summary>
    private async Task<bool> IsActiveAsync(string ip, string unit, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip), $"systemctl is-active {unit} 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim() == "active";
    }

    /// <summary>iceberg-pg role via <c>pg_is_in_recovery()</c> → "primary" | "replica" | "".</summary>
    private async Task<string> PgRoleAsync(string ip, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip), "sudo -u postgres psql -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        var v = r.IsOk ? r.Value!.Stdout.Trim() : "";
        return v == "f" ? "primary" : v == "t" ? "replica" : "";
    }

    /// <summary>Which iceberg-pg node currently holds the VRRP VIP (ip addr show).</summary>
    private async Task<string?> VipHolderAsync(List<NodeRecord> pair, string vip, CancellationToken ct)
    {
        foreach (var n in pair)
        {
            var r = await _ssh.ExecuteAsync(T(n.Vmnet11), $"ip -4 -o addr show 2>/dev/null | grep -c '{vip}'", SshTimeout, ct).ConfigureAwait(false);
            if (r.IsOk && r.Value!.Stdout.Trim().StartsWith('1')) return n.Name;
        }
        return null;
    }

    /// <summary>Run an on-node `sudo mc ...` (the nexuslocal alias) on a MinIO node. Returns raw stdout (empty on failure).</summary>
    private async Task<string> McAsync(string minioIp, string mcArgs, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(minioIp), $"sudo {McBin} {mcArgs} 2>/dev/null", McTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value is not null ? r.Value.Stdout : "";
    }

    /// <summary>Query a Spark master's REST <c>/json/</c> on its OWN vmnet11 IP (the UI binds there, not loopback).</summary>
    private async Task<(int Code, string Body)> SparkJsonAsync(NodeRecord master, CancellationToken ct) =>
        await CurlAsync(master.Vmnet11, master.Vmnet11, SparkMasterSpec.Port, false, null, "/json/", null, ct).ConfigureAwait(false);

    // === parsing helpers (internal static for unit tests) ==================
    /// <summary>Safe string-property read off a <see cref="JsonElement"/> ("" when absent or non-string).</summary>
    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Parse a Spark master <c>/json/</c> → (status ALIVE|STANDBY, aliveWorkers, cores).</summary>
    internal static (string Status, int AliveWorkers, int Cores) ParseSparkStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = Str(root, "status");
            var aw = root.TryGetProperty("aliveworkers", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
            var cores = root.TryGetProperty("cores", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            return (status, aw, cores);
        }
        catch (JsonException) { return ("", 0, 0); }
    }

    /// <summary>
    /// Parse a Nessie Quarkus <c>/q/health</c> body → (overallStatus, list of (checkName, status)).
    /// The overall is "UP"/"DOWN"; the per-check list surfaces WHICH check is down (e.g. the
    /// "Warehouses Object Stores" S3 check fails under the cross-tier CA split).
    /// </summary>
    internal static (string Overall, List<(string Name, string Status)> Checks) ParseNessieHealth(string json)
    {
        var checks = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var overall = Str(root, "status");
            if (root.TryGetProperty("checks", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var c in arr.EnumerateArray())
                    checks.Add((Str(c, "name"), Str(c, "status")));
            return (overall, checks);
        }
        catch (JsonException) { return ("", checks); }
    }

    /// <summary>Parse <c>mc admin info --json</c> → (mode, onlineDrives, offlineDrives).</summary>
    internal static (string Mode, int Online, int Offline) ParseMcAdminInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("info", out var info)) return ("", 0, 0);
            var mode = Str(info, "mode");
            int online = 0, offline = 0;
            if (info.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                foreach (var s in servers.EnumerateArray())
                    if (s.TryGetProperty("drives", out var drives) && drives.ValueKind == JsonValueKind.Array)
                        foreach (var d in drives.EnumerateArray())
                        {
                            var st = Str(d, "state");
                            if (st.Equals("ok", StringComparison.OrdinalIgnoreCase)) online++;
                            else offline++;
                        }
            return (mode, online, offline);
        }
        catch (JsonException) { return ("", 0, 0); }
    }

    /// <summary>Classify a ZooKeeper <c>echo srvr | nc</c> body → "leader" | "follower" | "standalone" | "".</summary>
    internal static string ParseZkMode(string body)
    {
        var m = Regex.Match(body, @"Mode:\s*(\w+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
    }

    /// <summary>Parse <c>mc admin policy ls</c> (one policy per line) → names.</summary>
    internal static List<string> ParseMcList(string stdout)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(stdout)) return list;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            // mc may emit a JSON line per item (--json) or a bare name; handle both.
            if (line.StartsWith('{'))
            {
                try { using var d = JsonDocument.Parse(line); var n = Str(d.RootElement, "policy"); if (n.Length == 0) n = Str(d.RootElement, "accessKey"); if (n.Length == 0) n = Str(d.RootElement, "name"); if (n.Length > 0) list.Add(n); }
                catch (JsonException) { }
            }
            else list.Add(line.Split(WsChars, StringSplitOptions.RemoveEmptyEntries)[0]);
        }
        return list;
    }

    // === lazy KV (MinIO root creds for mc / acl) ===========================
    /// <summary>
    /// Lazily fetch the MinIO root credential from Vault KV (mount <c>nexus</c>, field
    /// <c>value</c>) — needed only for the mc-driven acl paths; fails with an operator-token
    /// hint when the adapter was built without a <see cref="INexusVaultClient"/>.
    /// </summary>
    private async Task<Result<(string User, string Pw)>> MinioRootAsync(CancellationToken ct)
    {
        if (_vault is null)
            return Result.Fail<(string, string)>(
                "reading the MinIO root credential needs the operator token. Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var u = await _vault.ReadKvFieldAsync("nexus", KvMinioRootUser, "value", ct).ConfigureAwait(false);
        if (u.IsFail) return Result.Fail<(string, string)>(u.Error!);
        var p = await _vault.ReadKvFieldAsync("nexus", KvMinioRootPw, "value", ct).ConfigureAwait(false);
        if (p.IsFail) return Result.Fail<(string, string)>(p.Error!);
        return Result.Ok((u.Value!, p.Value!));
    }

    // === GetStatusAsync ====================================================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ClusterStatus>(nodesR.Error!);
        var all = nodesR.Value!;

        var pgPair = Role(all, "iceberg-pg");
        _icebergPgVipHolder = await VipHolderAsync(pgPair, IcebergPgVip, cancellationToken).ConfigureAwait(false);

        // Resolve the ALIVE Spark master leader (label the masters + for CanResizeVm).
        _sparkAliveLeader = await SparkAliveLeaderAsync(Role(all, "spark-master"), cancellationToken).ConfigureAwait(false);

        var members = new List<ClusterMember>();
        foreach (var n in all)
        {
            var role = ClassifyRole(n.Name);
            var spec = SpecFor(role);
            var active = await IsActiveAsync(n.Vmnet11, spec.Unit, cancellationToken).ConfigureAwait(false);
            var roleLabel = role switch
            {
                "minio" => "minio (EC:2 node)",
                "nessie" => "nessie (Iceberg REST catalog)",
                "iceberg-pg" => n.Name == _icebergPgVipHolder ? "iceberg-pg (VIP .151 PRIMARY)" : "iceberg-pg (standby)",
                "spark-master" => n.Name == _sparkAliveLeader ? "spark-master (ALIVE leader)" : "spark-master (STANDBY)",
                "spark-worker" => "spark-worker",
                "zookeeper" => "zookeeper (ensemble)",
                _ => role
            };
            members.Add(new ClusterMember(n.Name, n.Vmnet11, roleLabel, active ? "alive" : "failed"));
        }

        var anyDown = members.Count(m => m.Status == "failed");
        var overall = anyDown == 0 ? "green" : anyDown >= 2 ? "red" : "yellow";
        return Result.Ok(new ClusterStatus(ClusterName, DisplayNameConst, overall, members, Leader: _sparkAliveLeader, DateTimeOffset.UtcNow));
    }

    /// <summary>Which spark-master currently reports status=ALIVE (the ZK-elected leader).</summary>
    private async Task<string?> SparkAliveLeaderAsync(List<NodeRecord> masters, CancellationToken ct)
    {
        foreach (var m in masters)
        {
            var (code, body) = await SparkJsonAsync(m, ct).ConfigureAwait(false);
            if (code == 200 && ParseSparkStatus(body).Status.Equals("ALIVE", StringComparison.OrdinalIgnoreCase))
                return m.Name;
        }
        return null;
    }

    // === HealthAsync =======================================================
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<HealthReport>(nodesR.Error!);
        var all = nodesR.Value!;
        var probes = new List<HealthProbe>();

        var minios = Role(all, "minio");
        var nessies = Role(all, "nessie");
        var pgs = Role(all, "iceberg-pg");
        var masters = Role(all, "spark-master");
        var workers = Role(all, "spark-worker");
        var zks = Role(all, "zookeeper");

        // --- MinIO: per-node /minio/health/live + cluster health + EC drives ---
        int minioLive = 0;
        foreach (var n in minios)
        {
            var (code, _) = await CurlAsync(n.Vmnet11, "127.0.0.1", MinioSpec.Port, true, MinioCa, "/minio/health/live", null, cancellationToken).ConfigureAwait(false);
            if (code == 200) minioLive++;
        }
        probes.Add(new HealthProbe("minio-live", "minio", minioLive == minios.Count ? "green" : minioLive > 0 ? "yellow" : "red",
            $"{minioLive}/{minios.Count} live", $"{minios.Count} live"));
        if (minios.Count > 0)
        {
            var (ccode, _) = await CurlAsync(minios[0].Vmnet11, "127.0.0.1", MinioSpec.Port, true, MinioCa, "/minio/health/cluster", null, cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe("minio-cluster", "minio", ccode == 200 ? "green" : "red", $"/minio/health/cluster = {ccode}", "200 (EC quorum)"));
            var info = await McAsync(minios[0].Vmnet11, $"admin info {McAlias} --json", cancellationToken).ConfigureAwait(false);
            var (mode, online, offline) = ParseMcAdminInfo(info);
            probes.Add(new HealthProbe("minio-drives", "minio", mode.Equals("online", StringComparison.OrdinalIgnoreCase) && offline == 0 && online > 0 ? "green" : "red",
                $"mode={mode}, {online} drives ok, {offline} offline", "online, 0 drives offline"));
        }

        // --- Nessie: mgmt /q/health per-check + app /iceberg/v1/config per node ---
        int nessieCfg = 0, nessieS3Up = 0;
        string nessieDetail = "";
        foreach (var n in nessies)
        {
            var (hcode, hbody) = await CurlAsync(n.Vmnet11, "127.0.0.1", NessieMgmtPort, false, null, "/q/health", null, cancellationToken).ConfigureAwait(false);
            var (overall, checks) = ParseNessieHealth(hbody);
            var s3 = checks.FirstOrDefault(c => c.Name.Contains("Object Store", StringComparison.OrdinalIgnoreCase));
            if (s3.Status.Equals("UP", StringComparison.OrdinalIgnoreCase)) nessieS3Up++;
            else if (s3.Name.Length > 0) nessieDetail = $"{n.Name}: '{s3.Name}'={s3.Status}";
            var (ccode, _) = await CurlAsync(n.Vmnet11, "127.0.0.1", NessieSpec.Port, true, $"{NessieSpec.TlsDir}/ca.crt", "/iceberg/v1/config", null, cancellationToken).ConfigureAwait(false);
            if (ccode == 200) nessieCfg++;
        }
        probes.Add(new HealthProbe("nessie-config", "nessie", nessieCfg == nessies.Count ? "green" : nessieCfg > 0 ? "yellow" : "red",
            $"{nessieCfg}/{nessies.Count} /iceberg/v1/config=200", $"{nessies.Count} catalog up"));
        probes.Add(new HealthProbe("nessie-objectstore", "nessie", nessieS3Up == nessies.Count ? "green" : nessieS3Up > 0 ? "yellow" : "red",
            nessieS3Up == nessies.Count ? $"{nessieS3Up}/{nessies.Count} S3 store UP"
                : $"{nessieS3Up}/{nessies.Count} S3 store UP — {(nessieDetail.Length > 0 ? nessieDetail + " (cross-tier CA split: old-root Nessie truststore vs new-root MinIO leaf; see handbook §3)" : "object store down")}",
            $"{nessies.Count} S3 store UP"));

        // --- Spark: master ALIVE + aliveworkers; standby present; workers reachable ---
        string? aliveLeader = null; int aliveWorkers = 0; int standby = 0;
        foreach (var m in masters)
        {
            var (code, body) = await SparkJsonAsync(m, cancellationToken).ConfigureAwait(false);
            if (code != 200) continue;
            var (st, aw, _) = ParseSparkStatus(body);
            if (st.Equals("ALIVE", StringComparison.OrdinalIgnoreCase)) { aliveLeader = m.Name; aliveWorkers = aw; }
            else if (st.Equals("STANDBY", StringComparison.OrdinalIgnoreCase)) standby++;
        }
        probes.Add(new HealthProbe("spark-master", "spark", aliveLeader is not null && standby >= 1 ? "green" : aliveLeader is not null ? "yellow" : "red",
            aliveLeader is not null ? $"ALIVE={aliveLeader}, {standby} STANDBY" : "no ALIVE master", "1 ALIVE + ≥1 STANDBY"));
        probes.Add(new HealthProbe("spark-workers", "spark", aliveWorkers == workers.Count ? "green" : aliveWorkers > 0 ? "yellow" : "red",
            $"{aliveWorkers}/{workers.Count} workers registered", $"{workers.Count} workers"));

        // --- ZooKeeper quorum: exactly 1 leader + rest followers ---
        int zkLeader = 0, zkFollower = 0;
        foreach (var z in zks)
        {
            var r = await _ssh.ExecuteAsync(T(z.Vmnet11), "echo srvr | nc -q2 127.0.0.1 2181 2>/dev/null | grep -i Mode", SshTimeout, cancellationToken).ConfigureAwait(false);
            var mode = ParseZkMode(r.IsOk ? r.Value!.Stdout : "");
            if (mode == "leader") zkLeader++;
            else if (mode == "follower") zkFollower++;
        }
        probes.Add(new HealthProbe("zookeeper-quorum", "zookeeper", zkLeader == 1 && zkFollower == zks.Count - 1 ? "green" : zkLeader >= 1 ? "yellow" : "red",
            $"{zkLeader} leader + {zkFollower} follower / {zks.Count}", "1 leader + rest followers"));

        // --- iceberg-pg streaming replication ---
        await PgReplicationHealthAsync(pgs, probes, cancellationToken).ConfigureAwait(false);

        // --- VRRP VIP bound ---
        var vipHolder = await VipHolderAsync(pgs, IcebergPgVip, cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("vip-iceberg-db", "keepalived", vipHolder is not null ? "green" : "red",
            vipHolder is not null ? $"{IcebergPgVip} on {vipHolder}" : $"{IcebergPgVip} unbound", "bound to 1 node"));
        _icebergPgVipHolder = vipHolder;
        _sparkAliveLeader = aliveLeader;

        var overallStatus = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overallStatus, probes, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Probe iceberg-pg streaming replication: find the primary (<c>in_recovery=f</c>) + confirm at
    /// least one streaming standby via <c>pg_stat_replication</c>. Flags the two-primary split
    /// (pg-2 never re-seeded — the live-contract casualty) explicitly RED.
    /// </summary>
    private async Task PgReplicationHealthAsync(List<NodeRecord> pgs, List<HealthProbe> probes, CancellationToken ct)
    {
        string? primary = null; bool sawStandby = false;
        foreach (var n in pgs)
        {
            var r = await _ssh.ExecuteAsync(T(n.Vmnet11), "sudo -u postgres psql -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
            var v = r.IsOk ? r.Value!.Stdout.Trim() : "";
            if (v == "f" && primary is null) primary = n.Vmnet11;
            else if (v == "t") sawStandby = true;
        }
        if (primary is null)
        {
            probes.Add(new HealthProbe("iceberg-pg-replication", "iceberg-pg", "red", "no primary (in_recovery=f) detected", "1 primary + 1 streaming standby"));
            return;
        }
        var rep = await _ssh.ExecuteAsync(T(primary), "sudo -u postgres psql -tAc \"SELECT count(*) FROM pg_stat_replication WHERE state='streaming'\" 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        var streaming = rep.IsOk && int.TryParse(rep.Value!.Stdout.Trim(), out var c) ? c : 0;
        probes.Add(new HealthProbe("iceberg-pg-replication", "iceberg-pg", streaming >= 1 && sawStandby ? "green" : "red",
            !sawStandby ? "both nodes are primary (split — iceberg-pg-2 never re-seeded as standby; see handbook §3)" : $"{streaming} streaming standby",
            "1 streaming standby"));
    }

    // === TopologyAsync =====================================================
    /// <inheritdoc />
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<TopologySnapshot>(nodesR.Error!);
        var all = nodesR.Value!;

        var pgs = Role(all, "iceberg-pg");
        var masters = Role(all, "spark-master");
        var zks = Role(all, "zookeeper");

        var vipHolder = await VipHolderAsync(pgs, IcebergPgVip, cancellationToken).ConfigureAwait(false);
        var aliveLeader = await SparkAliveLeaderAsync(masters, cancellationToken).ConfigureAwait(false);

        // ZK roles up front (one nc per node).
        var zkRole = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var z in zks)
        {
            var r = await _ssh.ExecuteAsync(T(z.Vmnet11), "echo srvr | nc -q2 127.0.0.1 2181 2>/dev/null | grep -i Mode", SshTimeout, cancellationToken).ConfigureAwait(false);
            zkRole[z.Name] = ParseZkMode(r.IsOk ? r.Value!.Stdout : "");
        }

        var nodes = new List<TopologyNode>();
        foreach (var n in all)
        {
            var role = ClassifyRole(n.Name);
            var spec = SpecFor(role);
            var active = await IsActiveAsync(n.Vmnet11, spec.Unit, cancellationToken).ConfigureAwait(false);
            var label = role switch
            {
                "minio" => "minio (distributed EC:2; RR DNS minio.nexus.lab; no VIP)",
                "nessie" => "nessie (Iceberg REST catalog HA; RR DNS iceberg.nexus.lab)",
                "iceberg-pg" => n.Name == vipHolder ? "iceberg-pg (PG17 catalog; VIP .151 PRIMARY)" : "iceberg-pg (PG17 catalog; standby)",
                "spark-master" => n.Name == aliveLeader ? "spark-master (ZK-elected ALIVE leader)" : "spark-master (STANDBY)",
                "spark-worker" => "spark-worker (standalone)",
                "zookeeper" => $"zookeeper ({(zkRole.TryGetValue(n.Name, out var zr) && zr.Length > 0 ? zr : "ensemble")}; backplane-only)",
                _ => role
            };
            nodes.Add(new TopologyNode(n.Name, label, active ? "alive" : "failed"));
        }

        // VRRP VIP as a pseudo-node (the catalog-DB front door).
        nodes.Add(new TopologyNode($"VIP {IcebergPgVip} (iceberg-db.nexus.lab)", $"VRRP front door → {vipHolder ?? "unbound"}", vipHolder is not null ? "alive" : "failed"));
        // Spark master URL enrichment.
        if (masters.Count > 0)
            nodes.Add(new TopologyNode("spark-master-url", $"spark://{string.Join(",", masters.Select(m => m.Vmnet11 + ":7077"))} (ZK recovery)", "info"));

        // Lakehouse is not sharded.
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (Spark master ZK re-elect; iceberg-pg = graceful N/A) ==
    /// <inheritdoc />
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<FailoverResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var direction = (request.Direction ?? "spark-master").Trim().ToLowerInvariant();

        if (direction is "spark-master" or "spark" or "master")
            return await SparkMasterFailoverAsync(all, request, cancellationToken).ConfigureAwait(false);
        if (direction is "iceberg-pg" or "iceberg-db" or "pg" or "db" or "catalog" or ".151")
            return await IcebergPgFailoverAsync(all, request, cancellationToken).ConfigureAwait(false);

        return Result.Fail<FailoverResult>(
            $"unknown failover direction '{direction}'. The lakehouse has TWO one-shot failovers: `--direction spark-master` "
            + "(ZooKeeper auto-promotes the STANDBY master, ~30s) and `--direction iceberg-pg` (the catalog-DB VRRP cutover .151 "
            + "— promote the standby + fence/re-seed the old primary). MinIO (EC, no leader), Nessie (RR-DNS HA), ZooKeeper (its "
            + "own Zab quorum), and the Spark workers have no safe one-shot operator failover.");
    }

    /// <summary>
    /// Spark master HA drill: stop <c>nexus-spark-master</c> on the ALIVE leader so ZooKeeper
    /// promotes the STANDBY, poll the survivor to ALIVE (RTO), then restart the stopped node as
    /// the new STANDBY unless <c>--no-recover</c>.
    /// </summary>
    private async Task<Result<FailoverResult>> SparkMasterFailoverAsync(List<NodeRecord> all, FailoverRequest request, CancellationToken ct)
    {
        var masters = Role(all, "spark-master");
        if (masters.Count < 2) return Result.Fail<FailoverResult>("need a 2-master Spark HA pair to fail over.");
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var preFlight = sw.Elapsed;

        var leaderName = await SparkAliveLeaderAsync(masters, ct).ConfigureAwait(false);
        if (leaderName is null) return Result.Fail<FailoverResult>("no ALIVE Spark master found to fail over; the cluster has no current leader.");
        var leader = masters.First(m => m.Name == leaderName);
        var standby = masters.First(m => m.Name != leaderName);

        // Inject: stop nexus-spark-master on the ALIVE leader → ZooKeeper promotes the STANDBY.
        var stop = await _ssh.ExecuteAsync(T(leader.Vmnet11), $"sudo systemctl stop {SparkMasterSpec.Unit} && echo STOPPED", SshTimeout, ct).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<FailoverResult>($"could not stop {SparkMasterSpec.Unit} on {leader.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 160))}");
        var injected = sw.Elapsed;

        // Poll the surviving standby until it reports ALIVE (ZK leader election + worker re-registration).
        bool promoted = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(75);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var (code, body) = await SparkJsonAsync(standby, ct).ConfigureAwait(false);
            if (code == 200 && ParseSparkStatus(body).Status.Equals("ALIVE", StringComparison.OrdinalIgnoreCase)) { promoted = true; break; }
        }
        var observed = sw.Elapsed;

        // Recover: restart the stopped master (rejoins the ZK election as the new STANDBY).
        var recovery = "skipped"; string? recoveryHint = null;
        if (!request.NoRecover)
        {
            var restart = await _ssh.ExecuteAsync(T(leader.Vmnet11), $"sudo systemctl start {SparkMasterSpec.Unit} && echo STARTED", SshTimeout, ct).ConfigureAwait(false);
            recovery = restart.IsOk && restart.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal) ? "recovered" : "failed";
            if (recovery == "failed") recoveryHint = $"restart {SparkMasterSpec.Unit} on {leader.Name} manually.";
        }
        else recoveryHint = $"{SparkMasterSpec.Unit} left stopped on {leader.Name} (--no-recover).";
        var recovered = sw.Elapsed;
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "spark-master-zk-reelect",
            OriginalPrimary: leader.Name,
            NewPrimary: promoted ? standby.Name : null,
            Rto: observed - injected,
            Recovery: recovery,
            RecoveryHint: promoted ? recoveryHint : (recoveryHint ?? $"the standby {standby.Name} did not reach ALIVE within 75s — check ZooKeeper + nexus-spark-master."),
            Timeline: new FailoverTimeline(preFlight, injected, observed, recovered, recovered),
            StartedAtUtc: startedAt));
    }

    // iceberg-pg catalog-DB failover: a real one-shot VRRP cutover of the .151 VIP (0.L.2.1
    // fencing hardening). The v0.8.4 blockers were fixed in nexus-infra-lakehouse: the
    // NEXUS-ICEBERG-HBA block is now on BOTH nodes (a promoted standby admits Nessie) and a
    // guarded /usr/local/sbin/nexus-iceberg-reseed.sh fences + re-seeds the demoted old primary
    // so there is no split-brain. This adapter DRIVES the drill deterministically (it holds the
    // nexusadmin key + reaches both nodes — no inter-node SSH); the keepalived notify_fault hook
    // is the unattended-crash backstop. Same shape as RegistryAdapter's registry-db cutover.
    /// <summary>
    /// iceberg-pg catalog-DB VRRP cutover of the <c>.151</c> VIP: pre-check the target is a
    /// streaming standby, stop keepalived on the VIP-holding primary so the standby claims the VIP
    /// + its <c>notify_master</c> promotes it, then fence + <c>pg_basebackup</c> re-seed the old
    /// primary as a fresh standby (guarded helper — never wipes a live primary) and restart its
    /// keepalived as BACKUP (<c>nopreempt</c>).
    /// </summary>
    private async Task<Result<FailoverResult>> IcebergPgFailoverAsync(List<NodeRecord> all, FailoverRequest request, CancellationToken ct)
    {
        var pgs = Role(all, "iceberg-pg");
        if (pgs.Count < 2) return Result.Fail<FailoverResult>("need the 2-node iceberg-pg catalog pair to fail over.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var preFlight = sw.Elapsed;

        // The VIP holder is the current primary; the other node must be a streaming standby.
        var master = await VipHolderAsync(pgs, IcebergPgVip, ct).ConfigureAwait(false);
        if (master is null)
            return Result.Fail<FailoverResult>($"VIP {IcebergPgVip} is not bound to either iceberg-pg node; refusing to fail over an unbound VIP.");
        var masterNode = pgs.First(n => n.Name == master);
        var targetNode = pgs.First(n => n.Name != master);

        // Pre-flight: the target must be a standby (in_recovery=t) so its notify_master can promote it.
        var targetRole = await PgRoleAsync(targetNode.Vmnet11, ct).ConfigureAwait(false);
        if (targetRole != "replica")
            return Result.Fail<FailoverResult>(
                $"{targetNode.Name} is not a streaming standby (role={(targetRole.Length == 0 ? "unreachable" : targetRole)}); refusing to fail over "
                + "into a non-standby. Run `nexus status lakehouse` — the catalog pair must be 1 primary + 1 standby first "
                + "(a prior split-brain re-seeds via the overlay / nexus-iceberg-reseed.sh).");

        // Inject: stop keepalived on the VIP-holding primary → the standby claims the VIP;
        // its notify_master runs pg_ctl promote.
        var stop = await _ssh.ExecuteAsync(T(masterNode.Vmnet11), "sudo systemctl stop keepalived && echo STOPPED", SshTimeout, ct).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<FailoverResult>($"could not stop keepalived on {master}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 160))}");
        var injected = sw.Elapsed;

        // Poll: the VIP lands on the target AND the target PG is promoted (in_recovery=f).
        string? newHolder = null; bool promoted = false;
        var moveDeadline = sw.Elapsed + TimeSpan.FromSeconds(45);
        while (sw.Elapsed < moveDeadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            newHolder = await VipHolderAsync(pgs, IcebergPgVip, ct).ConfigureAwait(false);
            if (newHolder == targetNode.Name && await PgRoleAsync(targetNode.Vmnet11, ct).ConfigureAwait(false) == "primary")
            { promoted = true; break; }
        }
        var observed = sw.Elapsed;

        // Fence + re-seed the OLD primary as a fresh standby of the NEW primary (deterministic).
        // The helper refuses if the node still holds the VIP or the source is not a primary, so it
        // can NEVER wipe a live primary; it streams over the new primary's backplane IP.
        var reseed = "skipped"; string? reseedHint = null;
        if (promoted)
        {
            var rc = await _ssh.ExecuteAsync(T(masterNode.Vmnet11),
                $"sudo /usr/local/sbin/nexus-iceberg-reseed.sh {targetNode.Vmnet10} 2>&1", ReseedTimeout, ct).ConfigureAwait(false);
            var ok = rc.IsOk && rc.Value!.Stdout.Contains("RESEED_OK", StringComparison.Ordinal);
            reseed = ok ? "re-seeded" : "failed";
            if (!ok) reseedHint = $"the old primary {master} did NOT re-seed as a standby (split-brain risk) — run manually: "
                + $"ssh {masterNode.Vmnet11} 'sudo /usr/local/sbin/nexus-iceberg-reseed.sh {targetNode.Vmnet10}'. "
                + $"[{Tail((rc.IsOk ? rc.Value!.Stdout : rc.Error) ?? "", 220)}]";
        }

        // Recover: restart keepalived on the old primary (nopreempt → it returns as BACKUP; it is
        // now the streaming standby, so the VIP stays on the new primary).
        var kaBack = "skipped";
        if (!request.NoRecover)
        {
            var restart = await _ssh.ExecuteAsync(T(masterNode.Vmnet11), "sudo systemctl start keepalived && echo STARTED", SshTimeout, ct).ConfigureAwait(false);
            kaBack = restart.IsOk && restart.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal) ? "recovered" : "failed";
            if (kaBack == "failed") reseedHint = $"restart keepalived on {master} manually (`sudo systemctl start keepalived`). {reseedHint}";
        }
        else reseedHint ??= $"keepalived left stopped on {master} (--no-recover); restart it when ready.";
        var recovered = sw.Elapsed;
        sw.Stop();

        // Overall recovery = the tier is back to 1 primary + 1 standby (VIP moved, target promoted,
        // old primary re-seeded, keepalived back). Any missing piece is surfaced as "failed" + hint.
        var moved = newHolder == targetNode.Name;
        var recovery = !promoted ? "failed"
            : reseed != "re-seeded" ? "failed"
            : request.NoRecover ? "skipped"
            : kaBack == "recovered" ? "recovered" : "failed";
        var okHint = $"{master} re-seeded as a streaming standby of {targetNode.Name}; the .151 VIP now fronts {targetNode.Name} "
            + "(nopreempt keeps it there). Nessie reconnects through the VIP — its pg_hba admits the nessie role on the promoted node.";

        return Result.Ok(new FailoverResult(
            Scenario: "vrrp-cutover:iceberg-pg",
            OriginalPrimary: master,
            NewPrimary: moved ? targetNode.Name : newHolder,
            Rto: observed - injected,
            Recovery: recovery,
            RecoveryHint: moved && promoted ? (reseedHint ?? okHint)
                : $"VIP/promotion did not complete on {targetNode.Name} within 45s (moved={moved}, pg-primary={promoted}) — check keepalived + the notify_master pg_ctl promote on the standby. {reseedHint}",
            Timeline: new FailoverTimeline(preFlight, injected, observed, recovered, recovered),
            StartedAtUtc: startedAt));
    }

    // === ScaleOut (graceful actionable N/A) ================================
    /// <inheritdoc />
    public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(ScaleOutNaMessage));

    /// <inheritdoc />
    public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(ScaleOutNaMessage));

    private const string ScaleOutNaMessage =
        "scale-out is graceful N/A for the lakehouse tier — none of its roles scale at runtime. The MinIO erasure set is FIXED at 4 "
        + "nodes (EC:2; the set size is baked at format time — growing it is a new server pool, a terraform/Packer op), the Spark "
        + "worker count + the iceberg-pg / Nessie / ZooKeeper pairs/ensemble are all fixed-size IaC. Add capacity by adding the VM + "
        + "overlay in nexus-infra-lakehouse and re-applying (the node joins on boot); there is no safe runtime add/remove to expose here.";

    // === Backup (mc mirror s3://warehouse round-trip) ======================
    /// <inheritdoc />
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<BackupResult>(nodesR.Error!);
        var minios = Role(nodesR.Value!, "minio");
        if (minios.Count == 0) return Result.Fail<BackupResult>("no MinIO node in vms.yaml cluster lakehouse.");
        var via = minios[0].Vmnet11;

        var tag = string.IsNullOrWhiteSpace(request.Tag) ? "warehouse" : Regex.Replace(request.Tag!, "[^A-Za-z0-9_.-]", "-");
        var dest = $"/var/tmp/nexus-lakehouse-backup/{tag}";
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // mc mirror the warehouse bucket (the Iceberg/Spark data) to a node-local dir; the S3 data
        // is already EC-durable, so this is a portable point-in-time copy + an integrity round-trip.
        var script = string.Join(" && ", new[]
        {
            $"sudo rm -rf {dest}",
            $"sudo mkdir -p {dest}",
            $"sudo {McBin} mirror --overwrite {McAlias}/{WarehouseBucket} {dest} >/dev/null 2>&1 || true",
            $"echo OBJECTS=$(sudo find {dest} -type f | wc -l)",
            $"echo BYTES=$(sudo du -sb {dest} 2>/dev/null | cut -f1)",
        });
        var r = await _ssh.ExecuteAsync(T(via), script, McTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<BackupResult>($"mc mirror of s3://{WarehouseBucket} failed: {r.Error}");
        var objs = MatchInt(r.Value!.Stdout, @"OBJECTS=(\d+)");
        var bytes = MatchLong(r.Value.Stdout, @"BYTES=(\d+)");
        return Result.Ok(new BackupResult($"{tag} ({objs} objects @ {via}:{dest})", $"{via}:{dest}", bytes, sw.Elapsed, startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<RestoreResult>(nodesR.Error!);
        var minios = Role(nodesR.Value!, "minio");
        if (minios.Count == 0) return Result.Fail<RestoreResult>("no MinIO node in vms.yaml cluster lakehouse.");
        var via = minios[0].Vmnet11;

        // The backup-id carries the on-node dest path (via:dest); accept the tag too.
        var idTag = Regex.Match(request.BackupId ?? "", @"^(?<tag>[^ ]+)").Groups["tag"].Value;
        if (idTag.Length == 0) idTag = "warehouse";
        var src = $"/var/tmp/nexus-lakehouse-backup/{idTag}";
        var verifyBucket = "warehouse-restore-verify";
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var script = string.Join(" && ", new[]
        {
            $"sudo test -d {src} || {{ echo MISSING; exit 0; }}",
            $"sudo {McBin} mb --ignore-existing {McAlias}/{verifyBucket} >/dev/null 2>&1 || true",
            $"sudo {McBin} mirror --overwrite {src} {McAlias}/{verifyBucket} >/dev/null 2>&1 || true",
            $"echo RESTORED=$(sudo {McBin} ls --recursive {McAlias}/{verifyBucket} 2>/dev/null | wc -l)",
        });
        var r = await _ssh.ExecuteAsync(T(via), script, McTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<RestoreResult>($"restore round-trip failed: {r.Error}");
        if (r.Value!.Stdout.Contains("MISSING", StringComparison.Ordinal))
            return Result.Fail<RestoreResult>($"no backup found at {via}:{src}; run `backup take {ClusterName}` first.");
        var restored = MatchInt(r.Value.Stdout, @"RESTORED=(\d+)");
        return Result.Ok(new RestoreResult(idTag, restored, sw.Elapsed, startedAt));
    }

    // === RotateCertAsync (force vault-agent re-render; MinIO big-bang) ======
    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<CertRotationResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        var minios = Role(all, "minio");
        // --- MinIO: re-render ALL four bundles, THEN big-bang restart (a rolling 1-node
        //     re-cert breaks distributed MinIO's inter-node mTLS — the v0.8.3 lesson). ---
        if (minios.Count > 0)
        {
            var oldSerials = new Dictionary<string, string>(StringComparer.Ordinal);
            bool rerenderOk = true;
            foreach (var n in minios)
            {
                oldSerials[n.Name] = await WireSerialAsync(n.Vmnet11, $"{MinioSpec.TlsDir}/public.crt", cancellationToken).ConfigureAwait(false);
                var ok = await ForceReRenderAsync(n.Vmnet11, $"{MinioSpec.TlsDir}/public.crt", cancellationToken).ConfigureAwait(false);
                if (!ok) rerenderOk = false;
            }
            // Big-bang restart all four together.
            foreach (var n in minios)
                await _ssh.ExecuteAsync(T(n.Vmnet11), $"sudo systemctl restart {MinioSpec.Unit} 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            foreach (var n in minios)
            {
                var newSerial = await WireSerialAsync(n.Vmnet11, $"{MinioSpec.TlsDir}/public.crt", cancellationToken).ConfigureAwait(false);
                rotated.Add(new CertRotatedNode(n.Name, oldSerials[n.Name], newSerial,
                    Error: rerenderOk ? null : "vault-agent re-render did not confirm (node may be on the OLD Vault root — needs the trust re-cert; handbook §3)"));
            }
        }

        // --- Nessie: per-node force re-render of its server leaf + restart. ---
        foreach (var n in Role(all, "nessie"))
        {
            var certPath = $"{NessieSpec.TlsDir}/cert.pem";
            var oldSerial = await WireSerialAsync(n.Vmnet11, certPath, cancellationToken).ConfigureAwait(false);
            var rerendered = await ForceReRenderAsync(n.Vmnet11, certPath, cancellationToken).ConfigureAwait(false);
            await _ssh.ExecuteAsync(T(n.Vmnet11), $"sudo systemctl restart {NessieSpec.Unit} 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var newSerial = await WireSerialAsync(n.Vmnet11, certPath, cancellationToken).ConfigureAwait(false);
            rotated.Add(new CertRotatedNode(n.Name, oldSerial, newSerial,
                Error: rerendered ? null : "vault-agent re-render did not confirm (node may be on the OLD Vault root — needs the trust re-cert; handbook §3)"));
        }

        // --- Spark + ZooKeeper: graceful N/A — neither has a rotatable server leaf. ---
        // Spark RPC is shared-secret + AES (spark.authenticate / spark.network.crypto), and its
        // only on-node trust material is the JVM truststore CA (vault-agent renders ca-bundle.crt,
        // not a per-node leaf) — there is no leaf serial to rotate; the CA refreshes on the tier's
        // ca-bundle re-render + a Spark restart, not this verb. ZooKeeper is backplane-only
        // plaintext (ADR-0035; no TLS, no vault-agent at all).
        foreach (var n in Role(all, "spark-master").Concat(Role(all, "spark-worker")))
            rotated.Add(new CertRotatedNode(n.Name, "(n/a)", "(n/a)",
                Error: "Spark has no rotatable server leaf — RPC is shared-secret + AES and the only trust material is the JVM truststore CA (no per-node leaf); the CA refreshes on a ca-bundle re-render + restart, not cert-rotate."));
        foreach (var n in Role(all, "zookeeper"))
            rotated.Add(new CertRotatedNode(n.Name, "(n/a)", "(n/a)",
                Error: "ZooKeeper is backplane-only plaintext (ADR-0035) — no TLS, no vault-agent, nothing to rotate."));

        // iceberg-pg (GAP #6): rotate the Iceberg catalog-DB pair's leaf STANDBY-FIRST then
        // PRIMARY, with a SIGHUP reload (no restart → the streaming-replication connection +
        // Nessie's live catalog connections are never dropped). Shared with grafana-pg (obs)
        // via PgSslCertRotator.
        rotated.AddRange(await PgSslCertRotator.RotatePairAsync(
            _ssh, T, Role(all, "iceberg-pg"), IcebergPgSpec.TlsDir, IcebergPgSpec.Unit,
            SshTimeout, cancellationToken).ConfigureAwait(false));

        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    /// <summary>
    /// Force a node's vault-agent to re-issue its leaf: back up + remove the rendered
    /// bundle.pem (pkiCert PERSISTS + reuses the leaf otherwise — the Swarm v0.8.2 lesson),
    /// restart the agent, wait for the re-render, restore the backup if it didn't reappear.
    /// Returns true if a fresh artifact is present after the restart. (On an OLD-root node
    /// whose agent token is absent, the re-render won't confirm — reported, not fatal.)
    /// </summary>
    private async Task<bool> ForceReRenderAsync(string ip, string witnessFile, CancellationToken ct)
    {
        var bundle = $"{Path.GetDirectoryName(witnessFile)!.Replace('\\', '/')}/bundle.pem";
        var script = string.Join(" ; ", new[]
        {
            $"if sudo test -f \"{bundle}\"; then sudo cp -a \"{bundle}\" \"{bundle}.bak\"; sudo rm -f \"{bundle}\"; fi",
            "sudo systemctl restart nexus-vault-agent 2>/dev/null",
            $"for i in $(seq 1 20); do sudo test -f \"{bundle}\" && break; sleep 1; done",
            $"if sudo test -f \"{bundle}.bak\"; then if sudo test -f \"{bundle}\"; then sudo rm -f \"{bundle}.bak\"; else sudo mv \"{bundle}.bak\" \"{bundle}\"; fi; fi",
            $"sudo test -f \"{bundle}\" && echo RERENDERED || echo NORENDER",
        });
        var r = await _ssh.ExecuteAsync(T(ip), script, SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Contains("RERENDERED", StringComparison.Ordinal);
    }

    /// <summary>Read a node's current leaf serial from a cert file (proof of rotation).</summary>
    private async Task<string> WireSerialAsync(string ip, string certFile, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip),
            $"sudo openssl x509 -in {certFile} -noout -serial 2>/dev/null | sed 's/serial=//'", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim().Length > 0 ? r.Value.Stdout.Trim() : "(unknown)";
    }

    // === AclAsync (MinIO policies + users via mc admin) ====================
    /// <inheritdoc />
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<AclSnapshot>(nodesR.Error!);
        var minios = Role(nodesR.Value!, "minio");
        if (minios.Count == 0) return Result.Fail<AclSnapshot>("no MinIO node in vms.yaml cluster lakehouse.");
        var via = minios[0].Vmnet11;
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var policiesOut = await McAsync(via, $"admin policy ls {McAlias}", cancellationToken).ConfigureAwait(false);
            var usersOut = await McAsync(via, $"admin user ls {McAlias} --json", cancellationToken).ConfigureAwait(false);
            var policies = ParseMcList(policiesOut);
            var users = ParseMcUsers(usersOut);
            var entries = new List<AclUser>();
            foreach (var p in policies)
                entries.Add(new AclUser($"policy:{p}", ["policy"], Enabled: true));
            foreach (var u in users)
                entries.Add(new AclUser(u.AccessKey,
                    ProtectedMinioUsers.Contains(u.AccessKey, StringComparer.OrdinalIgnoreCase) ? [u.Status, "protected"] : [u.Status],
                    Enabled: u.Status.Equals("enabled", StringComparison.OrdinalIgnoreCase)));
            if (entries.Count == 0) return Result.Fail<AclSnapshot>("MinIO returned no policies/users — check the mc nexuslocal alias (root creds may have drifted in KV; the on-node alias is authoritative).");
            return Result.Ok(new AclSnapshot(ClusterName, verb, entries, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user <minio-access-key>.");
            var user = operation.User!;
            if (verb == "revoke" && ProtectedMinioUsers.Contains(user, StringComparer.OrdinalIgnoreCase))
                return Result.Fail<AclSnapshot>($"refusing to detach a policy from the protected MinIO user '{user}' (operator/service identity).");
            var policy = operation.Permissions is { Count: > 0 } ? operation.Permissions[0] : "readwrite";
            var action = verb == "grant" ? "attach" : "detach";
            var outp = await McAsync(via, $"admin policy {action} {McAlias} {policy} --user {user}", cancellationToken).ConfigureAwait(false);
            // mc prints "Attached/Detached Policies: [...]" on success; treat a non-error run as applied.
            var r = await _ssh.ExecuteAsync(T(via), $"sudo {McBin} admin policy {action} {McAlias} {policy} --user {user} 2>&1 | tail -2; echo MC_RC=$?", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (r.IsFail || (r.Value!.Stdout.Contains("ERROR", StringComparison.OrdinalIgnoreCase) && !r.Value.Stdout.Contains("already", StringComparison.OrdinalIgnoreCase)))
                return Result.Fail<AclSnapshot>($"mc admin policy {action} for '{user}' failed: {Tail((r.Value?.Stdout ?? "") + (r.Value?.Stderr ?? ""), 200)}");
            return await AclAsync(new AclOperation("list"), cancellationToken).ConfigureAwait(false);
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke.");
    }

    /// <summary>Parse <c>mc admin user ls --json</c> (one JSON object per line) → (accessKey, status).</summary>
    internal static List<(string AccessKey, string Status)> ParseMcUsers(string stdout)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(stdout)) return list;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !line.StartsWith('{')) continue;
            try
            {
                using var d = JsonDocument.Parse(line);
                var ak = Str(d.RootElement, "accessKey");
                var st = Str(d.RootElement, "userStatus");
                if (st.Length == 0) st = Str(d.RootElement, "status");
                if (ak.Length > 0) list.Add((ak, st.Length > 0 ? st : "enabled"));
            }
            catch (JsonException) { }
        }
        return list;
    }

    // === ApplyChaosAsync (nexus-chaos.sh process-kill a tolerant node) =====
    /// <inheritdoc />
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ChaosOutcome>(nodesR.Error!);
        var all = nodesR.Value!;

        var minios = Role(all, "minio");
        var pgHolder = await VipHolderAsync(Role(all, "iceberg-pg"), IcebergPgVip, cancellationToken).ConfigureAwait(false);
        var sparkLeader = await SparkAliveLeaderAsync(Role(all, "spark-master"), cancellationToken).ConfigureAwait(false);

        NodeRecord victim;
        if (!string.IsNullOrWhiteSpace(scenario.Target))
        {
            var t = all.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase));
            if (t is null) return Result.Fail<ChaosOutcome>($"chaos target '{scenario.Target}' is not a lakehouse node.");
            if (t.Name == pgHolder)
                return Result.Fail<ChaosOutcome>($"'{t.Name}' currently holds the iceberg-pg VIP {IcebergPgVip}; pick a non-VIP node (a minio-N is EC-tolerant and safest) or fail the VIP over first.");
            victim = t;
        }
        else
        {
            // Default victim: the highest-numbered MinIO node (EC:2 tolerates 1 lost node; it self-heals on restart).
            if (minios.Count == 0) return Result.Fail<ChaosOutcome>("no MinIO node available as a chaos victim.");
            victim = minios[^1];
        }

        var role = ClassifyRole(victim.Name);
        var spec = SpecFor(role);
        var target = T(victim.Vmnet11);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var isProcKill = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase);
        var helperTarget = isProcKill ? spec.Unit : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Name} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 12)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (isProcKill)
            await _ssh.ExecuteAsync(target, $"sudo systemctl reset-failed {spec.Unit} 2>/dev/null; sudo systemctl start {spec.Unit} 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        // Recover: poll the victim's service back to active (+ MinIO cluster health back to 200).
        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(90);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            if (!await IsActiveAsync(victim.Vmnet11, spec.Unit, cancellationToken).ConfigureAwait(false)) continue;
            if (role == "minio")
            {
                var (code, _) = await CurlAsync(victim.Vmnet11, "127.0.0.1", MinioSpec.Port, true, MinioCa, "/minio/health/cluster", null, cancellationToken).ConfigureAwait(false);
                if (code == 200) { recovered = true; break; }
            }
            else { recovered = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ChaosOutcome(scenario.ScenarioType, victim.Name, observed, sw.Elapsed, startedAt, recovered));
    }

    /// <summary>Base64-stage the embedded <c>nexus-chaos.sh</c> resource to <c>/usr/local/bin</c> on the victim (CRLF-stripped, chmod +x).</summary>
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
        // Refuse the iceberg-pg VIP holder + the ALIVE Spark master (resizing flaps the front door /
        // forces a needless re-election); everything else is safe.
        if (string.Equals(vmName, _icebergPgVipHolder, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(vmName, _sparkAliveLeader, StringComparison.OrdinalIgnoreCase)) return false;
        return ClassifyRole(vmName) is not "other";
    }

    // === helpers ===========================================================
    /// <summary>First int captured by <paramref name="pattern"/> group 1, else 0.</summary>
    private static int MatchInt(string s, string pattern)
        => Regex.Match(s, pattern) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    /// <summary>First long captured by <paramref name="pattern"/> group 1, else 0.</summary>
    private static long MatchLong(string s, string pattern)
        => Regex.Match(s, pattern) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    /// <summary>Last <paramref name="n"/> chars of <paramref name="s"/> (for truncating stderr in error messages).</summary>
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
}
