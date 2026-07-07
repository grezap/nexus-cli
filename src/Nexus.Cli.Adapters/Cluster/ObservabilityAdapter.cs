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
/// Observability-tier adapter (nexus-cli v0.8.3, Phase 0.I, ADR-0024) — the THIRD
/// non-data-tier adapter. Manages the Grafana LGTM stack (Prometheus + Loki +
/// Grafana + Tempo + Alertmanager + OTel Collector) over 14 VMs + 2 VRRP VIPs.
/// <para>
/// Nodes (vms.yaml cluster <c>observability</c>): prom-1/2 (Prometheus + Alertmanager
/// mesh), loki-1/2/3 (simple-scalable, memberlist ring), tempo-1/2/3 (scalable,
/// memberlist ring), grafana-1/2 (active-active over shared PG, VRRP VIP
/// <c>.184</c>), grafana-pg-1/2 (PG17 streaming repl, VRRP VIP <c>.185</c>),
/// otel-collector-1/2 (OTLP receivers, RR DNS). MinIO (lakehouse tier) is the S3
/// backend for Loki + Tempo.
/// </para>
/// <para>
/// Access posture (a deliberate divergence from the build-host-HTTP shape of
/// VaultAdapter/SwarmAdapter, forced by the LIVE contract — see ADR-0024 §Access):
/// the service health endpoints are HTTPS with <c>client_auth_type:NoClientCert</c>
/// but their leaves are issued by the observability tier's OWN CA generation, which
/// (because the tier was offline during the v0.8.1 Vault greenfield) can differ from
/// the build host's current <c>vault-ca-bundle.crt</c> root. So the adapter probes
/// each endpoint over SSH with the node's own <c>ca.crt</c> (always self-consistent),
/// reads runtime credentials from Vault KV via the build-host
/// <see cref="INexusVaultClient"/> (Vault is reachable + CA-valid from the build
/// host), and drives all service control / PG / VRRP / chaos over node SSH. OTel's
/// health extension is loopback-only (127.0.0.1:13133) so it is ALWAYS probed
/// on-node. No managed Prometheus/Grafana/Loki driver — NetArchTest-clean.
/// </para>
/// <list type="bullet">
///   <item>status = per-service up + VIP holders (.184/.185) + Loki/Tempo ring counts.</item>
///   <item>health = each /ready|/api/health + Prom targets-up + AM mesh peers +
///   Grafana-PG streaming replication + S3 (MinIO) reachable + VIPs bound.</item>
///   <item>topology = 14 nodes + 2 VIPs + Loki/Tempo memberlist + Prom scrape-target count.</item>
///   <item>failover = Grafana / Grafana-PG VRRP cutover (stop keepalived on the
///   MASTER → poll the VIP move → restart; RTO measured).</item>
///   <item>scale-out = Loki/Tempo ring add/remove (memberlist self-heals ~60s);
///   Prometheus/Grafana HA are FIXED at 2 (scrape-all / VRRP) → graceful N/A.</item>
///   <item>cert-rotate = re-issue every node's leaf from <c>pki_int/observability-server</c>
///   on the build host + SSH-push + per-service reload (SIGHUP Prom/AM/Loki/Tempo,
///   restart Grafana/OTel).</item>
///   <item>acl = Grafana users via <c>/api/admin/users</c> (admin protected).</item>
///   <item>chaos = nexus-chaos.sh on a ring node + recover-to-green.</item>
///   <item>backup = graceful actionable N/A (state durable elsewhere: MinIO EC +
///   Grafana-PG repl RPO0 + dashboards provisioned-as-code; Prom TSDB ephemeral).</item>
/// </list>
/// </summary>
public sealed class ObservabilityAdapter : IClusterAdapter
{
    private const string ClusterName = "observability";
    private const string DisplayNameConst = "Observability tier (Grafana LGTM: Prometheus + Loki + Grafana + Tempo + Alertmanager + OTel)";
    private const string VmsCluster = "observability";

    // VRRP VIPs (vms.yaml virtual_ips; the front doors for the two HA pairs).
    private const string GrafanaVip = "192.168.70.184";
    private const string GrafanaDbVip = "192.168.70.185";

    // Grafana-PG canonical primary backplane (vms.yaml grafana-pg-1 vmnet10).
    private const string GrafanaPgPrimaryBackplane = "192.168.10.180";

    // Vault KV (mount nexus); EVERY observability secret uses the field name "value".
    private const string KvAdminPwPath = "observability/grafana/admin-password";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CurlTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly HashSet<string> NftScenarios = new(StringComparer.OrdinalIgnoreCase)
        { "network-partition", "packet-loss" };

    // Grafana logins acl revoke must never disable (built-in operator identity).
    private static readonly string[] ProtectedGrafanaUsers = ["admin"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    // Cached VIP holders (for CanResizeVm after a status/health call).
    private string? _grafanaVipHolder;
    private string? _grafanaDbVipHolder;

    public ObservabilityAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
        _vault = vault;
    }

    public string ClusterId => ClusterName;
    public string DisplayName => DisplayNameConst;

    // === per-role service contract (from the live probe) ===================
    /// <summary>Static per-role facts: the systemd unit, HTTPS port, TLS dir, readiness path, and reload mode.</summary>
    internal sealed record RoleSpec(string Role, string Unit, int Port, string TlsDir, string ReadyPath, bool ReloadIsRestart, bool LoopbackHttp);

