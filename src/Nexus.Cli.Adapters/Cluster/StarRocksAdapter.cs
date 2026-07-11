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
/// StarRocks (3 FE BDB-JE quorum + 3 BE) adapter for Phase 0.G.6 (nexus-cli
/// v0.6.5). Implements <see cref="IClusterAdapter"/> via SSH-shell-out to the
/// on-node <c>mysql</c> client against an FE's MySQL-protocol query port
/// (<c>:9030</c>) — no managed MySqlConnector/JDBC driver (NetArchTest-enforced).
/// ADR-0015.
/// <para>
/// Topology per vms.yaml (cluster <c>starrocks</c>): 3 FE nodes (sr-fe-leader +
/// sr-fe-follower-1/2 @ .31/.32/.33) running <c>nexus-starrocks-fe.service</c> —
/// a BDB-JE replicated metadata quorum (1 LEADER + 2 FOLLOWER, dynamic election)
/// — plus 3 BE nodes (sr-be-1/2/3 @ .34/.36) running
/// <c>nexus-starrocks-be.service</c> that hold the tablet data. A table is
/// <c>DISTRIBUTED BY HASH(...) BUCKETS n</c> (sharded across the BE) ×
/// <c>replication_num=3</c> (replicated). Front door = round-robin DNS
/// <c>starrocks-fe.nexus.lab</c>, no VIP (ADR-0031).
/// </para>
/// <para>
/// Connection contract (live, 0.G.6): <c>mysql --skip-ssl -h 127.0.0.1 -P 9030
/// -u nexus-cluster-admin</c> (the deb13 MariaDB 11.8 client requires
/// <c>--skip-ssl</c> for password auth; the password is passed via
/// <c>MYSQL_PWD</c> to avoid argv exposure). <c>SHOW FRONTENDS</c> /
/// <c>SHOW BACKENDS</c> report the <b>VMnet10 backplane</b> IP (.10.x), not the
/// service IP — mapped back to a node via vms.yaml's vmnet10. The FE leader is
/// the <c>Role=LEADER</c> row. Backup repository <c>nexus_backups</c> (file://
/// NFS, ADR-0032). PKI role <c>starrocks-server</c> (domain
/// <c>starrocks.nexus.lab</c>, all 6 nodes). FE TLS
/// <c>/opt/starrocks/fe/conf/tls</c>, BE <c>/opt/starrocks/be/conf/tls</c>.
/// </para>
/// <para>
/// Operator identity (ADR-0015, the LOCKED Vault-KV model — identical to
/// clickhouse/mongo/percona/patroni): the dedicated <c>nexus-cluster-admin</c>
/// StarRocks user (granted <c>cluster_admin</c> + <c>db_admin</c> +
/// <c>user_admin</c>, <c>DEFAULT ROLE ALL</c>; distinct from the built-in
/// <c>root</c>); its password lives ONLY in Vault KV
/// (<c>nexus/analytics/starrocks/operator-password</c>), fetched at runtime via
/// <see cref="INexusVaultClient"/>. StarRocks is password-auth (root requires a
/// password over the MySQL wire).
/// </para>
/// </summary>
public sealed class StarRocksAdapter : IClusterAdapter
{
    private const string ClusterName = "starrocks";
    private const string DisplayNameConst = "StarRocks (FE quorum + BE)";
    private const string OperatorUser = "nexus-cluster-admin";

    private const string FeSvc = "nexus-starrocks-fe";
    private const string BeSvc = "nexus-starrocks-be";
    private const string FeTlsDir = "/opt/starrocks/fe/conf/tls";
    private const string BeTlsDir = "/opt/starrocks/be/conf/tls";
    private const int QueryPort = 9030;     // FE MySQL-protocol
    private const int FeEditLogPort = 9010; // ALTER SYSTEM ADD FOLLOWER target
    private const int BeHeartbeatPort = 9050;
    private const string PkiRole = "starrocks-server";
    private const string Repo = "nexus_backups";
    private const string Db = "nexus";
    private const string Table = "events";

