using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Nexus.Cli.Adapters.Consul;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Nomad;
using Nexus.Cli.Adapters.Portainer;
using Nexus.Cli.Adapters.Vault;
using Nexus.Cli.Adapters.Vmware;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Swarm / Nomad / Consul / Portainer orchestration-tier adapter — the SECOND
/// non-data-tier adapter (nexus-cli v0.8.2, Phase 0.E, ADR-0023) and the most
/// reusable: it wires the already-built <see cref="ConsulClient"/> +
/// <see cref="NomadClient"/> + <see cref="PortainerClient"/> +
/// <see cref="ClusterStatusService"/> + <see cref="FailoverTestService"/>
/// (built v0.1–v0.5) into the full <see cref="IClusterAdapter"/> surface.
/// <para>
/// The tier is 3 combined Consul-server + Nomad-server + Swarm-manager nodes
/// (swarm-manager-1/2/3 @ .111/.112/.113) + 3 Consul-client + Nomad-client +
/// Swarm-worker + Portainer-agent nodes (swarm-worker-1/2/3 @ .131/.132/.133).
/// Portainer runs as a manager-pinned Swarm service (no dedicated VM).
/// </para>
/// <para>
/// Access posture (the build-host control-plane shape proven by VaultAdapter):
/// the Consul / Nomad mgmt tokens stay on the build host (read from Vault KV
/// <c>nexus/swarm/{consul,nomad}-bootstrap-token</c>) and reach the cluster over
/// HTTPS; node-local actions (service restarts, snapshot save, cert re-render via
/// the node's own <c>nexus-vault-agent</c>, chaos) go over SSH. No managed
/// Docker / Consul / Nomad driver and no embedded credentials — NetArchTest-clean.
/// </para>
/// <list type="bullet">
///   <item>status/health/topology = REUSE <see cref="ClusterStatusService"/>
///   (rolls up Consul + Nomad + Portainer) enriched with <c>docker node ls</c>
///   (SSH) + Portainer <c>/api/endpoints</c>.</item>
///   <item>failover = REUSE <see cref="FailoverTestService"/> — dispatch on
///   <c>--direction</c> to the consul-leader / nomad-leader / swarm-manager
///   runner (the swarm-manager runner is a vmrun host-level suspend of the raft
///   leader VM); RTO measured.</item>
///   <item>scale-out = <c>docker node demote/update --availability drain</c> +
///   <c>nomad node drain</c> (reversible) with a leader/quorum guard; growing the
///   fixed 3+3 fleet is terraform → graceful N/A.</item>
///   <item>backup = <c>consul snapshot save</c> + <c>consul kv export</c> +
///   <c>nomad operator snapshot save</c> (+ best-effort Portainer boltdb copy),
///   round-trip-verified via <c>consul snapshot inspect</c>.</item>
///   <item>cert-rotate = restart <c>nexus-vault-agent</c> per node (re-renders the
///   pki_int mTLS leaves) + consul ROLLING / nomad PARALLEL big-bang restart
///   (Nomad's TLS RPC layer can't survive a rolling flip —
///   [[nomad-tls-rolling-restart-must-be-parallel]]).</item>
///   <item>acl = Consul + Nomad ACL tokens (list/describe/grant/revoke), with the
///   bootstrap/management/agent tokens revoke-protected.</item>
///   <item>chaos = nexus-chaos.sh on a WORKER (managers keep quorum); after any
///   nftables-based scenario the worker's <c>docker</c> is restarted to rebuild
///   the ingress-mesh rules <c>flush ruleset</c> wiped
///   ([[nftables-flush-ruleset-wipes-docker]]).</item>
/// </list>
/// </summary>
public sealed class SwarmAdapter : IClusterAdapter, IDisposable
{
    private const string ClusterName = "swarm";
    private const string DisplayNameConst = "Orchestration tier (Docker Swarm + Nomad + Consul + Portainer)";
    private const string VmsCluster = "swarm";

    // Vault KV paths (frozen 0.E.4 close-out canon; same the NexusBootstrapper uses).
    private const string ConsulTokenPath = "swarm/consul-bootstrap-token";
    private const string NomadTokenPath = "swarm/nomad-bootstrap-token";
    private const string PortainerPwdPath = "portainer/admin-bcrypt";

    private const int ConsulPort = 8501;
    private const int NomadPort = 4646;
    private const int PortainerPort = 9443;

    private const string ConsulCa = "/etc/ssl/certs/consul-ca.pem";
    private const string NomadCa = "/etc/ssl/certs/nomad-ca.pem";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SettleDeadline = TimeSpan.FromSeconds(90);

    // ACL tokens that acl revoke must never delete (cluster identity / operator).
    private static readonly string[] ProtectedTokenMarkers =
        ["bootstrap", "management", "agent", "anonymous", "server", "client"];
    private static readonly string[] ProtectedPolicies =
        ["global-management", "node-identity", "agent-policy"];

    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    // Scenarios that touch nftables and therefore wipe Docker's ingress-mesh rules.
    private static readonly HashSet<string> NftScenarios = new(StringComparer.OrdinalIgnoreCase)
        { "network-partition", "packet-loss" };

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    private Swarm? _svc;
    private bool _svcTried;
    private string? _svcErr;

    private string? _lastSwarmLeader;
    private string? _lastNomadLeader;

    /// <summary>
    /// Creates the adapter over the vms.yaml catalog, an SSH client + credentials for
    /// node-local ops, and an optional operator <see cref="INexusVaultClient"/> (the
    /// Vault-KV source of the Consul/Nomad management tokens + Portainer admin password).
    /// </summary>
    public SwarmAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
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

