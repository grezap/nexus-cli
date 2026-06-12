using System.Diagnostics;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// SQL Server <b>Always On Availability Group</b> adapter for Phase 0.G.7
/// (nexus-cli v0.6.6). Implements <see cref="IClusterAdapter"/> for the AG +
/// Listener plane of the <c>sqlserver</c> cluster (ClusterId
/// <c>sqlserver-ag</c>; the companion <see cref="SqlFciAdapter"/> owns the WSFC
/// + FCI shared-storage plane under ClusterId <c>sqlserver</c>). ADR-0017.
/// <para>
/// Topology per vms.yaml (cluster <c>sqlserver</c>, AG <c>nexus-ag</c>): the FCI
/// virtual server <c>sqlfci</c> is the AG PRIMARY (SYNCHRONOUS_COMMIT); the two
/// standalone replicas sql-ag-rep-1/2 (@ .13/.14) are ASYNCHRONOUS_COMMIT
/// secondaries holding async copies of <c>nexus_demo</c>. The AG Listener
/// <c>sql-ag-listener</c> @ .17:1433 is the client front door (ADR-0025: the
/// Listener IS the LB-tier HA primitive); WSFC migrates the Listener IP across
/// AG failover, and the unified cert's .17 IP-SAN makes
/// Encrypt=True;TrustServerCertificate=False validate across the move.
/// </para>
/// <para>
/// Auth (decided from the live probe): the FCI + Listener path uses the
/// <c>nexus-cluster-admin</c> SQL login (mixed-mode FCI). The standalone replicas
/// are Windows-auth-only, so the few direct-replica ops (the AG FAILOVER issued
/// ON the target secondary) use Windows-auth <c>-E</c>. See
/// <see cref="SqlServerControl"/>.
/// </para>
/// </summary>
public sealed class SqlAgAdapter : IClusterAdapter
{
    private const string ClusterName = "sqlserver-ag";
    private const string DisplayNameConst = "SQL Server Always On AG (nexus-ag + Listener)";

    private static readonly TimeSpan SyncDeadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(120);
    private static readonly string[] KnownChaosScenarios = ["process-kill"];

    private readonly SqlServerControl _c;
    private ClusterStatus? _lastStatus;

    public SqlAgAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
        => _c = new SqlServerControl(catalog, ssh, sshUsername, sshKeyPath, vault);

    public string ClusterId => ClusterName;
    public string DisplayName => DisplayNameConst;

    // === replica-state read (from the primary via the FCI) =================
    private sealed record ReplicaState(string Server, string Role, string Mode, string Conn, string SyncState, string SyncHealth);

    private async Task<Result<List<ReplicaState>>> ReadReplicaStatesAsync(string fciIp, CancellationToken ct)
    {
        // sys.availability_replicas (config: mode) JOIN replica states (role/conn)
        // LEFT JOIN db replica states (sync). Pipe-delimited tuples.
        var sql =
            "SET NOCOUNT ON; SELECT ar.replica_server_name COLLATE DATABASE_DEFAULT + '|' + " +
            "ISNULL(rs.role_desc,'?') COLLATE DATABASE_DEFAULT + '|' + ar.availability_mode_desc COLLATE DATABASE_DEFAULT + '|' + " +
            "ISNULL(rs.connected_state_desc,'?') COLLATE DATABASE_DEFAULT + '|' + " +
            "ISNULL((SELECT TOP 1 drs.synchronization_state_desc FROM sys.dm_hadr_database_replica_states drs WHERE drs.replica_id=ar.replica_id AND DB_NAME(drs.database_id)='" + SqlServerControl.AgDb + "'),'?') COLLATE DATABASE_DEFAULT + '|' + " +
            "ISNULL(rs.synchronization_health_desc,'?') COLLATE DATABASE_DEFAULT " +
            "FROM sys.availability_replicas ar LEFT JOIN sys.dm_hadr_availability_replica_states rs ON ar.replica_id=rs.replica_id ORDER BY ar.replica_server_name";
        var r = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer, sql, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<List<ReplicaState>>(r.Error!);
        var list = SqlServerControl.PipeRows(r.Value!)
            .Where(p => p.Length >= 6)
            .Select(p => new ReplicaState(p[0].Trim(), p[1].Trim(), p[2].Trim(), p[3].Trim(), p[4].Trim(), p[5].Trim()))
            .ToList();
        return Result.Ok(list);
    }

    // map an AG replica_server_name (sqlfci / sql-ag-rep-1 / sql-ag-rep-2) to a node
    private NodeRecord? NodeForReplica(string replicaServer)
    {
        if (replicaServer.Equals(SqlServerControl.FciVirtualServer, StringComparison.OrdinalIgnoreCase)
            || replicaServer.Equals("SQLFCI", StringComparison.OrdinalIgnoreCase))
            return null; // the FCI primary isn't a single node
        return _c.NodeByName(replicaServer);
    }