    private const string VaultMount = "nexus";
    private const string OperatorPwdPath = "analytics/starrocks/operator-password";
    private const string PwdField = "password";
    private const string VaultAddr = "https://192.168.70.121:8200";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan JoinDeadline = TimeSpan.FromMinutes(3);
    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly string[] DefaultGrantPrivs = ["SELECT"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    private string? _operatorPassword;
    private ClusterStatus? _lastStatus;

    /// <summary>
    /// Creates the adapter over the vms.yaml catalog, an SSH client + credentials for
    /// on-node <c>mysql</c> dispatch, and an optional operator
    /// <see cref="INexusVaultClient"/> (the Vault-KV source of the nexus-cluster-admin
    /// password).
    /// </summary>
    public StarRocksAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
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
    private static bool IsFe(NodeRecord n) => n.Name.StartsWith("sr-fe", StringComparison.OrdinalIgnoreCase);
    private static bool IsBe(NodeRecord n) => n.Name.StartsWith("sr-be", StringComparison.OrdinalIgnoreCase);

    private Result<(IReadOnlyList<NodeRecord> Fe, IReadOnlyList<NodeRecord> Be)> Split()
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>(cluster.Error!);
        var fe = cluster.Value!.Nodes.Where(IsFe).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        var be = cluster.Value.Nodes.Where(IsBe).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (fe.Count == 0) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>("no sr-fe* nodes in vms.yaml cluster 'starrocks'");
        return Result.Ok(((IReadOnlyList<NodeRecord>)fe, (IReadOnlyList<NodeRecord>)be));
    }

    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    /// <summary>Map a StarRocks-reported IP (VMnet10 backplane, or service) to a node.</summary>
    private static NodeRecord? ByReportedIp(IReadOnlyList<NodeRecord> nodes, string ip) =>
        nodes.FirstOrDefault(n => n.Vmnet10 == ip || n.Vmnet11 == ip);