    // === node classification (from the vms.yaml name) ======================
    /// <summary>"manager" for swarm-manager-N, "worker" for swarm-worker-N, "other" otherwise.</summary>
    internal static string ClassifyNode(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.StartsWith("swarm-manager-", StringComparison.Ordinal)) return "manager";
        if (n.StartsWith("swarm-worker-", StringComparison.Ordinal)) return "worker";
        return "other";
    }

    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    private Result<(List<NodeRecord> Managers, List<NodeRecord> Workers)> Nodes()
    {
        var cluster = _catalog.GetCluster(VmsCluster);
        if (cluster.IsFail) return Result.Fail<(List<NodeRecord>, List<NodeRecord>)>(cluster.Error!);
        var managers = cluster.Value!.Nodes.Where(n => ClassifyNode(n.Name) == "manager")
            .OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        var workers = cluster.Value.Nodes.Where(n => ClassifyNode(n.Name) == "worker")
            .OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (managers.Count == 0)
            return Result.Fail<(List<NodeRecord>, List<NodeRecord>)>($"no swarm-manager-N nodes in vms.yaml cluster '{VmsCluster}'");
        return Result.Ok((managers, workers));
    }

    // === lazy control-plane bootstrap ======================================
    private sealed record Swarm(
        NexusHttpClientFactory Http,
        string ConsulToken,
        string NomadToken,
        string PortainerPwd);

    private async Task<Result<Swarm>> ServicesAsync(CancellationToken ct)
    {
        if (_svc is not null) return Result.Ok(_svc);
        if (_svcTried) return Result.Fail<Swarm>(_svcErr!);
        _svcTried = true;

        if (_vault is null)
        {
            _svcErr = "swarm verbs need the operator token to read the Consul/Nomad mgmt tokens from Vault KV. "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.";
            return Result.Fail<Swarm>(_svcErr);
        }

        VaultContext ctx;
        try
        {
            var resolved = new VaultTokenResolver(new ProcessEnvironmentReader()).Resolve();
            if (resolved.IsFail) { _svcErr = resolved.Error; return Result.Fail<Swarm>(_svcErr!); }
            ctx = resolved.Value!;
        }
        catch (Exception ex) { _svcErr = $"could not resolve the Vault context: {ex.Message}"; return Result.Fail<Swarm>(_svcErr); }

        var consulTok = await _vault.ReadKvFieldAsync("nexus", ConsulTokenPath, "management_token", ct).ConfigureAwait(false);
        if (consulTok.IsFail) { _svcErr = consulTok.Error; return Result.Fail<Swarm>(_svcErr!); }
        var nomadTok = await _vault.ReadKvFieldAsync("nexus", NomadTokenPath, "management_token", ct).ConfigureAwait(false);
        if (nomadTok.IsFail) { _svcErr = nomadTok.Error; return Result.Fail<Swarm>(_svcErr!); }
        if (string.IsNullOrWhiteSpace(consulTok.Value) || string.IsNullOrWhiteSpace(nomadTok.Value))
        {
            _svcErr = "the Consul/Nomad bootstrap tokens in Vault KV (nexus/swarm/{consul,nomad}-bootstrap-token) are empty "
                + "(status=not-bootstrapped). The orchestration tier's ACL bootstrap has not run — apply the swarm env "
                + "(`pwsh scripts/swarm.ps1 apply` in nexus-infra-swarm-nomad) to bootstrap + persist them, then retry.";
            return Result.Fail<Swarm>(_svcErr);
        }

        NexusHttpClientFactory http;
        try { http = new NexusHttpClientFactory(ctx.CaBundlePath, TimeSpan.FromSeconds(15)); }
        catch (Exception ex) { _svcErr = $"could not build the HTTP client factory: {ex.Message}"; return Result.Fail<Swarm>(_svcErr); }

        var pwd = await _vault.ReadKvFieldAsync("nexus", PortainerPwdPath, "plaintext", ct).ConfigureAwait(false);
        _svc = new Swarm(http, consulTok.Value!.Trim(), nomadTok.Value!.Trim(), pwd.IsOk ? pwd.Value! : "");
        return Result.Ok(_svc);
    }

    private static ConsulClient MakeConsul(Swarm s, string ip) =>
        new(new ConsulClient.Settings($"https://{ip}:{ConsulPort}", s.ConsulToken), s.Http);
    private static NomadClient MakeNomad(Swarm s, string ip) =>
        new(new NomadClient.Settings($"https://{ip}:{NomadPort}", s.NomadToken), s.Http);
    private static PortainerClient MakePortainer(Swarm s, string ip) =>
        new(new PortainerClient.Settings($"https://{ip}:{PortainerPort}", "admin", s.PortainerPwd), s.Http);

    /// <summary>Pick the first manager whose Consul answers; falls back to managers[0].</summary>
    private static async Task<NodeRecord> PickManagerAsync(Swarm s, List<NodeRecord> managers, CancellationToken ct)
    {
        foreach (var m in managers)
        {
            using var c = MakeConsul(s, m.Vmnet11);
            var h = await c.GetHealthAsync(ct).ConfigureAwait(false);
            if (h.IsOk) return m;
        }
        return managers[0];
    }

    private static ClusterStatusService MakeStatusService(Swarm s, string managerIp) =>
        new(MakeConsul(s, managerIp), MakeNomad(s, managerIp), MakePortainer(s, managerIp));

    // === docker node ls (the authoritative swarm membership view) ===========
    internal sealed record DockerNode(string Hostname, string Status, string Availability, string ManagerStatus)
    {
        public bool IsManager => ManagerStatus.Length > 0;
        public bool IsLeader => string.Equals(ManagerStatus, "Leader", StringComparison.Ordinal);
    }

    /// <summary>Parse `docker node ls --format json` NDJSON (one object per line).</summary>
    internal static List<DockerNode> ParseDockerNodes(string ndjson)
    {
        var list = new List<DockerNode>();
        foreach (var raw in ndjson.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                list.Add(new DockerNode(
                    Hostname: Str(r, "Hostname"),
                    Status: Str(r, "Status"),
                    Availability: Str(r, "Availability"),
                    ManagerStatus: Str(r, "ManagerStatus")));
            }
            catch (JsonException) { /* skip a malformed line */ }
        }
        return list;
    }

    private async Task<Result<List<DockerNode>>> DockerNodesAsync(string managerIp, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(managerIp), "docker node ls --format json 2>/dev/null", SshTimeout, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<List<DockerNode>>(r.Error!);
        if (r.Value!.ExitCode != 0)
            return Result.Fail<List<DockerNode>>($"docker node ls on {managerIp} exit {r.Value.ExitCode}: {Tail(r.Value.Stderr, 200)}");
        var nodes = ParseDockerNodes(r.Value.Stdout);
        if (nodes.Count == 0) return Result.Fail<List<DockerNode>>("docker node ls returned no parseable nodes");
        return Result.Ok(nodes);
    }

    // === GetStatusAsync ====================================================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ClusterStatus>(nodesR.Error!);
        var (managers, workers) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<ClusterStatus>(svcR.Error!);
        var s = svcR.Value!;

        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var dockerR = await DockerNodesAsync(via.Vmnet11, cancellationToken).ConfigureAwait(false);
        if (dockerR.IsFail) return Result.Fail<ClusterStatus>(dockerR.Error!);
        var docker = dockerR.Value!.ToDictionary(d => d.Hostname, d => d, StringComparer.OrdinalIgnoreCase);

        var status = MakeStatusService(s, via.Vmnet11);
        var report = await status.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        var nomadLeaderName = report.Nomad.IsOk ? MapNomadLeader(report.Nomad.Value!, managers) : null;

        var members = new List<ClusterMember>();
        string? leader = null;
        foreach (var n in managers.Concat(workers))
        {
            var cls = ClassifyNode(n.Name);
            docker.TryGetValue(n.Name, out var d);
            var mgrStatus = d?.ManagerStatus ?? "";
            var role = cls == "manager"
                ? (string.Equals(mgrStatus, "Leader", StringComparison.Ordinal) ? "manager/leader" : $"manager/{(mgrStatus.Length > 0 ? mgrStatus.ToLowerInvariant() : "unknown")}")
                : "worker";
            if (d?.IsLeader == true) leader = n.Name;
            var alive = d is not null && string.Equals(d.Status, "Ready", StringComparison.OrdinalIgnoreCase);
            var drain = d is not null && !string.Equals(d.Availability, "Active", StringComparison.OrdinalIgnoreCase);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, role,
                d is null ? "unknown" : alive ? (drain ? "draining" : "alive") : "failed"));
        }

        _lastSwarmLeader = leader;
        _lastNomadLeader = nomadLeaderName;

        var allReady = members.All(m => m.Status == "alive");
        var rollup = report.Overall;
        var overall = (!allReady || rollup == HealthLevel.Red) ? (rollup == HealthLevel.Red || !allReady && members.Any(m => m.Status == "failed") ? "red" : "yellow")
            : rollup == HealthLevel.Yellow ? "yellow" : "green";

        var st = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, leader, DateTimeOffset.UtcNow);
        return Result.Ok(st);
    }

    private static string? MapNomadLeader(NomadHealth nh, List<NodeRecord> managers)
    {
        if (string.IsNullOrEmpty(nh.LeaderAddress)) return null;
        var ip = nh.LeaderAddress.Split(':')[0];
        return managers.FirstOrDefault(m => m.Vmnet10 == ip || m.Vmnet11 == ip)?.Name;
    }

    // === HealthAsync =======================================================
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<HealthReport>(nodesR.Error!);
        var (managers, workers) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<HealthReport>(svcR.Error!);
        var s = svcR.Value!;
        var total = managers.Count + workers.Count;
        var probes = new List<HealthProbe>();

        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var status = MakeStatusService(s, via.Vmnet11);
        var report = await status.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        // --- Consul ---
        if (report.Consul.IsOk)
        {
            var c = report.Consul.Value!;
            probes.Add(new HealthProbe("consul-members", "consul", c.Alive == total && c.Failed == 0 ? "green" : "yellow",
                $"{c.Alive} alive / {c.Failed} failed", $"{total} alive, 0 failed"));
            probes.Add(new HealthProbe("consul-leader", "consul", string.IsNullOrEmpty(c.Leader) ? "red" : "green",
                string.IsNullOrEmpty(c.Leader) ? "no leader" : c.Leader, "1 raft leader"));
        }
        else probes.Add(new HealthProbe("consul", "consul", "red", report.Consul.Error, "reachable + leader"));

        // --- Nomad ---
        if (report.Nomad.IsOk)
        {
            var n = report.Nomad.Value!;
            var leaders = n.Servers.Count(x => x.IsLeader);
            var readyClients = n.Clients.Count(x => string.Equals(x.Status, "ready", StringComparison.OrdinalIgnoreCase));
            probes.Add(new HealthProbe("nomad-servers", "nomad", n.Servers.Count == managers.Count ? "green" : "yellow",
                $"{n.Servers.Count} servers", $"{managers.Count} servers"));
            probes.Add(new HealthProbe("nomad-leader", "nomad", leaders == 1 ? "green" : "red",
                $"{leaders} leader", "exactly 1"));
            probes.Add(new HealthProbe("nomad-clients", "nomad", readyClients == workers.Count ? "green" : "yellow",
                $"{readyClients}/{workers.Count} ready", $"{workers.Count} ready"));
        }
        else probes.Add(new HealthProbe("nomad", "nomad", "red", report.Nomad.Error, "reachable + leader"));

        // --- Portainer ---
        if (report.Portainer.IsOk)
            probes.Add(new HealthProbe("portainer", "portainer", report.Portainer.Value!.Reachable ? "green" : "yellow",
                report.Portainer.Value!.Reachable ? $"reachable (v{report.Portainer.Value.Version})" : "unreachable", "reachable"));
        else probes.Add(new HealthProbe("portainer", "portainer", "yellow", report.Portainer.Error, "reachable"));

        // --- Docker Swarm (authoritative membership + raft) ---
        var dockerR = await DockerNodesAsync(via.Vmnet11, cancellationToken).ConfigureAwait(false);
        if (dockerR.IsOk)
        {
            var d = dockerR.Value!;
            var mgrReady = d.Count(x => x.IsManager && string.Equals(x.Status, "Ready", StringComparison.OrdinalIgnoreCase));
            var wkrReady = d.Count(x => !x.IsManager && string.Equals(x.Status, "Ready", StringComparison.OrdinalIgnoreCase));
            var swarmLeaders = d.Count(x => x.IsLeader);
            probes.Add(new HealthProbe("swarm-managers", "docker", mgrReady == managers.Count ? "green" : "yellow",
                $"{mgrReady}/{managers.Count} Ready", $"{managers.Count} Ready"));
            probes.Add(new HealthProbe("swarm-workers", "docker", wkrReady == workers.Count ? "green" : "yellow",
                $"{wkrReady}/{workers.Count} Ready", $"{workers.Count} Ready"));
            probes.Add(new HealthProbe("swarm-leader", "docker", swarmLeaders == 1 ? "green" : "red",
                $"{swarmLeaders} leader", "exactly 1"));
            _lastSwarmLeader = d.FirstOrDefault(x => x.IsLeader)?.Hostname;
        }
        else probes.Add(new HealthProbe("swarm", "docker", "red", dockerR.Error, "3 mgrs + 3 wkrs Ready"));

        if (report.Nomad.IsOk) _lastNomadLeader = MapNomadLeader(report.Nomad.Value!, managers);

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync =====================================================
    /// <inheritdoc />
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<TopologySnapshot>(nodesR.Error!);
        var (managers, workers) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<TopologySnapshot>(svcR.Error!);
        var s = svcR.Value!;

        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var dockerR = await DockerNodesAsync(via.Vmnet11, cancellationToken).ConfigureAwait(false);
        if (dockerR.IsFail) return Result.Fail<TopologySnapshot>(dockerR.Error!);
        var docker = dockerR.Value!.ToDictionary(d => d.Hostname, d => d, StringComparer.OrdinalIgnoreCase);

        var nodes = new List<TopologyNode>();
        foreach (var n in managers)
        {
            docker.TryGetValue(n.Name, out var d);
            var raft = d?.IsLeader == true ? "raft-leader" : d?.IsManager == true ? d.ManagerStatus.ToLowerInvariant() : "unknown";
            nodes.Add(new TopologyNode(n.Name, $"manager/{raft} (consul-server,nomad-server)", d?.Status ?? "unknown"));
        }
        foreach (var n in workers)
        {
            docker.TryGetValue(n.Name, out var d);
            nodes.Add(new TopologyNode(n.Name, "worker (consul-client,nomad-client,portainer-agent)", d?.Status ?? "unknown"));
        }

        // Portainer (swarm service): reachability via the unauthenticated /api/system/status,
        // enriched best-effort with the managed-endpoint count from /api/endpoints (needs the
        // admin JWT — null if the KV admin credential can't authenticate).
        using var portainer = MakePortainer(s, via.Vmnet11);
        var pstatus = await portainer.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var endpoints = await PortainerEndpointCountAsync(s, via.Vmnet11, cancellationToken).ConfigureAwait(false);
        var prole = endpoints is null
            ? (pstatus.IsOk ? $"portainer-server (v{pstatus.Value!.Version})" : "portainer-server")
            : $"portainer-server ({endpoints} endpoints)";
        nodes.Add(new TopologyNode("portainer (swarm service)", prole, pstatus.IsOk ? "alive" : "unknown"));

        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, DateTimeOffset.UtcNow));
    }

    private static async Task<int?> PortainerEndpointCountAsync(Swarm s, string managerIp, CancellationToken ct)
    {
        // /api/endpoints needs a JWT; if the admin password isn't available, fall back to null (best-effort enrich).
        if (string.IsNullOrEmpty(s.PortainerPwd)) return null;
        try
        {
            using var http = s.Http.Create();
            var baseUrl = $"https://{managerIp}:{PortainerPort}";
            // Hand-escape the password into the JSON string (AOT-safe; no reflection serializer).
            var pwdEscaped = JsonEncodedText.Encode(s.PortainerPwd).ToString();
            var authBody = new StringContent(
                $"{{\"username\":\"admin\",\"password\":\"{pwdEscaped}\"}}",
                Encoding.UTF8, "application/json");
            using var authResp = await http.PostAsync($"{baseUrl}/api/auth", authBody, ct).ConfigureAwait(false);
            if (!authResp.IsSuccessStatusCode) return null;
            var authJson = await authResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var authDoc = JsonDocument.Parse(authJson);
            if (!authDoc.RootElement.TryGetProperty("jwt", out var jwtEl)) return null;
            var jwt = jwtEl.GetString();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/endpoints");
            req.Headers.Add("Authorization", $"Bearer {jwt}");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : null;
        }
        catch { return null; }
    }

    // === FailoverAsync (REUSE FailoverTestService) ==========================
    /// <inheritdoc />
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<FailoverResult>(svcR.Error!);
        var s = svcR.Value!;

        var scenario = (request.Direction ?? "consul-leader").Trim().ToLowerInvariant();
        var failover = new FailoverTestService(_catalog, _ssh, new VmrunProcessClient(), s.Http,
            s.ConsulToken, s.NomadToken, _sshUsername, _sshKeyPath);

        Result<FailoverTestReport> rep = scenario switch
        {
            "consul-leader" or "consul" => await failover.RunConsulLeaderAsync(request.TargetNode, cancellationToken).ConfigureAwait(false),
            "nomad-leader" or "nomad" => await failover.RunNomadLeaderAsync(request.TargetNode, cancellationToken).ConfigureAwait(false),
            "swarm-manager" or "swarm" => await failover.RunSwarmManagerAsync(request.TargetNode, cancellationToken).ConfigureAwait(false),
            _ => Result.Fail<FailoverTestReport>(
                $"unknown failover scenario '{scenario}'. Pass --direction consul-leader | nomad-leader | swarm-manager.")
        };
        if (rep.IsFail) return Result.Fail<FailoverResult>(rep.Error!);
        var r = rep.Value!;

        var recovery = r.Recovery switch
        {
            FailoverRecoveryStatus.Recovered => "recovered",
            FailoverRecoveryStatus.RecoveryFailed => "failed",
            _ => "skipped"
        };
        return Result.Ok(new FailoverResult(
            Scenario: scenario,
            OriginalPrimary: r.OriginalLeader,
            NewPrimary: r.NewLeader,
            Rto: r.Rto,
            Recovery: recovery,
            RecoveryHint: r.RecoveryHint,
            Timeline: r.Timeline,
            StartedAtUtc: r.StartedAtUtc));
    }

    // === ScaleOut (drain/demote a node; reversible) =========================
    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name (a swarm-manager-N or swarm-worker-N).");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var (managers, workers) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<ScaleOutResult>(svcR.Error!);
        var s = svcR.Value!;

        var cls = ClassifyNode(request.NodeName);
        var node = managers.Concat(workers).FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"'{request.NodeName}' is not a swarm node (swarm-manager-N / swarm-worker-N).");

        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var dockerR = await DockerNodesAsync(via.Vmnet11, cancellationToken).ConfigureAwait(false);
        if (dockerR.IsFail) return Result.Fail<ScaleOutResult>(dockerR.Error!);
        var dn = dockerR.Value!.FirstOrDefault(x => string.Equals(x.Hostname, node.Name, StringComparison.OrdinalIgnoreCase));

        // Quorum / leader guards.
        if (cls == "manager")
        {
            if (dn?.IsLeader == true)
                return Result.Fail<ScaleOutResult>(
                    $"{node.Name} is the current Swarm raft leader; fail it over first " +
                    $"(`nexus failover-test {ClusterName} --direction swarm-manager`) before draining it.");
            var readyMgrs = dockerR.Value!.Count(x => x.IsManager && string.Equals(x.Status, "Ready", StringComparison.OrdinalIgnoreCase));
            if (readyMgrs <= 2)
                return Result.Fail<ScaleOutResult>(
                    $"only {readyMgrs} managers are Ready; demoting one would drop the raft quorum below 2. Refusing.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var steps = new StringBuilder();

        // 1. Drain the Swarm node (stop scheduling tasks onto it).
        var drain = await _ssh.ExecuteAsync(T(via.Vmnet11),
            $"docker node update --availability drain {node.Name} 2>&1 && echo DRAINED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (drain.IsFail || !(drain.Value!.Stdout.Contains("DRAINED", StringComparison.Ordinal)))
            return Result.Fail<ScaleOutResult>($"docker node drain {node.Name} failed: {(drain.IsFail ? drain.Error : Tail(drain.Value!.Stdout + drain.Value.Stderr, 200))}");
        steps.Append("swarm-drained; ");

        // 2. Demote if it's a manager (removes it from raft, reversibly).
        if (cls == "manager")
        {
            var demote = await _ssh.ExecuteAsync(T(via.Vmnet11),
                $"docker node demote {node.Name} 2>&1 && echo DEMOTED", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (demote.IsOk && demote.Value!.Stdout.Contains("DEMOTED", StringComparison.Ordinal)) steps.Append("swarm-demoted; ");
        }

        // 3. Drain the Nomad client (eligibility off + drain). Workers are Nomad clients.
        var nomadDrain = await _ssh.ExecuteAsync(T(node.Vmnet11),
            "export NOMAD_ADDR=https://127.0.0.1:4646 NOMAD_CACERT=" + NomadCa + " NOMAD_TOKEN='" + s.NomadToken + "'; " +
            "nomad node drain -enable -yes -self 2>&1 | tail -2; echo NOMADDRAIN", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (nomadDrain.IsOk && nomadDrain.Value!.Stdout.Contains("NOMADDRAIN", StringComparison.Ordinal)) steps.Append("nomad-drained; ");

        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"{steps}{node.Name} drained out of scheduling (reversible). The VM stays up + a raft/gossip member. "
                + $"Re-add via `scale-out add {ClusterName} --role {cls}`. Permanently removing a node (`docker node rm`) or growing the "
                + "fixed 3+3 fleet is a terraform operation in nexus-infra-swarm-nomad.",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var (managers, workers) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<ScaleOutResult>(svcR.Error!);
        var s = svcR.Value!;

        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var dockerR = await DockerNodesAsync(via.Vmnet11, cancellationToken).ConfigureAwait(false);
        if (dockerR.IsFail) return Result.Fail<ScaleOutResult>(dockerR.Error!);

        // A "removed" node = a swarm node currently drained (Availability != Active).
        var drained = dockerR.Value!.FirstOrDefault(d => !string.Equals(d.Availability, "Active", StringComparison.OrdinalIgnoreCase));
        if (drained is null)
            return Result.Fail<ScaleOutResult>(
                "all swarm nodes are Active. Growing the fixed 3-manager + 3-worker fleet is a terraform/Packer operation, not a "
                + "runtime scale-out: add the VM + overlays in nexus-infra-swarm-nomad/terraform/envs/swarm-nomad and re-apply "
                + "(the node auto-joins the Swarm + Consul gossip + Nomad). This verb only re-activates a drained existing node.");

        var node = managers.Concat(workers).FirstOrDefault(n => string.Equals(n.Name, drained.Hostname, StringComparison.OrdinalIgnoreCase));
        var cls = node is null ? "worker" : ClassifyNode(node.Name);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var steps = new StringBuilder();

        // 1. Re-activate scheduling on the Swarm node.
        var active = await _ssh.ExecuteAsync(T(via.Vmnet11),
            $"docker node update --availability active {drained.Hostname} 2>&1 && echo ACTIVE", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (active.IsFail || !active.Value!.Stdout.Contains("ACTIVE", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"docker node activate {drained.Hostname} failed: {(active.IsFail ? active.Error : Tail(active.Value!.Stdout + active.Value.Stderr, 200))}");
        steps.Append("swarm-activated; ");

        // 2. Re-promote if it was originally a manager.
        if (cls == "manager")
        {
            var promote = await _ssh.ExecuteAsync(T(via.Vmnet11),
                $"docker node promote {drained.Hostname} 2>&1 && echo PROMOTED", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (promote.IsOk && promote.Value!.Stdout.Contains("PROMOTED", StringComparison.Ordinal)) steps.Append("swarm-promoted; ");
        }

        // 3. Re-enable Nomad eligibility on the node.
        if (node is not null)
        {
            var elig = await _ssh.ExecuteAsync(T(node.Vmnet11),
                "export NOMAD_ADDR=https://127.0.0.1:4646 NOMAD_CACERT=" + NomadCa + " NOMAD_TOKEN='" + s.NomadToken + "'; " +
                "nomad node drain -disable -yes -self 2>&1 | tail -1; nomad node eligibility -enable -self 2>&1 | tail -1; echo NOMADELIG",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            if (elig.IsOk && elig.Value!.Stdout.Contains("NOMADELIG", StringComparison.Ordinal)) steps.Append("nomad-eligible; ");
        }

        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [drained.Hostname],
            Outcome: "ok",
            OutcomeReason: $"{steps}{drained.Hostname} re-activated into scheduling.",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === Backup (consul snapshot + kv export + nomad snapshot) ==============
    /// <inheritdoc />
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<BackupResult>(nodesR.Error!);
        var (managers, _) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<BackupResult>(svcR.Error!);
        var s = svcR.Value!;
        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var ip = via.Vmnet11;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"swarm-{startedAt:yyyyMMdd-HHmmss}"
            : $"swarm-{Sanitize(request.Tag)}-{startedAt:yyyyMMdd-HHmmss}";
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "backups", "swarm", backupId);
        Directory.CreateDirectory(dir);

        var consulEnv = $"CONSUL_HTTP_ADDR=https://127.0.0.1:{ConsulPort} CONSUL_CACERT={ConsulCa} CONSUL_HTTP_TOKEN='{s.ConsulToken}'";
        var nomadEnv = $"NOMAD_ADDR=https://127.0.0.1:{NomadPort} NOMAD_CACERT={NomadCa} NOMAD_TOKEN='{s.NomadToken}'";

        // 1. consul snapshot save + inspect (the round-trip verify) on a manager.
        var consulSnap = await _ssh.ExecuteAsync(T(ip),
            $"sudo rm -f /tmp/{backupId}-consul.snap; export {consulEnv}; consul snapshot save /tmp/{backupId}-consul.snap 2>&1 | tail -1 && " +
            $"consul snapshot inspect /tmp/{backupId}-consul.snap 2>&1", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (consulSnap.IsFail || consulSnap.Value!.ExitCode != 0)
            return Result.Fail<BackupResult>($"consul snapshot save/inspect failed: {(consulSnap.IsFail ? consulSnap.Error : Tail(consulSnap.Value!.Stdout + consulSnap.Value.Stderr, 300))}");
        var inspectMeta = consulSnap.Value.Stdout.Trim();

        // 2. consul kv export.
        await _ssh.ExecuteAsync(T(ip),
            $"export {consulEnv}; consul kv export 2>/dev/null > /tmp/{backupId}-kv.json; echo KVEXPORT", SshTimeout, cancellationToken).ConfigureAwait(false);

        // 3. nomad operator snapshot save.
        var nomadSnap = await _ssh.ExecuteAsync(T(ip),
            $"sudo rm -f /tmp/{backupId}-nomad.snap; export {nomadEnv}; nomad operator snapshot save /tmp/{backupId}-nomad.snap 2>&1 | tail -1; " +
            $"ls -l /tmp/{backupId}-nomad.snap 2>/dev/null | awk '{{print $5}}'", SshTimeout, cancellationToken).ConfigureAwait(false);

        // 4. Download artifacts to the build host.
        long total = 0;
        foreach (var (remote, local) in new[]
        {
            ($"/tmp/{backupId}-consul.snap", Path.Combine(dir, "consul.snap")),
            ($"/tmp/{backupId}-kv.json", Path.Combine(dir, "consul-kv.json")),
            ($"/tmp/{backupId}-nomad.snap", Path.Combine(dir, "nomad.snap")),
        })
        {
            var dl = await _ssh.DownloadBytesAsync(T(ip), remote, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (dl.IsOk) { await File.WriteAllBytesAsync(local, dl.Value!, cancellationToken).ConfigureAwait(false); total += dl.Value!.Length; }
        }

        // 5. best-effort Portainer boltdb copy (NFS-mounted state on a manager).
        var portainerDb = await _ssh.ExecuteAsync(T(ip),
            "f=$(sudo find /data /var/lib/docker/volumes -name portainer.db 2>/dev/null | head -1); " +
            "if [ -n \"$f\" ]; then sudo cp \"$f\" /tmp/" + backupId + "-portainer.db && sudo chmod 644 /tmp/" + backupId + "-portainer.db && echo \"$f\"; else echo NONE; fi",
            SshTimeout, cancellationToken).ConfigureAwait(false);
        if (portainerDb.IsOk && !portainerDb.Value!.Stdout.Contains("NONE", StringComparison.Ordinal))
        {
            var dl = await _ssh.DownloadBytesAsync(T(ip), $"/tmp/{backupId}-portainer.db", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (dl.IsOk) { await File.WriteAllBytesAsync(Path.Combine(dir, "portainer.db"), dl.Value!, cancellationToken).ConfigureAwait(false); total += dl.Value!.Length; }
        }

        // 6. cleanup remote temp.
        await _ssh.ExecuteAsync(T(ip), $"sudo rm -f /tmp/{backupId}-*.snap /tmp/{backupId}-*.json /tmp/{backupId}-*.db; echo CLEAN", SshTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{dir} (consul.snap+consul-kv.json+nomad.snap [+portainer.db]; consul snapshot inspect OK: {Tail(inspectMeta, 120)})",
            SizeBytes: total,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        // GUARD: `consul snapshot restore` / `nomad operator snapshot restore` REPLACE
        // the live KV + job state of the running orchestration tier in place. Require
        // an EXPLICIT --confirm-destructive (on top of the command's --yes) before
        // touching the live cluster.
        if (!request.ConfirmDestructive)
            return Result.Fail<RestoreResult>(
                "swarm restore OVERWRITES the live Consul KV + Nomad job state in place — refused without an explicit opt-in. "
                + "Re-run with --confirm-destructive (in addition to --yes) once certain. Backups are verified non-destructively "
                + "at take time (consul snapshot inspect); to recover onto an ISOLATED cluster instead, follow the DR runbook (handbook §3).");

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "backups", "swarm", request.BackupId);
        var consulSnapLocal = Path.Combine(dir, "consul.snap");
        var nomadSnapLocal = Path.Combine(dir, "nomad.snap");
        if (!File.Exists(consulSnapLocal) && !File.Exists(nomadSnapLocal))
            return Result.Fail<RestoreResult>($"backup '{request.BackupId}' not found under {dir} (expected consul.snap / nomad.snap from a prior `backup take swarm`).");

        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<RestoreResult>(nodesR.Error!);
        var (managers, _) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<RestoreResult>(svcR.Error!);
        var s = svcR.Value!;
        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var ip = via.Vmnet11;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var consulEnv = $"CONSUL_HTTP_ADDR=https://127.0.0.1:{ConsulPort} CONSUL_CACERT={ConsulCa} CONSUL_HTTP_TOKEN='{s.ConsulToken}'";
        var nomadEnv = $"NOMAD_ADDR=https://127.0.0.1:{NomadPort} NOMAD_CACERT={NomadCa} NOMAD_TOKEN='{s.NomadToken}'";
        var remoteConsul = $"/tmp/restore-{request.BackupId}-consul.snap";
        var remoteNomad = $"/tmp/restore-{request.BackupId}-nomad.snap";
        long items = 0;

        // 1. Consul: upload snapshot + `consul snapshot restore` (online, to the leader).
        if (File.Exists(consulSnapLocal))
        {
            var bytes = await File.ReadAllBytesAsync(consulSnapLocal, cancellationToken).ConfigureAwait(false);
            var up = await _ssh.UploadBytesAsync(T(ip), bytes, remoteConsul, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (up.IsFail) return Result.Fail<RestoreResult>($"upload consul.snap to {via.Name} failed: {up.Error}");
            var rr = await _ssh.ExecuteAsync(T(ip), $"export {consulEnv}; consul snapshot restore {remoteConsul} 2>&1", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (rr.IsFail || rr.Value!.ExitCode != 0)
                return Result.Fail<RestoreResult>($"consul snapshot restore failed on {via.Name}: {(rr.IsFail ? rr.Error : Tail(rr.Value!.Stdout + rr.Value.Stderr, 300))}");
            var cnt = await _ssh.ExecuteAsync(T(ip), $"export {consulEnv}; consul kv export 2>/dev/null | grep -c '\"key\"'", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (cnt.IsOk && long.TryParse(cnt.Value!.Stdout.Trim(), out var k)) items += k;
        }

        // 2. Nomad: upload snapshot + `nomad operator snapshot restore` (online).
        if (File.Exists(nomadSnapLocal))
        {
            var bytes = await File.ReadAllBytesAsync(nomadSnapLocal, cancellationToken).ConfigureAwait(false);
            var up = await _ssh.UploadBytesAsync(T(ip), bytes, remoteNomad, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (up.IsFail) return Result.Fail<RestoreResult>($"upload nomad.snap to {via.Name} failed: {up.Error}");
            var rr = await _ssh.ExecuteAsync(T(ip), $"export {nomadEnv}; nomad operator snapshot restore {remoteNomad} 2>&1", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (rr.IsFail || rr.Value!.ExitCode != 0)
                return Result.Fail<RestoreResult>($"nomad operator snapshot restore failed on {via.Name}: {(rr.IsFail ? rr.Error : Tail(rr.Value!.Stdout + rr.Value.Stderr, 300))}");
            var cnt = await _ssh.ExecuteAsync(T(ip), $"export {nomadEnv}; nomad job status 2>/dev/null | tail -n +2 | grep -c .", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (cnt.IsOk && long.TryParse(cnt.Value!.Stdout.Trim(), out var j)) items += j;
        }

        // 3. Cleanup remote temp snapshots.
        await _ssh.ExecuteAsync(T(ip), $"sudo rm -f {remoteConsul} {remoteNomad}; echo CLEAN", SshTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        return Result.Ok(new RestoreResult(request.BackupId, items, sw.Elapsed, startedAt));
    }

    // === RotateCertAsync (vault-agent re-render; consul rolling, nomad parallel) ===
    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<CertRotationResult>(nodesR.Error!);
        var (managers, workers) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<CertRotationResult>(svcR.Error!);
        var s = svcR.Value!;

        // Order consul restarts followers-first / leader-last (workers, then non-leader managers, then leader).
        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var dockerR = await DockerNodesAsync(via.Vmnet11, cancellationToken).ConfigureAwait(false);
        var leaderName = dockerR.IsOk ? dockerR.Value!.FirstOrDefault(d => d.IsLeader)?.Hostname : null;
        var rollingOrder = workers
            .Concat(managers.Where(m => !string.Equals(m.Name, leaderName, StringComparison.OrdinalIgnoreCase)))
            .Concat(managers.Where(m => string.Equals(m.Name, leaderName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        // Phase A: per-node (rolling) — FORCE a fresh leaf, then restart consul.
        //
        // The vault-agent templates issue via `pkiCert "pki_int/issue/<role>"`, which
        // PERSISTS the leaf to the destination file and reuses it across agent
        // restarts (it only re-issues near expiry). So a bare `systemctl restart
        // nexus-vault-agent` does NOT rotate the cert. To force a fresh issue we
        // delete the rendered bundle (after a .bak safety copy) so pkiCert re-issues
        // on the next render, then restart the agent + the service.
        foreach (var n in rollingOrder)
        {
            var oldSerial = await WireSerialAsync(n.Vmnet11, ConsulPort, cancellationToken).ConfigureAwait(false);
            var script = string.Join(" ; ", new[]
            {
                "for f in /etc/consul.d/tls/bundle.pem /etc/nomad.d/tls/bundle.pem /etc/portainer/tls/bundle.pem; do "
                  + "if sudo test -f \"$f\"; then sudo cp -a \"$f\" \"$f.bak\"; sudo rm -f \"$f\"; fi; done",
                "sudo systemctl restart nexus-vault-agent",
                // wait for pkiCert to re-render the consul bundle (the post-render split rebuilds server.crt).
                "for i in $(seq 1 25); do sudo test -f /etc/consul.d/tls/bundle.pem && break; sleep 1; done",
                // restore any bundle that did NOT re-render (vault unreachable etc.); else drop the .bak.
                "for f in /etc/consul.d/tls/bundle.pem /etc/nomad.d/tls/bundle.pem /etc/portainer/tls/bundle.pem; do "
                  + "if sudo test -f \"$f.bak\"; then if sudo test -f \"$f\"; then sudo rm -f \"$f.bak\"; else sudo mv \"$f.bak\" \"$f\"; fi; fi; done",
                "sudo systemctl restart consul",
                "echo ROTATED",
            });
            var exec = await _ssh.ExecuteAsync(T(n.Vmnet11), script, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (exec.IsFail || !exec.Value!.Stdout.Contains("ROTATED", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(n.Name, oldSerial, "(unchanged)",
                    Error: exec.IsFail ? exec.Error : $"force-rerender/consul restart failed: {Tail(exec.Value!.Stdout + exec.Value.Stderr, 200)}"));
                continue;
            }
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var newSerial = await WireSerialAsync(n.Vmnet11, ConsulPort, cancellationToken).ConfigureAwait(false);
            rotated.Add(new CertRotatedNode(n.Name, oldSerial, newSerial, Error: null));
        }

        // Phase B: nomad PARALLEL big-bang restart across all servers + clients
        // ([[nomad-tls-rolling-restart-must-be-parallel]] — a rolling flip strands
        // the first TLS-only node and raft can't elect).
        var nomadTasks = managers.Concat(workers).Select(n =>
            _ssh.ExecuteAsync(T(n.Vmnet11), "sudo systemctl restart nomad && echo NOMADOK", SshTimeout, cancellationToken)).ToArray();
        await Task.WhenAll(nomadTasks).ConfigureAwait(false);

        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    /// <summary>Read the leaf serial a node presents on a TLS port via openssl s_client (path-independent proof of rotation).</summary>
    private async Task<string> WireSerialAsync(string ip, int port, CancellationToken ct)
    {
        var cmd = $"echo | openssl s_client -connect 127.0.0.1:{port} 2>/dev/null | openssl x509 -noout -serial 2>/dev/null | sed 's/serial=//'";
        var r = await _ssh.ExecuteAsync(T(ip), cmd, SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim().Length > 0 ? r.Value.Stdout.Trim() : "(unknown)";
    }

    // === AclAsync (Consul + Nomad ACL tokens) ==============================
    internal sealed record AclTokenInfo(string Accessor, string Description, IReadOnlyList<string> Policies, string Engine);

    /// <summary>Parse `consul acl token list -format=json` (array of token objects).</summary>
    internal static List<AclTokenInfo> ParseConsulAclTokens(string json)
    {
        var list = new List<AclTokenInfo>();
        try
        {
            using var doc = JsonDocument.Parse(Slice(json));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var policies = new List<string>();
                if (t.TryGetProperty("Policies", out var pol) && pol.ValueKind == JsonValueKind.Array)
                    foreach (var p in pol.EnumerateArray())
                        if (p.TryGetProperty("Name", out var nm) && nm.ValueKind == JsonValueKind.String) policies.Add(nm.GetString()!);
                list.Add(new AclTokenInfo(Str(t, "AccessorID"), Str(t, "Description"), policies, "consul"));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Parse `nomad acl token list -json` (array of token objects).</summary>
    internal static List<AclTokenInfo> ParseNomadAclTokens(string json)
    {
        var list = new List<AclTokenInfo>();
        try
        {
            using var doc = JsonDocument.Parse(Slice(json));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var policies = new List<string>();
                if (t.TryGetProperty("Policies", out var pol) && pol.ValueKind == JsonValueKind.Array)
                    foreach (var p in pol.EnumerateArray())
                        if (p.ValueKind == JsonValueKind.String) policies.Add(p.GetString()!);
                var type = Str(t, "Type");
                if (type.Length > 0) policies.Add(type);
                list.Add(new AclTokenInfo(Str(t, "AccessorID"), Str(t, "Name"), policies, "nomad"));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private static bool IsProtectedToken(AclTokenInfo t)
    {
        var d = t.Description.ToLowerInvariant();
        if (ProtectedTokenMarkers.Any(m => d.Contains(m, StringComparison.Ordinal))) return true;
        if (t.Policies.Any(p => ProtectedPolicies.Contains(p.ToLowerInvariant()))) return true;
        return false;
    }

    /// <inheritdoc />
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<AclSnapshot>(nodesR.Error!);
        var (managers, _) = nodesR.Value;
        var svcR = await ServicesAsync(cancellationToken).ConfigureAwait(false);
        if (svcR.IsFail) return Result.Fail<AclSnapshot>(svcR.Error!);
        var s = svcR.Value!;
        var via = await PickManagerAsync(s, managers, cancellationToken).ConfigureAwait(false);
        var ip = via.Vmnet11;
        var verb = operation.Verb.ToLowerInvariant();

        var consulEnv = $"CONSUL_HTTP_ADDR=https://127.0.0.1:{ConsulPort} CONSUL_CACERT={ConsulCa} CONSUL_HTTP_TOKEN='{s.ConsulToken}'";
        var nomadEnv = $"NOMAD_ADDR=https://127.0.0.1:{NomadPort} NOMAD_CACERT={NomadCa} NOMAD_TOKEN='{s.NomadToken}'";

        if (verb is "list" or "describe")
        {
            var all = await ListAllTokensAsync(ip, consulEnv, nomadEnv, cancellationToken).ConfigureAwait(false);
            if (all.IsFail) return Result.Fail<AclSnapshot>(all.Error!);
            var tokens = all.Value!;
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
            {
                var hit = tokens.FirstOrDefault(t =>
                    string.Equals(t.Accessor, operation.User, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Description, operation.User, StringComparison.OrdinalIgnoreCase));
                if (hit is null) return Result.Fail<AclSnapshot>($"no Consul/Nomad ACL token with accessor or description '{operation.User}'.");
                return Result.Ok(new AclSnapshot(ClusterName, verb,
                    [new AclUser($"{hit.Engine}:{(hit.Description.Length > 0 ? hit.Description : hit.Accessor)}",
                        hit.Policies.Concat([hit.Engine]).ToList(), Enabled: true)], DateTimeOffset.UtcNow));
            }
            var users = tokens.Select(t => new AclUser(
                $"{t.Engine}:{(t.Description.Length > 0 ? t.Description : t.Accessor)}",
                t.Policies.Concat(IsProtectedToken(t) ? [t.Engine, "protected"] : [t.Engine]).ToList(),
                Enabled: true)).ToList();
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user (a token description to create, or an accessor id to revoke).");
            var name = operation.User!;

            if (verb == "grant")
            {
                // Create a Consul ACL token with a demonstrative description + the minimal
                // builtin/dns templated policy (Consul refuses a token with no policy/role/identity).
                var desc = $"nexus-acl-{Sanitize(name)}";
                var create = await _ssh.ExecuteAsync(T(ip),
                    $"export {consulEnv}; consul acl token create -description '{desc}' -templated-policy builtin/dns -format=json 2>&1", SshTimeout, cancellationToken).ConfigureAwait(false);
                if (create.IsFail || create.Value!.ExitCode != 0)
                    return Result.Fail<AclSnapshot>($"consul acl token create failed: {(create.IsFail ? create.Error : Tail(create.Value!.Stdout + create.Value.Stderr, 200))}");
                var made = ParseConsulAclTokens($"[{create.Value.Stdout.Trim()}]").FirstOrDefault();
                return Result.Ok(new AclSnapshot(ClusterName, verb,
                    [new AclUser($"consul:{desc}", [made?.Accessor ?? "(created)", "builtin/dns", "consul"], Enabled: true)], DateTimeOffset.UtcNow));
            }
            else
            {
                // Revoke by accessor id; protect bootstrap/management/agent tokens.
                var all = await ListAllTokensAsync(ip, consulEnv, nomadEnv, cancellationToken).ConfigureAwait(false);
                if (all.IsFail) return Result.Fail<AclSnapshot>(all.Error!);
                var hit = all.Value!.FirstOrDefault(t =>
                    string.Equals(t.Accessor, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Description, name, StringComparison.OrdinalIgnoreCase));
                if (hit is null) return Result.Fail<AclSnapshot>($"no ACL token with accessor or description '{name}'.");
                if (IsProtectedToken(hit))
                    return Result.Fail<AclSnapshot>($"refusing to revoke the protected {hit.Engine} token '{(hit.Description.Length > 0 ? hit.Description : hit.Accessor)}' (cluster/operator identity).");
                var del = hit.Engine == "consul"
                    ? await _ssh.ExecuteAsync(T(ip), $"export {consulEnv}; consul acl token delete -accessor-id {hit.Accessor} 2>&1 && echo DELETED", SshTimeout, cancellationToken).ConfigureAwait(false)
                    : await _ssh.ExecuteAsync(T(ip), $"export {nomadEnv}; nomad acl token delete {hit.Accessor} 2>&1 && echo DELETED", SshTimeout, cancellationToken).ConfigureAwait(false);
                if (del.IsFail || !del.Value!.Stdout.Contains("DELETED", StringComparison.Ordinal))
                    return Result.Fail<AclSnapshot>($"acl revoke failed: {(del.IsFail ? del.Error : Tail(del.Value!.Stdout + del.Value.Stderr, 200))}");
                return await AclAsync(new AclOperation("list"), cancellationToken).ConfigureAwait(false);
            }
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    private async Task<Result<List<AclTokenInfo>>> ListAllTokensAsync(string ip, string consulEnv, string nomadEnv, CancellationToken ct)
    {
        var tokens = new List<AclTokenInfo>();
        var consul = await _ssh.ExecuteAsync(T(ip), $"export {consulEnv}; consul acl token list -format=json 2>&1", SshTimeout, ct).ConfigureAwait(false);
        if (consul.IsOk && consul.Value!.ExitCode == 0) tokens.AddRange(ParseConsulAclTokens(consul.Value.Stdout));
        var nomad = await _ssh.ExecuteAsync(T(ip), $"export {nomadEnv}; nomad acl token list -json 2>&1", SshTimeout, ct).ConfigureAwait(false);
        if (nomad.IsOk && nomad.Value!.ExitCode == 0) tokens.AddRange(ParseNomadAclTokens(nomad.Value.Stdout));
        if (tokens.Count == 0)
            return Result.Fail<List<AclTokenInfo>>(
                $"no ACL tokens listed; consul/nomad acl token list returned nothing (consul exit {(consul.IsOk ? consul.Value!.ExitCode : -1)}).");
        return Result.Ok(tokens);
    }

    // === ApplyChaosAsync (nexus-chaos.sh on a WORKER; docker restart after nft) ===
    /// <inheritdoc />
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ChaosOutcome>(nodesR.Error!);
        var (managers, workers) = nodesR.Value;
        if (workers.Count == 0) return Result.Fail<ChaosOutcome>("no swarm worker available as a chaos victim (managers are spared to keep quorum).");

        // Target a WORKER (managers keep raft quorum).
        NodeRecord victim;
        if (!string.IsNullOrWhiteSpace(scenario.Target))
        {
            var t = workers.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase));
            if (t is null)
                return Result.Fail<ChaosOutcome>(ClassifyNode(scenario.Target!) == "manager"
                    ? $"chaos targets a WORKER so the manager raft quorum is preserved; '{scenario.Target}' is a manager. Pick a swarm-worker-N or omit --target."
                    : $"chaos target '{scenario.Target}' is not a swarm worker.");
            victim = t;
        }
        else victim = workers[0];

        var target = T(victim.Vmnet11);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var isProcKill = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase);
        var helperTarget = isProcKill ? "nomad" : "";   // process-kill a worker's nomad client
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Name} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 12)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        // Lift the scenario.
        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);

        // nftables-based scenarios atomically flushed the ruleset (incl Docker's
        // ingress-mesh DNAT) — restart docker to rebuild it ([[nftables-flush-ruleset-wipes-docker]]).
        if (NftScenarios.Contains(scenario.ScenarioType))
            await _ssh.ExecuteAsync(target, "sudo systemctl restart docker 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (isProcKill)
            await _ssh.ExecuteAsync(target, "sudo systemctl reset-failed nomad 2>/dev/null; sudo systemctl start nomad 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        // Recover: poll a LIGHTWEIGHT liveness check (docker node ls on a manager) until the
        // victim worker is Ready+Active again. This is the relevant signal for worker chaos and
        // avoids the full status rollup (whose Portainer probe can each wait the HTTP timeout) —
        // keeping the whole verb inside the chaos command's Duration+60s budget.
        var recovered = false;
        var pollViaIp = managers[0].Vmnet11;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(60);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var post = await DockerNodesAsync(pollViaIp, cancellationToken).ConfigureAwait(false);
            if (post.IsOk)
            {
                var v = post.Value!.FirstOrDefault(d => string.Equals(d.Hostname, victim.Name, StringComparison.OrdinalIgnoreCase));
                var allReady = post.Value!.All(d => string.Equals(d.Status, "Ready", StringComparison.OrdinalIgnoreCase));
                if (allReady && v is not null && string.Equals(v.Availability, "Active", StringComparison.OrdinalIgnoreCase)) { recovered = true; break; }
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
    /// <inheritdoc />
    public bool CanResizeVm(string vmName, string role)
    {
        // Refuse the current Swarm raft leader OR the Nomad raft leader; everything else is safe.
        if (string.Equals(vmName, _lastSwarmLeader, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(vmName, _lastNomadLeader, StringComparison.OrdinalIgnoreCase)) return false;
        return ClassifyNode(vmName) is "manager" or "worker";
    }

    // === helpers ===========================================================
    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string Slice(string json)
    {
        var i = json.IndexOf('[');
        var j = json.LastIndexOf(']');
        return i >= 0 && j > i ? json.Substring(i, j - i + 1) : json;
    }
    private static string Sanitize(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9_-]", "_");
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));

    /// <inheritdoc />
    public void Dispose() => _svc?.Http.Dispose();
}
