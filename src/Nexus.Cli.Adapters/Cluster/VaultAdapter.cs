using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Nexus.Cli.Adapters.Vault;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Foundation Vault HA cluster adapter — the FIRST non-data-tier adapter
/// (nexus-cli v0.8.1, Phase 0.A-0.D/0.M, ADR-0022). Manages the platform trust
/// root: 3 Raft HA nodes (vault-1/2/3) + the single-node Shamir seal-key
/// custodian (vault-transit) that auto-unseals them.
/// <para>
/// DELIBERATE access split (probed live 2026-06-18, [[reference_foundation_live_contract]]):
/// the control plane runs over <b>HTTP from the build host</b> via
/// <see cref="VaultAdminClient"/> using the operator's existing <c>VAULT_TOKEN</c>
/// (the locked auth model, ADR-0004) so the root token NEVER touches a node's
/// process table; node-local actions (service stop/start/reload, cert file push,
/// chaos, the recover-ha restarts, and vault-transit which is outside the build
/// host's CA bundle) go over SSH. No managed Vault driver and no shelled-out
/// <c>vault</c> binary — NetArchTest-clean.
/// </para>
/// <para>
/// Mutating verbs TARGET STANDBYS so the active node keeps serving:
/// <list type="bullet">
///   <item>failover = <c>sys/step-down</c> on the active → a standby is promoted
///   (RTO measured; Raft leadership is location-independent so there is no forced
///   "return").</item>
///   <item>scale-out = stop/start a STANDBY's <c>vault.service</c> (leaves/rejoins
///   Raft; transit re-auto-unseals on start). Quorum growth (a 4th node) is
///   terraform → graceful N/A.</item>
///   <item>backup = <c>sys/storage/raft/snapshot</c> save to a build-host file +
///   non-destructive gzip/tar <c>meta.json</c> inspect. A destructive restore on
///   the live trust root is deliberately refused.</item>
///   <item>cert-rotate = re-issue each listener cert from <c>pki_int/vault-server</c>
///   + SIGHUP reload, standbys first / active LAST.</item>
///   <item>acl = Vault ACL policies (list/describe/grant/revoke) + AppRoles (list).</item>
///   <item>chaos = process-kill a STANDBY <c>vault.service</c> + Raft rejoin.</item>
/// </list>
/// Plus the bespoke <see cref="IRecoverableCluster"/> <c>recover-ha</c> verb — the
/// declarative boot-race recovery (the only exposed unseal path).
/// </para>
/// </summary>
public sealed class VaultAdapter : IClusterAdapter, IRecoverableCluster, IDisposable
{
    private const string ClusterName = "vault";
    private const string DisplayNameConst = "Foundation Vault HA (Raft + transit auto-unseal)";
    private const string VaultSvc = "vault";
    private const string TlsDir = "/etc/vault.d/tls";
    private const string PkiMount = "pki_int";
    private const string PkiRole = "vault-server";

    private const int VaultPort = 8200;
    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(90);

    // Built-in policies that acl revoke/grant must never touch (operator/system
    // identities + the per-node agent policies that gate the whole fleet).
    private static readonly HashSet<string> ProtectedPolicies = new(StringComparer.OrdinalIgnoreCase)
    { "root", "default", "nexus-admin", "nexus-operator", "nexus-reader", "nexus-foundation-reader", "nomad-jobs", "nexus-bootstrap" };

    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;   // reused for cert-rotate PKI issue

    private VaultAdminClient? _admin;
    private bool _adminTried;
    private ClusterStatus? _lastStatus;