    // === GetStatusAsync ====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<ClusterStatus>(split.Error!);
        var fciIp = split.Value.Fci[0].Vmnet11;

        var states = await ReadReplicaStatesAsync(fciIp, ct).ConfigureAwait(false);
        if (states.IsFail) return Result.Fail<ClusterStatus>($"could not read AG replica states from the FCI: {states.Error}");

        var members = new List<ClusterMember>();
        string? primary = null;
        foreach (var s in states.Value!)
        {
            var isPrimary = s.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase);
            if (isPrimary) primary = s.Server;
            var node = NodeForReplica(s.Server);
            var ip = node?.Vmnet11 ?? (s.Server.Equals("sqlfci", StringComparison.OrdinalIgnoreCase) ? "192.168.70.16" : s.Server);
            var status = isPrimary ? "primary"
                : (s.Conn.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase) && s.SyncHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase)) ? "syncing" : "degraded";
            members.Add(new ClusterMember(s.Server, ip, isPrimary ? "primary" : "secondary", status));
        }

        var primaryCount = states.Value.Count(s => s.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase));
        var allHealthy = states.Value.All(s => s.SyncHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase));
        var allConnected = states.Value.All(s => s.Conn.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase));
        var overall = (primaryCount == 1 && allHealthy && allConnected) ? "green"
            : (primaryCount == 1 && allHealthy) ? "yellow" : "red";

        var st = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, primary, DateTimeOffset.UtcNow);
        _lastStatus = st;
        return Result.Ok(st);
    }

    // === HealthAsync =======================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<HealthReport>(split.Error!);
        var fciIp = split.Value.Fci[0].Vmnet11;
        var probes = new List<HealthProbe>();

        var states = await ReadReplicaStatesAsync(fciIp, ct).ConfigureAwait(false);
        if (states.IsFail) return Result.Fail<HealthReport>(states.Error!);

        var primaryCount = states.Value!.Count(s => s.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase));
        probes.Add(new HealthProbe("ag-single-primary", SqlServerControl.AgName, primaryCount == 1 ? "green" : "red",
            $"{primaryCount} primary replica(s)", "exactly 1"));

        foreach (var s in states.Value!)
        {
            var ok = s.Conn.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase) && s.SyncHealth.Equals("HEALTHY", StringComparison.OrdinalIgnoreCase);
            probes.Add(new HealthProbe("replica", s.Server, ok ? "green" : s.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) ? "green" : "red",
                $"{s.Role} {s.Mode} {s.Conn} {s.SyncState}/{s.SyncHealth}", "CONNECTED + HEALTHY"));
        }

        // Listener answers under strict TLS (operator auth) + returns the AG primary.
        var lr = await _c.SqlAsync(split.Value.Rep[0].Vmnet11, SqlServerControl.ListenerFqdn,
            "SET NOCOUNT ON; SELECT 'PRIMARY=' + @@SERVERNAME", ct, enc: "-N").ConfigureAwait(false);
        var listenerOk = lr.IsOk && lr.Value!.Contains("PRIMARY=", StringComparison.OrdinalIgnoreCase);
        probes.Add(new HealthProbe("ag-listener", SqlServerControl.ListenerFqdn, listenerOk ? "green" : "red",
            lr.IsOk ? lr.Value!.Trim() : "strict-TLS connect failed", "answers (Encrypt+validate) as the AG primary"));

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync =====================================================
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var nodes = status.Value!.Members.Select(m => new TopologyNode(m.Hostname, m.Role, m.Status)).ToList();
        // The AG is replication (not sharding); the Listener is the front door —
        // model it as one "shard" mapping the listener to the current primary +
        // the secondary replicas.
        var primary = status.Value.Leader ?? "(unknown)";
        var secondaries = status.Value.Members.Where(m => m.Role == "secondary").Select(m => m.Hostname).ToList();
        var shards = new List<TopologyShard>
        {
            new(SqlServerControl.ListenerName, primary, secondaries, SlotRange: $"{SqlServerControl.ListenerFqdn}:1433 (.17)")
        };
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, shards, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (graceful AG failover: sync → ALTER FAILOVER → fail back) ===
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<FailoverResult>(split.Error!);
        var (fci, rep) = split.Value;
        var fciIp = fci[0].Vmnet11;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Current primary must be the FCI (sqlfci).
        var pre = await ReadReplicaStatesAsync(fciIp, ct).ConfigureAwait(false);
        if (pre.IsFail) return Result.Fail<FailoverResult>(pre.Error!);
        var primaryRow = pre.Value!.FirstOrDefault(s => s.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase));
        if (primaryRow is null) return Result.Fail<FailoverResult>("no AG primary found");
        if (!primaryRow.Server.Equals("sqlfci", StringComparison.OrdinalIgnoreCase))
            return Result.Fail<FailoverResult>($"AG primary is '{primaryRow.Server}', expected the FCI 'sqlfci'; this verb fails over FROM the FCI to a replica and back");

        // Pick a target secondary replica.
        var target = request.TargetNode is not null
            ? rep.FirstOrDefault(n => string.Equals(n.Name, request.TargetNode, StringComparison.OrdinalIgnoreCase))
            : (rep.Count > 0 ? rep[0] : null);
        if (target is null) return Result.Fail<FailoverResult>($"target replica '{request.TargetNode}' is not an AG replica (sql-ag-rep-1/2)");
        var preFlightAt = sw.Elapsed;

        // 1) Promote the target to SYNCHRONOUS_COMMIT (async can't planned-failover).
        var setSync = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer,
            $"ALTER AVAILABILITY GROUP [{SqlServerControl.AgName}] MODIFY REPLICA ON N'{target.Name}' WITH (AVAILABILITY_MODE = SYNCHRONOUS_COMMIT)", ct).ConfigureAwait(false);
        if (setSync.IsFail) return Result.Fail<FailoverResult>($"could not set {target.Name} to SYNCHRONOUS_COMMIT: {setSync.Error}");

        // 2) Wait until the target reports SYNCHRONIZED.
        var synced = false;
        var syncDeadline = sw.Elapsed + SyncDeadline;
        while (sw.Elapsed < syncDeadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            var cur = await ReadReplicaStatesAsync(fciIp, ct).ConfigureAwait(false);
            if (cur.IsOk && cur.Value!.Any(s => s.Server.Equals(target.Name, StringComparison.OrdinalIgnoreCase)
                && s.SyncState.Equals("SYNCHRONIZED", StringComparison.OrdinalIgnoreCase))) { synced = true; break; }
        }
        if (!synced)
        {
            await RevertAsyncMode(fciIp, target.Name, ct).ConfigureAwait(false);
            return Result.Fail<FailoverResult>($"{target.Name} did not reach SYNCHRONIZED within {SyncDeadline.TotalSeconds:N0}s; reverted to async, no failover performed");
        }
        var injectedAt = sw.Elapsed;

        // 3) Issue the FAILOVER on the TARGET secondary (Windows-auth -E; replicas
        //    are Windows-auth-only). The target becomes the new primary.
        var doFail = await _c.SqlAsync(target.Vmnet11, ".",
            $"ALTER AVAILABILITY GROUP [{SqlServerControl.AgName}] FAILOVER", ct, operatorAuth: false).ConfigureAwait(false);
        if (doFail.IsFail) return Result.Fail<FailoverResult>($"ALTER AVAILABILITY GROUP FAILOVER on {target.Name} failed: {doFail.Error}");

        // 4) Measure RTO: poll the target (via -E) until it reports PRIMARY.
        var newPrimaryAt = TimeSpan.Zero;
        var becamePrimary = false;
        var foDeadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < foDeadline)
        {
            await Task.Delay(SqlServerControl.PollInterval, ct).ConfigureAwait(false);
            var role = await _c.SqlAsync(target.Vmnet11, ".",
                "SET NOCOUNT ON; SELECT TOP 1 rs.role_desc FROM sys.dm_hadr_availability_replica_states rs JOIN sys.availability_replicas ar ON rs.replica_id=ar.replica_id WHERE ar.replica_server_name = @@SERVERNAME",
                ct, operatorAuth: false).ConfigureAwait(false);
            if (role.IsOk && role.Value!.Contains("PRIMARY", StringComparison.OrdinalIgnoreCase)) { newPrimaryAt = sw.Elapsed; becamePrimary = true; break; }
        }
        var rto = becamePrimary ? newPrimaryAt - injectedAt : TimeSpan.Zero;

        // 5) Recovery: fail back to the FCI + revert the target to async.
        var recovery = "skipped";
        if (!request.NoRecover && becamePrimary)
        {
            // Fail back: issue FAILOVER on the FCI (now a synchronized secondary; operator auth).
            // Wait until the FCI shows SYNCHRONIZED as secondary, then fail over.
            var backDeadline = sw.Elapsed + SyncDeadline;
            while (sw.Elapsed < backDeadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var role = await _c.SqlAsync(target.Vmnet11, ".",
                    "SET NOCOUNT ON; SELECT TOP 1 drs.synchronization_state_desc FROM sys.dm_hadr_database_replica_states drs JOIN sys.availability_replicas ar ON drs.replica_id=ar.replica_id WHERE ar.replica_server_name='sqlfci' AND DB_NAME(drs.database_id)='" + SqlServerControl.AgDb + "'",
                    ct, operatorAuth: false).ConfigureAwait(false);
                if (role.IsOk && role.Value!.Contains("SYNCHRONIZED", StringComparison.OrdinalIgnoreCase)) break;
            }
            var failBack = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer,
                $"ALTER AVAILABILITY GROUP [{SqlServerControl.AgName}] FAILOVER", ct).ConfigureAwait(false);
            // settle: FCI is primary again?
            var settled = false;
            var sdl = sw.Elapsed + TimeSpan.FromSeconds(60);
            while (sw.Elapsed < sdl)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var cur = await ReadReplicaStatesAsync(fciIp, ct).ConfigureAwait(false);
                if (cur.IsOk && cur.Value!.Any(s => s.Server.Equals("sqlfci", StringComparison.OrdinalIgnoreCase) && s.Role.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase))) { settled = true; break; }
            }
            await RevertAsyncMode(fciIp, target.Name, ct).ConfigureAwait(false);
            recovery = (failBack.IsOk && settled) ? "recovered" : "failed";
        }
        else if (becamePrimary)
        {
            // NoRecover: at least revert the target's commit mode marker is left as-is.
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "ag-alter-failover",
            OriginalPrimary: "sqlfci",
            NewPrimary: becamePrimary ? target.Name : null,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: becamePrimary ? null : $"{target.Name} did not become PRIMARY within the deadline; check sys.dm_hadr_availability_replica_states + the AG cluster group on WSFC",
            Timeline: new FailoverTimeline(preFlightAt, injectedAt, newPrimaryAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    private async Task RevertAsyncMode(string fciIp, string replica, CancellationToken ct) =>
        await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer,
            $"ALTER AVAILABILITY GROUP [{SqlServerControl.AgName}] MODIFY REPLICA ON N'{replica}' WITH (AVAILABILITY_MODE = ASYNCHRONOUS_COMMIT)", ct).ConfigureAwait(false);

    // === ScaleOut (remove/add an AG secondary replica) =====================
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a replica name (sql-ag-rep-1/2)");
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<ScaleOutResult>(split.Error!);
        var node = split.Value.Rep.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"'{request.NodeName}' is not an AG replica (sql-ag-rep-1/2)");
        var fciIp = split.Value.Fci[0].Vmnet11;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rm = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer,
            $"ALTER AVAILABILITY GROUP [{SqlServerControl.AgName}] REMOVE REPLICA ON N'{node.Name}'", ct).ConfigureAwait(false);
        sw.Stop();
        if (rm.IsFail) return Result.Fail<ScaleOutResult>($"REMOVE REPLICA {node.Name} failed: {rm.Error}");
        return Result.Ok(new ScaleOutResult("remove", [node.Name], "ok",
            $"removed {node.Name} from AG {SqlServerControl.AgName} (the FCI primary + the other secondary keep serving; re-add with `scale-out add sqlserver-ag --role replica`)",
            sw.Elapsed, startedAt));
    }

    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<ScaleOutResult>(split.Error!);
        var (fci, rep) = split.Value;
        var fciIp = fci[0].Vmnet11;

        // Find a provisioned replica that is NOT currently in the AG.
        var states = await ReadReplicaStatesAsync(fciIp, ct).ConfigureAwait(false);
        if (states.IsFail) return Result.Fail<ScaleOutResult>(states.Error!);
        var inAg = states.Value!.Select(s => s.Server).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = rep.FirstOrDefault(n => !inAg.Contains(n.Name));
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                "both AG replicas are already members. To grow beyond 2 replicas, provision a new sql-ag-rep-N VM (apply-on-demand IaC) first, then re-run.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Identify the ACTIVE FCI node — the manual-seed backup base lands on its
        // LOCAL C:\Windows\Temp (NOT the shared S:\), so we ferry from the right node.
        var grp = await _c.ClusterGroupAsync(fciIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
        var activeOwner = grp.IsOk ? grp.Value.Owner : fci[0].Name;
        var activeNode = fci.FirstOrDefault(n => string.Equals(n.Name, activeOwner, StringComparison.OrdinalIgnoreCase)) ?? fci[0];

        // Clean the candidate's STALE local AG state first (live-caught: a removed or
        // failed-seed secondary keeps a half-joined replica + an orphaned DB, so a
        // fresh JOIN/seed fails). On a secondary, DROP AVAILABILITY GROUP = leave;
        // then drop any orphaned nexus_demo so the manual restore can recreate it.
        var clean = await _c.SqlAsync(candidate.Vmnet11, ".",
            $"IF EXISTS(SELECT 1 FROM sys.availability_groups WHERE name='{SqlServerControl.AgName}') DROP AVAILABILITY GROUP [{SqlServerControl.AgName}]; " +
            $"IF DB_ID('{SqlServerControl.AgDb}') IS NOT NULL DROP DATABASE [{SqlServerControl.AgDb}]; SELECT 'CLEANED'",
            ct, operatorAuth: false, timeout: TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if (clean.IsFail) return Result.Fail<ScaleOutResult>($"could not clean stale AG state on {candidate.Name}: {clean.Error}");

        // Re-add the replica (ASYNC, manual failover) with MANUAL seeding. AUTOMATIC
        // seeding FAILS in this hybrid FCI+AG topology (live-caught 2026-06-12,
        // failure_state=Seeding): the FCI primary's nexus_demo data files live on the
        // shared iSCSI S:\, and automatic seeding tries to recreate S:\SQLData\*.mdf
        // on a standalone replica that has only local C:\ — there is no S:\ there.
        // Manual backup → RESTORE WITH MOVE NORECOVERY → SET HADR is path-agnostic
        // (mirrors role-overlay-ag-bootstrap §6 + the sealed 0.G.7 reality).
        var addSql =
            $"ALTER AVAILABILITY GROUP [{SqlServerControl.AgName}] ADD REPLICA ON N'{candidate.Name}' WITH " +
            $"(ENDPOINT_URL = N'TCP://{candidate.Name}.nexus.lab:5022', AVAILABILITY_MODE = ASYNCHRONOUS_COMMIT, FAILOVER_MODE = MANUAL, SEEDING_MODE = MANUAL, SECONDARY_ROLE(ALLOW_CONNECTIONS = NO))";
        var add = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer, addSql, ct, timeout: TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        if (add.IsFail) return Result.Fail<ScaleOutResult>($"ADD REPLICA {candidate.Name} failed: {add.Error}");

        // On the replica (-E): JOIN the AG (replica-level membership).
        var join = await _c.SqlAsync(candidate.Vmnet11, ".",
            $"ALTER AVAILABILITY GROUP [{SqlServerControl.AgName}] JOIN", ct, operatorAuth: false, timeout: TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        if (join.IsFail) return Result.Fail<ScaleOutResult>($"JOIN on {candidate.Name} failed: {join.Error}");

        // Take a fresh manual-seed base (full + log) on the FCI primary — lands on the
        // active node's LOCAL C:\Windows\Temp so it is SFTP-ferryable.
        var stamp = $"{startedAt:yyyyMMdd_HHmmss}";
        var bakName = $"nexus_demo_reseed_{stamp}.bak";
        var trnName = $"nexus_demo_reseed_{stamp}.trn";
        var bakRemote = $"C:\\Windows\\Temp\\{bakName}";
        var trnRemote = $"C:\\Windows\\Temp\\{trnName}";
        var backup = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer,
            $"BACKUP DATABASE [{SqlServerControl.AgDb}] TO DISK = N'{bakRemote}' WITH INIT, FORMAT, COMPRESSION; " +
            $"BACKUP LOG [{SqlServerControl.AgDb}] TO DISK = N'{trnRemote}' WITH INIT, FORMAT; SELECT 'BAKOK'",
            ct, timeout: TimeSpan.FromSeconds(180)).ConfigureAwait(false);
        if (backup.IsFail) return Result.Fail<ScaleOutResult>($"manual-seed backup failed: {backup.Error}");

        // Ferry the .bak/.trn from the active FCI node to the candidate via the build
        // host (SFTP down → up; the only path — S:\ has no peer path + a plain-SSH
        // session has no network identity for a peer admin share), then grant Everyone
        // read so the candidate's SQL service (NT AUTHORITY\NETWORK SERVICE) can read
        // them during RESTORE (Msg 15208 ACL gotcha; single-wildcard icacls arg).
        foreach (var name in new[] { bakName, trnName })
        {
            var sftpPath = $"/C:/Windows/Temp/{name}";
            var dl = await _c.DownloadAsync(activeNode.Vmnet11, sftpPath, ct, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            if (dl.IsFail) return Result.Fail<ScaleOutResult>($"download {name} from {activeNode.Name} failed: {dl.Error}");
            var ul = await _c.UploadAsync(candidate.Vmnet11, dl.Value!, sftpPath, ct, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            if (ul.IsFail) return Result.Fail<ScaleOutResult>($"upload {name} to {candidate.Name} failed: {ul.Error}");
        }
        await _c.WinPsAsync(candidate.Vmnet11,
            $"$ErrorActionPreference='Continue'; icacls 'C:\\Windows\\Temp\\nexus_demo_reseed_{stamp}.*' /grant '*S-1-1-0:(R)' /Q | Out-Null; Write-Output 'GRANTED'",
            ct).ConfigureAwait(false);

        // On the candidate (-E): RESTORE the DB + LOG WITH MOVE (to its own local
        // default data/log dir) NORECOVERY, then SET HADR to bind the DB to the AG.
        var restore =
            "SET NOCOUNT ON; " +
            "DECLARE @dataDir nvarchar(512) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(512)); " +
            "DECLARE @logDir  nvarchar(512) = CAST(SERVERPROPERTY('InstanceDefaultLogPath')  AS nvarchar(512)); " +
            "DECLARE @r nvarchar(max) = N'RESTORE DATABASE [" + SqlServerControl.AgDb + "] FROM DISK = ''" + bakRemote + "'' WITH MOVE ''" + SqlServerControl.AgDb + "'' TO ''' + @dataDir + N'" + SqlServerControl.AgDb + ".mdf'', MOVE ''" + SqlServerControl.AgDb + "_log'' TO ''' + @logDir + N'" + SqlServerControl.AgDb + "_log.ldf'', NORECOVERY, REPLACE;'; " +
            "EXEC sp_executesql @r; " +
            "RESTORE LOG [" + SqlServerControl.AgDb + "] FROM DISK = N'" + trnRemote + "' WITH NORECOVERY; " +
            "ALTER DATABASE [" + SqlServerControl.AgDb + "] SET HADR AVAILABILITY GROUP = [" + SqlServerControl.AgName + "]; SELECT 'SEEDED'";
        var rst = await _c.SqlAsync(candidate.Vmnet11, ".", restore, ct, operatorAuth: false, timeout: TimeSpan.FromSeconds(180)).ConfigureAwait(false);
        if (rst.IsFail) return Result.Fail<ScaleOutResult>($"manual seed (restore + SET HADR) on {candidate.Name} failed: {rst.Error}");

        // Best-effort cleanup of the transferred seed files on both nodes.
        await _c.WinPsAsync(activeNode.Vmnet11, $"Remove-Item 'C:\\Windows\\Temp\\nexus_demo_reseed_{stamp}.*' -EA SilentlyContinue; Write-Output 'OK'", ct).ConfigureAwait(false);
        await _c.WinPsAsync(candidate.Vmnet11, $"Remove-Item 'C:\\Windows\\Temp\\nexus_demo_reseed_{stamp}.*' -EA SilentlyContinue; Write-Output 'OK'", ct).ConfigureAwait(false);

        // Wait until the replica reconnects + the DB resyncs.
        var rejoined = false;
        var deadline = sw.Elapsed + TimeSpan.FromMinutes(4);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            var cur = await ReadReplicaStatesAsync(fciIp, ct).ConfigureAwait(false);
            if (cur.IsOk && cur.Value!.Any(s => s.Server.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)
                && s.Conn.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase)
                && (s.SyncState.Equals("SYNCHRONIZING", StringComparison.OrdinalIgnoreCase) || s.SyncState.Equals("SYNCHRONIZED", StringComparison.OrdinalIgnoreCase))))
            { rejoined = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ScaleOutResult("add", [candidate.Name], rejoined ? "ok" : "partial",
            rejoined ? $"{candidate.Name} re-added to AG {SqlServerControl.AgName} via MANUAL seeding (backup→restore WITH MOVE NORECOVERY→SET HADR); CONNECTED + SYNCHRONIZING" : $"{candidate.Name} added + joined + restored but not yet CONNECTED/SYNCHRONIZING — check the HADR endpoint (TCP 5022) + sys.dm_hadr_database_replica_states",
            sw.Elapsed, startedAt));
    }

    // === Backup (BACKUP/RESTORE DATABASE nexus_demo round-trip via the AG primary) ===
    private const string BackupDir = "S:\\Backups";

    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<BackupResult>(split.Error!);
        var fciIp = split.Value.Fci[0].Vmnet11;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var label = string.IsNullOrWhiteSpace(request.Tag)
            ? $"ag_nexus_demo_{startedAt:yyyyMMdd_HHmmss}"
            : $"ag_{Sanitize(request.Tag)}_{startedAt:yyyyMMdd_HHmmss}";
        var bak = $"{BackupDir}\\{label}.bak";
        var sql =
            $"SET NOCOUNT ON; EXEC xp_create_subdir N'{BackupDir}'; " +
            $"BACKUP DATABASE [{SqlServerControl.AgDb}] TO DISK = N'{bak}' WITH COPY_ONLY, INIT, COMPRESSION, FORMAT; " +
            $"SELECT 'SIZE=' + CONVERT(varchar(30),(SELECT CAST(backup_size AS bigint) FROM msdb.dbo.backupset WHERE backup_set_id=(SELECT MAX(backup_set_id) FROM msdb.dbo.backupset WHERE database_name='{SqlServerControl.AgDb}')))";
        var r = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer, sql, ct, timeout: TimeSpan.FromSeconds(240)).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<BackupResult>($"BACKUP DATABASE {SqlServerControl.AgDb} failed: {r.Error}");
        long size = 0;
        var m = System.Text.RegularExpressions.Regex.Match(r.Value!, @"SIZE=(\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var sz)) size = sz;
        return Result.Ok(new BackupResult(label + ".bak",
            $"{bak} (BACKUP DATABASE {SqlServerControl.AgDb} WITH COPY_ONLY via the AG primary; honors backup-preference=secondary intent)",
            size, sw.Elapsed, startedAt));
    }

    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id (the .bak filename from `backup take`)");
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<RestoreResult>(split.Error!);
        var fciIp = split.Value.Fci[0].Vmnet11;
        var bak = request.BackupId.Contains('\\') ? request.BackupId : $"{BackupDir}\\{request.BackupId}";
        const string verifyDb = "nexus_demo_ag_restore_verify";

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var sql =
            "SET NOCOUNT ON; IF DB_ID(N'" + verifyDb + "') IS NOT NULL BEGIN ALTER DATABASE [" + verifyDb + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + verifyDb + "]; END " +
            "DECLARE @data sysname, @log sysname; " +
            "DECLARE @fl TABLE (LogicalName sysname, PhysicalName nvarchar(260), Type char(1), FileGroupName sysname NULL, Size numeric(20,0), MaxSize numeric(20,0), FileID bigint, CreateLSN numeric(25,0), DropLSN numeric(25,0) NULL, UniqueID uniqueidentifier, ReadOnlyLSN numeric(25,0) NULL, ReadWriteLSN numeric(25,0) NULL, BackupSizeInBytes bigint, SourceBlockSize int, FileGroupID int, LogGroupGUID uniqueidentifier NULL, DifferentialBaseLSN numeric(25,0) NULL, DifferentialBaseGUID uniqueidentifier NULL, IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32) NULL, SnapshotURL nvarchar(360) NULL); " +
            $"INSERT INTO @fl EXEC ('RESTORE FILELISTONLY FROM DISK = N''{bak}'''); " +
            "SELECT @data = LogicalName FROM @fl WHERE Type='D'; SELECT @log = LogicalName FROM @fl WHERE Type='L'; " +
            "DECLARE @cmd nvarchar(max) = 'RESTORE DATABASE [" + verifyDb + "] FROM DISK = N''" + bak + "'' WITH MOVE '''+@data+''' TO N''" + BackupDir + "\\" + verifyDb + ".mdf'', MOVE '''+@log+''' TO N''" + BackupDir + "\\" + verifyDb + ".ldf'', REPLACE, RECOVERY'; " +
            "EXEC (@cmd); " +
            "DECLARE @rows bigint = (SELECT ISNULL(SUM(p.rows),0) FROM [" + verifyDb + "].sys.partitions p WHERE p.index_id IN (0,1)); " +
            "ALTER DATABASE [" + verifyDb + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + verifyDb + "]; " +
            "SELECT 'ROWS=' + CONVERT(varchar(30), @rows)";
        var r = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer, sql, ct, timeout: TimeSpan.FromSeconds(240)).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<RestoreResult>($"RESTORE round-trip failed: {r.Error}");
        var m = System.Text.RegularExpressions.Regex.Match(r.Value!, @"ROWS=(\d+)");
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out var rows))
            return Result.Fail<RestoreResult>($"restore did not confirm a row count: {SqlServerControl.Tail(r.Value!, 200)}");
        return Result.Ok(new RestoreResult(request.BackupId, rows, sw.Elapsed, startedAt));
    }

    private static string Sanitize(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9_]", "_");

    // === RotateCertAsync (per-node rotate of the 2 standalone AG replicas) ==
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<CertRotationResult>(split.Error!);
        var (_, rep) = split.Value;
        if (_c.Vault is null) return Result.Fail<CertRotationResult>("cert-rotate issues certs via Vault PKI; set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();
        // The replicas are STANDALONE instances → per-node cert + per-node registry.
        // The FCI's shared cert is rotated by `cert-rotate sqlserver` (the FCI adapter).
        foreach (var node in rep)
        {
            var rr = await SqlServerCert.RotateStandaloneAsync(_c, node, fciSans: false, ct).ConfigureAwait(false);
            rotated.Add(rr);
            if (rr.Error is null)
            {
                // restart the standalone SQL service so it picks up the new cert.
                await _c.WinPsAsync(node.Vmnet11,
                    "$ErrorActionPreference='Continue'; Restart-Service MSSQLSERVER -Force; Start-Sleep -Seconds 3; Write-Output 'RESTARTED'",
                    ct, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
            }
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === AclAsync (AG-relevant server logins, via the primary) =============
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<AclSnapshot>(split.Error!);
        var fciIp = split.Value.Fci[0].Vmnet11;
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var filter = verb == "describe" && !string.IsNullOrWhiteSpace(operation.User)
                ? $"AND sp.name = '{operation.User.Replace("'", "''")}' " : "";
            var sql =
                "SET NOCOUNT ON; SELECT sp.name COLLATE DATABASE_DEFAULT + '|' + sp.type_desc COLLATE DATABASE_DEFAULT + '|' + CONVERT(varchar(2),sp.is_disabled) + '|' + " +
                "ISNULL(STUFF((SELECT ',' + r.name COLLATE DATABASE_DEFAULT FROM sys.server_role_members m JOIN sys.server_principals r ON m.role_principal_id=r.principal_id WHERE m.member_principal_id=sp.principal_id FOR XML PATH('')),1,1,''),'') " +
                $"FROM sys.server_principals sp WHERE sp.type IN ('S','U','G') AND sp.name NOT LIKE '##%' {filter}ORDER BY sp.name";
            var r = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer, sql, ct).ConfigureAwait(false);
            if (r.IsFail) return Result.Fail<AclSnapshot>(r.Error!);
            var users = new List<AclUser>();
            foreach (var row in SqlServerControl.PipeRows(r.Value!))
            {
                var name = row[0].Trim();
                var typ = row.Length > 1 ? row[1].Trim() : "";
                var disabled = row.Length > 2 && row[2].Trim() == "1";
                var roles = row.Length > 3 && row[3].Trim().Length > 0
                    ? row[3].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
                var perms = new List<string> { typ };
                perms.AddRange(roles.Select(x => "role:" + x));
                users.Add(new AclUser(name, perms, Enabled: !disabled));
            }
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user.");
            var roles = operation.Permissions is { Count: > 0 } ? operation.Permissions : new[] { "dbcreator" };
            var u = operation.User.Replace("'", "''").Replace("]", "]]");
            var sb = new System.Text.StringBuilder("SET NOCOUNT ON; ");
            if (verb == "grant")
                sb.Append("IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name='" + u + "') CREATE LOGIN [" + u + "] WITH PASSWORD='Chg" + Guid.NewGuid().ToString("N") + "Aa9!', CHECK_POLICY=OFF; ");
            foreach (var role in roles)
            {
                var rl = role.Replace("'", "''").Replace("]", "]]");
                sb.Append(verb == "grant" ? "ALTER SERVER ROLE [" + rl + "] ADD MEMBER [" + u + "]; " : "ALTER SERVER ROLE [" + rl + "] DROP MEMBER [" + u + "]; ");
            }
            sb.Append("SELECT 'ACLOK'");
            var r = await _c.SqlAsync(fciIp, SqlServerControl.FciVirtualServer, sb.ToString(), ct).ConfigureAwait(false);
            if (r.IsFail) return Result.Fail<AclSnapshot>($"acl {verb} failed: {r.Error}");
            return await AclAsync(new AclOperation("describe", operation.User), ct).ConfigureAwait(false);
        }
        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    // === ApplyChaosAsync (process-kill SQL on a secondary replica → resync) ==
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<ChaosOutcome>(split.Error!);
        var (fci, rep) = split.Value;
        if (rep.Count == 0) return Result.Fail<ChaosOutcome>("no AG replica to target");
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? rep.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : rep[^1];
        if (victim is null) return Result.Fail<ChaosOutcome>($"chaos target '{scenario.Target}' is not an AG replica");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Inject: kill SQL on the secondary → its AG replica disconnects.
        var kill = await _c.WinPsAsync(victim.Vmnet11,
            "$ErrorActionPreference='Continue'; Stop-Process -Name sqlservr -Force -EA SilentlyContinue; Write-Output 'KILLED'", ct).ConfigureAwait(false);
        if (kill.IsFail) return Result.Fail<ChaosOutcome>($"failed to kill sqlservr on {victim.Name}: {kill.Error}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(scenario.DurationSeconds <= 0 ? 15 : scenario.DurationSeconds, 25)), ct).ConfigureAwait(false);
        var impact = await HealthAsync(ct).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        // Recover: SQL service auto-restarts (SCM recovery) → AG replica reconnects.
        await _c.WinPsAsync(victim.Vmnet11,
            "$ErrorActionPreference='Continue'; Start-Service MSSQLSERVER -EA SilentlyContinue; Write-Output 'STARTED'", ct).ConfigureAwait(false);
        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(150);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var st = await GetStatusAsync(ct).ConfigureAwait(false);
            if (st.IsOk && st.Value!.OverallHealth == "green") { recovered = true; break; }
        }
        sw.Stop();
        return Result.Ok(new ChaosOutcome(scenario.ScenarioType, victim.Name, observed, sw.Elapsed, startedAt, recovered));
    }

    // === CanResizeVm =======================================================
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false;
        var member = _lastStatus.Members.FirstOrDefault(m => string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        return member.Role != "primary";
    }
}