    private static readonly RoleSpec PrometheusSpec = new("prometheus", "nexus-prometheus", 9090, "/etc/nexus-prometheus/tls", "/-/ready", false, false);
    private static readonly RoleSpec AlertmanagerSpec = new("alertmanager", "nexus-alertmanager", 9093, "/etc/nexus-alertmanager/tls", "/-/ready", false, false);
    // Loki + Tempo do NOT cleanly pick up a rotated cert on SIGHUP (a reload after a cert
    // swap leaves them inactive) → cert-rotate RESTARTS them (rolling, the ring tolerates it).
    private static readonly RoleSpec LokiSpec = new("loki", "nexus-loki", 3100, "/etc/nexus-loki/tls", "/ready", true, false);
    private static readonly RoleSpec TempoSpec = new("tempo", "nexus-tempo", 3200, "/etc/nexus-tempo/tls", "/ready", true, false);
    private static readonly RoleSpec GrafanaSpec = new("grafana", "grafana-server", 3000, "/etc/nexus-grafana/tls", "/api/health", true, false);
    private static readonly RoleSpec GrafanaPgSpec = new("grafana-pg", "postgresql@17-main", 5432, "/etc/nexus-grafana-pg/tls", "", true, false);
    private static readonly RoleSpec OtelSpec = new("otel", "nexus-otel-collector", 13133, "/etc/nexus-otel-collector/tls", "/", true, true);

