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
/// Registry-tier adapter (nexus-cli v0.8.5, Phase 0.L.4, ADR-0026) — the FIFTH and
/// LAST non-data-tier adapter, completing full IClusterAdapter coverage of the
/// platform. Manages the Harbor container registry HA cluster over 4 VMs + 1 VRRP VIP.
/// <para>
/// Nodes (vms.yaml cluster <c>platform-tools</c>; the adapter filters to the four
/// <c>registry-*</c> members — the prefect/unleash/marquez/backstage entries in that
/// cluster are unbuilt "future" reservations classified <c>other</c>): registry-1/2
/// (Harbor app HA pair — docker-compose stack core/portal/registry/jobservice/trivy/
/// nginx; round-robin DNS <c>registry.nexus.lab</c>), registry-pg-1/2 (dedicated
/// datastore HA pair — PostgreSQL 17 streaming repl + co-located Redis master/replica;
/// keepalived VRRP VIP <c>.119</c> <c>registry-db.nexus.lab</c>). Harbor's durable
/// state is EXTERNAL: image layers/blobs → MinIO <c>s3://harbor</c> (lakehouse tier,
/// EC-durable), metadata → the registry PG, cache → Redis. SSO via Vault OIDC.
/// </para>
/// <para>
/// Access posture (mirrors <see cref="ObservabilityAdapter"/> / <see cref="LakehouseAdapter"/>):
/// the Harbor API is HTTPS on :443 (nginx) with a leaf issued by the tier's own CA
/// generation, so the adapter probes it over SSH with the node's own
/// <c>/etc/nexus-registry/tls/ca.crt</c> (always self-consistent regardless of the
/// build host's current root). Runtime credentials (the Harbor admin password) come
/// from Vault KV via the build-host <see cref="INexusVaultClient"/> (field
/// <c>value</c>); PG/Redis/VRRP/chaos/cert control runs over node SSH. No managed
/// Harbor/Npgsql/Redis driver — NetArchTest-clean.
/// </para>
/// <list type="bullet">
///   <item>status = Harbor app health (RR pair) + PG primary/replica + Redis master/replica + VIP holder.</item>
///   <item>health = /api/v2.0/health component checklist + systeminfo (auth_mode) + PG
///   streaming repl + Redis repl + S3 (MinIO) reachable + VIP bound.</item>
///   <item>topology = 4 nodes + VIP pseudo-node + datastore backends (MinIO/PG/Redis).</item>
///   <item>failover = datastore VRRP cutover (stop keepalived on the .119 holder → PG
///   promote + Redis re-master via the keepalived notify scripts → RTO measured).</item>
///   <item>scale-out = graceful actionable N/A (2-node RR-DNS app HA + 2-node datastore
///   pair is the ADR-0036 standard; growth is a terraform op).</item>
///   <item>backup = pg_dump the Harbor metadata DB (registry) round-trip — the
///   adapter-ownable authoritative state; blobs are EC-durable in MinIO, Redis is cache.</item>
///   <item>cert-rotate = force each node's vault-agent to re-render its leaf + reload
///   (app = restart the nginx container; PG = ssl reload), VIP holder last.</item>
///   <item>acl = Harbor users via /api/v2.0/users (sysadmin promote/demote; admin protected) +
///   project/robot counts surfaced.</item>
///   <item>chaos = nexus-chaos.sh on a non-VIP node + recover-to-green.</item>
/// </list>
/// </summary>
public sealed class RegistryAdapter : IClusterAdapter
{
    private const string ClusterName = "registry";
    private const string DisplayNameConst = "Registry tier (Harbor container registry HA: app pair + PostgreSQL/Redis datastore + MinIO S3 blobs)";
    private const string VmsCluster = "platform-tools";

    // keepalived VRRP VIP (vms.yaml comment; no virtual_ips block on platform-tools) —
    // the datastore front door Harbor's app nodes reach PG + Redis through.
    private const string RegistryDbVip = "192.168.70.119";

    // Harbor app TLS (nginx :443) + the datastore TLS dir.
    private const string HarborTlsDir = "/etc/nexus-registry/tls";
    private const string PgTlsDir = "/etc/nexus-registry-pg/tls";
    private const int HarborHttpsPort = 443;
    private const string RedisPwFile = "/etc/nexus-registry-pg/redis-password";

    // Harbor app docker-compose project dir + the canonical "is Harbor up" container.
    private const string HarborComposeDir = "/opt/harbor";

    // Vault KV (mount nexus); the registry secrets use the field name "value".
    private const string KvHarborAdminPath = "registry/harbor-admin";

    // MinIO S3 blob backend (lakehouse tier; new-root) — the cross-tier dependency.
    private const string MinioHealthUrl = "https://minio.nexus.lab:9000/minio/health/live";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CurlTimeout = TimeSpan.FromSeconds(12);

    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly HashSet<string> NftScenarios = new(StringComparer.OrdinalIgnoreCase)
        { "network-partition", "packet-loss" };

    // The Harbor user acl revoke must never demote (built-in break-glass operator).
    private static readonly string[] ProtectedHarborUsers = ["admin"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    // Cached VIP holder (for CanResizeVm after a status/health call).
    private string? _dbVipHolder;

    public RegistryAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
        _vault = vault;
    }

    public string ClusterId => ClusterName;
    public string DisplayName => DisplayNameConst;