    public VaultAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
        _vault = vault;
    }

    public string ClusterId => ClusterName;
    public string DisplayName => DisplayNameConst;

    // === node classification (from the vms.yaml name) ======================
    /// <summary>"transit" for the seal custodian, "ha" for vault-1/2/3, "other" for anything else.</summary>
    internal static string ClassifyNode(string name)
    {
        var n = name.ToLowerInvariant();
        if (n == "vault-transit") return "transit";
        if (n.StartsWith("vault-", StringComparison.Ordinal)) return "ha";
        return "other";
    }

    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);
    private static string Addr(string ip) => $"https://{ip}:{VaultPort}";

    private Result<(List<NodeRecord> Ha, NodeRecord? Transit)> Nodes()
    {
        var cluster = _catalog.GetCluster("foundation");
        if (cluster.IsFail) return Result.Fail<(List<NodeRecord>, NodeRecord?)>(cluster.Error!);
        var ha = cluster.Value!.Nodes.Where(n => ClassifyNode(n.Name) == "ha")
            .OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (ha.Count == 0) return Result.Fail<(List<NodeRecord>, NodeRecord?)>("no vault-N HA nodes in vms.yaml cluster 'foundation'");
        var transit = cluster.Value.Nodes.FirstOrDefault(n => ClassifyNode(n.Name) == "transit");
        return Result.Ok((ha, transit));
    }

    private Result<VaultAdminClient> Admin()
    {
        if (_admin is not null) return Result.Ok(_admin);
        if (_adminTried) return Result.Fail<VaultAdminClient>(
            "vault control-plane verbs need the operator token. Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        _adminTried = true;
        try
        {
            var resolver = new VaultTokenResolver(new ProcessEnvironmentReader());
            var ctx = resolver.Resolve();
            if (ctx.IsFail) return Result.Fail<VaultAdminClient>(
                $"vault control-plane verbs need the operator token ({ctx.Error}). Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
            _admin = new VaultAdminClient(ctx.Value!);
            return Result.Ok(_admin);
        }
        catch (Exception ex)
        {
            return Result.Fail<VaultAdminClient>($"could not build the vault admin client: {ex.Message}");
        }
    }

    // === transit node status (SSH; outside the build-host CA bundle) ========
    private async Task<bool?> TransitSealedAsync(NodeRecord? transit, CancellationToken ct)
    {
        if (transit is null) return null;
        var r = await _ssh.ExecuteAsync(T(transit.Vmnet11),
            "sudo env VAULT_ADDR=https://127.0.0.1:8200 VAULT_SKIP_VERIFY=1 vault status -format=json 2>/dev/null; true",
            SshTimeout, ct).ConfigureAwait(false);
        if (r.IsFail) return null;
        return ParseSealed(r.Value!.Stdout);
    }

    /// <summary>Extract the boolean "sealed" field from a `vault status -format=json` blob. null if absent.</summary>
    internal static bool? ParseSealed(string statusJson)
    {
        var i = statusJson.IndexOf('{');
        var j = statusJson.LastIndexOf('}');
        if (i < 0 || j <= i) return null;
        try
        {
            using var doc = JsonDocument.Parse(statusJson.Substring(i, j - i + 1));
            return doc.RootElement.TryGetProperty("sealed", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<bool> IsActiveSvcAsync(string ip, CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(ip), $"systemctl is-active {VaultSvc} 2>/dev/null; true", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim().StartsWith("active", StringComparison.Ordinal);
    }

    // === GetStatusAsync ====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ClusterStatus>(nodesR.Error!);
        var (ha, transit) = nodesR.Value;
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<ClusterStatus>(adminR.Error!);
        var admin = adminR.Value!;

        var members = new List<ClusterMember>();
        string? leader = null;
        var unsealed = 0;
        var actives = 0;
        foreach (var n in ha)
        {
            var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
            if (st.IsFail)
            {
                members.Add(new ClusterMember(n.Name, n.Vmnet11, "standby", "failed"));
                continue;
            }
            var s = st.Value!;
            var role = s.IsActive ? "active" : "standby";
            if (s.IsActive) { actives++; leader = n.Name; }
            if (!s.Sealed) unsealed++;
            members.Add(new ClusterMember(n.Name, n.Vmnet11, role, s.Sealed ? "failed" : "alive"));
        }

        var transitSealed = await TransitSealedAsync(transit, cancellationToken).ConfigureAwait(false);
        if (transit is not null)
            members.Add(new ClusterMember(transit.Name, transit.Vmnet11, "transit",
                transitSealed == false ? "alive" : transitSealed == true ? "failed" : "unknown"));

        var overall =
            (unsealed == ha.Count && actives == 1 && transitSealed == false) ? "green"
            : (leader is not null && unsealed >= (ha.Count / 2 + 1)) ? "yellow" : "red";

        var status = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, leader, DateTimeOffset.UtcNow);
        _lastStatus = status;
        return Result.Ok(status);
    }

    // === HealthAsync =======================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<HealthReport>(nodesR.Error!);
        var (ha, transit) = nodesR.Value;
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<HealthReport>(adminR.Error!);
        var admin = adminR.Value!;
        var probes = new List<HealthProbe>();

        var activeCount = 0;
        foreach (var n in ha)
        {
            var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
            if (st.IsFail) { probes.Add(new HealthProbe("vault-node", n.Name, "red", "unreachable", "sealed=false")); continue; }
            var s = st.Value!;
            if (s.IsActive) activeCount++;
            probes.Add(new HealthProbe("seal-status", n.Name, s.Sealed ? "red" : "green",
                s.Sealed ? "sealed" : $"unsealed ({(s.IsActive ? "active" : "standby")}, v{s.Version})", "unsealed"));
        }
        probes.Add(new HealthProbe("active-leader", "ha", activeCount == 1 ? "green" : "red",
            $"{activeCount} active", "exactly 1"));

        // Raft quorum: 3 voters, exactly 1 leader.
        var peers = await admin.RaftPeersAsync(cancellationToken).ConfigureAwait(false);
        if (peers.IsOk)
        {
            var voters = peers.Value!.Count(p => p.Voter);
            var leaders = peers.Value!.Count(p => p.Leader);
            probes.Add(new HealthProbe("raft-peers", "raft", voters == ha.Count && leaders == 1 ? "green" : "yellow",
                $"{peers.Value!.Count} peers / {voters} voters / {leaders} leader", $"{ha.Count} voters + 1 leader"));
        }
        else probes.Add(new HealthProbe("raft-peers", "raft", "red", peers.Error, $"{ha.Count} voters + 1 leader"));

        // transit auto-unseal custodian.
        var ts = await TransitSealedAsync(transit, cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("transit-unseal", transit?.Name ?? "vault-transit",
            ts == false ? "green" : ts == true ? "red" : "yellow",
            ts == false ? "unsealed (auto-unseal serving)" : ts == true ? "SEALED (HA nodes cannot unseal)" : "unknown",
            "unsealed"));

        // gateway (foundation egress) reachability is folded into foundation-ad health,
        // but the operator token round-trip belongs here: list policies proves auth.
        var pol = await admin.ListPoliciesAsync(cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("operator-auth", "VAULT_TOKEN", pol.IsOk ? "green" : "red",
            pol.IsOk ? $"token authorized ({pol.Value!.Count} policies)" : pol.Error, "sys/policies/acl readable"));

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync (Raft peer set; not sharded) =========================
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<TopologySnapshot>(adminR.Error!);

        // Enrich each HA node with its raft voter/leader role.
        var peers = await adminR.Value!.RaftPeersAsync(cancellationToken).ConfigureAwait(false);
        var peerByNode = peers.IsOk
            ? peers.Value!.ToDictionary(p => p.NodeId, p => p, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, VaultRaftPeer>(StringComparer.OrdinalIgnoreCase);

        var nodes = status.Value!.Members.Select(m =>
        {
            var role = m.Role;
            if (peerByNode.TryGetValue(m.Hostname, out var p))
                role = $"{m.Role}/{(p.Leader ? "raft-leader" : p.Voter ? "voter" : "non-voter")}";
            return new TopologyNode(m.Hostname, role, m.Status);
        }).ToList();

        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (sys/step-down on the active node) ===================
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<FailoverResult>(nodesR.Error!);
        var (ha, _) = nodesR.Value;
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<FailoverResult>(adminR.Error!);
        var admin = adminR.Value!;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Find the current active node.
        NodeRecord? active = null;
        foreach (var n in ha)
        {
            var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
            if (st.IsOk && st.Value!.IsActive) { active = n; break; }
        }
        if (active is null) return Result.Fail<FailoverResult>("no active Vault node found to step down");
        var preFlightAt = sw.Elapsed;

        var step = await admin.StepDownAsync(Addr(active.Vmnet11), cancellationToken).ConfigureAwait(false);
        var injectedAt = sw.Elapsed;
        if (step.IsFail) return Result.Fail<FailoverResult>($"step-down on {active.Name} failed: {step.Error}");

        // Poll until a DIFFERENT node is active.
        string? newLeader = null;
        var newLeaderAt = TimeSpan.Zero;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            foreach (var n in ha)
            {
                if (string.Equals(n.Name, active.Name, StringComparison.Ordinal)) continue;
                var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
                if (st.IsOk && st.Value!.IsActive) { newLeader = n.Name; newLeaderAt = sw.Elapsed; break; }
            }
            if (newLeader is not null) break;
        }
        var rto = newLeader is not null ? newLeaderAt - injectedAt : TimeSpan.Zero;
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "vault-step-down",
            OriginalPrimary: active.Name,
            NewPrimary: newLeader,
            Rto: rto,
            Recovery: "skipped",
            RecoveryHint: newLeader is null
                ? $"step-down issued but no standby became active within {FailoverDeadline.TotalSeconds:N0}s; check `vault operator raft list-peers` + transit unseal."
                : $"Raft leadership is location-independent — {active.Name} is now a healthy standby and the cluster served throughout (clients follow the active-node redirect). No forced return needed; re-run failover to move leadership again.",
            Timeline: new FailoverTimeline(preFlightAt, injectedAt, newLeaderAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOut (stop/start a STANDBY HA node) ============================
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name (a vault-N standby)");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var (ha, _) = nodesR.Value;
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<ScaleOutResult>(adminR.Error!);

        var node = ha.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null)
            return Result.Fail<ScaleOutResult>(
                ClassifyNode(request.NodeName) == "transit"
                    ? "refusing to stop vault-transit — it is the Shamir seal-key custodian; stopping it would seal the whole HA cluster."
                    : $"'{request.NodeName}' is not a vault HA node (vault-1/2/3).");

        // Refuse the active node.
        var st = await adminR.Value!.NodeStatusAsync(Addr(node.Vmnet11), cancellationToken).ConfigureAwait(false);
        if (st.IsOk && st.Value!.IsActive)
            return Result.Fail<ScaleOutResult>(
                $"{node.Name} is the current active node; fail it over first (`nexus failover-test cluster vault`) before removing it.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var stop = await _ssh.ExecuteAsync(T(node.Vmnet11), $"sudo systemctl stop {VaultSvc} && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to stop {VaultSvc} on {node.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 200))}");

        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"stopped vault.service on standby {node.Name}; it remains a Raft peer (offline) and the cluster keeps quorum on the other two. Re-add via `scale-out add vault`.",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ScaleOutResult>(nodesR.Error!);
        var (ha, _) = nodesR.Value;
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<ScaleOutResult>(adminR.Error!);
        var admin = adminR.Value!;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // A "removed" node = a vault-N whose vault.service is not active.
        NodeRecord? candidate = null;
        foreach (var n in ha)
            if (!await IsActiveSvcAsync(n.Vmnet11, cancellationToken).ConfigureAwait(false)) { candidate = n; break; }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "all 3 vault HA nodes are already up. Growing the Raft quorum (a 4th voter) is a terraform/Packer operation, "
                + "not a runtime scale-out: add the VM + overlays in nexus-infra-vmware/terraform/envs/foundation and re-apply "
                + "(the node auto-joins Raft + auto-unseals via vault-transit). This verb only restarts a stopped existing standby.");

        var start = await _ssh.ExecuteAsync(T(candidate.Vmnet11),
            $"sudo systemctl reset-failed {VaultSvc} 2>/dev/null; sudo systemctl start {VaultSvc} && echo STARTED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (start.IsFail || !start.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to start {VaultSvc} on {candidate.Name}: {(start.IsFail ? start.Error : Tail(start.Value!.Stderr, 200))}");

        // Poll until it auto-unseals (transit) + rejoins.
        var unsealed = false;
        var deadline = sw.Elapsed + StartDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            var st = await admin.NodeStatusAsync(Addr(candidate.Vmnet11), cancellationToken).ConfigureAwait(false);
            if (st.IsOk && !st.Value!.Sealed) { unsealed = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: unsealed ? "ok" : "partial",
            OutcomeReason: unsealed
                ? $"{candidate.Name} restarted, auto-unsealed via vault-transit, and rejoined Raft as a standby."
                : $"{candidate.Name} started but did not report unsealed within {StartDeadline.TotalSeconds:N0}s (transit may be sealed — try `recover-ha`).",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === Backup (raft snapshot save + non-destructive inspect) ==============
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<BackupResult>(adminR.Error!);
        var admin = adminR.Value!;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"vault-snapshot-{startedAt:yyyyMMdd-HHmmss}"
            : $"vault-{Sanitize(request.Tag)}-{startedAt:yyyyMMdd-HHmmss}";
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "backups", "vault");
        var localPath = Path.Combine(dir, backupId + ".snap");

        var saved = await admin.SaveRaftSnapshotAsync(localPath, cancellationToken).ConfigureAwait(false);
        if (saved.IsFail) return Result.Fail<BackupResult>(saved.Error!);
        var size = saved.Value;

        var inspect = VaultAdminClient.InspectSnapshot(localPath);
        sw.Stop();
        if (inspect.IsFail)
            return Result.Fail<BackupResult>($"snapshot saved ({size} bytes) but failed non-destructive inspect: {inspect.Error}");
        var meta = inspect.Value!;

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{localPath} (build-host raft snapshot; inspect OK: index={meta.Index} term={meta.Term} v{meta.Version}). "
                + "Restore is deliberately not wired on the live trust root — DR runbook restores onto an isolated cluster.",
            SizeBytes: size,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    public Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        // Refuse: `vault operator raft snapshot restore` REPLACES the entire data
        // store of the live trust root (every secret/policy/PKI mount). Surfacing it
        // as a one-liner verb is too dangerous. The take-time inspect already proves
        // the snapshot is structurally restorable.
        return Task.FromResult(Result.Fail<RestoreResult>(
            "restore is intentionally NOT exposed for the foundation Vault trust root — `raft snapshot restore` overwrites EVERY "
            + "secret/policy/PKI mount in place. The snapshot is verified non-destructively at backup time (gzip/tar meta.json inspect). "
            + "To recover from a snapshot, follow the DR runbook (handbook §3) on an ISOLATED cluster, never the live one."));
    }

    // === RotateCertAsync (pki_int/vault-server; SIGHUP reload, active LAST) ==
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<CertRotationResult>(nodesR.Error!);
        var (ha, _) = nodesR.Value;
        if (_vault is null)
            return Result.Fail<CertRotationResult>(
                "cert-rotate issues from pki_int/issue/vault-server via the operator token. Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<CertRotationResult>(adminR.Error!);
        var admin = adminR.Value!;

        // Order: standbys first, active LAST (the active node's reload is a SIGHUP
        // with no leadership change, but rotating it last keeps the blast radius minimal).
        var ordered = new List<(NodeRecord Node, bool Active)>();
        foreach (var n in ha)
        {
            var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
            ordered.Add((n, st.IsOk && st.Value!.IsActive));
        }
        ordered = ordered.OrderBy(x => x.Active ? 1 : 0).ToList();

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();
        foreach (var (node, _) in ordered)
        {
            var oldSerialExec = await _ssh.ExecuteAsync(T(node.Vmnet11),
                $"sudo openssl x509 -in {TlsDir}/vault.crt -noout -serial 2>/dev/null | sed 's/serial=//'", SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.Stdout.Trim().Length > 0 ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            var cn = $"{node.Name}.nexus.lab";
            var alts = $"{node.Name},{cn},localhost";
            var ips = $"{node.Vmnet11},{node.Vmnet10},127.0.0.1";
            var issue = await _vault.IssuePkiCertAsync(PkiMount, PkiRole, cn, alts, ips, "2160h", cancellationToken).ConfigureAwait(false);
            if (issue.IsFail)
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)", Error: $"vault issue failed: {issue.Error}"));
                continue;
            }
            var d = issue.Value!;
            var certPem = d.Certificate.TrimEnd() + "\n" + (d.IssuingCa?.TrimEnd() ?? "") + "\n";
            var keyPem = d.PrivateKey.TrimEnd() + "\n";

            var writeCmd =
                $"echo {B64(certPem)}|base64 -d|sudo tee {TlsDir}/vault.crt >/dev/null; "
                + $"echo {B64(keyPem)}|base64 -d|sudo tee {TlsDir}/vault.key >/dev/null; "
                + $"sudo chown vault:vault {TlsDir}/vault.crt {TlsDir}/vault.key; "
                + $"sudo chmod 644 {TlsDir}/vault.crt; sudo chmod 600 {TlsDir}/vault.key; "
                + $"sudo systemctl reload {VaultSvc}; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(T(node.Vmnet11), writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stdout + writeExec.Value.Stderr, 220)}"));
                continue;
            }
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, d.SerialNumber, Error: null));
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === AclAsync (Vault ACL policies + AppRoles) ===========================
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<AclSnapshot>(adminR.Error!);
        var admin = adminR.Value!;
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
            {
                var hcl = await admin.ReadPolicyAsync(operation.User!, cancellationToken).ConfigureAwait(false);
                if (hcl.IsOk && hcl.Value!.Length > 0)
                {
                    var lines = hcl.Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(20).ToArray();
                    return Result.Ok(new AclSnapshot(ClusterName, verb, [new AclUser(operation.User!, lines, Enabled: true)], DateTimeOffset.UtcNow));
                }
                // maybe it's an approle
                var roles0 = await admin.ListApprolesAsync(cancellationToken).ConfigureAwait(false);
                if (roles0.IsOk && roles0.Value!.Any(r => string.Equals(r, operation.User, StringComparison.OrdinalIgnoreCase)))
                    return Result.Ok(new AclSnapshot(ClusterName, verb, [new AclUser(operation.User!, ["approle"], Enabled: true)], DateTimeOffset.UtcNow));
                return Result.Fail<AclSnapshot>($"no Vault policy or approle named '{operation.User}'.");
            }

            var pols = await admin.ListPoliciesAsync(cancellationToken).ConfigureAwait(false);
            if (pols.IsFail) return Result.Fail<AclSnapshot>(pols.Error!);
            var roles = await admin.ListApprolesAsync(cancellationToken).ConfigureAwait(false);
            var users = new List<AclUser>();
            foreach (var p in pols.Value!.OrderBy(x => x, StringComparer.Ordinal))
                users.Add(new AclUser(p, ProtectedPolicies.Contains(p) ? ["policy", "protected"] : ["policy"], Enabled: true));
            if (roles.IsOk)
                foreach (var r in roles.Value!.OrderBy(x => x, StringComparer.Ordinal))
                    users.Add(new AclUser(r, ["approle"], Enabled: true));
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user (the policy name).");
            var name = operation.User!;
            if (ProtectedPolicies.Contains(name) || name.StartsWith("nexus-agent-", StringComparison.OrdinalIgnoreCase))
                return Result.Fail<AclSnapshot>($"refusing to {verb} the built-in/agent policy '{name}' (operator/system/fleet identity).");

            if (verb == "grant")
            {
                // Permissions = capabilities; default read+list on a demonstrative path.
                var caps = operation.Permissions is { Count: > 0 }
                    ? string.Join(", ", operation.Permissions.Select(c => $"\"{c}\""))
                    : "\"read\", \"list\"";
                var hcl = $"path \"nexus/data/demo/*\" {{\n  capabilities = [{caps}]\n}}\n";
                var w = await admin.WritePolicyAsync(name, hcl, cancellationToken).ConfigureAwait(false);
                if (w.IsFail) return Result.Fail<AclSnapshot>($"acl grant failed: {w.Error}");
            }
            else
            {
                var del = await admin.DeletePolicyAsync(name, cancellationToken).ConfigureAwait(false);
                if (del.IsFail) return Result.Fail<AclSnapshot>($"acl revoke failed: {del.Error}");
            }
            // echo back the resulting state.
            return await AclAsync(new AclOperation(verb == "grant" ? "describe" : "list", verb == "grant" ? name : null), cancellationToken).ConfigureAwait(false);
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    // === ApplyChaosAsync (process-kill a STANDBY vault + Raft rejoin) ========
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<ChaosOutcome>(nodesR.Error!);
        var (ha, _) = nodesR.Value;
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<ChaosOutcome>(adminR.Error!);
        var admin = adminR.Value!;

        // Pick a STANDBY victim (never the active, never transit).
        NodeRecord? victim = null;
        if (!string.IsNullOrWhiteSpace(scenario.Target))
        {
            victim = ha.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase));
            if (victim is null) return Result.Fail<ChaosOutcome>($"chaos target '{scenario.Target}' is not a vault HA node.");
            var st = await admin.NodeStatusAsync(Addr(victim.Vmnet11), cancellationToken).ConfigureAwait(false);
            if (st.IsOk && st.Value!.IsActive)
                return Result.Fail<ChaosOutcome>($"{victim.Name} is the active node; chaos targets a STANDBY so the cluster keeps serving. Omit --target or pick a standby.");
        }
        else
        {
            foreach (var n in ha)
            {
                var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
                if (st.IsOk && !st.Value!.IsActive && !st.Value.Sealed) { victim = n; break; }
            }
        }
        if (victim is null) return Result.Fail<ChaosOutcome>("no standby vault node available as a chaos victim");

        var target = T(victim.Vmnet11);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? VaultSvc : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Name} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 15)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase))
            await _ssh.ExecuteAsync(target, $"sudo systemctl reset-failed {VaultSvc} 2>/dev/null; sudo systemctl start {VaultSvc} 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        // Recover: poll until the victim is unsealed again + cluster green.
        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(120);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var post = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (post.IsOk && post.Value!.OverallHealth == "green") { recovered = true; break; }
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

    // === RecoverHaAsync (IRecoverableCluster; the boot-race recovery) ========
    /// <summary>Parse a vault-transit-init.json into (unseal_keys_b64, threshold). Threshold defaults to 3.</summary>
    internal static Result<(List<string> Keys, int Threshold)> ParseTransitInit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("unseal_keys_b64", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return Result.Fail<(List<string>, int)>("init file has no unseal_keys_b64 array");
            var keys = arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            var threshold = root.TryGetProperty("unseal_threshold", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 3;
            if (keys.Count < threshold) return Result.Fail<(List<string>, int)>($"init file has {keys.Count} keys but threshold is {threshold}");
            return Result.Ok((keys, threshold));
        }
        catch (JsonException ex) { return Result.Fail<(List<string>, int)>($"init file is not valid JSON: {ex.Message}"); }
    }

    public async Task<Result<RecoverHaResult>> RecoverHaAsync(CancellationToken cancellationToken)
    {
        var nodesR = Nodes();
        if (nodesR.IsFail) return Result.Fail<RecoverHaResult>(nodesR.Error!);
        var (ha, transit) = nodesR.Value;
        if (transit is null) return Result.Fail<RecoverHaResult>("no vault-transit node in vms.yaml; cannot drive the unseal chain");
        var adminR = Admin();
        if (adminR.IsFail) return Result.Fail<RecoverHaResult>(adminR.Error!);
        var admin = adminR.Value!;

        var initFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nexus", "vault-transit-init.json");
        if (!File.Exists(initFile))
            return Result.Fail<RecoverHaResult>($"transit init file not found at {initFile} (holds the Shamir unseal keys); cannot recover.");
        var parsed = ParseTransitInit(await File.ReadAllTextAsync(initFile, cancellationToken).ConfigureAwait(false));
        if (parsed.IsFail) return Result.Fail<RecoverHaResult>(parsed.Error!);
        var (keys, threshold) = parsed.Value;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // 1. Unseal vault-transit (idempotent — already-unsealed is a no-op).
        var transitSealed = await TransitSealedAsync(transit, cancellationToken).ConfigureAwait(false);
        if (transitSealed != false)
        {
            for (var i = 0; i < threshold; i++)
            {
                var unseal = await _ssh.ExecuteAsync(T(transit.Vmnet11),
                    $"sudo env VAULT_ADDR=https://127.0.0.1:8200 VAULT_SKIP_VERIFY=1 vault operator unseal '{keys[i].Replace("'", "")}' >/dev/null 2>&1; echo DONE",
                    SshTimeout, cancellationToken).ConfigureAwait(false);
                if (unseal.IsFail) return Result.Fail<RecoverHaResult>($"failed to submit unseal key {i + 1}/{threshold} to vault-transit: {unseal.Error}");
            }
            transitSealed = await TransitSealedAsync(transit, cancellationToken).ConfigureAwait(false);
            if (transitSealed != false)
                return Result.Fail<RecoverHaResult>($"vault-transit STILL sealed after submitting {threshold} keys; check journalctl on {transit.Name}.");
        }

        // 2. reset-failed + start vault on the HA nodes.
        foreach (var n in ha)
            await _ssh.ExecuteAsync(T(n.Vmnet11),
                $"sudo systemctl reset-failed {VaultSvc} 2>/dev/null; sudo systemctl start {VaultSvc} 2>/dev/null; exit 0",
                SshTimeout, cancellationToken).ConfigureAwait(false);

        // 3. Poll each HA node until unsealed.
        var nodeResults = new Dictionary<string, RecoverNodeResult>(StringComparer.OrdinalIgnoreCase);
        var deadline = sw.Elapsed + StartDeadline;
        while (sw.Elapsed < deadline && nodeResults.Count < ha.Count)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            foreach (var n in ha)
            {
                if (nodeResults.ContainsKey(n.Name)) continue;
                var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
                if (st.IsOk && !st.Value!.Sealed)
                    nodeResults[n.Name] = new RecoverNodeResult(n.Name, Sealed: false, "unsealed");
            }
        }
        // Any node not yet unsealed -> final probe + mark failed/sealed.
        string? leader = null;
        foreach (var n in ha)
        {
            if (nodeResults.ContainsKey(n.Name)) continue;
            var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
            var sealedNow = st.IsFail || st.Value!.Sealed;
            nodeResults[n.Name] = new RecoverNodeResult(n.Name, sealedNow, sealedNow ? "failed: still sealed" : "unsealed");
        }
        foreach (var n in ha)
        {
            var st = await admin.NodeStatusAsync(Addr(n.Vmnet11), cancellationToken).ConfigureAwait(false);
            if (st.IsOk && st.Value!.IsActive) { leader = n.Name; break; }
        }
        sw.Stop();

        var ordered = ha.Select(n => nodeResults[n.Name]).ToList();
        var allUnsealed = ordered.All(r => !r.Sealed);
        return Result.Ok(new RecoverHaResult(ClusterName, TransitUnsealed: true, ordered, allUnsealed, leader, sw.Elapsed, startedAt));
    }

    // === CanResizeVm =======================================================
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false;
        var member = _lastStatus.Members.FirstOrDefault(m => string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        // Refuse the active node + the transit seal custodian; standbys are safe.
        return member.Role == "standby";
    }

    private static string Sanitize(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9_]", "_");
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    public void Dispose() => _admin?.Dispose();
}
