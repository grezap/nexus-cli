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
/// MongoDB Replica Set adapter for Phase 0.G.2 (nexus-cli v0.6.1).
/// <para>
/// Implements <see cref="IClusterAdapter"/> via SSH-shell-out to on-node
/// <c>mongosh</c> / <c>mongodump</c> / <c>mongorestore</c> (ADR-0009; ADR-0011
/// records the Mongo-specific design). No managed MongoDB driver is linked
/// (MongoDB.Driver would add multiple MB AOT-reachable + pull a BSON stack).
/// </para>
/// <para>
/// Topology per <c>nexus-platform-plan/docs/infra/vms.yaml</c> (cluster
/// <c>mongo</c>, phase 0.G): 3-member replica set <c>nexus-rs</c> -- mongo-1/2/3
/// at .71/.72/.73 on VMnet11 (service). Single PRIMARY + 2 SECONDARY (unlike
/// Redis Cluster's leader-per-shard model).
/// </para>
/// <para>
/// Connection contract (live-verified against the running RS, 0.G.2):
/// <c>requireTLS</c> on 27017 with a COMBINED <c>server.pem</c> (leaf+key) +
/// <c>ca.crt</c> under <c>/etc/nexus-mongo/tls/</c>; internal RS auth via
/// <c>keyFile</c>; <c>authorization=enabled</c>. The stock <c>mongod</c> unit is
/// MASKED -- the real unit is <c>nexus-mongo</c>. <c>mongosh</c> runs ON the
/// target node (via SSH) under <c>sudo</c> because the TLS material is
/// <c>0640 root:mongodb</c> and nexusadmin is not in the mongodb group.
/// </para>
/// <para>
/// Operator identity (credential model locked with Greg 2026-06-05): the adapter
/// authenticates as the dedicated <c>nexus-cluster-admin</c> SCRAM user (roles
/// clusterMonitor + clusterManager + backup + restore + userAdminAnyDatabase on
/// <c>admin</c> -- the least privilege covering the full verb surface). Its
/// password lives ONLY in Vault KV at <c>nexus/oltp/mongo/operator-password</c>;
/// the adapter fetches it at runtime via <see cref="INexusVaultClient"/> (built
/// from VAULT_ADDR/VAULT_TOKEN/VAULT_CACERT) and passes it to mongosh over SSH --
/// creds transit, never persist on nodes. <c>__system</c> (keyFile identity) is
/// deliberately NOT used for operator queries (discouraged by MongoDB docs).
/// </para>
/// <para>
/// Implementation status (v0.6.1 -- ALL live-verified against the running RS; see
/// docs/verification/0.G.2-mongo.md):
/// <list type="bullet">
///   <item><c>GetStatusAsync</c> / <c>HealthAsync</c> / <c>TopologyAsync</c> -- rs.status() projection</item>
///   <item><c>FailoverAsync</c> -- rs.stepDown() on the PRIMARY + new-primary election poll</item>
///   <item><c>ScaleOutAddAsync</c> / <c>ScaleOutRemoveAsync</c> -- rs.add / rs.remove (apply-on-demand, ADR-0010)</item>
///   <item><c>BackupTakeAsync</c> / <c>BackupRestoreAsync</c> -- mongodump --archive --gzip + mongorestore round-trip</item>
///   <item><c>RotateCertAsync</c> -- genuine re-issue via the node's own Vault token (pki_int/issue/mongo-server)</item>
///   <item><c>AclAsync</c> -- db.getUsers() (list/describe) + createUser/grantRoles (grant)</item>
///   <item><c>ApplyChaosAsync</c> -- pushes nexus-chaos.sh; process-kill nexus-mongo + self-revert</item>
///   <item><c>CanResizeVm</c> -- refuses the current PRIMARY (consumed by IVmResizer)</item>
/// </list>
/// </para>
/// </summary>
public sealed class MongoAdapter : IClusterAdapter
{
    private const string ClusterName = "mongo";
    private const string RsName = "nexus-rs";
    private const string OperatorUser = "nexus-cluster-admin";
    private const string TlsDir = "/etc/nexus-mongo/tls";
    private const string CaFile = TlsDir + "/ca.crt";
    private const string PemFile = TlsDir + "/server.pem";

