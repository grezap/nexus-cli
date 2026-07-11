using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Percona XtraDB Cluster (Galera) + ProxySQL adapter for Phase 0.G.3
/// (nexus-cli v0.6.2). Implements <see cref="IClusterAdapter"/> via SSH-shell-out
/// to on-node <c>mysql</c> (PXC backends + the ProxySQL <c>:6032</c> admin
/// interface) + <c>mysqldump</c> (no managed MySQL driver). ADR-0012.
/// <para>
/// Topology per vms.yaml (cluster <c>percona</c>): 3 PXC nodes
/// (<c>pxc-node-1/2/3</c> @ .51/.52/.53, Galera synchronous multi-primary on
/// mTLS :3306) + 2 ProxySQL nodes (<c>proxysql-1/2</c> @ .54/.55) fronted by a
/// keepalived VRRP VIP <c>.50</c>. ProxySQL's <c>mysql_galera_hostgroups</c>
/// keeps exactly ONE writer (hostgroup 10), the rest as backup_writer (20) /
/// reader (30), offline (40) when not Synced.
/// </para>
/// <para>
/// Connection contract (live, 0.G.3): unit <c>nexus-percona.service</c>;
/// mTLS-only :3306; certs <c>/etc/nexus-percona/tls/{server-cert,server-key,ca}.pem</c>;
/// PXC nodes reached via <c>sudo mysql -h 127.0.0.1 -u nexus-cluster-admin
/// -p&lt;kv&gt; --ssl-ca=ca.pem --ssl-mode=VERIFY_CA</c> (sudo for the 0750
/// root:mysql cert dir). ProxySQL admin via <c>mysql -h 127.0.0.1 -P6032 -u
/// admin -p&lt;kv&gt;</c>.
/// </para>
/// <para>
/// Operator identity (ADR-0011 model): the dedicated <c>nexus-cluster-admin</c>
/// SQL user (ALL PRIVILEGES WITH GRANT OPTION); its password + the ProxySQL admin
/// password live ONLY in Vault KV (<c>nexus/oltp/percona/operator-password</c> +
/// <c>.../proxysql-admin-password</c>), fetched at runtime via
/// <see cref="INexusVaultClient"/>.
/// </para>
/// </summary>
public sealed class PerconaAdapter : IClusterAdapter
{
    private const string ClusterName = "percona";
    private const string OperatorUser = "nexus-cluster-admin";
    private const string TlsDir = "/etc/nexus-percona/tls";
    private const string CaFile = TlsDir + "/ca.pem";
    private const string TlsArgs = "--ssl-ca=" + CaFile + " --ssl-mode=VERIFY_CA";