    /// <summary>Map a vms.yaml node name to its observability role.</summary>
    internal static string ClassifyRole(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("prom-", StringComparison.Ordinal)) return "prometheus";
        if (n.StartsWith("loki-", StringComparison.Ordinal)) return "loki";
        if (n.StartsWith("tempo-", StringComparison.Ordinal)) return "tempo";
        if (n.StartsWith("grafana-pg-", StringComparison.Ordinal)) return "grafana-pg";
        if (n.StartsWith("grafana-", StringComparison.Ordinal)) return "grafana";
        if (n.StartsWith("otel-collector-", StringComparison.Ordinal)) return "otel";
        return "other";
    }

    private static RoleSpec SpecFor(string role) => role switch
    {
        "prometheus" => PrometheusSpec,
        "loki" => LokiSpec,
        "tempo" => TempoSpec,
        "grafana" => GrafanaSpec,
        "grafana-pg" => GrafanaPgSpec,
        "otel" => OtelSpec,
        _ => PrometheusSpec
    };

    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    private Result<List<NodeRecord>> Nodes()
    {
        var cluster = _catalog.GetCluster(VmsCluster);
        if (cluster.IsFail) return Result.Fail<List<NodeRecord>>(cluster.Error!);
        var nodes = cluster.Value!.Nodes.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (nodes.Count == 0) return Result.Fail<List<NodeRecord>>($"vms.yaml cluster '{VmsCluster}' has no nodes");
        return Result.Ok(nodes);
    }

    private static List<NodeRecord> Role(List<NodeRecord> all, string role) =>
        all.Where(n => ClassifyRole(n.Name) == role).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

    // === SSH-local-curl (the access posture) ===============================
    /// <summary>
    /// curl a service's HTTPS endpoint locally on its own node, validating with the
    /// node's own ca.crt (always self-consistent regardless of the build host's CA
    /// generation). Returns (httpCode, body). OTel's loopback health is plain HTTP.
    /// </summary>
    private async Task<(int Code, string Body)> CurlAsync(string ip, RoleSpec spec, string path, string? basicAuth, CancellationToken ct)
    {
        var scheme = spec.LoopbackHttp ? "http" : "https";
        var ca = spec.LoopbackHttp ? "" : $"--cacert {spec.TlsDir}/ca.crt ";
        var auth = string.IsNullOrEmpty(basicAuth) ? "" : $"-u '{basicAuth}' ";
        var cmd = $"sudo curl -sS --max-time 8 {ca}{auth}{scheme}://127.0.0.1:{spec.Port}{path} -w '\\n__HTTP_%{{http_code}}__' 2>/dev/null";
        var r = await _ssh.ExecuteAsync(T(ip), cmd, CurlTimeout, ct).ConfigureAwait(false);
        if (r.IsFail || r.Value is null) return (0, "");
        var raw = r.Value.Stdout;
        var m = Regex.Match(raw, @"__HTTP_(\d+)__\s*$");
        var code = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var body = m.Success ? raw.Substring(0, m.Index) : raw;
        return (code, body.Trim());
    }

    /// <summary>`systemctl is-active <unit>` on a node → true if "active".</summary>
    private async Task<bool> IsActiveAsync(string ip, string unit, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip), $"systemctl is-active {unit} 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim() == "active";
    }

    /// <summary>Which node of a pair currently holds the VRRP VIP (ip addr show nic0).</summary>
    private async Task<string?> VipHolderAsync(List<NodeRecord> pair, string vip, CancellationToken ct)
    {
        foreach (var n in pair)
        {
            var r = await _ssh.ExecuteAsync(T(n.Vmnet11), $"ip -4 -o addr show nic0 2>/dev/null | grep -c '{vip}'", SshTimeout, ct).ConfigureAwait(false);
            if (r.IsOk && r.Value!.Stdout.Trim().StartsWith('1')) return n.Name;
        }
        return null;
    }

    // === parsing helpers (internal static for unit tests) ==================
    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Parse Prometheus /api/v1/targets → (active total, up count).</summary>
    internal static (int Active, int Up) ParsePromTargets(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return (0, 0);
            if (!data.TryGetProperty("activeTargets", out var at) || at.ValueKind != JsonValueKind.Array) return (0, 0);
            int total = 0, up = 0;
            foreach (var t in at.EnumerateArray())
            {
                total++;
                if (Str(t, "health").Equals("up", StringComparison.OrdinalIgnoreCase)) up++;
            }
            return (total, up);
        }
        catch (JsonException) { return (0, 0); }
    }

    /// <summary>Parse Alertmanager /api/v2/status → (peer count, status string).</summary>
    internal static (int Peers, string Status) ParseAmPeers(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cluster", out var c)) return (0, "");
            var status = Str(c, "status");
            var peers = c.TryGetProperty("peers", out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : 0;
            return (peers, status);
        }
        catch (JsonException) { return (0, ""); }
    }

    /// <summary>Count distinct ring members of a given role-prefix in a Loki/Tempo /memberlist HTML page.</summary>
    internal static int ParseMemberlistCount(string html, string rolePrefix)
    {
        if (string.IsNullOrEmpty(html)) return 0;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, rolePrefix + @"-\d+", RegexOptions.IgnoreCase))
            set.Add(m.Value.ToLowerInvariant());
        return set.Count;
    }

    /// <summary>Parse Grafana /api/health → (database, version).</summary>
    internal static (string Database, string Version) ParseGrafanaHealth(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return (Str(doc.RootElement, "database"), Str(doc.RootElement, "version"));
        }
        catch (JsonException) { return ("", ""); }
    }

    /// <summary>
    /// Parse Grafana <c>/api/org/users</c> → list of (userId, login, role). The
    /// org-scoped endpoint is used in preference to <c>/api/admin/users</c>, which
    /// 404s under basic auth on the live tier even for a Grafana server admin (the
    /// cold-rebuild live-caught this once the admin-password drift was fixed).
    /// </summary>
    internal static List<(int UserId, string Login, string Role)> ParseGrafanaOrgUsers(string json)
    {
        var list = new List<(int, string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var u in doc.RootElement.EnumerateArray())
            {
                var login = Str(u, "login");
                if (login.Length == 0) login = Str(u, "name");
                var role = Str(u, "role");
                var uid = u.TryGetProperty("userId", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : 0;
                list.Add((uid, login, role));
            }
        }
        catch (JsonException) { }
        return list;
    }

    // === lazy KV (Grafana admin password) ==================================
    private async Task<Result<string>> AdminPasswordAsync(CancellationToken ct)
    {
        if (_vault is null)
            return Result.Fail<string>(
                "reading the Grafana admin password needs the operator token. Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        return await _vault.ReadKvFieldAsync("nexus", KvAdminPwPath, "value", ct).ConfigureAwait(false);
    }

    // === GetStatusAsync ====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ClusterStatus>(nodesR.Error!);
        var all = nodesR.Value!;

        var grafanaPair = Role(all, "grafana");
        var pgPair = Role(all, "grafana-pg");
        _grafanaVipHolder = await VipHolderAsync(grafanaPair, GrafanaVip, cancellationToken).ConfigureAwait(false);
        _grafanaDbVipHolder = await VipHolderAsync(pgPair, GrafanaDbVip, cancellationToken).ConfigureAwait(false);

        var members = new List<ClusterMember>();
        foreach (var n in all)
        {
            var role = ClassifyRole(n.Name);
            var spec = SpecFor(role);
            var active = await IsActiveAsync(n.Vmnet11, spec.Unit, cancellationToken).ConfigureAwait(false);
            var roleLabel = role switch
            {
                "prometheus" => "prometheus+alertmanager",
                "grafana" => n.Name == _grafanaVipHolder ? "grafana/vip-master" : "grafana/backup",
                "grafana-pg" => n.Name == _grafanaDbVipHolder ? "grafana-pg/vip-master" : "grafana-pg/backup",
                _ => role
            };
            members.Add(new ClusterMember(n.Name, n.Vmnet11, roleLabel, active ? "alive" : "failed"));
        }

        // Ring counts (informational, surfaced through the leader field as a summary is not ideal;
        // keep leader=null — observability has no single leader — and let health/topology carry rings).
        var allAlive = members.All(m => m.Status == "alive");
        var anyDown = members.Any(m => m.Status == "failed");
        var overall = allAlive ? "green" : anyDown && members.Count(m => m.Status == "failed") >= 2 ? "red" : "yellow";

        return Result.Ok(new ClusterStatus(ClusterName, DisplayNameConst, overall, members, Leader: null, DateTimeOffset.UtcNow));
    }

    // === HealthAsync =======================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<HealthReport>(nodesR.Error!);
        var all = nodesR.Value!;
        var probes = new List<HealthProbe>();

        var proms = Role(all, "prometheus");
        var lokis = Role(all, "loki");
        var tempos = Role(all, "tempo");
        var grafanas = Role(all, "grafana");
        var pgs = Role(all, "grafana-pg");
        var otels = Role(all, "otel");

        // --- Prometheus: per-node ready + targets-up (from whichever Prom answers) ---
        int promReady = 0;
        foreach (var n in proms)
        {
            var (code, _) = await CurlAsync(n.Vmnet11, PrometheusSpec, "/-/ready", null, cancellationToken).ConfigureAwait(false);
            if (code == 200) promReady++;
        }
        probes.Add(new HealthProbe("prometheus-ready", "prometheus", promReady == proms.Count ? "green" : promReady > 0 ? "yellow" : "red",
            $"{promReady}/{proms.Count} ready", $"{proms.Count} ready"));
        if (proms.Count > 0)
        {
            var (tcode, tbody) = await CurlAsync(proms[0].Vmnet11, PrometheusSpec, "/api/v1/targets?state=active", null, cancellationToken).ConfigureAwait(false);
            var (active, up) = ParsePromTargets(tbody);
            probes.Add(new HealthProbe("prometheus-targets", "prometheus", tcode == 200 && active > 0 && up == active ? "green" : up > 0 ? "yellow" : "red",
                $"{up}/{active} scrape targets up", "all active targets up"));
        }

        // --- Alertmanager mesh peers ---
        if (proms.Count > 0)
        {
            var (acode, abody) = await CurlAsync(proms[0].Vmnet11, AlertmanagerSpec, "/api/v2/status", null, cancellationToken).ConfigureAwait(false);
            var (peers, status) = ParseAmPeers(abody);
            probes.Add(new HealthProbe("alertmanager-mesh", "alertmanager", acode == 200 && peers == proms.Count && status.Equals("ready", StringComparison.OrdinalIgnoreCase) ? "green" : "yellow",
                $"{peers} peers, status={status}", $"{proms.Count} peers ready"));
        }

        // --- Loki: per-node ready + ring count ---
        await RingHealthAsync(lokis, LokiSpec, "loki", probes, cancellationToken).ConfigureAwait(false);
        // --- Tempo: per-node ready + ring count ---
        await RingHealthAsync(tempos, TempoSpec, "tempo", probes, cancellationToken).ConfigureAwait(false);

        // --- Grafana: /api/health database=ok per node ---
        int grafOk = 0;
        foreach (var n in grafanas)
        {
            var (code, body) = await CurlAsync(n.Vmnet11, GrafanaSpec, "/api/health", null, cancellationToken).ConfigureAwait(false);
            var (db, _) = ParseGrafanaHealth(body);
            if (code == 200 && db.Equals("ok", StringComparison.OrdinalIgnoreCase)) grafOk++;
        }
        probes.Add(new HealthProbe("grafana-health", "grafana", grafOk == grafanas.Count ? "green" : grafOk > 0 ? "yellow" : "red",
            $"{grafOk}/{grafanas.Count} database=ok", $"{grafanas.Count} database=ok"));

        // --- OTel: loopback health per node ---
        int otelOk = 0;
        foreach (var n in otels)
        {
            var (code, _) = await CurlAsync(n.Vmnet11, OtelSpec, "/", null, cancellationToken).ConfigureAwait(false);
            if (code == 200) otelOk++;
        }
        probes.Add(new HealthProbe("otel-health", "otel", otelOk == otels.Count ? "green" : otelOk > 0 ? "yellow" : "red",
            $"{otelOk}/{otels.Count} healthy", $"{otels.Count} healthy"));

        // --- Grafana-PG streaming replication ---
        await PgReplicationHealthAsync(pgs, probes, cancellationToken).ConfigureAwait(false);

        // --- S3 (MinIO) reachable — the Loki/Tempo object store backend ---
        var s3 = await S3ReachableAsync(all, cancellationToken).ConfigureAwait(false);
        probes.Add(s3);

        // --- VRRP VIPs bound to exactly one node each ---
        var gHolder = await VipHolderAsync(grafanas, GrafanaVip, cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("vip-grafana", "keepalived", gHolder is not null ? "green" : "red",
            gHolder is not null ? $"{GrafanaVip} on {gHolder}" : $"{GrafanaVip} unbound", "bound to 1 node"));
        var pgHolder = await VipHolderAsync(pgs, GrafanaDbVip, cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("vip-grafana-db", "keepalived", pgHolder is not null ? "green" : "red",
            pgHolder is not null ? $"{GrafanaDbVip} on {pgHolder}" : $"{GrafanaDbVip} unbound", "bound to 1 node"));
        _grafanaVipHolder = gHolder;
        _grafanaDbVipHolder = pgHolder;

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    private async Task RingHealthAsync(List<NodeRecord> ring, RoleSpec spec, string label, List<HealthProbe> probes, CancellationToken ct)
    {
        int ready = 0;
        foreach (var n in ring)
        {
            var (code, _) = await CurlAsync(n.Vmnet11, spec, "/ready", null, ct).ConfigureAwait(false);
            if (code == 200) ready++;
        }
        probes.Add(new HealthProbe($"{label}-ready", label, ready == ring.Count ? "green" : ready > 0 ? "yellow" : "red",
            $"{ready}/{ring.Count} ready", $"{ring.Count} ready"));
        if (ring.Count > 0)
        {
            var (_, body) = await CurlAsync(ring[0].Vmnet11, spec, "/memberlist", null, ct).ConfigureAwait(false);
            var count = ParseMemberlistCount(body, label);
            probes.Add(new HealthProbe($"{label}-ring", label, count == ring.Count ? "green" : count > 0 ? "yellow" : "red",
                $"{count}/{ring.Count} ring members", $"{ring.Count} members"));
        }
    }

    private async Task PgReplicationHealthAsync(List<NodeRecord> pgs, List<HealthProbe> probes, CancellationToken ct)
    {
        // Dynamic primary detection (nopreempt VRRP — the primary can be either node).
        string? primary = null, replica = null;
        foreach (var n in pgs)
        {
            var r = await _ssh.ExecuteAsync(T(n.Vmnet11), "sudo -u postgres psql -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
            var v = r.IsOk ? r.Value!.Stdout.Trim() : "";
            if (v == "f") primary = n.Vmnet11;
            else if (v == "t") replica = n.Vmnet11;
        }
        if (primary is null)
        {
            probes.Add(new HealthProbe("grafana-pg-replication", "grafana-pg", "red", "no primary in_recovery=f detected", "1 primary + 1 streaming standby"));
            return;
        }
        var rep = await _ssh.ExecuteAsync(T(primary), "sudo -u postgres psql -tAc \"SELECT count(*) FROM pg_stat_replication WHERE state='streaming'\" 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        var streaming = rep.IsOk && int.TryParse(rep.Value!.Stdout.Trim(), out var c) ? c : 0;
        probes.Add(new HealthProbe("grafana-pg-replication", "grafana-pg", streaming >= 1 && replica is not null ? "green" : "red",
            replica is null ? "both nodes are primary (split — no standby)" : $"{streaming} streaming standby",
            "1 streaming standby"));
    }

    private async Task<HealthProbe> S3ReachableAsync(List<NodeRecord> all, CancellationToken ct)
    {
        // Probe MinIO (the Loki/Tempo object store) health from a loki node, which already
        // trusts the platform CA. MinIO's own /minio/health/live is the canonical liveness.
        var loki = Role(all, "loki").FirstOrDefault();
        if (loki is null) return new HealthProbe("s3-backend", "minio", "yellow", "no loki node to probe from", "MinIO reachable");
        var r = await _ssh.ExecuteAsync(T(loki.Vmnet11),
            "curl -sS --max-time 6 -k https://minio.nexus.lab:9000/minio/health/live -o /dev/null -w '%{http_code}' 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        var code = r.IsOk ? r.Value!.Stdout.Trim() : "000";
        return new HealthProbe("s3-backend", "minio", code == "200" ? "green" : "red",
            $"minio.nexus.lab:9000/health = {code}", "200 (Loki/Tempo S3 backend)");
    }

    // === TopologyAsync =====================================================
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<TopologySnapshot>(nodesR.Error!);
        var all = nodesR.Value!;

        var grafanas = Role(all, "grafana");
        var pgs = Role(all, "grafana-pg");
        var lokis = Role(all, "loki");
        var tempos = Role(all, "tempo");
        var proms = Role(all, "prometheus");

        var gHolder = await VipHolderAsync(grafanas, GrafanaVip, cancellationToken).ConfigureAwait(false);
        var pgHolder = await VipHolderAsync(pgs, GrafanaDbVip, cancellationToken).ConfigureAwait(false);

        var nodes = new List<TopologyNode>();
        foreach (var n in all)
        {
            var role = ClassifyRole(n.Name);
            var spec = SpecFor(role);
            var active = await IsActiveAsync(n.Vmnet11, spec.Unit, cancellationToken).ConfigureAwait(false);
            var label = role switch
            {
                "prometheus" => "prometheus+alertmanager (HA scrape-all + gossip mesh)",
                "loki" => "loki (simple-scalable; memberlist ring; S3=MinIO)",
                "tempo" => "tempo (scalable; memberlist ring; S3=MinIO; OTLP 4317/4318)",
                "grafana" => n.Name == gHolder ? "grafana (active-active; VIP .184 MASTER)" : "grafana (active-active; VIP .184 backup)",
                "grafana-pg" => n.Name == pgHolder ? "grafana-pg (PG17 repl; VIP .185 MASTER)" : "grafana-pg (PG17 repl; VIP .185 backup)",
                "otel" => "otel-collector (OTLP receivers; RR DNS)",
                _ => role
            };
            nodes.Add(new TopologyNode(n.Name, label, active ? "alive" : "failed"));
        }

        // Two VRRP VIPs as pseudo-nodes (the HA front doors).
        nodes.Add(new TopologyNode($"VIP {GrafanaVip} (grafana.nexus.lab)", $"VRRP front door → {gHolder ?? "unbound"}", gHolder is not null ? "alive" : "failed"));
        nodes.Add(new TopologyNode($"VIP {GrafanaDbVip} (grafana-db.nexus.lab)", $"VRRP front door → {pgHolder ?? "unbound"}", pgHolder is not null ? "alive" : "failed"));

        // Ring + scrape-target enrichment as pseudo-nodes.
        if (lokis.Count > 0)
        {
            var (_, body) = await CurlAsync(lokis[0].Vmnet11, LokiSpec, "/memberlist", null, cancellationToken).ConfigureAwait(false);
            nodes.Add(new TopologyNode("loki-ring", $"memberlist: {ParseMemberlistCount(body, "loki")}/{lokis.Count} members", "info"));
        }
        if (tempos.Count > 0)
        {
            var (_, body) = await CurlAsync(tempos[0].Vmnet11, TempoSpec, "/memberlist", null, cancellationToken).ConfigureAwait(false);
            nodes.Add(new TopologyNode("tempo-ring", $"memberlist: {ParseMemberlistCount(body, "tempo")}/{tempos.Count} members", "info"));
        }
        if (proms.Count > 0)
        {
            var (_, tbody) = await CurlAsync(proms[0].Vmnet11, PrometheusSpec, "/api/v1/targets?state=active", null, cancellationToken).ConfigureAwait(false);
            var (active, up) = ParsePromTargets(tbody);
            nodes.Add(new TopologyNode("prometheus-scrape", $"{up}/{active} scrape targets up", "info"));
        }

        // Observability is not sharded.
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (VRRP cutover for grafana / grafana-db) ==============
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<FailoverResult>(nodesR.Error!);
        var all = nodesR.Value!;

        var direction = (request.Direction ?? "grafana").Trim().ToLowerInvariant();
        (List<NodeRecord> Pair, string Vip, string Label) target = direction switch
        {
            "grafana" or "grafana-app" or ".184" => (Role(all, "grafana"), GrafanaVip, "grafana.nexus.lab"),
            "grafana-db" or "grafana-pg" or "pg" or ".185" => (Role(all, "grafana-pg"), GrafanaDbVip, "grafana-db.nexus.lab"),
            _ => (new List<NodeRecord>(), "", "")
        };
        if (target.Pair.Count == 0)
            return Result.Fail<FailoverResult>(
                $"unknown failover direction '{direction}'. Pass --direction grafana (VIP .184) or grafana-db (VIP .185) — "
                + "the two VRRP front doors. Prometheus/Loki/Tempo/OTel have no VIP (scrape-all / memberlist / RR DNS).");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var preFlight = sw.Elapsed;

        var master = await VipHolderAsync(target.Pair, target.Vip, cancellationToken).ConfigureAwait(false);
        if (master is null)
            return Result.Fail<FailoverResult>($"VIP {target.Vip} is not currently bound to either {target.Label} node; refusing to fail over an unbound VIP.");
        var masterNode = target.Pair.First(n => n.Name == master);
        var backupNode = target.Pair.FirstOrDefault(n => n.Name != master);
        if (backupNode is null) return Result.Fail<FailoverResult>($"no backup node for {target.Label}; need a 2-node pair.");

        // Inject: stop keepalived on the MASTER → the BACKUP (higher of the surviving prio) claims the VIP.
        var stop = await _ssh.ExecuteAsync(T(masterNode.Vmnet11), "sudo systemctl stop keepalived && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<FailoverResult>($"could not stop keepalived on {master}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 160))}");
        var injected = sw.Elapsed;

        // Poll for the VIP to land on the backup.
        string? newHolder = null;
        var moveDeadline = sw.Elapsed + TimeSpan.FromSeconds(30);
        while (sw.Elapsed < moveDeadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            newHolder = await VipHolderAsync(target.Pair, target.Vip, cancellationToken).ConfigureAwait(false);
            if (newHolder == backupNode.Name) break;
        }
        var observed = sw.Elapsed;

        // Recover: restart keepalived on the original master (nopreempt → it comes back as BACKUP, VIP stays put).
        var recovery = "skipped";
        string? recoveryHint = null;
        if (!request.NoRecover)
        {
            var restart = await _ssh.ExecuteAsync(T(masterNode.Vmnet11), "sudo systemctl start keepalived && echo STARTED", SshTimeout, cancellationToken).ConfigureAwait(false);
            recovery = restart.IsOk && restart.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal) ? "recovered" : "failed";
            if (recovery == "failed") recoveryHint = $"restart keepalived on {master} manually (`sudo systemctl start keepalived`).";
        }
        else recoveryHint = $"keepalived left stopped on {master} (--no-recover); restart it when ready.";
        var recovered = sw.Elapsed;
        sw.Stop();

        var moved = newHolder == backupNode.Name;
        return Result.Ok(new FailoverResult(
            Scenario: $"vrrp-cutover:{direction}",
            OriginalPrimary: master,
            NewPrimary: moved ? backupNode.Name : newHolder,
            Rto: observed - injected,
            Recovery: recovery,
            RecoveryHint: moved ? recoveryHint : (recoveryHint is null ? $"VIP {target.Vip} did not move to {backupNode.Name} within 30s — check keepalived on the backup." : recoveryHint),
            Timeline: new FailoverTimeline(preFlight, injected, observed, recovered, recovered),
            StartedAtUtc: startedAt));
    }

    // === ScaleOut (Loki/Tempo ring add/remove) =============================
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name (a loki-N or tempo-N ring member).");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var node = all.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"'{request.NodeName}' is not an observability node.");
        var role = ClassifyRole(node.Name);
        if (role is not ("loki" or "tempo"))
            return Result.Fail<ScaleOutResult>(RingOnlyMessage(role));

        var ring = Role(all, role);
        var readyOthers = 0;
        foreach (var n in ring.Where(x => x.Name != node.Name))
        {
            var (code, _) = await CurlAsync(n.Vmnet11, SpecFor(role), "/ready", null, cancellationToken).ConfigureAwait(false);
            if (code == 200) readyOthers++;
        }
        if (readyOthers < 2)
            return Result.Fail<ScaleOutResult>($"only {readyOthers} other {role} ring members are ready; removing {node.Name} would drop the ring below the 2-replica floor. Refusing.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var spec = SpecFor(role);
        var stop = await _ssh.ExecuteAsync(T(node.Vmnet11), $"sudo systemctl stop {spec.Unit} && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"could not stop {spec.Unit} on {node.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 160))}");
        sw.Stop();
        return Result.Ok(new ScaleOutResult("remove", [node.Name], "ok",
            $"{spec.Unit} stopped on {node.Name}; the {role} memberlist ring evicts it after the dead-member timeout (~60s) and rebalances. "
            + $"Re-add via `scale-out add {ClusterName} --role {role}`. Permanently growing the ring is a terraform op in nexus-infra-observability.",
            sw.Elapsed, startedAt));
    }

    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var all = nodesR.Value!;
        var role = (request.Role ?? "").Trim().ToLowerInvariant();
        if (role is not ("loki" or "tempo"))
            return Result.Fail<ScaleOutResult>(string.IsNullOrEmpty(role)
                ? "scale-out add requires --role loki|tempo (the only memberlist-ring roles that scale at runtime)."
                : RingOnlyMessage(role));

        var ring = Role(all, role);
        var spec = SpecFor(role);
        // Find a ring node whose service is stopped (a previously removed member).
        NodeRecord? down = null;
        foreach (var n in ring)
            if (!await IsActiveAsync(n.Vmnet11, spec.Unit, cancellationToken).ConfigureAwait(false)) { down = n; break; }
        if (down is null)
            return Result.Fail<ScaleOutResult>(
                $"all {role} ring members are already running. Growing the ring beyond {ring.Count} is a terraform/Packer op "
                + $"(add the VM + overlay in nexus-infra-observability and re-apply — the node joins the memberlist ring on boot). "
                + "This verb only restarts a previously removed ring member.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var start = await _ssh.ExecuteAsync(T(down.Vmnet11), $"sudo systemctl start {spec.Unit} && echo STARTED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (start.IsFail || !start.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"could not start {spec.Unit} on {down.Name}: {(start.IsFail ? start.Error : Tail(start.Value!.Stderr, 160))}");
        // Poll for the node to rejoin the ring (ready).
        var rejoined = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(90);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var (code, _) = await CurlAsync(down.Vmnet11, spec, "/ready", null, cancellationToken).ConfigureAwait(false);
            if (code == 200) { rejoined = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult("add", [down.Name], rejoined ? "ok" : "partial",
            rejoined ? $"{spec.Unit} restarted on {down.Name}; rejoined the {role} memberlist ring (ready)."
                     : $"{spec.Unit} restarted on {down.Name} but /ready did not return 200 within 90s; the ring may still be converging.",
            sw.Elapsed, startedAt));
    }

    private static string RingOnlyMessage(string role) =>
        $"{role} is not a runtime-scalable role. Only the Loki + Tempo memberlist rings scale at runtime; "
        + "Prometheus + Grafana HA are FIXED at 2 (both Proms scrape every target; Grafana is active-active behind a VRRP VIP), "
        + "Grafana-PG is a 2-node streaming pair, and OTel Collector is a fixed RR-DNS pair. Growing any of those is a terraform op.";

    // === Backup (graceful actionable N/A) ==================================
    public Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<BackupResult>(
            "backup is graceful N/A for the observability tier — every piece of durable state already lives in a system with its "
            + "own recovery story: Loki/Tempo blocks + WAL are in MinIO (erasure-coded, the lakehouse tier's own backup), the Grafana "
            + "state DB is a streaming-replicated PG pair (RPO≈0; pg_basebackup belongs to the grafana-pg DR runbook), and Grafana "
            + "dashboards + datasources are provisioned-as-code from nexus-infra-observability (re-applied, not snapshotted). "
            + "Prometheus TSDB is intentionally ephemeral (HA = both Proms scrape every target; ADR-0038). Nothing here is "
            + "adapter-ownable to snapshot that isn't already durable or reproducible."));

    public Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<RestoreResult>(
            "restore is graceful N/A — see `backup take` for why nothing is adapter-snapshotted. Recover via the component's own DR "
            + "path: MinIO EC heal, grafana-pg pg_basebackup re-seed (handbook §3.D), or re-apply the provisioned dashboards."));

    // === RotateCertAsync (force the node's vault-agent to re-render its leaves) ==
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<CertRotationResult>(nodesR.Error!);
        var all = nodesR.Value!;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        // Order: ring/scrape nodes first, VIP holders last (minimise blast radius).
        var gHolder = await VipHolderAsync(Role(all, "grafana"), GrafanaVip, cancellationToken).ConfigureAwait(false);
        var pgHolder = await VipHolderAsync(Role(all, "grafana-pg"), GrafanaDbVip, cancellationToken).ConfigureAwait(false);
        var ordered = all.OrderBy(n => n.Name == gHolder || n.Name == pgHolder ? 1 : 0).ThenBy(n => n.Name, StringComparer.Ordinal).ToList();

        foreach (var n in ordered)
        {
            var role = ClassifyRole(n.Name);
            if (role == "grafana-pg")
                continue; // handled as a pair after the loop (standby-first PG SIGHUP reload — GAP #5)
            // Per-node TLS dir(s) + service reload command(s). A prom node carries BOTH the
            // Prometheus and the Alertmanager leaf (its vault-agent renders two templates).
            var units = role == "prometheus"
                ? new[] { (PrometheusSpec.TlsDir, $"sudo systemctl reload {PrometheusSpec.Unit}"), (AlertmanagerSpec.TlsDir, $"sudo systemctl reload {AlertmanagerSpec.Unit}") }
                : new[] { (SpecFor(role).TlsDir, SpecFor(role).ReloadIsRestart ? $"sudo systemctl restart {SpecFor(role).Unit}" : $"sudo systemctl reload {SpecFor(role).Unit}") };
            var primaryDir = units[0].Item1;
            var oldSerial = await WireSerialAsync(n.Vmnet11, primaryDir, cancellationToken).ConfigureAwait(false);

            // Force the vault-agent's pkiCert to re-issue: back up + remove the rendered bundle(s)
            // (pkiCert PERSISTS + reuses the leaf otherwise — the Swarm v0.8.2 lesson), restart the
            // agent, wait for the re-render (the post-render command splits bundle.pem →
            // server.crt/server.key), restore any bundle that didn't reappear, then reload the
            // service(s). Re-issuing from the NODE'S OWN TEMPLATE (not a build-host issue) is what
            // keeps the FULL alt_names — the RR-DNS aliases (prometheus/alertmanager/loki/tempo/
            // otel.nexus.lab) + the grafana VIP — and the PKCS#8 key the services + smokes expect.
            var rmList = string.Join(" ", units.Select(u => $"\"{u.Item1}/bundle.pem\""));
            var reloadList = string.Join("; ", units.Select(u => u.Item2));
            var script = string.Join(" ; ", new[]
            {
                $"for f in {rmList}; do if sudo test -f \"$f\"; then sudo cp -a \"$f\" \"$f.bak\"; sudo rm -f \"$f\"; fi; done",
                "sudo systemctl restart nexus-vault-agent",
                $"for i in $(seq 1 25); do sudo test -f {primaryDir}/bundle.pem && break; sleep 1; done",
                $"for f in {rmList}; do if sudo test -f \"$f.bak\"; then if sudo test -f \"$f\"; then sudo rm -f \"$f.bak\"; else sudo mv \"$f.bak\" \"$f\"; fi; fi; done",
                reloadList,
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
            var newSerial = await WireSerialAsync(n.Vmnet11, primaryDir, cancellationToken).ConfigureAwait(false);
            rotated.Add(new CertRotatedNode(n.Name, oldSerial, newSerial, Error: null));
        }

        // grafana-pg (GAP #5): rotate the Grafana state-DB pair's leaf STANDBY-FIRST then
        // PRIMARY, with a SIGHUP reload (no restart → the streaming-replication connection
        // + live Grafana sessions are never dropped). Shared with iceberg-pg (lakehouse)
        // via PgSslCertRotator.
        rotated.AddRange(await PgSslCertRotator.RotatePairAsync(
            _ssh, T, Role(all, "grafana-pg"), GrafanaPgSpec.TlsDir, GrafanaPgSpec.Unit,
            SshTimeout, cancellationToken).ConfigureAwait(false));

        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    /// <summary>Read a node's current leaf serial from its rendered server.crt (proof of rotation).</summary>
    private async Task<string> WireSerialAsync(string ip, string tlsDir, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip),
            $"sudo openssl x509 -in {tlsDir}/server.crt -noout -serial 2>/dev/null | sed 's/serial=//'", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim().Length > 0 ? r.Value.Stdout.Trim() : "(unknown)";
    }

    // === AclAsync (Grafana users via /api/admin/users) =====================
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<AclSnapshot>(nodesR.Error!);
        var grafanas = Role(nodesR.Value!, "grafana");
        if (grafanas.Count == 0) return Result.Fail<AclSnapshot>("no grafana node in vms.yaml cluster observability.");
        var pwR = await AdminPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwR.IsFail) return Result.Fail<AclSnapshot>(pwR.Error!);
        var auth = $"admin:{pwR.Value}";
        var via = grafanas[0].Vmnet11;
        var verb = operation.Verb.ToLowerInvariant();

        // The org-scoped /api/org/users is used in preference to /api/admin/users
        // (the latter 404s under basic auth on the live tier even for a server admin);
        // grant/revoke manage the org ROLE (Admin vs Viewer) via PATCH /api/org/users/<id>.
        if (verb is "list" or "describe")
        {
            var (code, body) = await CurlAsync(via, GrafanaSpec, "/api/org/users", auth, cancellationToken).ConfigureAwait(false);
            if (code == 401)
                return Result.Fail<AclSnapshot>(
                    "Grafana refused the admin credential from Vault KV (nexus/observability/grafana/admin-password) — HTTP 401. "
                    + "The live Grafana admin password has drifted from KV. Reconcile with "
                    + "`grafana-cli admin reset-admin-password <kv-value>` on a grafana node (writes the shared PG), then retry.");
            if (code != 200) return Result.Fail<AclSnapshot>($"Grafana /api/org/users returned HTTP {code}.");
            var users = ParseGrafanaOrgUsers(body).Select(u => new AclUser(
                u.Login,
                ProtectedGrafanaUsers.Contains(u.Login, StringComparer.OrdinalIgnoreCase) ? [u.Role, "protected"] : [u.Role],
                Enabled: true)).ToList();
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user (a Grafana login to promote to org Admin / demote to Viewer).");
            var login = operation.User!;
            if (verb == "revoke" && ProtectedGrafanaUsers.Contains(login, StringComparer.OrdinalIgnoreCase))
                return Result.Fail<AclSnapshot>($"refusing to demote the protected Grafana '{login}' user (built-in operator identity).");

            // Resolve the org userId from /api/org/users.
            var (lcode, lbody) = await CurlAsync(via, GrafanaSpec, "/api/org/users", auth, cancellationToken).ConfigureAwait(false);
            if (lcode == 401)
                return Result.Fail<AclSnapshot>("Grafana admin credential drift (HTTP 401) — see `acl list` for the reconcile command.");
            if (lcode != 200) return Result.Fail<AclSnapshot>($"Grafana /api/org/users returned HTTP {lcode}.");
            var hit = ParseGrafanaOrgUsers(lbody).FirstOrDefault(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase));
            if (hit.UserId == 0) return Result.Fail<AclSnapshot>($"no Grafana org user '{login}'.");

            var newRole = verb == "grant" ? "Admin" : "Viewer";
            var patchCmd =
                $"sudo curl -sS --max-time 8 --cacert {GrafanaSpec.TlsDir}/ca.crt -u '{auth}' -X PATCH -H 'Content-Type: application/json' "
                + $"-d '{{\"role\":\"{newRole}\"}}' https://127.0.0.1:3000/api/org/users/{hit.UserId} -w '__HTTP_%{{http_code}}__' 2>/dev/null";
            var permR = await _ssh.ExecuteAsync(T(via), patchCmd, CurlTimeout, cancellationToken).ConfigureAwait(false);
            if (permR.IsFail || !permR.Value!.Stdout.Contains("__HTTP_200__", StringComparison.Ordinal))
                return Result.Fail<AclSnapshot>($"Grafana org-role update for '{login}' failed: {(permR.IsFail ? permR.Error : Tail(permR.Value!.Stdout, 160))}");
            return await AclAsync(new AclOperation("list"), cancellationToken).ConfigureAwait(false);
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke.");
    }

    // === ApplyChaosAsync (nexus-chaos.sh on a ring node) ===================
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ChaosOutcome>(nodesR.Error!);
        var all = nodesR.Value!;

        // Default victim: a Loki or Tempo ring node (the ring self-heals; Prom/Grafana/VIP holders spared).
        var ringNodes = Role(all, "loki").Concat(Role(all, "tempo")).ToList();
        NodeRecord victim;
        if (!string.IsNullOrWhiteSpace(scenario.Target))
        {
            var t = all.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase));
            if (t is null) return Result.Fail<ChaosOutcome>($"chaos target '{scenario.Target}' is not an observability node.");
            var gHolder = await VipHolderAsync(Role(all, "grafana"), GrafanaVip, cancellationToken).ConfigureAwait(false);
            var pgHolder = await VipHolderAsync(Role(all, "grafana-pg"), GrafanaDbVip, cancellationToken).ConfigureAwait(false);
            if (t.Name == gHolder || t.Name == pgHolder)
                return Result.Fail<ChaosOutcome>($"'{t.Name}' currently holds a VRRP VIP; pick a non-VIP node (a loki-N/tempo-N ring member is safest) or fail the VIP over first.");
            victim = t;
        }
        else
        {
            if (ringNodes.Count == 0) return Result.Fail<ChaosOutcome>("no loki/tempo ring node available as a chaos victim.");
            victim = ringNodes[0];
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
        if (NftScenarios.Contains(scenario.ScenarioType))
            await _ssh.ExecuteAsync(target, "exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (isProcKill)
            await _ssh.ExecuteAsync(target, $"sudo systemctl reset-failed {spec.Unit} 2>/dev/null; sudo systemctl start {spec.Unit} 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        // Recover: poll the victim's own /ready (or service active for grafana/otel) until green.
        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(75);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            if (spec.LoopbackHttp || role == "grafana")
            {
                if (await IsActiveAsync(victim.Vmnet11, spec.Unit, cancellationToken).ConfigureAwait(false)) { recovered = true; break; }
            }
            else
            {
                var (code, _) = await CurlAsync(victim.Vmnet11, spec, "/ready", null, cancellationToken).ConfigureAwait(false);
                if (code == 200) { recovered = true; break; }
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
        // Refuse the current VRRP VIP holders (resizing them flaps the front door); everything else is safe.
        if (string.Equals(vmName, _grafanaVipHolder, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(vmName, _grafanaDbVipHolder, StringComparison.OrdinalIgnoreCase)) return false;
        return ClassifyRole(vmName) is not "other";
    }

    // === helpers ===========================================================
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
}