    // === Vault password ====================================================
    private async Task<Result<string>> OperatorPwdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_operatorPassword)) return Result.Ok(_operatorPassword);
        if (_vault is null)
            return Result.Fail<string>(
                "starrocks verbs authenticate as nexus-cluster-admin, whose password lives in Vault KV. "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var r = await _vault.ReadKvFieldAsync(VaultMount, OperatorPwdPath, PwdField, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"could not read operator password from Vault ({VaultMount}/{OperatorPwdPath}): {r.Error}");
        _operatorPassword = r.Value;
        return Result.Ok(_operatorPassword!);
    }

    // === mysql helpers =====================================================
    /// <summary>Run SQL as the operator on an FE node; returns trimmed stdout. -N for tuple-only.</summary>
    private async Task<Result<string>> SqlAsync(string feIp, string pwd, string sql, CancellationToken ct, bool tuplesOnly = false, bool vertical = false, TimeSpan? timeout = null)
    {
        var esc = sql.Replace("'", "'\\''");
        var flags = (tuplesOnly ? "-N " : "") + (vertical ? "--vertical " : "");
        // MYSQL_PWD avoids both argv exposure and the "password on command line"
        // warning. Filter that warning line out of stderr-merged output anyway.
        var cmd = $"MYSQL_PWD='{pwd}' mysql --skip-ssl -h 127.0.0.1 -P {QueryPort} -u {OperatorUser} {flags}-e '{esc}' 2>&1";
        var exec = await _ssh.ExecuteAsync(T(feIp), cmd, timeout ?? SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {feIp} failed: {exec.Error}");
        var outp = StripPwWarning(exec.Value!.Stdout);
        if (exec.Value.ExitCode != 0)
            return Result.Fail<string>($"mysql on {feIp} exit {exec.Value.ExitCode}: {Tail(outp, 300)}");
        return Result.Ok(outp.Trim());
    }

    private static string StripPwWarning(string s) =>
        string.Join('\n', s.Split('\n').Where(l => !l.Contains("Using a password on the command line", StringComparison.Ordinal)));

    private async Task<bool> IsActiveAsync(string nodeIp, string unit, CancellationToken ct)
    {
        var ping = await _ssh.ExecuteAsync(T(nodeIp), $"systemctl is-active {unit} 2>/dev/null; true", SshTimeout, ct).ConfigureAwait(false);
        return ping.IsOk && ping.Value!.Stdout.Trim().StartsWith("active", StringComparison.Ordinal);
    }

    // === SHOW FRONTENDS / SHOW BACKENDS parsing (\G vertical) ===============
    private sealed record FeRow(string Ip, string Role, bool Alive);
    private sealed record BeRow(string Ip, bool Alive, long TabletNum);

    private static List<Dictionary<string, string>> ParseVertical(string stdout)
    {
        var rows = new List<Dictionary<string, string>>();
        Dictionary<string, string>? cur = null;
        foreach (var line in stdout.Split('\n'))
        {
            if (line.Contains(". row ", StringComparison.Ordinal)) { cur = new Dictionary<string, string>(StringComparer.Ordinal); rows.Add(cur); continue; }
            var idx = line.IndexOf(':');
            if (cur is null || idx < 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (key.Length > 0) cur[key] = val;
        }
        return rows;
    }

    /// <summary>Read SHOW FRONTENDS from the first reachable, alive FE.</summary>
    private async Task<Result<(IReadOnlyList<FeRow> Rows, string SourceIp)>> ShowFrontendsAsync(IReadOnlyList<NodeRecord> fe, string pwd, CancellationToken ct, string? excludeIp = null)
    {
        foreach (var n in fe)
        {
            if (excludeIp is not null && n.Vmnet11 == excludeIp) continue;
            var r = await SqlAsync(n.Vmnet11, pwd, "SHOW FRONTENDS", ct, vertical: true).ConfigureAwait(false);
            if (r.IsFail) continue;
            var rows = ParseVertical(r.Value!)
                .Select(d => new FeRow(d.GetValueOrDefault("IP", ""), d.GetValueOrDefault("Role", ""),
                    string.Equals(d.GetValueOrDefault("Alive", "false"), "true", StringComparison.OrdinalIgnoreCase)))
                .Where(x => x.Ip.Length > 0).ToList();
            if (rows.Count > 0) return Result.Ok(((IReadOnlyList<FeRow>)rows, n.Vmnet11));
        }
        return Result.Fail<(IReadOnlyList<FeRow>, string)>("could not read SHOW FRONTENDS from any alive FE");
    }

    private async Task<Result<IReadOnlyList<BeRow>>> ShowBackendsAsync(IReadOnlyList<NodeRecord> fe, string pwd, CancellationToken ct)
    {
        foreach (var n in fe)
        {
            var r = await SqlAsync(n.Vmnet11, pwd, "SHOW BACKENDS", ct, vertical: true).ConfigureAwait(false);
            if (r.IsFail) continue;
            var rows = ParseVertical(r.Value!)
                .Select(d => new BeRow(d.GetValueOrDefault("IP", ""),
                    string.Equals(d.GetValueOrDefault("Alive", "false"), "true", StringComparison.OrdinalIgnoreCase),
                    long.TryParse(d.GetValueOrDefault("TabletNum", "0"), out var tn) ? tn : 0))
                .Where(x => x.Ip.Length > 0).ToList();
            return Result.Ok((IReadOnlyList<BeRow>)rows);
        }
        return Result.Fail<IReadOnlyList<BeRow>>("could not read SHOW BACKENDS from any alive FE");
    }

    private static bool IsLeader(FeRow r) => r.Role.Equals("LEADER", StringComparison.OrdinalIgnoreCase);

    // === GetStatusAsync ====================================================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<ClusterStatus>(split.Error!);
        var (fe, be) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ClusterStatus>(pwd.Error!);

        var feRes = await ShowFrontendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (feRes.IsFail) return Result.Fail<ClusterStatus>(feRes.Error!);
        var beRes = await ShowBackendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);

        var members = new List<ClusterMember>();
        string? leader = null;
        foreach (var row in feRes.Value.Rows)
        {
            var node = ByReportedIp(fe, row.Ip);
            var role = IsLeader(row) ? "leader" : "follower";
            if (role == "leader") leader = node?.Name ?? row.Ip;
            members.Add(new ClusterMember(node?.Name ?? row.Ip, node?.Vmnet11 ?? row.Ip, role, row.Alive ? "alive" : "failed"));
        }
        if (beRes.IsOk)
            foreach (var row in beRes.Value!)
            {
                var node = ByReportedIp(be, row.Ip);
                members.Add(new ClusterMember(node?.Name ?? row.Ip, node?.Vmnet11 ?? row.Ip, "backend", row.Alive ? "alive" : "failed"));
            }

        var feAlive = feRes.Value.Rows.Count(r => r.Alive);
        var beAlive = beRes.IsOk ? beRes.Value!.Count(r => r.Alive) : 0;
        var feQuorum = feAlive >= (fe.Count / 2 + 1);
        var overall = (leader is not null && feQuorum && beAlive == be.Count && feAlive == fe.Count) ? "green"
            : (leader is not null && feQuorum && beAlive >= (be.Count / 2 + 1)) ? "yellow" : "red";

        var s = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, leader, DateTimeOffset.UtcNow);
        _lastStatus = s;
        return Result.Ok(s);
    }

    // === HealthAsync =======================================================
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<HealthReport>(split.Error!);
        var (fe, be) = split.Value;
        var probes = new List<HealthProbe>();
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<HealthReport>(pwd.Error!);

        var feRes = await ShowFrontendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (feRes.IsFail) return Result.Fail<HealthReport>(feRes.Error!);
        var leaders = feRes.Value.Rows.Count(IsLeader);
        var feAlive = feRes.Value.Rows.Count(r => r.Alive);
        probes.Add(new HealthProbe("fe-quorum", "fe", leaders == 1 && feAlive >= (fe.Count / 2 + 1) ? "green" : "red",
            $"{feAlive}/{fe.Count} alive, {leaders} leader", $">={fe.Count / 2 + 1} + exactly 1 leader"));

        // operator-auth round-trip.
        var who = await SqlAsync(feRes.Value.SourceIp, pwd.Value!, "SELECT current_user()", cancellationToken, tuplesOnly: true).ConfigureAwait(false);
        var authed = who.IsOk && who.Value!.Contains(OperatorUser, StringComparison.Ordinal);
        probes.Add(new HealthProbe("operator-auth", OperatorUser, authed ? "green" : "red",
            who.IsOk ? who.Value!.Trim() : "unreachable", "authenticates as nexus-cluster-admin"));

        // BE liveness + tablet distribution.
        var beRes = await ShowBackendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (beRes.IsOk)
            foreach (var row in beRes.Value!)
            {
                var node = ByReportedIp(be, row.Ip);
                probes.Add(new HealthProbe("backend", node?.Name ?? row.Ip, row.Alive && row.TabletNum > 0 ? "green" : row.Alive ? "yellow" : "red",
                    $"{(row.Alive ? "alive" : "down")}, {row.TabletNum} tablets", "alive + tablets>0"));
            }

        // distributed-query round-trip.
        var cnt = await SqlAsync(feRes.Value.SourceIp, pwd.Value!, $"SELECT count(*) FROM {Db}.{Table}", cancellationToken, tuplesOnly: true).ConfigureAwait(false);
        var ok = cnt.IsOk && long.TryParse(cnt.Value!.Trim(), out var c) && c > 0;
        probes.Add(new HealthProbe("distributed-query", $"{Db}.{Table}", ok ? "green" : "red",
            cnt.IsOk ? $"{cnt.Value!.Trim()} rows" : "unreachable", ">0 rows"));

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync =====================================================
    /// <inheritdoc />
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var nodes = status.Value!.Members.Select(m => new TopologyNode(m.Hostname, m.Role, m.Status)).ToList();
        // StarRocks shards by tablet hash (BUCKETS n) across the BE, replication_num
        // per table — there are no fixed named shards (the BE tablet distribution is
        // the sharding, surfaced in status/health). Shards=null, like Patroni.
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, CapturedAtUtc: DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (FE leader re-election, RTO measured) ================
    /// <inheritdoc />
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<FailoverResult>(split.Error!);
        var (fe, _) = split.Value;
        if (fe.Count < 3) return Result.Fail<FailoverResult>("FE failover needs the 3-node BDB-JE quorum");
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<FailoverResult>(pwd.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var pre = await ShowFrontendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (pre.IsFail) return Result.Fail<FailoverResult>(pre.Error!);
        var preLeaderRow = pre.Value.Rows.FirstOrDefault(IsLeader);
        if (preLeaderRow is null) return Result.Fail<FailoverResult>("no current FE leader to fail over");
        var leaderNode = ByReportedIp(fe, preLeaderRow.Ip);
        if (leaderNode is null) return Result.Fail<FailoverResult>($"FE leader IP {preLeaderRow.Ip} not in vms.yaml");
        var aliveCount = pre.Value.Rows.Count(r => r.Alive);
        if (aliveCount < 3) return Result.Fail<FailoverResult>($"only {aliveCount}/3 FE alive; refusing to fail over a degraded quorum");
        var preFlightAt = sw.Elapsed;

        // Inject: stop the FE service on the current leader -> BDB-JE re-elects.
        var stop = await _ssh.ExecuteAsync(T(leaderNode.Vmnet11), $"sudo systemctl stop {FeSvc}.service && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<FailoverResult>($"failed to stop {FeSvc} on {leaderNode.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 200))}");
        var injectedAt = sw.Elapsed;

        // Poll the survivors until a DIFFERENT node reports LEADER.
        NodeRecord? newLeader = null;
        var newLeaderAt = TimeSpan.Zero;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            var cur = await ShowFrontendsAsync(fe, pwd.Value!, cancellationToken, excludeIp: leaderNode.Vmnet11).ConfigureAwait(false);
            if (cur.IsFail) continue;
            var l = cur.Value.Rows.FirstOrDefault(IsLeader);
            if (l is not null && l.Ip != preLeaderRow.Ip)
            {
                newLeader = ByReportedIp(fe, l.Ip);
                newLeaderAt = sw.Elapsed;
                break;
            }
        }
        var rto = newLeader is not null ? newLeaderAt - injectedAt : TimeSpan.Zero;

        // Recovery: restart the stopped FE -> rejoins as a follower.
        var recovery = "skipped";
        if (!request.NoRecover)
        {
            await _ssh.ExecuteAsync(T(leaderNode.Vmnet11), $"sudo systemctl start {FeSvc}.service 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);
            var rejoinDeadline = sw.Elapsed + TimeSpan.FromSeconds(90);
            var rejoined = false;
            while (sw.Elapsed < rejoinDeadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                var cur = await ShowFrontendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
                if (cur.IsOk && cur.Value.Rows.Any(r => r.Ip == preLeaderRow.Ip && r.Alive)) { rejoined = true; break; }
            }
            recovery = rejoined ? "recovered" : "failed";
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "fe-leader-failover",
            OriginalPrimary: leaderNode.Name,
            NewPrimary: newLeader?.Name,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: newLeader is null ? "no survivor FE became leader within the deadline; check nexus-starrocks-fe BDB-JE (:9010) on the survivors" : null,
            Timeline: new FailoverTimeline(preFlightAt, injectedAt, newLeaderAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOutAddAsync / RemoveAsync (BE join/leave) ====================
    /// <inheritdoc />
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<ScaleOutResult>(split.Error!);
        var (fe, be) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ScaleOutResult>(pwd.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        NodeRecord? candidate = null;
        foreach (var n in be)
            if (!await IsActiveAsync(n.Vmnet11, BeSvc, cancellationToken).ConfigureAwait(false)) { candidate = n; break; }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "all provisioned BE nodes are already joined. Provision a new BE first (apply-on-demand, ADR-0015): "
                + "add a sr-be-N + overlays in analytics-starrocks, `pwsh -File scripts/analytics-starrocks.ps1 apply`, then re-run `scale-out add`.");

        var start = await _ssh.ExecuteAsync(T(candidate.Vmnet11), $"sudo systemctl start {BeSvc}.service && echo STARTED", BackupTimeout, cancellationToken).ConfigureAwait(false);
        if (start.IsFail || !start.Value!.Stdout.Contains("STARTED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to start {BeSvc} on {candidate.Name}: {(start.IsFail ? start.Error : Tail(start.Value!.Stderr, 200))}");

        // Wait for the BE to report Alive=true in SHOW BACKENDS.
        var deadline = sw.Elapsed + JoinDeadline;
        var alive = false;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var beRes = await ShowBackendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
            if (beRes.IsOk && beRes.Value!.Any(r => (r.Ip == candidate.Vmnet10 || r.Ip == candidate.Vmnet11) && r.Alive)) { alive = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "add",
            AffectedNodes: [candidate.Name],
            Outcome: alive ? "ok" : "partial",
            OutcomeReason: alive ? $"{candidate.Name} rejoined the cluster (BE Alive=true; tablets re-replicate)" : $"{candidate.Name} started but not yet Alive in SHOW BACKENDS",
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
        var (_, be) = split.Value;
        var node = be.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not a sr-be* BE node in the starrocks cluster");

        // Refuse if removing it would drop the live BE count below the replication
        // factor floor (need >=2 live so tablets keep a surviving replica).
        var liveOthers = 0;
        foreach (var n in be.Where(n => !string.Equals(n.Name, node.Name, StringComparison.Ordinal)))
            if (await IsActiveAsync(n.Vmnet11, BeSvc, cancellationToken).ConfigureAwait(false)) liveOthers++;
        if (request.Drain && liveOthers < 2)
            return Result.Fail<ScaleOutResult>(
                $"removing {node.Name} would leave {liveOthers} live BE; tablets need >=2 surviving replicas. Bring another BE up first.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var stop = await _ssh.ExecuteAsync(T(node.Vmnet11), $"sudo systemctl stop {BeSvc}.service && echo STOPPED", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (stop.IsFail || !stop.Value!.Stdout.Contains("STOPPED", StringComparison.Ordinal))
            return Result.Fail<ScaleOutResult>($"failed to stop {BeSvc} on {node.Name}: {(stop.IsFail ? stop.Error : Tail(stop.Value!.Stderr, 200))}");
        sw.Stop();
        return Result.Ok(new ScaleOutResult(
            OperationType: "remove",
            AffectedNodes: [node.Name],
            Outcome: "ok",
            OutcomeReason: $"gracefully removed {node.Name} (BE service stopped; surviving replicas keep tablets served; ready for re-add via `scale-out add`)",
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    // === BackupTakeAsync / RestoreAsync (BACKUP/RESTORE SNAPSHOT) ===========
    private static readonly Regex StateRx = new(@"\bState:\s*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<BackupResult>(split.Error!);
        var (fe, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<BackupResult>(pwd.Error!);
        var feRes = await ShowFrontendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (feRes.IsFail) return Result.Fail<BackupResult>(feRes.Error!);
        var feIp = feRes.Value.SourceIp;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var label = string.IsNullOrWhiteSpace(request.Tag)
            ? $"sr_backup_{startedAt:yyyyMMdd_HHmmss}"
            : $"sr_{Sanitize(request.Tag)}_{startedAt:yyyyMMdd_HHmmss}";

        var bk = await SqlAsync(feIp, pwd.Value!, $"BACKUP SNAPSHOT {Db}.{label} TO {Repo} ON ({Table})", cancellationToken, timeout: BackupTimeout).ConfigureAwait(false);
        if (bk.IsFail) return Result.Fail<BackupResult>($"BACKUP SNAPSHOT failed: {bk.Error}");

        // BACKUP is async: poll SHOW BACKUP until State=FINISHED (or CANCELLED).
        var state = await PollJobAsync(feIp, pwd.Value!, $"SHOW BACKUP FROM {Db}", label, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (state != "FINISHED")
            return Result.Fail<BackupResult>($"BACKUP SNAPSHOT {label} ended in state '{state}' (expected FINISHED); check SHOW BACKUP FROM {Db}");

        return Result.Ok(new BackupResult(
            BackupId: label,
            Destination: $"REPOSITORY {Repo} (file:// NFS, ADR-0032; SNAPSHOT {Db}.{label} ON ({Table}))",
            SizeBytes: 0, // StarRocks SHOW BACKUP doesn't surface a byte size; the snapshot is in the repo.
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id (the snapshot label)");
        var split = Split();
        if (split.IsFail) return Result.Fail<RestoreResult>(split.Error!);
        var (fe, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<RestoreResult>(pwd.Error!);
        var feRes = await ShowFrontendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (feRes.IsFail) return Result.Fail<RestoreResult>(feRes.Error!);
        var feIp = feRes.Value.SourceIp;
        var label = request.BackupId;

        // Need the snapshot's backup_timestamp for RESTORE.
        var snap = await SqlAsync(feIp, pwd.Value!, $"SHOW SNAPSHOT ON {Repo} WHERE SNAPSHOT = \"{label}\"", cancellationToken, vertical: true).ConfigureAwait(false);
        if (snap.IsFail) return Result.Fail<RestoreResult>($"could not read snapshot {label}: {snap.Error}");
        var ts = ParseVertical(snap.Value!).Select(d => d.GetValueOrDefault("Timestamp", "")).FirstOrDefault(s => s.Length > 0);
        if (string.IsNullOrWhiteSpace(ts))
            return Result.Fail<RestoreResult>($"snapshot '{label}' not found in repository {Repo} (SHOW SNAPSHOT returned no Timestamp)");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        const string verifyTable = "events_restore_verify";
        await SqlAsync(feIp, pwd.Value!, $"DROP TABLE IF EXISTS {Db}.{verifyTable}", cancellationToken).ConfigureAwait(false);
        // Restore the snapshot's `events` AS a throwaway verify table (replication_num=1
        // so it doesn't demand a 3-BE floor for the temp copy).
        var rs = await SqlAsync(feIp, pwd.Value!,
            $"RESTORE SNAPSHOT {Db}.{label} FROM {Repo} ON ({Table} AS {verifyTable}) PROPERTIES (\"backup_timestamp\" = \"{ts}\", \"replication_num\" = \"1\")",
            cancellationToken, timeout: BackupTimeout).ConfigureAwait(false);
        if (rs.IsFail) { sw.Stop(); return Result.Fail<RestoreResult>($"RESTORE SNAPSHOT failed: {rs.Error}"); }

        var state = await PollJobAsync(feIp, pwd.Value!, $"SHOW RESTORE FROM {Db}", label, cancellationToken).ConfigureAwait(false);
        if (state != "FINISHED")
        {
            sw.Stop();
            return Result.Fail<RestoreResult>($"RESTORE SNAPSHOT {label} ended in state '{state}' (expected FINISHED); check SHOW RESTORE FROM {Db}");
        }
        var cnt = await SqlAsync(feIp, pwd.Value!, $"SELECT count(*) FROM {Db}.{verifyTable}", cancellationToken, tuplesOnly: true).ConfigureAwait(false);
        await SqlAsync(feIp, pwd.Value!, $"DROP TABLE IF EXISTS {Db}.{verifyTable}", cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (cnt.IsFail || !long.TryParse(cnt.Value!.Trim(), out var rows))
            return Result.Fail<RestoreResult>($"restore round-trip did not confirm rows: {(cnt.IsFail ? cnt.Error : Tail(cnt.Value!, 200))}");

        return Result.Ok(new RestoreResult(BackupId: label, ItemsRestored: rows, Duration: sw.Elapsed, StartedAtUtc: startedAt));
    }

    /// <summary>Poll a SHOW BACKUP/RESTORE job (most recent row for the label) until a terminal State.</summary>
    private async Task<string> PollJobAsync(string feIp, string pwd, string showSql, string label, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + BackupTimeout;
        var last = "UNKNOWN";
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var r = await SqlAsync(feIp, pwd, showSql, ct, vertical: true).ConfigureAwait(false);
            if (r.IsFail) continue;
            // Pick the row whose SnapshotName matches the label (most recent).
            var row = ParseVertical(r.Value!).LastOrDefault(d =>
                d.GetValueOrDefault("SnapshotName", "") == label || d.GetValueOrDefault("Label", "") == label);
            if (row is null) continue;
            last = row.GetValueOrDefault("State", "UNKNOWN");
            if (last is "FINISHED" or "CANCELLED") return last;
        }
        return last;
    }

    private static string Sanitize(string s) => Regex.Replace(s, "[^A-Za-z0-9_]", "_");

    // === RotateCertAsync (Vault re-issue per node, rolling restart) =========
    private sealed record CertRole(string TlsDir, string Svc, string Group);
    private static CertRole RoleDescriptor(NodeRecord n) =>
        IsFe(n) ? new CertRole(FeTlsDir, FeSvc, "starrocks") : new CertRole(BeTlsDir, BeSvc, "starrocks");

    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var split = Split();
        if (split.IsFail) return Result.Fail<CertRotationResult>(split.Error!);
        var (fe, be) = split.Value;
        // BE first, then FE followers, the FE leader LAST (its restart re-elects).
        var statusForOrder = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var leader = statusForOrder.IsOk ? statusForOrder.Value!.Leader : null;
        var feOrdered = fe.OrderBy(n => string.Equals(n.Name, leader, StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
        var all = be.Concat(feOrdered).ToList();

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var node in all)
        {
            var rd = RoleDescriptor(node);
            var oldSerialExec = await _ssh.ExecuteAsync(T(node.Vmnet11),
                $"sudo openssl x509 -in {rd.TlsDir}/server.crt -noout -serial 2>/dev/null | sed 's/serial=//'",
                SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldSerialExec.IsOk && oldSerialExec.Value!.ExitCode == 0 && oldSerialExec.Value.Stdout.Trim().Length > 0
                ? oldSerialExec.Value.Stdout.Trim() : "(unknown)";

            var cn = $"{node.Name}.starrocks.nexus.lab";
            var alts = $"{node.Name},{node.Name}.nexus.lab,{cn},starrocks-fe.nexus.lab,localhost";
            var ips = $"{node.Vmnet10},{node.Vmnet11},127.0.0.1";
            var issueCmd =
                "T=$(sudo cat /run/nexus-vault-agent/token 2>/dev/null); "
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

            // server.crt + PKCS#8 server.key + ca.crt (issuing intermediate + root
            // anchor), mirroring starrocks-tls-split.sh; then restart the unit.
            var writeCmd =
                $"echo {B64(cert.TrimEnd() + "\n")}|base64 -d|sudo tee {rd.TlsDir}/server.crt >/dev/null; "
                + $"echo {B64(key.TrimEnd() + "\n")}|base64 -d|sudo openssl pkcs8 -topk8 -nocrypt -out {rd.TlsDir}/server.key 2>/dev/null; "
                + $"echo {B64(ca.TrimEnd() + "\n")}|base64 -d|sudo tee /tmp/_sica.pem >/dev/null; "
                + $"sudo bash -c 'cat /tmp/_sica.pem /etc/vault-agent/ca-bundle.crt > {rd.TlsDir}/ca.crt'; sudo rm -f /tmp/_sica.pem; "
                + $"sudo chown root:{rd.Group} {rd.TlsDir}/server.crt {rd.TlsDir}/server.key {rd.TlsDir}/ca.crt 2>/dev/null; "
                + $"sudo chmod 0640 {rd.TlsDir}/server.crt {rd.TlsDir}/server.key {rd.TlsDir}/ca.crt; "
                + $"sudo systemctl restart {rd.Svc}; echo WROTE";
            var writeExec = await _ssh.ExecuteAsync(T(node.Vmnet11), writeCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (writeExec.IsFail || writeExec.Value!.ExitCode != 0 || !writeExec.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial, "(unchanged)",
                    Error: writeExec.IsFail ? writeExec.Error : $"writing new cert failed: {Tail(writeExec.Value!.Stdout, 200)}"));
                continue;
            }
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: null));
            // FE re-election + BE re-register take a moment; settle before the next.
            await Task.Delay(TimeSpan.FromSeconds(IsFe(node) ? 10 : 6), cancellationToken).ConfigureAwait(false);
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
        var (fe, _) = split.Value;
        var pwd = await OperatorPwdAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<AclSnapshot>(pwd.Error!);
        var feRes = await ShowFrontendsAsync(fe, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (feRes.IsFail) return Result.Fail<AclSnapshot>(feRes.Error!);
        var feIp = feRes.Value.SourceIp;
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            // SHOW USERS (StarRocks has no mysql.user); enrich with SHOW GRANTS per user.
            var u = await SqlAsync(feIp, pwd.Value!, "SHOW USERS", cancellationToken, tuplesOnly: true).ConfigureAwait(false);
            if (u.IsFail) return Result.Fail<AclSnapshot>(u.Error!);
            var names = u.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var users = new List<AclUser>();
            foreach (var name in names)
            {
                if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User) && !name.Contains(operation.User, StringComparison.OrdinalIgnoreCase)) continue;
                var g = await SqlAsync(feIp, pwd.Value!, $"SHOW GRANTS FOR {name}", cancellationToken, tuplesOnly: true).ConfigureAwait(false);
                // SHOW GRANTS rows are "UserIdentity<TAB>Catalog<TAB>Grants"; surface
                // just the Grants statement (the last tab field).
                var grants = g.IsOk
                    ? g.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => { var p = l.Split('\t'); return Truncate((p.Length > 0 ? p[^1] : l).Trim(), 70); })
                        .Where(s => s.Length > 0).Distinct().ToArray()
                    : ["(grants unavailable)"];
                users.Add(new AclUser(name, grants.Length > 0 ? grants : ["(no grants)"], Enabled: true));
            }
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user.");
            var privs = operation.Permissions is { Count: > 0 } ? operation.Permissions : DefaultGrantPrivs;
            var uid = $"'{operation.User.Replace("'", "")}'@'%'";
            if (verb == "grant")
            {
                var c = await SqlAsync(feIp, pwd.Value!, $"CREATE USER IF NOT EXISTS {uid}", cancellationToken).ConfigureAwait(false);
                if (c.IsFail) return Result.Fail<AclSnapshot>($"acl grant (create user) failed: {c.Error}");
                var g = await SqlAsync(feIp, pwd.Value!, $"GRANT {string.Join(", ", privs)} ON {Db}.* TO USER {uid}", cancellationToken).ConfigureAwait(false);
                if (g.IsFail) return Result.Fail<AclSnapshot>($"acl grant failed: {g.Error}");
            }
            else
            {
                var g = await SqlAsync(feIp, pwd.Value!, $"REVOKE {string.Join(", ", privs)} ON {Db}.* FROM USER {uid}", cancellationToken).ConfigureAwait(false);
                if (g.IsFail) return Result.Fail<AclSnapshot>($"acl revoke failed: {g.Error}");
            }
            return await AclAsync(new AclOperation("describe", operation.User), cancellationToken).ConfigureAwait(false);
        }
        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // === ApplyChaosAsync ===================================================
    /// <inheritdoc />
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");

        var split = Split();
        if (split.IsFail) return Result.Fail<ChaosOutcome>(split.Error!);
        var (_, be) = split.Value;

        // Default target: a BE (the surviving BE keep tablets served).
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? be.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : (be.Count > 0 ? be[^1] : null);
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target BE found");

        var target = T(victim.Vmnet11);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? BeSvc : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Name} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase))
            await _ssh.ExecuteAsync(target, $"sudo systemctl start {BeSvc}.service 2>/dev/null; exit 0", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(150);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var post = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (post.IsOk && post.Value!.OverallHealth == "green") { recovered = true; break; }
        }
        sw.Stop();

        return Result.Ok(new ChaosOutcome(
            ScenarioApplied: scenario.ScenarioType,
            Target: victim.Name,
            ObservedImpact: observed,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt,
            Recovered: recovered));
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
        return member.Role != "leader"; // refuse the current FE leader (resize → re-election)
    }

    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