    // === node classification ===============================================
    /// <summary>Map a vms.yaml platform-tools node name to its registry role. The
    /// unbuilt future tools (prefect/unleash/marquez/backstage) classify "other".</summary>
    internal static string ClassifyRole(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("registry-pg-", StringComparison.Ordinal)) return "registry-pg";
        if (n.StartsWith("registry-", StringComparison.Ordinal)) return "harbor";
        return "other";
    }

    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    /// <summary>The four registry-* nodes (filters out the unbuilt future platform tools).</summary>
    private Result<List<NodeRecord>> Nodes()
    {
        var cluster = _catalog.GetCluster(VmsCluster);
        if (cluster.IsFail) return Result.Fail<List<NodeRecord>>(cluster.Error!);
        var nodes = cluster.Value!.Nodes
            .Where(n => ClassifyRole(n.Name) != "other")
            .OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (nodes.Count == 0)
            return Result.Fail<List<NodeRecord>>($"vms.yaml cluster '{VmsCluster}' has no registry-* nodes");
        return Result.Ok(nodes);
    }

    private static List<NodeRecord> Role(List<NodeRecord> all, string role) =>
        all.Where(n => ClassifyRole(n.Name) == role).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

    // === SSH-local-curl (the access posture) ===============================
    /// <summary>
    /// curl a service endpoint locally on its own node. <paramref name="caPath"/>
    /// validates the leaf with the node's own ca (null = -k). Returns (httpCode, body).
    /// </summary>
    private async Task<(int Code, string Body)> CurlAsync(string ip, int port, string? caPath, string path, string? basicAuth, CancellationToken ct)
    {
        var ca = caPath is not null ? $"--cacert {caPath} " : "-k ";
        var auth = string.IsNullOrEmpty(basicAuth) ? "" : $"-u '{basicAuth}' ";
        var cmd = $"sudo curl -sS --max-time 8 {ca}{auth}https://127.0.0.1:{port}{path} -w '\\n__HTTP_%{{http_code}}__' 2>/dev/null";
        var r = await _ssh.ExecuteAsync(T(ip), cmd, CurlTimeout, ct).ConfigureAwait(false);
        if (r.IsFail || r.Value is null) return (0, "");
        var raw = r.Value.Stdout;
        var m = Regex.Match(raw, @"__HTTP_(\d+)__\s*$");
        var code = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var body = m.Success ? raw.Substring(0, m.Index) : raw;
        return (code, body.Trim());
    }

    /// <summary>Probe a Harbor app node's /api/v2.0/health (its own ca.crt). Returns (code, body).</summary>
    private Task<(int Code, string Body)> HarborHealthAsync(string ip, CancellationToken ct) =>
        CurlAsync(ip, HarborHttpsPort, $"{HarborTlsDir}/ca.crt", "/api/v2.0/health", null, ct);

    /// <summary>`systemctl is-active &lt;unit&gt;` on a node → true if "active".</summary>
    private async Task<bool> IsActiveAsync(string ip, string unit, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip), $"systemctl is-active {unit} 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim() == "active";
    }

    /// <summary>Which PG node currently holds the VRRP VIP (ip addr show nic0).</summary>
    private async Task<string?> VipHolderAsync(List<NodeRecord> pair, CancellationToken ct)
    {
        foreach (var n in pair)
        {
            var r = await _ssh.ExecuteAsync(T(n.Vmnet11), $"ip -4 -o addr show nic0 2>/dev/null | grep -c '{RegistryDbVip}'", SshTimeout, ct).ConfigureAwait(false);
            if (r.IsOk && r.Value!.Stdout.Trim().StartsWith('1')) return n.Name;
        }
        return null;
    }

    /// <summary>PG recovery state on a node → "primary" (f) | "replica" (t) | "" (unknown).</summary>
    private async Task<string> PgRoleAsync(string ip, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip), "sudo -u postgres psql -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        var v = r.IsOk ? r.Value!.Stdout.Trim() : "";
        return v == "f" ? "primary" : v == "t" ? "replica" : "";
    }

    // === parsing helpers (internal static for unit tests) ==================
    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// Parse Harbor <c>/api/v2.0/health</c> → (overallStatus, healthyComponents,
    /// totalComponents). Shape: <c>{"status":"healthy","components":[{"name":"core",
    /// "status":"healthy"},...]}</c>.
    /// </summary>
    internal static (string Status, int Healthy, int Total) ParseHarborHealth(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = Str(root, "status");
            int healthy = 0, total = 0;
            if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in comps.EnumerateArray())
                {
                    total++;
                    if (Str(c, "status").Equals("healthy", StringComparison.OrdinalIgnoreCase)) healthy++;
                }
            }
            return (status, healthy, total);
        }
        catch (JsonException) { return ("", 0, 0); }
    }

    /// <summary>Parse Harbor <c>/api/v2.0/systeminfo</c> → (harborVersion, authMode).</summary>
    internal static (string Version, string AuthMode) ParseHarborSystemInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return (Str(doc.RootElement, "harbor_version"), Str(doc.RootElement, "auth_mode"));
        }
        catch (JsonException) { return ("", ""); }
    }

    /// <summary>Parse Harbor <c>/api/v2.0/users</c> → list of (userId, username, isSysAdmin).</summary>
    internal static List<(int UserId, string Username, bool SysAdmin)> ParseHarborUsers(string json)
    {
        var list = new List<(int, string, bool)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var u in doc.RootElement.EnumerateArray())
            {
                var uid = u.TryGetProperty("user_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0;
                var name = Str(u, "username");
                var sys = u.TryGetProperty("sysadmin_flag", out var s) && s.ValueKind == JsonValueKind.True;
                list.Add((uid, name, sys));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Parse <c>redis-cli INFO replication</c> → (role, connectedSlaves|masterLinkUp).</summary>
    internal static (string Role, int Connected) ParseRedisReplication(string info)
    {
        if (string.IsNullOrEmpty(info)) return ("", 0);
        var role = Regex.Match(info, @"(?m)^role:(\w+)").Groups[1].Value;
        if (role == "master")
        {
            var cs = Regex.Match(info, @"(?m)^connected_slaves:(\d+)");
            return ("master", cs.Success ? int.Parse(cs.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
        }
        if (role == "slave")
        {
            var up = Regex.IsMatch(info, @"(?m)^master_link_status:up") ? 1 : 0;
            return ("slave", up);
        }
        return (role, 0);
    }

    /// <summary>Count a Harbor JSON array's elements (projects / robots) without binding a schema.</summary>
    internal static int CountJsonArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    // === lazy KV (Harbor admin password) ===================================
    private async Task<Result<string>> HarborAdminPasswordAsync(CancellationToken ct)
    {
        if (_vault is null)
            return Result.Fail<string>(
                "reading the Harbor admin password needs the operator token. Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        return await _vault.ReadKvFieldAsync("nexus", KvHarborAdminPath, "value", ct).ConfigureAwait(false);
    }

    private async Task<string> RedisInfoAsync(string ip, CancellationToken ct)
    {
        // Read the on-node Redis password file, then INFO replication over the local socket.
        var cmd = $"PW=$(sudo cat {RedisPwFile} 2>/dev/null); redis-cli -a \"$PW\" --no-auth-warning -p 6379 INFO replication 2>/dev/null";
        var r = await _ssh.ExecuteAsync(T(ip), cmd, SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value is not null ? r.Value.Stdout : "";
    }

    // === GetStatusAsync ====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ClusterStatus>(nodesR.Error!);
        var all = nodesR.Value!;

        var harbors = Role(all, "harbor");
        var pgs = Role(all, "registry-pg");
        _dbVipHolder = await VipHolderAsync(pgs, cancellationToken).ConfigureAwait(false);

        var members = new List<ClusterMember>();
        foreach (var n in harbors)
        {
            var (code, body) = await HarborHealthAsync(n.Vmnet11, cancellationToken).ConfigureAwait(false);
            var (status, healthy, total) = ParseHarborHealth(body);
            var alive = code == 200 && status.Equals("healthy", StringComparison.OrdinalIgnoreCase);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, "harbor-app",
                alive ? "alive" : code == 200 ? "syncing" : "failed"));
        }
        foreach (var n in pgs)
        {
            var pgRole = await PgRoleAsync(n.Vmnet11, cancellationToken).ConfigureAwait(false);
            var holdsVip = n.Name == _dbVipHolder;
            var label = pgRole switch
            {
                "primary" => holdsVip ? "datastore/primary+vip" : "datastore/primary",
                "replica" => "datastore/replica",
                _ => "datastore/unknown"
            };
            var active = await IsActiveAsync(n.Vmnet11, "postgresql@17-main", cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, label, active && pgRole != "" ? "alive" : "failed"));
        }

        var leader = pgs.FirstOrDefault(n => n.Name == _dbVipHolder)?.Name;
        var failed = members.Count(m => m.Status == "failed");
        var overall = failed == 0 && members.All(m => m.Status == "alive") ? "green" : failed >= 2 ? "red" : "yellow";
        return Result.Ok(new ClusterStatus(ClusterName, DisplayNameConst, overall, members, leader, DateTimeOffset.UtcNow));
    }

    // === HealthAsync =======================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<HealthReport>(nodesR.Error!);
        var all = nodesR.Value!;
        var probes = new List<HealthProbe>();

        var harbors = Role(all, "harbor");
        var pgs = Role(all, "registry-pg");

        // --- Harbor app: per-node health + component checklist (from whichever answers) ---
        int harborUp = 0;
        string compSummary = "no component data";
        foreach (var n in harbors)
        {
            var (code, body) = await HarborHealthAsync(n.Vmnet11, cancellationToken).ConfigureAwait(false);
            var (status, healthy, total) = ParseHarborHealth(body);
            if (code == 200 && status.Equals("healthy", StringComparison.OrdinalIgnoreCase))
            {
                harborUp++;
                compSummary = $"{healthy}/{total} components healthy";
            }
            else if (code == 200 && total > 0)
            {
                compSummary = $"{healthy}/{total} components healthy (status={status})";
            }
        }
        probes.Add(new HealthProbe("harbor-app", "harbor", harborUp == harbors.Count ? "green" : harborUp > 0 ? "yellow" : "red",
            $"{harborUp}/{harbors.Count} app nodes healthy", $"{harbors.Count} healthy (RR registry.nexus.lab)"));
        probes.Add(new HealthProbe("harbor-components", "harbor", harborUp > 0 && compSummary.Contains("status=", StringComparison.Ordinal) == false ? "green" : harborUp > 0 ? "yellow" : "red",
            compSummary, "all components healthy (core/db/redis/registry/jobservice/portal/trivy)"));

        // --- systeminfo: harbor version + auth_mode (OIDC SSO) ---
        if (harbors.Count > 0)
        {
            var (code, body) = await CurlAsync(harbors[0].Vmnet11, HarborHttpsPort, $"{HarborTlsDir}/ca.crt", "/api/v2.0/systeminfo", null, cancellationToken).ConfigureAwait(false);
            var (ver, auth) = ParseHarborSystemInfo(body);
            // The UNAUTHENTICATED /api/v2.0/systeminfo returns auth_mode but omits
            // harbor_version (version is only in the admin-authed response). auth_mode
            // is the meaningful unauthenticated signal (OIDC SSO configured) → gate on
            // it; surface version only when present.
            probes.Add(new HealthProbe("harbor-systeminfo", "harbor", code == 200 && auth.Length > 0 ? "green" : "yellow",
                code == 200 ? $"auth_mode={auth}{(ver.Length > 0 ? $", version={ver}" : "")}" : $"HTTP {code}", "auth_mode set (oidc_auth = Vault SSO)"));
        }

        // --- PG streaming replication (1 primary + 1 streaming standby) ---
        await PgReplicationHealthAsync(pgs, probes, cancellationToken).ConfigureAwait(false);

        // --- Redis replication (master + 1 connected replica) ---
        await RedisReplicationHealthAsync(pgs, probes, cancellationToken).ConfigureAwait(false);

        // --- S3 (MinIO) reachable — the Harbor blob backend (cross-tier; new-root) ---
        probes.Add(await S3ReachableAsync(harbors, cancellationToken).ConfigureAwait(false));

        // --- VRRP VIP bound to exactly one PG node ---
        var holder = await VipHolderAsync(pgs, cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("vip-registry-db", "keepalived", holder is not null ? "green" : "red",
            holder is not null ? $"{RegistryDbVip} on {holder}" : $"{RegistryDbVip} unbound", "bound to 1 node"));
        _dbVipHolder = holder;

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    private async Task PgReplicationHealthAsync(List<NodeRecord> pgs, List<HealthProbe> probes, CancellationToken ct)
    {
        string? primary = null, replica = null;
        foreach (var n in pgs)
        {
            var role = await PgRoleAsync(n.Vmnet11, ct).ConfigureAwait(false);
            if (role == "primary") primary = n.Vmnet11;
            else if (role == "replica") replica = n.Vmnet11;
        }
        if (primary is null)
        {
            probes.Add(new HealthProbe("pg-replication", "registry-pg", "red",
                replica is null ? "no primary detected" : "both nodes report standby — no primary", "1 primary + 1 streaming standby"));
            return;
        }
        var rep = await _ssh.ExecuteAsync(T(primary), "sudo -u postgres psql -tAc \"SELECT count(*) FROM pg_stat_replication WHERE state='streaming'\" 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        var streaming = rep.IsOk && int.TryParse(rep.Value!.Stdout.Trim(), out var c) ? c : 0;
        probes.Add(new HealthProbe("pg-replication", "registry-pg", streaming >= 1 && replica is not null ? "green" : "red",
            replica is null ? "both nodes are primary (split — no standby)" : $"{streaming} streaming standby",
            "1 streaming standby"));
    }

    private async Task RedisReplicationHealthAsync(List<NodeRecord> pgs, List<HealthProbe> probes, CancellationToken ct)
    {
        string? masterNode = null; int connected = 0; bool replicaLinked = false;
        foreach (var n in pgs)
        {
            var (role, conn) = ParseRedisReplication(await RedisInfoAsync(n.Vmnet11, ct).ConfigureAwait(false));
            if (role == "master") { masterNode = n.Name; connected = conn; }
            else if (role == "slave" && conn == 1) replicaLinked = true;
        }
        probes.Add(new HealthProbe("redis-replication", "redis", masterNode is not null && connected >= 1 && replicaLinked ? "green" : masterNode is not null ? "yellow" : "red",
            masterNode is null ? "no Redis master detected" : $"master={masterNode}, {connected} connected replica(s), link={(replicaLinked ? "up" : "down")}",
            "1 master + 1 linked replica"));
    }

    private async Task<HealthProbe> S3ReachableAsync(List<NodeRecord> harbors, CancellationToken ct)
    {
        var via = harbors.FirstOrDefault();
        if (via is null) return new HealthProbe("s3-backend", "minio", "yellow", "no harbor node to probe from", "MinIO reachable");
        var r = await _ssh.ExecuteAsync(T(via.Vmnet11),
            $"curl -sS --max-time 6 -k {MinioHealthUrl} -o /dev/null -w '%{{http_code}}' 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        var code = r.IsOk ? r.Value!.Stdout.Trim() : "000";
        return new HealthProbe("s3-backend", "minio", code == "200" ? "green" : "red",
            $"minio.nexus.lab:9000/health = {code}", "200 (Harbor s3://harbor blob backend)");
    }

    // === TopologyAsync =====================================================
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<TopologySnapshot>(nodesR.Error!);
        var all = nodesR.Value!;

        var harbors = Role(all, "harbor");
        var pgs = Role(all, "registry-pg");
        var holder = await VipHolderAsync(pgs, cancellationToken).ConfigureAwait(false);

        var nodes = new List<TopologyNode>();
        foreach (var n in harbors)
        {
            var (code, body) = await HarborHealthAsync(n.Vmnet11, cancellationToken).ConfigureAwait(false);
            var (status, healthy, total) = ParseHarborHealth(body);
            var alive = code == 200 && status.Equals("healthy", StringComparison.OrdinalIgnoreCase);
            nodes.Add(new TopologyNode(n.Name, $"harbor-app (docker-compose; RR registry.nexus.lab; {healthy}/{total} components)", alive ? "alive" : code == 200 ? "syncing" : "failed"));
        }
        foreach (var n in pgs)
        {
            var role = await PgRoleAsync(n.Vmnet11, cancellationToken).ConfigureAwait(false);
            var (rrole, conn) = ParseRedisReplication(await RedisInfoAsync(n.Vmnet11, cancellationToken).ConfigureAwait(false));
            var label = $"datastore: PG {(role.Length == 0 ? "?" : role)}" + (n.Name == holder ? " +VIP .119" : "") + $"; Redis {(rrole.Length == 0 ? "?" : rrole)}";
            nodes.Add(new TopologyNode(n.Name, label, role.Length > 0 ? "alive" : "failed"));
        }

        // VRRP VIP as a pseudo-node (the datastore front door).
        nodes.Add(new TopologyNode($"VIP {RegistryDbVip} (registry-db.nexus.lab)", $"VRRP front door → {holder ?? "unbound"}", holder is not null ? "alive" : "failed"));

        // Datastore backends as info pseudo-nodes.
        nodes.Add(new TopologyNode("blob-store", "MinIO s3://harbor (lakehouse tier; EC-durable; new-root)", "info"));

        // Registry is not sharded.
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (datastore VRRP cutover) ============================
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<FailoverResult>(nodesR.Error!);
        var all = nodesR.Value!;

        var direction = (request.Direction ?? "registry-db").Trim().ToLowerInvariant();
        if (direction is "harbor" or "app" or "registry-app")
            return Result.Fail<FailoverResult>(
                "the Harbor app pair (registry-1/2) has NO VIP — it is round-robin DNS (registry.nexus.lab); "
                + "clients retry the surviving node on connection failure. Fail over the DATASTORE instead: "
                + "`failover registry --direction registry-db` (the keepalived VRRP VIP .119 carrying PG + Redis).");
        if (direction is not ("registry-db" or "registry-pg" or "pg" or "db" or "datastore" or ".119"))
            return Result.Fail<FailoverResult>(
                $"unknown failover direction '{direction}'. Pass --direction registry-db (the datastore VRRP VIP .119). "
                + "The app tier is RR-DNS (no VIP).");

        var pgs = Role(all, "registry-pg");
        if (pgs.Count < 2) return Result.Fail<FailoverResult>("need the 2-node registry-pg datastore pair to fail over.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var preFlight = sw.Elapsed;

        var master = await VipHolderAsync(pgs, cancellationToken).ConfigureAwait(false);
        if (master is null)
            return Result.Fail<FailoverResult>($"VIP {RegistryDbVip} is not bound to either registry-pg node; refusing to fail over an unbound VIP.");
        var masterNode = pgs.First(n => n.Name == master);
        var backupNode = pgs.First(n => n.Name != master);

        // Inject: stop keepalived on the MASTER → the BACKUP claims the VIP; its notify_master
        // promotes PG (pg_ctl promote) + re-masters Redis (replicaof no one).
        var stop = await _ssh.ExecuteAsync(T(masterNode.Vmnet11), "sudo systemctl stop keepalived && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<FailoverResult>($"could not stop keepalived on {master}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 160))}");
        var injected = sw.Elapsed;

        // Poll for the VIP to land on the backup AND the backup PG to become primary.
        string? newHolder = null; bool promoted = false;
        var moveDeadline = sw.Elapsed + TimeSpan.FromSeconds(30);
        while (sw.Elapsed < moveDeadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            newHolder = await VipHolderAsync(pgs, cancellationToken).ConfigureAwait(false);
            if (newHolder == backupNode.Name)
            {
                promoted = await PgRoleAsync(backupNode.Vmnet11, cancellationToken).ConfigureAwait(false) == "primary";
                if (promoted) break;
            }
        }
        var observed = sw.Elapsed;

        // Recover: restart keepalived on the original master (nopreempt → it returns as BACKUP).
        var recovery = "skipped";
        string? recoveryHint = null;
        if (!request.NoRecover)
        {
            var restart = await _ssh.ExecuteAsync(T(masterNode.Vmnet11), "sudo systemctl start keepalived && echo STARTED", SshTimeout, cancellationToken).ConfigureAwait(false);
            recovery = restart.IsOk && restart.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal) ? "recovered" : "failed";
            if (recovery == "failed") recoveryHint = $"restart keepalived on {master} manually (`sudo systemctl start keepalived`).";
            else recoveryHint = $"{master} rejoined as standby (nopreempt); it must re-sync as a PG replica of {backupNode.Name} "
                + "(the keepalived notify_backup / DR runbook re-seeds it via pg_basebackup if streaming doesn't auto-resume).";
        }
        else recoveryHint = $"keepalived left stopped on {master} (--no-recover); restart it when ready.";
        var recovered = sw.Elapsed;
        sw.Stop();

        var moved = newHolder == backupNode.Name;
        return Result.Ok(new FailoverResult(
            Scenario: "vrrp-cutover:registry-db",
            OriginalPrimary: master,
            NewPrimary: moved ? backupNode.Name : newHolder,
            Rto: observed - injected,
            Recovery: recovery,
            RecoveryHint: moved && promoted ? recoveryHint
                : $"VIP/promotion did not complete on {backupNode.Name} within 30s (moved={moved}, pg-primary={promoted}) — check keepalived + the notify_master pg_ctl promote on the backup. {recoveryHint}",
            Timeline: new FailoverTimeline(preFlight, injected, observed, recovered, recovered),
            StartedAtUtc: startedAt));
    }

    // === ScaleOut (graceful actionable N/A) ================================
    public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(ScaleOutNaMessage));

    public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(ScaleOutNaMessage));

    private const string ScaleOutNaMessage =
        "scale-out is graceful N/A for the registry tier. The HA shape is FIXED by ADR-0036: a 2-node Harbor app pair "
        + "behind round-robin DNS (registry.nexus.lab) + a 2-node PostgreSQL/Redis datastore pair behind a VRRP VIP. "
        + "Capacity scales by image-layer storage in MinIO (s3://harbor — grow the lakehouse EC pool) and by vertical "
        + "resize (`scale-up`), not by adding registry nodes. Adding an app/datastore node is a terraform op in "
        + "nexus-infra-registry (new VM + overlay + re-apply); it is not a runtime adapter action.";

    // === Backup (pg_dump the Harbor metadata DB round-trip) ================
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<BackupResult>(nodesR.Error!);
        var pgs = Role(nodesR.Value!, "registry-pg");
        if (pgs.Count == 0) return Result.Fail<BackupResult>("no registry-pg node in vms.yaml cluster platform-tools.");

        // Dump on the current PG primary (the authoritative Harbor metadata: projects,
        // repositories, users, robots, replication rules). Blobs are EC-durable in MinIO
        // s3://harbor; Redis is ephemeral cache — neither is adapter-snapshotted here.
        var primaryNode = await FindPgPrimaryAsync(pgs, cancellationToken).ConfigureAwait(false);
        if (primaryNode is null) return Result.Fail<BackupResult>("no PG primary (pg_is_in_recovery=f) found among the registry-pg pair.");

        var tag = string.IsNullOrWhiteSpace(request.Tag) ? "registry" : Regex.Replace(request.Tag!, "[^A-Za-z0-9_.-]", "-");
        var dest = $"/var/tmp/nexus-registry-backup/{tag}.sql.gz";
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var script = string.Join(" && ", new[]
        {
            "sudo mkdir -p /var/tmp/nexus-registry-backup",
            $"sudo -u postgres pg_dump -d registry 2>/dev/null | gzip | sudo tee {dest} >/dev/null",
            $"echo BYTES=$(sudo stat -c%s {dest} 2>/dev/null)",
            $"echo TABLES=$(sudo -u postgres psql -tAc \"SELECT count(*) FROM information_schema.tables WHERE table_schema='public'\" -d registry 2>/dev/null)",
        });
        var r = await _ssh.ExecuteAsync(T(primaryNode.Vmnet11), script, SshTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<BackupResult>($"pg_dump of the registry DB failed: {r.Error}");
        var bytes = MatchLong(r.Value!.Stdout, @"BYTES=(\d+)");
        var tables = MatchInt(r.Value.Stdout, @"TABLES=(\d+)");
        if (bytes == 0) return Result.Fail<BackupResult>($"pg_dump produced an empty file at {primaryNode.Name}:{dest} (is the registry DB present?).");
        return Result.Ok(new BackupResult($"{tag} ({tables} tables @ {primaryNode.Name}:{dest})", $"{primaryNode.Name}:{dest}", bytes, sw.Elapsed, startedAt));
    }

    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<RestoreResult>(nodesR.Error!);
        var pgs = Role(nodesR.Value!, "registry-pg");
        var primaryNode = await FindPgPrimaryAsync(pgs, cancellationToken).ConfigureAwait(false);
        if (primaryNode is null) return Result.Fail<RestoreResult>("no PG primary found among the registry-pg pair.");

        var idTag = Regex.Match(request.BackupId ?? "", @"^(?<tag>[^ ]+)").Groups["tag"].Value;
        if (idTag.Length == 0) idTag = "registry";
        var src = $"/var/tmp/nexus-registry-backup/{idTag}.sql.gz";
        var verifyDb = "registry_restore_verify";
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Non-destructive: reload the dump into a throwaway verify DB and count restored tables.
        var script = string.Join(" && ", new[]
        {
            $"sudo test -f {src} || {{ echo MISSING; exit 0; }}",
            $"sudo -u postgres psql -tAc \"DROP DATABASE IF EXISTS {verifyDb}\" >/dev/null 2>&1",
            $"sudo -u postgres psql -tAc \"CREATE DATABASE {verifyDb}\" >/dev/null 2>&1",
            $"sudo cat {src} | gunzip | sudo -u postgres psql -d {verifyDb} >/dev/null 2>&1 || true",
            $"echo TABLES=$(sudo -u postgres psql -tAc \"SELECT count(*) FROM information_schema.tables WHERE table_schema='public'\" -d {verifyDb} 2>/dev/null)",
            $"sudo -u postgres psql -tAc \"DROP DATABASE IF EXISTS {verifyDb}\" >/dev/null 2>&1",
        });
        var r = await _ssh.ExecuteAsync(T(primaryNode.Vmnet11), script, SshTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<RestoreResult>($"restore round-trip failed: {r.Error}");
        if (r.Value!.Stdout.Contains("MISSING", StringComparison.Ordinal))
            return Result.Fail<RestoreResult>($"no backup found at {primaryNode.Name}:{src}; run `backup take {ClusterName}` first.");
        var tables = MatchInt(r.Value.Stdout, @"TABLES=(\d+)");
        return Result.Ok(new RestoreResult(idTag, tables, sw.Elapsed, startedAt));
    }

    private async Task<NodeRecord?> FindPgPrimaryAsync(List<NodeRecord> pgs, CancellationToken ct)
    {
        foreach (var n in pgs)
            if (await PgRoleAsync(n.Vmnet11, ct).ConfigureAwait(false) == "primary") return n;
        return null;
    }

    // === RotateCertAsync (force vault-agent re-render + reload) =============
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<CertRotationResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        // Order: app nodes first, then the datastore — VIP holder LAST (minimise blast radius).
        var pgs = Role(all, "registry-pg");
        var holder = await VipHolderAsync(pgs, cancellationToken).ConfigureAwait(false);
        var ordered = all
            .OrderBy(n => ClassifyRole(n.Name) == "harbor" ? 0 : 1)
            .ThenBy(n => n.Name == holder ? 1 : 0)
            .ThenBy(n => n.Name, StringComparer.Ordinal).ToList();

        foreach (var n in ordered)
        {
            var role = ClassifyRole(n.Name);
            // App leaf = harbor.crt (split from bundle.pem; nginx serves it); datastore = server.crt.
            var (tlsDir, leaf, reload) = role == "harbor"
                ? (HarborTlsDir, "harbor.crt", $"cd {HarborComposeDir} && sudo docker compose restart nginx >/dev/null 2>&1")
                : (PgTlsDir, "server.crt", "sudo systemctl reload postgresql@17-main");
            var oldSerial = await WireSerialAsync(n.Vmnet11, $"{tlsDir}/{leaf}", cancellationToken).ConfigureAwait(false);

            // Force the vault-agent's pkiCert to re-issue: back up + remove the rendered
            // bundle (it PERSISTS + reuses the leaf otherwise — the Swarm/obs lesson), restart
            // the agent, wait for the re-render, restore any bundle that didn't reappear, reload.
            var bundle = $"{tlsDir}/bundle.pem";
            var script = string.Join(" ; ", new[]
            {
                $"if sudo test -f \"{bundle}\"; then sudo cp -a \"{bundle}\" \"{bundle}.bak\"; sudo rm -f \"{bundle}\"; fi",
                "sudo systemctl restart nexus-vault-agent",
                $"for i in $(seq 1 25); do sudo test -f \"{bundle}\" && break; sleep 1; done",
                $"if sudo test -f \"{bundle}.bak\"; then if sudo test -f \"{bundle}\"; then sudo rm -f \"{bundle}.bak\"; else sudo mv \"{bundle}.bak\" \"{bundle}\"; fi; fi",
                reload,
                "echo ROTATED",
            });
            var exec = await _ssh.ExecuteAsync(T(n.Vmnet11), script, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail || !exec.Value!.Stdout.Contains("ROTATED", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(n.Name, oldSerial, "(unchanged)",
                    Error: exec.IsFail ? exec.Error : $"force-rerender/reload failed: {Tail(exec.Value!.Stdout + exec.Value.Stderr, 200)}"));
                continue;
            }
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var newSerial = await WireSerialAsync(n.Vmnet11, $"{tlsDir}/{leaf}", cancellationToken).ConfigureAwait(false);
            rotated.Add(new CertRotatedNode(n.Name, oldSerial, newSerial, Error: null));
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    /// <summary>Read a node's current leaf serial (proof of rotation).</summary>
    private async Task<string> WireSerialAsync(string ip, string certPath, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip),
            $"sudo openssl x509 -in {certPath} -noout -serial 2>/dev/null | sed 's/serial=//'", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim().Length > 0 ? r.Value.Stdout.Trim() : "(unknown)";
    }

    // === AclAsync (Harbor users via /api/v2.0/users) =======================
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<AclSnapshot>(nodesR.Error!);
        var harbors = Role(nodesR.Value!, "harbor");
        if (harbors.Count == 0) return Result.Fail<AclSnapshot>("no harbor app node in vms.yaml cluster platform-tools.");
        var pwR = await HarborAdminPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwR.IsFail) return Result.Fail<AclSnapshot>(pwR.Error!);
        var auth = $"admin:{pwR.Value}";
        var via = harbors[0].Vmnet11;
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var (code, body) = await CurlAsync(via, HarborHttpsPort, $"{HarborTlsDir}/ca.crt", "/api/v2.0/users", auth, cancellationToken).ConfigureAwait(false);
            if (code == 401)
                return Result.Fail<AclSnapshot>(
                    "Harbor refused the admin credential from Vault KV (nexus/registry/harbor-admin) — HTTP 401. "
                    + "The live Harbor admin password has drifted from KV (or auth_mode=oidc_auth without the local admin override). "
                    + "Reconcile the admin password, or use the OIDC break-glass admin, then retry.");
            if (code != 200) return Result.Fail<AclSnapshot>($"Harbor /api/v2.0/users returned HTTP {code}.");

            // Enrich the snapshot with project + robot counts (surfaced via a pseudo-user).
            var (_, projBody) = await CurlAsync(via, HarborHttpsPort, $"{HarborTlsDir}/ca.crt", "/api/v2.0/projects", auth, cancellationToken).ConfigureAwait(false);
            var (_, robotBody) = await CurlAsync(via, HarborHttpsPort, $"{HarborTlsDir}/ca.crt", "/api/v2.0/robots", auth, cancellationToken).ConfigureAwait(false);
            var projects = CountJsonArray(projBody);
            var robots = CountJsonArray(robotBody);

            var users = ParseHarborUsers(body).Select(u => new AclUser(
                u.Username,
                BuildPerms(u.SysAdmin, ProtectedHarborUsers.Contains(u.Username, StringComparer.OrdinalIgnoreCase)),
                Enabled: true)).ToList();
            users.Add(new AclUser($"(harbor scope: {projects} projects, {robots} robot accounts)", ["info"], Enabled: true));
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user (a Harbor username to promote/demote to system admin).");
            var login = operation.User!;
            if (verb == "revoke" && ProtectedHarborUsers.Contains(login, StringComparer.OrdinalIgnoreCase))
                return Result.Fail<AclSnapshot>($"refusing to demote the protected Harbor '{login}' user (built-in break-glass operator).");

            var (lcode, lbody) = await CurlAsync(via, HarborHttpsPort, $"{HarborTlsDir}/ca.crt", "/api/v2.0/users", auth, cancellationToken).ConfigureAwait(false);
            if (lcode == 401) return Result.Fail<AclSnapshot>("Harbor admin credential drift (HTTP 401) — see `acl list` for the reconcile note.");
            if (lcode != 200) return Result.Fail<AclSnapshot>($"Harbor /api/v2.0/users returned HTTP {lcode}.");
            var hit = ParseHarborUsers(lbody).FirstOrDefault(u => string.Equals(u.Username, login, StringComparison.OrdinalIgnoreCase));
            if (hit.UserId == 0) return Result.Fail<AclSnapshot>($"no Harbor user '{login}'.");

            var sysadmin = verb == "grant" ? "true" : "false";
            var putCmd =
                $"sudo curl -sS --max-time 8 --cacert {HarborTlsDir}/ca.crt -u '{auth}' -X PUT -H 'Content-Type: application/json' "
                + $"-d '{{\"sysadmin_flag\":{sysadmin}}}' https://127.0.0.1:{HarborHttpsPort}/api/v2.0/users/{hit.UserId}/sysadmin -w '__HTTP_%{{http_code}}__' 2>/dev/null";
            var permR = await _ssh.ExecuteAsync(T(via), putCmd, CurlTimeout, cancellationToken).ConfigureAwait(false);
            if (permR.IsFail || !Regex.IsMatch(permR.Value!.Stdout, @"__HTTP_(200|201)__"))
                return Result.Fail<AclSnapshot>($"Harbor sysadmin {verb} for '{login}' failed: {(permR.IsFail ? permR.Error : Tail(permR.Value!.Stdout, 160))}");
            return await AclAsync(new AclOperation("list"), cancellationToken).ConfigureAwait(false);
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke.");
    }

    private static List<string> BuildPerms(bool sysAdmin, bool protectedUser)
    {
        var perms = new List<string> { sysAdmin ? "sysadmin" : "user" };
        if (protectedUser) perms.Add("protected");
        return perms;
    }

    // === ApplyChaosAsync (nexus-chaos.sh on a non-VIP node) ================
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ChaosOutcome>(nodesR.Error!);
        var all = nodesR.Value!;

        var harbors = Role(all, "harbor");
        var pgs = Role(all, "registry-pg");
        var dbHolder = await VipHolderAsync(pgs, cancellationToken).ConfigureAwait(false);

        // Default victim: a Harbor app node (the RR-DNS pair tolerates losing one).
        NodeRecord victim;
        if (!string.IsNullOrWhiteSpace(scenario.Target))
        {
            var t = all.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase));
            if (t is null) return Result.Fail<ChaosOutcome>($"chaos target '{scenario.Target}' is not a registry node.");
            if (t.Name == dbHolder)
                return Result.Fail<ChaosOutcome>($"'{t.Name}' currently holds the datastore VRRP VIP {RegistryDbVip}; pick a non-VIP node (a registry-1/2 app node is safest) or fail the VIP over first.");
            victim = t;
        }
        else
        {
            victim = harbors.FirstOrDefault() ?? pgs.First(n => n.Name != dbHolder);
        }

        var role = ClassifyRole(victim.Name);
        // process-kill targets a systemd unit: docker on an app node (compose restarts), redis on a datastore node.
        var killUnit = role == "harbor" ? "docker" : "redis-server";
        var target = T(victim.Vmnet11);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var isProcKill = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase);
        var helperTarget = isProcKill ? killUnit : "";
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
        {
            // docker: restart it + bring the compose stack back; redis: restart the unit.
            var recoverKill = role == "harbor"
                ? $"sudo systemctl reset-failed {killUnit} 2>/dev/null; sudo systemctl start {killUnit} 2>/dev/null; cd {HarborComposeDir} && sudo docker compose up -d >/dev/null 2>&1; exit 0"
                : $"sudo systemctl reset-failed {killUnit} 2>/dev/null; sudo systemctl start {killUnit} 2>/dev/null; exit 0";
            await _ssh.ExecuteAsync(target, recoverKill, SshTimeout, cancellationToken).ConfigureAwait(false);
        }

        // Recover: poll the victim back to healthy (Harbor health for app, PG/Redis active for datastore).
        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(90);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (role == "harbor")
            {
                var (code, body) = await HarborHealthAsync(victim.Vmnet11, cancellationToken).ConfigureAwait(false);
                if (code == 200 && ParseHarborHealth(body).Status.Equals("healthy", StringComparison.OrdinalIgnoreCase)) { recovered = true; break; }
            }
            else
            {
                if (await IsActiveAsync(victim.Vmnet11, "redis-server", cancellationToken).ConfigureAwait(false)) { recovered = true; break; }
            }
        }
        sw.Stop();
        return Result.Ok(new ChaosOutcome(scenario.ScenarioType, victim.Name, observed, sw.Elapsed, startedAt, recovered));
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
    public bool CanResizeVm(string vmName, string role)
    {
        // Refuse the current datastore VIP holder (resizing it flaps PG + Redis); everything else is safe
        // (the app nodes are stateless behind RR DNS; the standby datastore node carries no live VIP).
        if (string.Equals(vmName, _dbVipHolder, StringComparison.OrdinalIgnoreCase)) return false;
        return ClassifyRole(vmName) is not "other";
    }

    // === helpers ===========================================================
    private static int MatchInt(string s, string pattern)
    {
        var m = Regex.Match(s ?? "", pattern);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static long MatchLong(string s, string pattern)
    {
        var m = Regex.Match(s ?? "", pattern);
        return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
}