    private const string VaultMount = "nexus";
    private const string OperatorPwdPath = "oltp/percona/operator-password";
    private const string ProxysqlPwdPath = "oltp/percona/proxysql-admin-password";
    private const string PwdField = "content";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FailoverPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(180);
    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly string[] DefaultGrantPrivs = ["SELECT"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    private string? _operatorPassword;
    private string? _proxysqlPassword;
    private ClusterStatus? _lastStatus;

    /// <summary>
    /// Constructs the adapter over a vms.yaml catalog, an SSH transport, the lab
    /// SSH identity, and an optional Vault client. <paramref name="vault"/> may be
    /// null; the Vault-backed verbs then fail with a set-VAULT_* hint.
    /// </summary>
    public PerconaAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
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
    public string DisplayName => "Percona XtraDB Cluster";

    // === node helpers ======================================================
    /// <summary>True when the node is a PXC (Galera) backend (<c>pxc*</c> name prefix).</summary>
    private static bool IsPxc(NodeRecord n) => n.Name.StartsWith("pxc", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the node is a ProxySQL front-end (<c>proxysql*</c> name prefix).</summary>
    private static bool IsProxysql(NodeRecord n) => n.Name.StartsWith("proxysql", StringComparison.OrdinalIgnoreCase);

    /// <summary>Partition the cluster's vms.yaml nodes into (PXC backends, ProxySQL front-ends); fails if no PXC node exists.</summary>
    private Result<(IReadOnlyList<NodeRecord> Pxc, IReadOnlyList<NodeRecord> Proxysql)> Split()
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>(cluster.Error!);
        var pxc = cluster.Value!.Nodes.Where(IsPxc).ToList();
        var px = cluster.Value.Nodes.Where(IsProxysql).ToList();
        if (pxc.Count == 0) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>("no pxc nodes in vms.yaml");
        return Result.Ok(((IReadOnlyList<NodeRecord>)pxc, (IReadOnlyList<NodeRecord>)px));
    }

    // === Vault passwords ===================================================
    /// <summary>Fetch (and memoize) the <c>nexus-cluster-admin</c> SQL password from Vault KV.</summary>
    private async Task<Result<string>> OperatorPwdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_operatorPassword)) return Result.Ok(_operatorPassword);
        if (_vault is null)
            return Result.Fail<string>(
                "percona verbs authenticate as nexus-cluster-admin, whose password lives in Vault KV. "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var r = await _vault.ReadKvFieldAsync(VaultMount, OperatorPwdPath, PwdField, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"could not read operator password from Vault ({VaultMount}/{OperatorPwdPath}): {r.Error}");
        _operatorPassword = r.Value;
        return Result.Ok(_operatorPassword!);
    }

    /// <summary>Fetch (and memoize) the ProxySQL <c>:6032</c> admin password from Vault KV.</summary>
    private async Task<Result<string>> ProxysqlPwdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_proxysqlPassword)) return Result.Ok(_proxysqlPassword);
        if (_vault is null) return Result.Fail<string>("ProxySQL admin password lives in Vault KV; set VAULT_ADDR/TOKEN/CACERT.");
        var r = await _vault.ReadKvFieldAsync(VaultMount, ProxysqlPwdPath, PwdField, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"could not read proxysql-admin password from Vault: {r.Error}");
        _proxysqlPassword = r.Value;
        return Result.Ok(_proxysqlPassword!);
    }

    // === mysql exec helpers ================================================
    /// <summary>Run a SQL on a PXC node as the operator over mTLS; returns tab-separated rows (warning lines filtered).</summary>
    private async Task<Result<string>> PxcQueryAsync(string nodeIp, string pwd, string sql, CancellationToken ct, string db = "")
    {
        var target = new SshTarget(nodeIp, 22, _sshUsername, _sshKeyPath);
        var dbArg = string.IsNullOrEmpty(db) ? "" : $" {db}";
        var cmd = $"sudo mysql -h 127.0.0.1 -u {OperatorUser} -p'{pwd}' {TlsArgs}{dbArg} -BNe \"{sql}\" 2>&1";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {nodeIp} failed: {exec.Error}");
        var outp = FilterMysqlWarnings(exec.Value!.Stdout);
        if (exec.Value.ExitCode != 0)
            return Result.Fail<string>($"mysql on {nodeIp} exit {exec.Value.ExitCode}: {Tail(outp, 300)}");
        return Result.Ok(outp.Trim());
    }

    /// <summary>Query the ProxySQL admin interface (:6032) on a proxysql node.</summary>
    private async Task<Result<string>> ProxysqlAdminAsync(string nodeIp, string pwd, string sql, CancellationToken ct)
    {
        var target = new SshTarget(nodeIp, 22, _sshUsername, _sshKeyPath);
        var cmd = $"mysql -h 127.0.0.1 -P6032 -u admin -p'{pwd}' -BNe \"{sql}\" 2>&1";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {nodeIp} failed: {exec.Error}");
        var outp = FilterMysqlWarnings(exec.Value!.Stdout);
        if (exec.Value.ExitCode != 0)
            return Result.Fail<string>($"proxysql admin on {nodeIp} exit {exec.Value.ExitCode}: {Tail(outp, 300)}");
        return Result.Ok(outp.Trim());
    }

    /// <summary>Strip the mysql client's "Using a password on the command line ... insecure" warning lines.</summary>
    private static string FilterMysqlWarnings(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var keep = s.Split('\n')
            .Where(l => !l.Contains("Using a password on the command line", StringComparison.Ordinal))
            .ToArray();
        return string.Join("\n", keep);
    }

    /// <summary>Run a single-value SHOW STATUS LIKE on a PXC node, returning the value column.</summary>
    private async Task<string?> WsrepVarAsync(string nodeIp, string pwd, string var, CancellationToken ct)
    {
        var r = await PxcQueryAsync(nodeIp, pwd, $"SHOW STATUS LIKE '{var}'", ct).ConfigureAwait(false);
        if (r.IsFail) return null;
        // Output: "<var>\t<value>"
        foreach (var line in r.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[0].Equals(var, StringComparison.OrdinalIgnoreCase))
                return parts[1].Trim();
        }
        return null;
    }

    // === GetStatusAsync ====================================================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<ClusterStatus>(split.Error!);
        var (pxc, proxysql) = split.Value;

        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ClusterStatus>(pwd.Error!);

        // ProxySQL hostgroup assignment (writer=10 / backup_writer=20 / reader=30 / offline=40).
        var hostgroupByIp = await ReadHostgroupsAsync(proxysql, cancellationToken).ConfigureAwait(false);

        var members = new List<ClusterMember>();
        string? leader = null;
        foreach (var n in pxc)
        {
            var state = await WsrepVarAsync(n.Vmnet11, pwd.Value!, "wsrep_local_state_comment", cancellationToken).ConfigureAwait(false);
            var hg = hostgroupByIp.TryGetValue(n.Vmnet11, out var g) ? g : -1;
            var role = hg switch { 10 => "primary", 20 => "replica", 30 => "replica", 40 => "offline", _ => state == "Synced" ? "replica" : "unknown" };
            var status = state == "Synced" ? "alive" : state is null ? "failed" : "syncing";
            if (hg == 10) leader = n.Name;
            members.Add(new ClusterMember(n.Name, n.Vmnet11, role, status, ShardId: null, ReplicationLagSeconds: null));
        }
        foreach (var n in proxysql)
        {
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var ping = await _ssh.ExecuteAsync(t, "systemctl is-active nexus-proxysql 2>/dev/null; true", SshTimeout, cancellationToken).ConfigureAwait(false);
            var alive = ping.IsOk && ping.Value!.Stdout.Trim().StartsWith("active", StringComparison.Ordinal);
            members.Add(new ClusterMember(n.Name, n.Vmnet11, "router", alive ? "alive" : "failed", ShardId: null, ReplicationLagSeconds: null));
        }

        var pxcAlive = members.Where(m => m.Role != "router").Count(m => m.Status == "alive");
        var overall = pxcAlive == pxc.Count && members.Where(m => m.Role == "router").All(m => m.Status == "alive") ? "green"
            : pxcAlive >= (pxc.Count / 2 + 1) ? "yellow" : "red";

        var s = new ClusterStatus(ClusterName, DisplayName, overall, members, leader, DateTimeOffset.UtcNow);
        _lastStatus = s;
        return Result.Ok(s);
    }

    /// <summary>Read runtime_mysql_servers from a reachable ProxySQL admin → map PXC IP → hostgroup id.</summary>
    private async Task<Dictionary<string, int>> ReadHostgroupsAsync(IReadOnlyList<NodeRecord> proxysql, CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var pw = await ProxysqlPwdAsync(ct).ConfigureAwait(false);
        if (pw.IsFail) return map;
        foreach (var p in proxysql)
        {
            var r = await ProxysqlAdminAsync(p.Vmnet11, pw.Value!,
                "SELECT hostgroup_id, hostname, status FROM runtime_mysql_servers ORDER BY hostgroup_id", ct).ConfigureAwait(false);
            if (r.IsFail || string.IsNullOrWhiteSpace(r.Value)) continue;
            foreach (var line in r.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3 || !int.TryParse(parts[0], out var hg)) continue;
                var ip = parts[1].Trim();
                var st = parts[2].Trim();
                // Only ONLINE rows reflect a node's effective role -- a node lingers in
                // the writer hostgroup (10) as SHUNNED while it actually serves from
                // backup_writer (20). Pick the LOWEST hostgroup where the node is ONLINE.
                if (!st.Equals("ONLINE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!map.TryGetValue(ip, out var existing) || hg < existing) map[ip] = hg;
            }
            if (map.Count > 0) break; // first reachable proxysql suffices
        }
        return map;
    }

    // === HealthAsync =======================================================
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<HealthReport>(split.Error!);
        var (pxc, proxysql) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<HealthReport>(pwd.Error!);

        var probes = new List<HealthProbe>();
        foreach (var n in pxc)
        {
            var size = await WsrepVarAsync(n.Vmnet11, pwd.Value!, "wsrep_cluster_size", cancellationToken).ConfigureAwait(false);
            var state = await WsrepVarAsync(n.Vmnet11, pwd.Value!, "wsrep_local_state_comment", cancellationToken).ConfigureAwait(false);
            var cstatus = await WsrepVarAsync(n.Vmnet11, pwd.Value!, "wsrep_cluster_status", cancellationToken).ConfigureAwait(false);
            var ready = await WsrepVarAsync(n.Vmnet11, pwd.Value!, "wsrep_ready", cancellationToken).ConfigureAwait(false);
            if (size is null)
            {
                probes.Add(new HealthProbe("node-reachable", n.Name, "red", "unreachable / mysql down", "Synced + size 3"));
                continue;
            }
            probes.Add(new HealthProbe("wsrep-state", n.Name, state == "Synced" ? "green" : "red", state ?? "(null)", "Synced"));
            probes.Add(new HealthProbe("cluster-size", n.Name, size == pxc.Count.ToString(CultureInfo.InvariantCulture) ? "green" : "yellow", size, pxc.Count.ToString(CultureInfo.InvariantCulture)));
            probes.Add(new HealthProbe("cluster-status", n.Name, cstatus == "Primary" ? "green" : "red", cstatus ?? "(null)", "Primary"));
            probes.Add(new HealthProbe("wsrep-ready", n.Name, ready == "ON" ? "green" : "red", ready ?? "(null)", "ON"));
        }
        foreach (var p in proxysql)
        {
            var t = new SshTarget(p.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var ping = await _ssh.ExecuteAsync(t, "systemctl is-active nexus-proxysql 2>/dev/null; true", SshTimeout, cancellationToken).ConfigureAwait(false);
            var ok = ping.IsOk && ping.Value!.Stdout.Trim().StartsWith("active", StringComparison.Ordinal);
            probes.Add(new HealthProbe("proxysql", p.Name, ok ? "green" : "red", ok ? "active" : "down", "active"));
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
        var nodes = status.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.Role, m.Status, null))
            .ToList();
        // Galera = synchronous multi-primary replication, not sharded → Shards=null.
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, CapturedAtUtc: DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (ProxySQL writer failover) ==========================
    /// <inheritdoc />
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<FailoverResult>(split.Error!);
        var (pxc, proxysql) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<FailoverResult>(pwd.Error!);
        if (proxysql.Count == 0) return Result.Fail<FailoverResult>("no proxysql node to observe the writer failover");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var before = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (before.IsFail) return Result.Fail<FailoverResult>(before.Error!);
        var preFlightAt = sw.Elapsed;

        var writer = before.Value!.Members.FirstOrDefault(m => m.Role == "primary");
        if (writer is null) return Result.Fail<FailoverResult>("no ProxySQL writer (hostgroup 10) found to fail over");
        var writerNode = pxc.First(n => n.Vmnet11 == writer.IpAddress);

        // Stop the writer's mysql → ProxySQL's Galera monitor promotes a backup_writer.
        var killTarget = new SshTarget(writerNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        await _ssh.ExecuteAsync(killTarget, "sudo systemctl stop nexus-percona.service", SshTimeout, cancellationToken).ConfigureAwait(false);
        var failureInjectedAt = sw.Elapsed;

        // Poll ProxySQL until a DIFFERENT node holds hostgroup 10.
        string? newWriter = null;
        var newWriterAt = TimeSpan.Zero;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(FailoverPollInterval, cancellationToken).ConfigureAwait(false);
            var hg = await ReadHostgroupsAsync(proxysql, cancellationToken).ConfigureAwait(false);
            var newWriterIp = hg.FirstOrDefault(kv => kv.Value == 10).Key;
            if (!string.IsNullOrEmpty(newWriterIp) && newWriterIp != writerNode.Vmnet11)
            {
                newWriter = pxc.FirstOrDefault(n => n.Vmnet11 == newWriterIp)?.Name ?? newWriterIp;
                newWriterAt = sw.Elapsed;
                break;
            }
        }

        var rto = newWriter is not null ? newWriterAt - failureInjectedAt : TimeSpan.Zero;

        // Recovery: restart the stopped node so it rejoins Galera (unless NoRecover).
        var recovery = "skipped";
        if (!request.NoRecover)
        {
            await _ssh.ExecuteAsync(killTarget, "sudo systemctl start nexus-percona.service", SshTimeout, cancellationToken).ConfigureAwait(false);
            recovery = "recovered";
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "percona-proxysql-writer-failover",
            OriginalPrimary: writerNode.Name,
            NewPrimary: newWriter,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: newWriter is null ? "ProxySQL did not promote a new writer within the deadline; check mysql_galera_hostgroups + the clustercheck monitor user" : null,
            Timeline: new FailoverTimeline(preFlightAt, failureInjectedAt, newWriterAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOutAddAsync / RemoveAsync (Galera join/leave) ================
    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<ScaleOutResult>(split.Error!);
        var (pxc, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ScaleOutResult>(pwd.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // A "joined" node = nexus-percona active + Synced. Find a provisioned node that is NOT active.
        NodeRecord? candidate = null;
        foreach (var n in pxc)
        {
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            // `; true` forces exit 0 so the ssh client always reports success; the
            // state token is the is-active output. Exact-prefix match avoids the
            // "inactive".Contains("active") substring trap — a stopped (unjoined)
            // node reads "inactive"/"failed", a joined one reads "active".
            var ping = await _ssh.ExecuteAsync(t, "systemctl is-active nexus-percona 2>/dev/null; true", SshTimeout, cancellationToken).ConfigureAwait(false);
            var state = ping.IsOk ? ping.Value!.Stdout.Trim() : "";
            if (ping.IsOk && !state.StartsWith("active", StringComparison.Ordinal)) { candidate = n; break; }
        }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "all provisioned pxc nodes are already joined. Provision a new node first (apply-on-demand, ADR-0010): "
                + "add a pxc-node-N + overlays in oltp-percona, `pwsh -File scripts/oltp-percona.ps1 apply`, then re-run `scale-out add`.");

        // Start the service → Galera SST/IST join from a donor.
        var joinTarget = new SshTarget(candidate.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var join = await _ssh.ExecuteAsync(joinTarget, "sudo systemctl start nexus-percona.service && echo STARTED", BackupTimeout, cancellationToken).ConfigureAwait(false);
        if (join.IsFail || !join.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to start nexus-percona on {candidate.Name}: {(join.IsFail ? join.Error : Tail(join.Value!.Stderr, 200))}");

        // Wait for Synced.
        var deadline = sw.Elapsed + TimeSpan.FromMinutes(3);
        var synced = false;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var st = await WsrepVarAsync(candidate.Vmnet11, pwd.Value!, "wsrep_local_state_comment", cancellationToken).ConfigureAwait(false);
            if (st == "Synced") { synced = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: synced ? "ok" : "partial",
            OutcomeReason: synced ? $"{candidate.Name} joined the Galera cluster via SST/IST (Synced)" : $"{candidate.Name} started but not yet Synced (SST may still be running)",
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
        var (pxc, proxysql) = split.Value;
        var node = pxc.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not a pxc node in the percona cluster");

        // Refuse removing the current writer (would force a failover).
        var hg = await ReadHostgroupsAsync(proxysql, cancellationToken).ConfigureAwait(false);
        if (request.Drain && hg.TryGetValue(node.Vmnet11, out var g) && g == 10)
            return Result.Fail<ScaleOutResult>(
                $"{node.Name} is the current ProxySQL writer (hostgroup 10); fail it over first (`nexus failover-test cluster percona`) before removing.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var t = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
        // Graceful Galera leave = stop the service; the remaining members re-form, ProxySQL marks it offline.
        var stop = await _ssh.ExecuteAsync(t, "sudo systemctl stop nexus-percona.service && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to stop nexus-percona on {node.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 200))}");
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"gracefully left {node.Name} from the Galera cluster (service stopped; ProxySQL marks it offline; ready for re-add or deprovision)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupTakeAsync / RestoreAsync (mysqldump round-trip) =============
    /// <inheritdoc />
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<BackupResult>(split.Error!);
        var (pxc, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<BackupResult>(pwd.Error!);

        var statusRes = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (statusRes.IsFail) return Result.Fail<BackupResult>(statusRes.Error!);
        // Dump from a non-writer Synced node where possible (offload the writer).
        var runMember = statusRes.Value!.Members.FirstOrDefault(m => m.Role == "replica" && m.Status == "alive")
            ?? statusRes.Value.Members.First(m => m.Role is "primary" or "replica" && m.Status == "alive");
        var runNode = pxc.First(n => n.Vmnet11 == runMember.IpAddress);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"percona-backup-{startedAt:yyyyMMdd-HHmmss}"
            : $"percona-{request.Tag}-{startedAt:yyyyMMdd-HHmmss}";
        var dir = "/var/backups/nexus-percona";
        var file = $"{dir}/{backupId}.sql.gz";

        // --skip-add-locks + --no-tablespaces: PXC strict_mode=ENFORCING prohibits
        // the explicit LOCK TABLES statements mysqldump emits by default (they would
        // abort the restore between LOCK/UNLOCK). --single-transaction gives a
        // consistent snapshot without locking the source.
        var script =
            $"sudo mkdir -p {dir}; "
            + $"sudo mysqldump -h 127.0.0.1 -u {OperatorUser} -p'{pwd.Value}' {TlsArgs} --single-transaction --skip-add-locks --no-tablespaces --databases nexus_smoke 2>/dev/null | gzip | sudo tee {file} >/dev/null; "
            + $"sudo stat -c %s {file}";
        var target = new SshTarget(runNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<BackupResult>($"backup on {runNode.Name} failed: {exec.Error}");
        var lines = exec.Value!.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        long size = 0;
        if (lines.Length == 0 || !long.TryParse(lines[^1].Trim(), out size) || size <= 0)
            return Result.Fail<BackupResult>($"mysqldump did not produce a non-empty archive: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{file} (node-local on {runNode.Name}; mysqldump --single-transaction of nexus_smoke)",
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
        var (pxc, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<RestoreResult>(pwd.Error!);

        var dir = "/var/backups/nexus-percona";
        var file = $"{dir}/{request.BackupId}.sql.gz";

        // Find the node holding the (node-local) dump.
        NodeRecord? runNode = null;
        foreach (var n in pxc)
        {
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var probe = await _ssh.ExecuteAsync(t, $"test -s {file} && echo FOUND || echo NO", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (probe.IsOk && probe.Value!.Stdout.Contains("FOUND", StringComparison.Ordinal)) { runNode = n; break; }
        }
        if (runNode is null)
            return Result.Fail<RestoreResult>($"backup '{request.BackupId}' not found on any pxc node (looked for {file}).");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        // Restore into a verify schema (non-destructive): rewrite the dump's
        // `nexus_smoke` references to `nexus_restore_verify`, then count rows.
        // Strip the dump's db-context lines (USE / CREATE DATABASE) and pipe the
        // unqualified CREATE TABLE + INSERTs into nexus_restore_verify (the mysql
        // default db). No sed-renaming needed — the table names are unqualified.
        var script =
            "sudo mysql -h 127.0.0.1 -u " + OperatorUser + " -p'" + pwd.Value + "' " + TlsArgs + " -e \"DROP DATABASE IF EXISTS nexus_restore_verify; CREATE DATABASE nexus_restore_verify\" 2>/dev/null; "
            + $"zcat {file} | grep -v '^USE ' | grep -v '^CREATE DATABASE' "
            + "| sudo mysql -h 127.0.0.1 -u " + OperatorUser + " -p'" + pwd.Value + "' " + TlsArgs + " nexus_restore_verify 2>/dev/null; "
            + "sudo mysql -h 127.0.0.1 -u " + OperatorUser + " -p'" + pwd.Value + "' " + TlsArgs + " -BNe \"SELECT CONCAT('RESTORED=', COUNT(*)) FROM nexus_restore_verify.galera_init_test\" 2>&1";
        var target = new SshTarget(runNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<RestoreResult>($"restore on {runNode.Name} failed: {exec.Error}");
        var m = System.Text.RegularExpressions.Regex.Match(FilterMysqlWarnings(exec.Value!.Stdout), @"RESTORED=(\d+)");
        if (!m.Success)
            return Result.Fail<RestoreResult>($"restore round-trip did not confirm restored rows: {Tail(FilterMysqlWarnings(exec.Value.Stdout), 300)}");

        return Result.Ok(new RestoreResult(
            BackupId: request.BackupId,
            ItemsRestored: long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === RotateCertAsync (Vault re-issue per node, rolling restart) =========
    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<CertRotationResult>(split.Error!);
        var (pxc, proxysql) = split.Value;
        var all = pxc.Concat(proxysql).ToList();

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var node in all)
        {
            var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var oldSerialExec = await _ssh.ExecuteAsync(target,
                $"sudo openssl x509 -in {TlsDir}/server-cert.pem -noout -serial 2>/dev/null | sed 's/serial=//'",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.ExitCode == 0 && oldSerialExec.Value.Stdout.Trim().Length > 0
                ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            var svc = IsProxysql(node) ? "nexus-proxysql" : "nexus-percona";
            var cn = $"{node.Name}.percona.nexus.lab";
            var alts = $"{node.Name},{node.Name}.nexus.lab,{node.Name}.percona.nexus.lab,localhost";
            var ips = $"{node.Vmnet10},{node.Vmnet11},127.0.0.1";
            var issueCmd =
                "T=$(sudo cat /run/nexus-vault-agent/token 2>/dev/null); "
                + "sudo env VAULT_ADDR=https://192.168.70.121:8200 VAULT_TOKEN=\"$T\" VAULT_CACERT=" + CaFile + " "
                + $"/usr/local/bin/vault write -format=json pki_int/issue/percona-server common_name={cn} alt_names={alts} ip_sans={ips} ttl=2160h";
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
                using var doc = System.Text.Json.JsonDocument.Parse(issueExec.Value.Stdout);
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

            var writeCmd =
                $"echo {B64(cert.TrimEnd() + "\n")}|base64 -d|sudo tee {TlsDir}/server-cert.pem >/dev/null; "
                + $"echo {B64(key.TrimEnd() + "\n")}|base64 -d|sudo tee {TlsDir}/server-key.pem >/dev/null; "
                + $"echo {B64(ca.TrimEnd() + "\n")}|base64 -d|sudo tee /tmp/_ica.pem >/dev/null; "
                + $"sudo bash -c 'cat /tmp/_ica.pem $(ls /etc/vault-agent/ca-bundle.crt 2>/dev/null) > {CaFile} 2>/dev/null || cp /tmp/_ica.pem {CaFile}'; "
                + $"sudo rm -f /tmp/_ica.pem; sudo chown root:mysql {TlsDir}/server-cert.pem {TlsDir}/server-key.pem {CaFile}; "
                + $"sudo chmod 0640 {TlsDir}/server-cert.pem {TlsDir}/server-key.pem {CaFile}; "
                + $"sudo systemctl restart {svc}; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(target, writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stderr, 200)}"));
                continue;
            }
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: null));
            // Settle so the restarted PXC member rejoins Galera before the next node rotates.
            await Task.Delay(TimeSpan.FromSeconds(IsProxysql(node) ? 3 : 10), cancellationToken).ConfigureAwait(false);
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
        var (pxc, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<AclSnapshot>(pwd.Error!);
        var verb = operation.Verb.ToLowerInvariant();
        var node = pxc[0].Vmnet11;

        if (verb is "list" or "describe")
        {
            var r = await PxcQueryAsync(node, pwd.Value!,
                "SELECT user, host FROM mysql.user WHERE user NOT LIKE 'mysql.%' ORDER BY user", cancellationToken).ConfigureAwait(false);
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
            var privs = string.Join(", ", operation.Permissions is { Count: > 0 } ? operation.Permissions : DefaultGrantPrivs);
            string sql = verb == "grant"
                ? $"CREATE USER IF NOT EXISTS '{operation.User}'@'%' IDENTIFIED BY '{operation.User}-ChangeMe-{DateTime.UtcNow.Ticks}'; GRANT {privs} ON *.* TO '{operation.User}'@'%'; FLUSH PRIVILEGES; SELECT 'GRANT_OK'"
                : $"REVOKE {privs} ON *.* FROM '{operation.User}'@'%'; FLUSH PRIVILEGES; SELECT 'REVOKE_OK'";
            var r = await PxcQueryAsync(node, pwd.Value!, sql, cancellationToken).ConfigureAwait(false);
            if (r.IsFail || !(r.Value!.Contains("GRANT_OK") || r.Value.Contains("REVOKE_OK")))
                return Result.Fail<AclSnapshot>($"acl {verb} failed: {(r.IsFail ? r.Error : Tail(r.Value ?? "", 200))}");
            return await AclAsync(new AclOperation("describe", operation.User), cancellationToken).ConfigureAwait(false);
        }
        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    /// <summary>Parse tab-separated <c>user\thost</c> rows from <c>mysql.user</c> into <see cref="AclUser"/> entries.</summary>
    private static List<AclUser> ParseUsers(string stdout)
    {
        var users = new List<AclUser>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            users.Add(new AclUser(parts[0].Trim(), [$"@{parts[1].Trim()}"], Enabled: true));
        }
        return users;
    }

    // === ApplyChaosAsync ===================================================
    /// <inheritdoc />
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<ChaosOutcome>(status.Error!);

        // Default target: a non-writer PXC node (safer than the writer).
        var members = status.Value!.Members.Where(m => m.Role is "primary" or "replica").ToList();
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? members.FirstOrDefault(m => string.Equals(m.Hostname, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : (members.FirstOrDefault(m => m.Role == "replica") ?? (members.Count > 0 ? members[0] : null));
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target node found");

        var target = new SshTarget(victim.IpAddress, 22, _sshUsername, _sshKeyPath);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? "nexus-percona" : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Hostname} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);

        // For process-kill, the node's mysql stays down after the kill window → restart it so it rejoins.
        if (string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase))
            await _ssh.ExecuteAsync(target, "sudo systemctl start nexus-percona.service 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(90);
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

    /// <summary>Base64-stream the embedded <c>nexus-chaos.sh</c> helper onto the target and mark it executable.</summary>
    private async Task<Result<bool>> PushChaosHelperAsync(SshTarget target, CancellationToken cancellationToken)
    {
        // Read the helper from the assembly's embedded resources (RedisAdapter's
        // assembly is the anchor -- all adapters share it), normalize CRLF->LF so
        // it runs under bash, then push it base64-encoded to survive the SSH shell.
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
        return member.Role != "primary"; // refuse the current ProxySQL writer
    }

    /// <summary>Return the last <paramref name="n"/> characters of <paramref name="s"/> (for compact error tails).</summary>
    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));

    /// <summary>UTF-8 base64-encode a string so PEM material survives the SSH shell verbatim.</summary>
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