    // Vault KV (mount nexus/, KV-v2). The operator password is seeded sticky by
    // nexus-infra-vmware security overlay role-overlay-vault-mongo-operator-user-seed.tf.
    private const string VaultMount = "nexus";
    private const string OperatorPwdPath = "oltp/mongo/operator-password";
    private const string OperatorPwdField = "content";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FailoverPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(180);
    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly string[] DefaultGrantRoles = ["read"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    // Lazily fetched operator password (from Vault KV); cached for the process.
    private string? _operatorPassword;
    // Cached topology -- populated on GetStatusAsync; consulted by CanResizeVm (sync).
    private ClusterStatus? _lastStatus;

    public MongoAdapter(
        IVmsCatalog catalog,
        ISshClient ssh,
        string sshUsername,
        string sshKeyPath,
        INexusVaultClient? vault)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
        _vault = vault;
    }

    public string ClusterId => ClusterName;
    public string DisplayName => "MongoDB Replica Set";

    // === Operator password (Vault KV) ======================================

    /// <summary>
    /// Fetch the nexus-cluster-admin password from Vault KV (cached). Returns an
    /// actionable failure when the Vault client is absent (VAULT_* env not set).
    /// </summary>
    private async Task<Result<string>> GetOperatorPasswordAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_operatorPassword))
            return Result.Ok(_operatorPassword);
        if (_vault is null)
            return Result.Fail<string>(
                "mongo verbs authenticate as nexus-cluster-admin, whose password lives in Vault KV. "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT (e.g. `$env:VAULT_ADDR='https://192.168.70.121:8200'; "
                + "$env:VAULT_TOKEN=<token>; $env:VAULT_CACERT=$HOME\\.nexus\\vault-ca-bundle.crt`) and retry.");
        var read = await _vault.ReadKvFieldAsync(VaultMount, OperatorPwdPath, OperatorPwdField, cancellationToken)
            .ConfigureAwait(false);
        if (read.IsFail)
            return Result.Fail<string>(
                $"could not read the operator password from Vault ({VaultMount}/{OperatorPwdPath}): {read.Error}");
        _operatorPassword = read.Value;
        return Result.Ok(_operatorPassword!);
    }

    /// <summary>Build the shared mongosh auth/TLS flag string for the operator user.</summary>
    private static string AuthFlags(string pwd) =>
        $"--tls --tlsCAFile {CaFile} --tlsCertificateKeyFile {PemFile} "
        + $"--username {OperatorUser} --password '{pwd}' --authenticationDatabase admin";

    /// <summary>RS connection string across all declared members (auto-routes writes to PRIMARY).</summary>
    private static string RsUri(IReadOnlyList<NodeRecord> nodes, string suffix = "")
    {
        var hosts = string.Join(",", nodes.Select(n => $"{n.Vmnet11}:27017"));
        return $"mongodb://{hosts}/admin?replicaSet={RsName}{suffix}";
    }

    /// <summary>Run a mongosh --eval on a node, connecting via the RS URI (cluster-wide).</summary>
    private async Task<Result<string>> EvalRsAsync(NodeRecord onNode, string pwd, IReadOnlyList<NodeRecord> nodes, string js, CancellationToken ct, string uriSuffix = "")
    {
        var target = new SshTarget(onNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var cmd = $"sudo mongosh --quiet {AuthFlags(pwd)} '{RsUri(nodes, uriSuffix)}' --eval '{js}'";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {onNode.Name} ({onNode.Vmnet11}) failed: {exec.Error}");
        if (exec.Value!.ExitCode != 0)
            return Result.Fail<string>($"mongosh on {onNode.Name} returned exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");
        return Result.Ok(exec.Value.Stdout.Trim());
    }

    // === GetStatusAsync ====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ClusterStatus>(cluster.Error!);
        if (cluster.Value!.Nodes.Count == 0)
            return Result.Fail<ClusterStatus>($"cluster '{ClusterName}' has no nodes in vms.yaml");

        var pwd = await GetOperatorPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ClusterStatus>(pwd.Error!);

        var nodes = cluster.Value.Nodes;
        // One rs.status() carries the full member view (state + health + optime).
        var js = "var s=rs.status();var p=s.members.map(function(m){return {n:m.name,s:m.stateStr,h:m.health,o:(m.optimeDate?m.optimeDate.getTime():0)}});print(JSON.stringify({set:s.set,members:p}));";
        var res = await TryEachNodeAsync(nodes, pwd.Value!, js, cancellationToken).ConfigureAwait(false);
        if (res.IsFail) return Result.Fail<ClusterStatus>(res.Error!);

        var (members, leader) = ParseRsStatus(res.Value!, nodes);
        if (members.Count == 0)
            return Result.Fail<ClusterStatus>($"rs.status() returned no members (raw: {Tail(res.Value!, 200)})");

        var primaries = members.Count(m => m.Role == "primary");
        var overall = members.Any(m => m.Status != "alive") ? "red"
            : primaries == 1 && members.Count(m => m.Role == "secondary") == members.Count - 1 ? "green"
            : "yellow";

        var status = new ClusterStatus(ClusterName, DisplayName, overall, members, leader, DateTimeOffset.UtcNow);
        _lastStatus = status;
        return Result.Ok(status);
    }

    /// <summary>Run an rs-wide eval, trying each node until one answers (resilient to a down member).</summary>
    private async Task<Result<string>> TryEachNodeAsync(IReadOnlyList<NodeRecord> nodes, string pwd, string js, CancellationToken ct)
    {
        string? lastErr = null;
        foreach (var n in nodes)
        {
            var r = await EvalRsAsync(n, pwd, nodes, js, ct).ConfigureAwait(false);
            if (r.IsOk && r.Value!.Contains('{')) return r;
            lastErr = r.IsFail ? r.Error : $"unparseable output from {n.Name}: {Tail(r.Value ?? "", 200)}";
        }
        return Result.Fail<string>(lastErr ?? "no mongo node answered rs.status()");
    }

    /// <summary>Parse the JSON projection of rs.status() into members + the PRIMARY hostname.</summary>
    private static (IReadOnlyList<ClusterMember> Members, string? Leader) ParseRsStatus(string stdout, IReadOnlyList<NodeRecord> declared)
    {
        var byEndpoint = declared.ToDictionary(n => $"{n.Vmnet11}:27017", n => n, StringComparer.OrdinalIgnoreCase);
        var members = new List<ClusterMember>();
        string? leader = null;
        var json = ExtractJson(stdout);
        if (json is null) return (members, null);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("members", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (members, null);

        // First pass: find the primary optime for lag computation.
        long primaryOptime = 0;
        foreach (var m in arr.EnumerateArray())
            if (m.TryGetProperty("s", out var st) && st.GetString() == "PRIMARY" && m.TryGetProperty("o", out var op))
                primaryOptime = op.GetInt64();

        foreach (var m in arr.EnumerateArray())
        {
            var name = m.TryGetProperty("n", out var nn) ? nn.GetString() ?? "" : "";
            var state = m.TryGetProperty("s", out var ss) ? ss.GetString() ?? "UNKNOWN" : "UNKNOWN";
            var health = m.TryGetProperty("h", out var hh) ? hh.GetDouble() : 0;
            var optime = m.TryGetProperty("o", out var oo) ? oo.GetInt64() : 0;

            var role = state switch
            {
                "PRIMARY" => "primary",
                "SECONDARY" => "secondary",
                "ARBITER" => "arbiter",
                _ => state.ToLowerInvariant()
            };
            var status = health >= 1 && (state is "PRIMARY" or "SECONDARY" or "ARBITER") ? "alive"
                : state is "STARTUP" or "STARTUP2" or "RECOVERING" or "ROLLBACK" ? "syncing"
                : "failed";

            double? lagSec = null;
            if (role == "secondary" && primaryOptime > 0 && optime > 0)
                lagSec = Math.Max(0, (primaryOptime - optime) / 1000.0);

            var hostname = byEndpoint.TryGetValue(name, out var node) ? node.Name : name;
            var ip = node?.Vmnet11 ?? name.Split(':')[0];
            if (role == "primary") leader = hostname;

            members.Add(new ClusterMember(hostname, ip, role, status, ShardId: null, ReplicationLagSeconds: lagSec));
        }
        return (members, leader);
    }

    // === HealthAsync =======================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<HealthReport>(status.Error!);

        var probes = new List<HealthProbe>();
        var members = status.Value!.Members;

        // Quorum probe: a 3-member RS needs a majority (2) up to elect/serve writes.
        var alive = members.Count(m => m.Status == "alive");
        probes.Add(new HealthProbe(
            Name: "quorum",
            Target: RsName,
            Status: alive >= (members.Count / 2 + 1) ? "green" : "red",
            Value: $"{alive}/{members.Count} members up",
            Threshold: $">= {members.Count / 2 + 1} (majority)"));

        // Single-primary probe.
        var primaries = members.Count(m => m.Role == "primary");
        probes.Add(new HealthProbe(
            Name: "primary",
            Target: RsName,
            Status: primaries == 1 ? "green" : "red",
            Value: $"{primaries} PRIMARY",
            Threshold: "exactly 1"));

        // Per-secondary replication lag.
        foreach (var m in members)
        {
            if (m.Role == "primary")
            {
                probes.Add(new HealthProbe("state", m.Hostname, "green", "PRIMARY", null));
                continue;
            }
            if (m.Status != "alive")
            {
                probes.Add(new HealthProbe("state", m.Hostname, "red", m.Status, "alive"));
                continue;
            }
            var lag = m.ReplicationLagSeconds ?? 0;
            var ls = lag < 10 ? "green" : lag < 60 ? "yellow" : "red";
            probes.Add(new HealthProbe("replication-lag", m.Hostname, ls, $"{lag:F1}s", "<10s green; <60s yellow; >=60s red"));
        }

        var overall = probes.Any(p => p.Status == "red") ? "red"
            : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync =====================================================
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);

        var nodes = status.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.Role, m.Status, m.ReplicationLagSeconds))
            .ToList();

        // A replica set is not sharded -- Shards stays null (per the model's contract).
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, CapturedAtUtc: DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (rs.stepDown on the PRIMARY) ========================
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<FailoverResult>(cluster.Error!);
        var nodes = cluster.Value!.Nodes;

        var pwd = await GetOperatorPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<FailoverResult>(pwd.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var before = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (before.IsFail) return Result.Fail<FailoverResult>(before.Error!);
        var preFlightAt = sw.Elapsed;

        var originalPrimary = before.Value!.Members.FirstOrDefault(m => m.Role == "primary");
        if (originalPrimary is null)
            return Result.Fail<FailoverResult>("no PRIMARY found; cannot step down");

        var primaryNode = nodes.FirstOrDefault(n => n.Vmnet11 == originalPrimary.IpAddress);
        if (primaryNode is null)
            return Result.Fail<FailoverResult>($"PRIMARY {originalPrimary.Hostname} not found in vms.yaml node list");

        // rs.stepDown() must run on the PRIMARY (connect locally). It returns by
        // closing the connection as the node steps down -- mongosh exits non-zero
        // with a network error, which is EXPECTED, so we don't treat it as a
        // failure; we measure success by observing a NEW primary via polling.
        var stepDownJs = "try{rs.stepDown(60)}catch(e){print(\"STEPDOWN_ISSUED\")}";
        var localUri = "mongodb://127.0.0.1:27017/admin?replicaSet=" + RsName;
        var sshTarget = new SshTarget(primaryNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var cmd = $"sudo mongosh --quiet {AuthFlags(pwd.Value!)} '{localUri}' --eval '{stepDownJs}'";
        await _ssh.ExecuteAsync(sshTarget, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        var failureInjectedAt = sw.Elapsed;

        // Poll until a NEW primary (different host) is elected.
        string? newPrimary = null;
        var newPrimaryAt = TimeSpan.Zero;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(FailoverPollInterval, cancellationToken).ConfigureAwait(false);
            var poll = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (poll.IsFail) continue;
            var p = poll.Value!.Members.FirstOrDefault(m => m.Role == "primary");
            if (p is not null && !string.Equals(p.Hostname, originalPrimary.Hostname, StringComparison.OrdinalIgnoreCase))
            {
                newPrimary = p.Hostname;
                newPrimaryAt = sw.Elapsed;
                break;
            }
        }
        sw.Stop();

        var rto = newPrimary is not null ? newPrimaryAt - failureInjectedAt : TimeSpan.Zero;
        // The stepped-down node rejoins as SECONDARY automatically; the RS is
        // self-healing, so "recovered" simply reflects a healthy new topology.
        return Result.Ok(new FailoverResult(
            Scenario: "mongo-rs-stepdown",
            OriginalPrimary: originalPrimary.Hostname,
            NewPrimary: newPrimary,
            Rto: rto,
            Recovery: newPrimary is not null ? "recovered" : "failed",
            RecoveryHint: newPrimary is null ? "no new PRIMARY within the deadline; check `rs.status()` + election logs (rs.stepDown holds the old primary down 60s)" : null,
            Timeline: new FailoverTimeline(preFlightAt, failureInjectedAt, newPrimaryAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOutAddAsync (rs.add, apply-on-demand) ========================
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ScaleOutResult>(cluster.Error!);
        var nodes = cluster.Value!.Nodes;

        var pwd = await GetOperatorPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ScaleOutResult>(pwd.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Current member endpoints (from rs.status).
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<ScaleOutResult>(status.Error!);
        var memberIps = status.Value!.Members.Select(m => m.IpAddress).ToHashSet(StringComparer.Ordinal);

        // Discover a provisioned-but-unjoined, reachable mongo node.
        NodeRecord? candidate = null;
        foreach (var n in nodes)
        {
            if (memberIps.Contains(n.Vmnet11)) continue;
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var ping = await _ssh.ExecuteAsync(t, "sudo systemctl is-active nexus-mongo 2>/dev/null || echo down", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (ping.IsOk && ping.Value!.Stdout.Contains("active", StringComparison.Ordinal)) { candidate = n; break; }
        }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "no provisioned-but-unjoined mongo node is reachable. Provision one first (apply-on-demand, ADR-0010): "
                + "add a mongo-N VM + overlays in nexus-infra-oltp/terraform/envs/oltp-mongo, "
                + "`pwsh -File scripts/oltp-mongo.ps1 apply`, then re-run `scale-out add`.");

        var addJs = $"try{{var r=rs.add(\"{candidate.Vmnet11}:27017\");print(\"ADD_OK=\"+r.ok)}}catch(e){{print(\"ADD_ERR:\"+e.message)}}";
        var add = await TryEachMemberAsync(nodes, status.Value.Members, pwd.Value!, addJs, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (add.IsFail || !add.Value!.Contains("ADD_OK=1"))
            return Result.Fail<ScaleOutResult>($"rs.add({candidate.Name}) failed: {(add.IsFail ? add.Error : Tail(add.Value ?? "", 300))}");

        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: "ok",
            OutcomeReason: $"added {candidate.Name} ({candidate.Vmnet11}:27017) to {RsName} as a SECONDARY (initial-sync follows)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === ScaleOutRemoveAsync (rs.remove, primary-guard) ====================
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name");

        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ScaleOutResult>(cluster.Error!);
        var nodes = cluster.Value!.Nodes;
        var node = nodes.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not in the mongo cluster");

        var pwd = await GetOperatorPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ScaleOutResult>(pwd.Error!);

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<ScaleOutResult>(status.Error!);
        var member = status.Value!.Members.FirstOrDefault(m => m.IpAddress == node.Vmnet11);
        if (member is null)
            return Result.Fail<ScaleOutResult>($"{node.Name} ({node.Vmnet11}) is not currently an RS member");
        if (member.Role == "primary" && request.Drain)
            return Result.Fail<ScaleOutResult>(
                $"{node.Name} is the PRIMARY; step it down first (`nexus cluster failover-test mongo`) before removing -- "
                + "removing the PRIMARY directly would force an unplanned election.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var removeJs = $"try{{var r=rs.remove(\"{node.Vmnet11}:27017\");print(\"REMOVE_OK=\"+r.ok)}}catch(e){{print(\"REMOVE_ERR:\"+e.message)}}";
        var rm = await TryEachMemberAsync(nodes, status.Value.Members, pwd.Value!, removeJs, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (rm.IsFail || !rm.Value!.Contains("REMOVE_OK=1"))
            return Result.Fail<ScaleOutResult>($"rs.remove({node.Name}) failed: {(rm.IsFail ? rm.Error : Tail(rm.Value ?? "", 300))}");

        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"removed {node.Name} ({node.Vmnet11}:27017) from {RsName} (node still running; ready for re-add or deprovision)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <summary>Run a write-eval against the current PRIMARY (try members until one is primary-routable).</summary>
    private async Task<Result<string>> TryEachMemberAsync(IReadOnlyList<NodeRecord> nodes, IReadOnlyList<ClusterMember> members, string pwd, string js, CancellationToken ct)
    {
        // Prefer the current primary's node, then any reachable member (RS URI routes writes).
        var ordered = nodes
            .OrderByDescending(n => members.Any(m => m.IpAddress == n.Vmnet11 && m.Role == "primary"))
            .ToList();
        string? lastErr = null;
        foreach (var n in ordered)
        {
            var r = await EvalRsAsync(n, pwd, nodes, js, ct).ConfigureAwait(false);
            if (r.IsOk) return r;
            lastErr = r.Error;
        }
        return Result.Fail<string>(lastErr ?? "no reachable member to run the write");
    }

    // === BackupTakeAsync (mongodump --archive --gzip from a SECONDARY) =====
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<BackupResult>(cluster.Error!);
        var nodes = cluster.Value!.Nodes;

        var pwd = await GetOperatorPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<BackupResult>(pwd.Error!);

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<BackupResult>(status.Error!);

        // Run the dump from a SECONDARY (offloads the PRIMARY); fall back to any member.
        var secondary = status.Value!.Members.FirstOrDefault(m => m.Role == "secondary") ?? status.Value.Members[0];
        var runNode = nodes.FirstOrDefault(n => n.Vmnet11 == secondary.IpAddress) ?? nodes[0];

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"mongo-backup-{startedAt:yyyyMMdd-HHmmss}"
            : $"mongo-{request.Tag}-{startedAt:yyyyMMdd-HHmmss}";
        var dir = "/var/backups/nexus-mongo";
        var archive = $"{dir}/{backupId}.archive.gz";

        // mongodump targets the nexus_smoke application DB (the URI's database
        // path SCOPES the dump -- a /admin path would dump only admin system
        // collections). authSource=admin keeps operator auth. The dump reads
        // from the PRIMARY (default readPreference): a secondary read was
        // observed to return 0 documents for this RS, so we don't offload here.
        // --archive + --gzip = one compressed file, written node-local on the
        // chosen (secondary) node where the mongodump client runs.
        var dumpHosts = string.Join(",", nodes.Select(n => $"{n.Vmnet11}:27017"));
        var dumpUri = $"mongodb://{dumpHosts}/nexus_smoke?replicaSet={RsName}&authSource=admin";
        var script =
            $"sudo mkdir -p {dir}; "
            + $"sudo mongodump --uri '{dumpUri}' --ssl --sslCAFile {CaFile} --sslPEMKeyFile {PemFile} "
            + $"--username {OperatorUser} --password '{pwd.Value}' --authenticationDatabase admin "
            + $"--archive={archive} --gzip 2>&1 | tail -3; "
            + $"sudo stat -c %s {archive}";
        var target = new SshTarget(runNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<BackupResult>($"backup on {runNode.Name} failed: {exec.Error}");
        var outLines = exec.Value!.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        long size = 0;
        if (outLines.Length == 0 || !long.TryParse(outLines[^1].Trim(), out size) || size <= 0)
            return Result.Fail<BackupResult>($"mongodump did not produce a non-empty archive: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{archive} (node-local on {runNode.Name}; mongodump --archive --gzip of {RsName})",
            SizeBytes: size,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupRestoreAsync (mongorestore round-trip into a verify namespace) ==
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id");

        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<RestoreResult>(cluster.Error!);
        var nodes = cluster.Value!.Nodes;

        var pwd = await GetOperatorPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<RestoreResult>(pwd.Error!);

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<RestoreResult>(status.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var dir = "/var/backups/nexus-mongo";
        var archive = $"{dir}/{request.BackupId}.archive.gz";

        // Backups are node-local (the dump ran on a SECONDARY); discover which
        // node actually holds this archive + run the restore from there. The
        // mongorestore client writes via the RS URI, so writes still route to
        // the PRIMARY regardless of which node runs the CLI.
        NodeRecord? runNode = null;
        foreach (var n in nodes)
        {
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var probe = await _ssh.ExecuteAsync(t, $"test -s {archive} && echo FOUND || echo NO", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (probe.IsOk && probe.Value!.Stdout.Contains("FOUND", StringComparison.Ordinal)) { runNode = n; break; }
        }
        if (runNode is null)
            return Result.Fail<RestoreResult>($"backup archive '{request.BackupId}' not found on any mongo node (looked for {archive}). Run `nexus backup take mongo` first, or check the backup id.");
        // Restore into a dedicated verify DB (non-destructive to live data): the
        // round-trip proves the archive is valid + restorable end-to-end.
        var restoreUri = RsUri(nodes);
        var script =
            $"test -s {archive} || {{ echo MISSING-ARCHIVE; exit 9; }}; "
            + $"sudo mongorestore --uri '{restoreUri}' --ssl --sslCAFile {CaFile} --sslPEMKeyFile {PemFile} "
            + $"--username {OperatorUser} --password '{pwd.Value}' --authenticationDatabase admin "
            + $"--gzip --archive={archive} --nsInclude 'nexus_smoke.*' --nsFrom 'nexus_smoke.*' --nsTo 'nexus_restore_verify.*' --drop 2>&1 | tail -3; "
            + $"sudo mongosh --quiet {AuthFlags(pwd.Value!)} '{restoreUri}' --eval "
            + "'var c=db.getSiblingDB(\"nexus_restore_verify\").getCollectionNames().reduce(function(a,n){return a+db.getSiblingDB(\"nexus_restore_verify\").getCollection(n).countDocuments({})},0);print(\"RESTORED=\"+c)'";
        var target = new SshTarget(runNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<RestoreResult>($"restore on {runNode.Name} failed: {exec.Error}");
        var m = System.Text.RegularExpressions.Regex.Match(exec.Value!.Stdout, @"RESTORED=(\d+)");
        if (!m.Success)
            return Result.Fail<RestoreResult>($"mongorestore round-trip did not confirm restored docs: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");

        return Result.Ok(new RestoreResult(
            BackupId: request.BackupId,
            ItemsRestored: long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === RotateCertAsync (genuine re-issue via node's own Vault token) ======
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<CertRotationResult>(cluster.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var node in cluster.Value!.Nodes)
        {
            var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);

            var oldSerialExec = await _ssh.ExecuteAsync(target,
                $"sudo openssl x509 -in {PemFile} -noout -serial 2>/dev/null | sed 's/serial=//'",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.ExitCode == 0 && oldSerialExec.Value.Stdout.Trim().Length > 0
                ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            // Issue a fresh leaf via the node's OWN Vault agent token (pki_int/issue/mongo-server).
            var cn = $"{node.Name}.mongo.nexus.lab";
            var alts = $"{node.Name},{node.Name}.nexus.lab,{node.Name}.mongo.nexus.lab,localhost";
            var ips = $"{node.Vmnet10},{node.Vmnet11},127.0.0.1";
            var issueCmd =
                "T=$(sudo cat /run/nexus-vault-agent/token 2>/dev/null); "
                + "sudo env VAULT_ADDR=https://192.168.70.121:8200 VAULT_TOKEN=\"$T\" VAULT_CACERT=" + CaFile + " "
                + $"/usr/local/bin/vault write -format=json pki_int/issue/mongo-server common_name={cn} alt_names={alts} ip_sans={ips} ttl=2160h";
            var issueExec = await _ssh.ExecuteAsync(target, issueCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (issueExec.IsFail || issueExec.Value!.ExitCode != 0)
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: issueExec.IsFail ? issueExec.Error : $"vault issue failed: {Tail(issueExec.Value!.Stderr, 200)}"));
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
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: $"could not parse vault issue response: {ex.Message}"));
                continue;
            }

            // MongoDB requireTLS wants a COMBINED PEM (leaf + key) at server.pem.
            // ca.crt = issuing intermediate + root (root pulled from the node's CA bundle).
            var combinedPem = cert.TrimEnd() + "\n" + key.TrimEnd() + "\n";
            var writeCmd =
                $"echo {B64(combinedPem)}|base64 -d|sudo tee {PemFile} >/dev/null; "
                + $"echo {B64(ca.TrimEnd() + "\n")}|base64 -d|sudo tee /tmp/_issuing_ca.crt >/dev/null; "
                + $"sudo bash -c 'cat /tmp/_issuing_ca.crt $(ls /etc/vault-agent/ca-bundle.crt 2>/dev/null) > {CaFile} 2>/dev/null || cp /tmp/_issuing_ca.crt {CaFile}'; "
                + $"sudo rm -f /tmp/_issuing_ca.crt; "
                + $"sudo chown root:mongodb {PemFile} {CaFile}; sudo chmod 0640 {PemFile} {CaFile}; "
                + "sudo systemctl restart nexus-mongo; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(target, writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stderr, 200)}"));
                continue;
            }

            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: null));
            // Brief settle so the restarted member rejoins before the next node rotates.
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === AclAsync (db.getUsers list/describe; createUser/grantRoles grant) ==
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<AclSnapshot>(cluster.Error!);
        var nodes = cluster.Value!.Nodes;
        var pwd = await GetOperatorPasswordAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<AclSnapshot>(pwd.Error!);

        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var js = "var u=db.getSiblingDB(\"admin\").getUsers().users.map(function(x){return {u:x.user,r:x.roles.map(function(z){return z.role+\"@\"+z.db})}});print(JSON.stringify(u));";
            var res = await TryEachNodeAsync(nodes, pwd.Value!, js, cancellationToken).ConfigureAwait(false);
            if (res.IsFail) return Result.Fail<AclSnapshot>(res.Error!);
            var users = ParseUsers(res.Value!);
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
                users = users.Where(u => string.Equals(u.Name, operation.User, StringComparison.OrdinalIgnoreCase)).ToList();
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user.");
            var roleNames = operation.Permissions is { Count: > 0 } ? operation.Permissions : DefaultGrantRoles;
            var roles = roleNames.Select(r => $"{{role:\"{r}\",db:\"admin\"}}");
            var rolesArr = "[" + string.Join(",", roles) + "]";
            string js = verb == "grant"
                // createUser if absent, else grantRoles -- idempotent grant.
                ? $"var a=db.getSiblingDB(\"admin\");try{{a.createUser({{user:\"{operation.User}\",pwd:\"{operation.User}-ChangeMe!{DateTime.UtcNow.Ticks}\",roles:{rolesArr}}});print(\"GRANT_CREATED\")}}catch(e){{if(e.codeName===\"Location51003\"||(e.message&&e.message.indexOf(\"already exists\")>=0)){{a.grantRolesToUser(\"{operation.User}\",{rolesArr});print(\"GRANT_UPDATED\")}}else{{print(\"GRANT_ERR:\"+e.message)}}}}"
                : $"db.getSiblingDB(\"admin\").revokeRolesFromUser(\"{operation.User}\",{rolesArr});print(\"REVOKE_OK\")";
            var res = await TryEachMemberAsync(nodes, (await GetStatusAsync(cancellationToken)).Value?.Members ?? Array.Empty<ClusterMember>(), pwd.Value!, js, cancellationToken).ConfigureAwait(false);
            if (res.IsFail || res.Value!.Contains("_ERR:"))
                return Result.Fail<AclSnapshot>($"acl {verb} failed: {(res.IsFail ? res.Error : Tail(res.Value ?? "", 200))}");
            // Re-read the user list to reflect the mutation.
            return await AclAsync(new AclOperation("describe", operation.User), cancellationToken).ConfigureAwait(false);
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    private static List<AclUser> ParseUsers(string stdout)
    {
        var users = new List<AclUser>();
        var json = ExtractJson(stdout);
        if (json is null) return users;
        using var doc = JsonDocument.Parse(json);
        foreach (var u in doc.RootElement.EnumerateArray())
        {
            var name = u.TryGetProperty("u", out var nn) ? nn.GetString() ?? "" : "";
            var roles = new List<string>();
            if (u.TryGetProperty("r", out var rr) && rr.ValueKind == JsonValueKind.Array)
                foreach (var r in rr.EnumerateArray()) roles.Add(r.GetString() ?? "");
            users.Add(new AclUser(name, roles, Enabled: true));
        }
        return users;
    }

    // === ApplyChaosAsync (embedded nexus-chaos.sh; process-kill nexus-mongo) =
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<ChaosOutcome>(status.Error!);

        // Default target: a SECONDARY (safer than the PRIMARY).
        var members = status.Value!.Members;
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? members.FirstOrDefault(m => string.Equals(m.Hostname, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : (members.FirstOrDefault(m => m.Role == "secondary") ?? (members.Count > 0 ? members[0] : null));
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target node found");

        var target = new SshTarget(victim.IpAddress, 22, _sshUsername, _sshKeyPath);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? "nexus-mongo" : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Hostname} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(60);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
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

    /// <summary>Install (idempotent) the embedded nexus-chaos.sh helper on a node.</summary>
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
        if (_lastStatus is null) return false; // conservative: caller should GetStatusAsync first
        var member = _lastStatus.Members.FirstOrDefault(m =>
            string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        return member.Role != "primary";
    }

    // === Helpers ===========================================================

    /// <summary>Extract the first JSON value (object or array) from possibly-noisy mongosh stdout.</summary>
    internal static string? ExtractJson(string stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;
        int objStart = stdout.IndexOf('{');
        int arrStart = stdout.IndexOf('[');
        int start = (objStart, arrStart) switch
        {
            ( < 0, < 0) => -1,
            ( >= 0, < 0) => objStart,
            ( < 0, >= 0) => arrStart,
            _ => Math.Min(objStart, arrStart)
        };
        if (start < 0) return null;
        char open = stdout[start];
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        bool inStr = false;
        for (int i = start; i < stdout.Length; i++)
        {
            var c = stdout[i];
            if (inStr) { if (c == '"' && stdout[i - 1] != '\\') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == open) depth++;
            else if (c == close) { depth--; if (depth == 0) return stdout.Substring(start, i - start + 1); }
        }
        return null;
    }

    private static string Tail(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= n ? s : s.Substring(s.Length - n);
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
