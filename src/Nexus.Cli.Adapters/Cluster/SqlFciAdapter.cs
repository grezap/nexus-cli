using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// SQL Server <b>Failover Cluster Instance</b> adapter for Phase 0.G.7
/// (nexus-cli v0.6.6). Implements <see cref="IClusterAdapter"/> for the
/// WSFC + shared-storage plane of the <c>sqlserver</c> cluster (ClusterId
/// <c>sqlserver</c>; the companion <see cref="SqlAgAdapter"/> owns the Always On
/// AG plane under ClusterId <c>sqlserver-ag</c>). ADR-0016.
/// <para>
/// Topology per vms.yaml (cluster <c>sqlserver</c>): sql-fci-1/2 (@ .11/.12) form
/// a 2-node WSFC Failover Cluster Instance sharing one iSCSI LUN from
/// nexus-gateway (clustered Physical Disk at S:\). The FCI virtual server
/// <c>sqlfci</c> @ .16 is the client SQL endpoint; the WSFC CNO is
/// <c>sql-fci-cluster</c> @ .15; quorum (NodeMajority) spans all 4 nodes
/// (the 2 FCI + the 2 AG replicas). SQL service identity =
/// <c>nexus.lab\gmsa-sql-engine$</c>.
/// </para>
/// <para>
/// All cluster-resource ops (Get-Cluster*, Move-ClusterGroup) run over plain
/// Windows-SSH as the local nexusadmin; all T-SQL runs as the
/// <c>nexus-cluster-admin</c> SQL login against the FCI virtual server (the
/// LOCKED Vault-KV operator-credential model). See <see cref="SqlServerControl"/>.
/// </para>
/// </summary>
public sealed class SqlFciAdapter : IClusterAdapter
{
    private const string ClusterName = "sqlserver";
    private const string DisplayNameConst = "SQL Server FCI (WSFC failover cluster instance)";

    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(240);
    private static readonly string[] KnownChaosScenarios = ["process-kill", "node-kill"];

    private readonly SqlServerControl _c;
    private ClusterStatus? _lastStatus;

    public SqlFciAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
        => _c = new SqlServerControl(catalog, ssh, sshUsername, sshKeyPath, vault);

    public string ClusterId => ClusterName;
    public string DisplayName => DisplayNameConst;

    // === GetStatusAsync ====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<ClusterStatus>(split.Error!);
        var (fci, _) = split.Value;
        var probeIp = fci[0].Vmnet11;

        var groupRes = await _c.ClusterGroupAsync(probeIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
        if (groupRes.IsFail) return Result.Fail<ClusterStatus>($"WSFC unreachable from {fci[0].Name}: {groupRes.Error}");
        var (groupState, owner) = groupRes.Value;

        var nodesRes = await _c.ClusterNodesAsync(probeIp, ct).ConfigureAwait(false);
        var nodeStates = nodesRes.IsOk ? nodesRes.Value! : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // FCI virtual server answers (operator auth round-trip)?
        var who = await _c.SqlAsync(probeIp, SqlServerControl.FciVirtualServer,
            "SET NOCOUNT ON; SELECT @@SERVERNAME", ct).ConfigureAwait(false);
        var fciAnswers = who.IsOk && who.Value!.Trim().Length > 0;

        var members = new List<ClusterMember>();
        foreach (var n in fci)
        {
            var st = nodeStates.TryGetValue(n.Name, out var s) ? s : "Unknown";
            var isOwner = string.Equals(n.Name, owner, StringComparison.OrdinalIgnoreCase);
            var role = isOwner ? "fci-active" : "fci-passive";
            var status = !st.Equals("Up", StringComparison.OrdinalIgnoreCase) ? st.ToLowerInvariant()
                : isOwner ? (fciAnswers ? "online" : "owner-not-answering") : "standby";
            members.Add(new ClusterMember(n.Name, n.Vmnet11, role, status));
        }

        var ownerIsFci = fci.Any(n => string.Equals(n.Name, owner, StringComparison.OrdinalIgnoreCase));
        var bothUp = fci.All(n => nodeStates.TryGetValue(n.Name, out var s) && s.Equals("Up", StringComparison.OrdinalIgnoreCase));
        var online = groupState.Equals("Online", StringComparison.OrdinalIgnoreCase);
        var overall = (online && ownerIsFci && fciAnswers && bothUp) ? "green"
            : (online && ownerIsFci && fciAnswers) ? "yellow" : "red";

        var s2 = new ClusterStatus(ClusterName, DisplayNameConst, overall, members,
            ownerIsFci ? owner : null, DateTimeOffset.UtcNow);
        _lastStatus = s2;
        return Result.Ok(s2);
    }

    // === HealthAsync =======================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<HealthReport>(split.Error!);
        var (fci, _) = split.Value;
        var probeIp = fci[0].Vmnet11;
        var probes = new List<HealthProbe>();

        // 1) WSFC quorum + node majority.
        var quorum = await _c.WinPsAsync(probeIp,
            "$ErrorActionPreference='SilentlyContinue';" +
            "$q=[string]((Get-ClusterQuorum).QuorumType); $up=@(Get-ClusterNode | Where-Object {$_.State -eq 'Up'}).Count; $tot=@(Get-ClusterNode).Count;" +
            "Write-Output \"$q|$up|$tot\"", ct).ConfigureAwait(false);
        if (quorum.IsOk)
        {
            var p = quorum.Value!.Split('|');
            var up = p.Length > 1 && int.TryParse(p[1], out var u) ? u : 0;
            var tot = p.Length > 2 && int.TryParse(p[2], out var t) ? t : 0;
            var quorumOk = up >= (tot / 2 + 1);
            probes.Add(new HealthProbe("wsfc-quorum", SqlServerControl.WsfcCluster, quorumOk ? "green" : "red",
                $"{up}/{tot} nodes Up, quorum={p[0]}", $">={tot / 2 + 1} (NodeMajority)"));
        }
        else probes.Add(new HealthProbe("wsfc-quorum", SqlServerControl.WsfcCluster, "red", "Get-ClusterQuorum failed", "reachable"));

        // 2) SQL Server cluster role Online on an FCI node.
        var group = await _c.ClusterGroupAsync(probeIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
        var ownerIsFci = group.IsOk && fci.Any(n => string.Equals(n.Name, group.Value.Owner, StringComparison.OrdinalIgnoreCase));
        probes.Add(new HealthProbe("fci-role-online", SqlServerControl.SqlServerGroup,
            group.IsOk && group.Value.State.Equals("Online", StringComparison.OrdinalIgnoreCase) && ownerIsFci ? "green" : "red",
            group.IsOk ? $"{group.Value.State} on {group.Value.Owner}" : "unreachable", "Online on an FCI node"));

        // 3) Clustered Physical Disk Online.
        var disk = await _c.WinPsAsync(probeIp,
            "$ErrorActionPreference='SilentlyContinue';" +
            "Write-Output (@(Get-ClusterResource | Where-Object {$_.ResourceType -eq 'Physical Disk' -and $_.State -eq 'Online'}).Count)", ct).ConfigureAwait(false);
        var diskOk = disk.IsOk && int.TryParse(disk.Value!.Trim(), out var dc) && dc >= 1;
        probes.Add(new HealthProbe("shared-disk", "iSCSI LUN (S:)", diskOk ? "green" : "red",
            disk.IsOk ? $"{disk.Value!.Trim()} Physical Disk online" : "unreachable", ">=1 clustered Physical Disk Online"));

        // 4) iSCSI session on each FCI node (so failover has shared storage on both).
        foreach (var n in fci)
        {
            var sess = await _c.WinPsAsync(n.Vmnet11,
                "$ErrorActionPreference='SilentlyContinue';" +
                "Write-Output (@(Get-IscsiSession | Where-Object {$_.TargetNodeAddress -match 'sql-fci'}).Count)", ct).ConfigureAwait(false);
            var ok = sess.IsOk && int.TryParse(sess.Value!.Trim(), out var sc) && sc >= 1;
            probes.Add(new HealthProbe("iscsi-session", n.Name, ok ? "green" : "red",
                sess.IsOk ? $"{sess.Value!.Trim()} session(s)" : "unreachable", ">=1 active iSCSI session"));
        }

        // 5) FCI virtual server answers + identity (operator auth round-trip).
        var who = await _c.SqlAsync(probeIp, SqlServerControl.FciVirtualServer,
            "SET NOCOUNT ON; SELECT @@SERVERNAME + '|' + SUSER_SNAME()", ct).ConfigureAwait(false);
        var answers = who.IsOk && who.Value!.Contains(SqlServerControl.OperatorUser, StringComparison.OrdinalIgnoreCase);
        probes.Add(new HealthProbe("fci-virtual-server", SqlServerControl.FciVirtualServer, answers ? "green" : "red",
            who.IsOk ? who.Value!.Trim() : "unreachable", "answers as nexus-cluster-admin"));

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync =====================================================
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<TopologySnapshot>(split.Error!);
        var (fci, rep) = split.Value;
        var probeIp = fci[0].Vmnet11;

        var nodesRes = await _c.ClusterNodesAsync(probeIp, ct).ConfigureAwait(false);
        var nodeStates = nodesRes.IsOk ? nodesRes.Value! : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var group = await _c.ClusterGroupAsync(probeIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
        var owner = group.IsOk ? group.Value.Owner : null;

        var nodes = new List<TopologyNode>();
        foreach (var n in fci)
        {
            var st = nodeStates.TryGetValue(n.Name, out var s) ? s : "Unknown";
            var role = string.Equals(n.Name, owner, StringComparison.OrdinalIgnoreCase) ? "fci-active" : "fci-passive";
            nodes.Add(new TopologyNode(n.Name, role, st));
        }
        // The AG replicas are WSFC members too (they vote in quorum) but are not
        // FCI nodes — surface them as wsfc-member so the topology shows the full
        // 4-node WSFC the FCI lives inside.
        foreach (var n in rep)
        {
            var st = nodeStates.TryGetValue(n.Name, out var s) ? s : "Unknown";
            nodes.Add(new TopologyNode(n.Name, "wsfc-member", st));
        }
        // The FCI is single-instance shared-storage — the clustered Physical Disk
        // is the "shard": one disk, owned by the active node.
        var shards = new List<TopologyShard>
        {
            new("fci-shared-disk", owner ?? "(unknown)", Array.Empty<string>(), SlotRange: "S:\\ (iSCSI LUN)")
        };
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, shards, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (Move-ClusterGroup between FCI nodes) ================
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<FailoverResult>(split.Error!);
        var (fci, _) = split.Value;
        if (fci.Count < 2) return Result.Fail<FailoverResult>("FCI failover needs both sql-fci-1/2");
        var probeIp = fci[0].Vmnet11;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var pre = await _c.ClusterGroupAsync(probeIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
        if (pre.IsFail) return Result.Fail<FailoverResult>(pre.Error!);
        var currentOwner = fci.FirstOrDefault(n => string.Equals(n.Name, pre.Value.Owner, StringComparison.OrdinalIgnoreCase));
        if (currentOwner is null) return Result.Fail<FailoverResult>($"SQL Server group owner '{pre.Value.Owner}' is not an FCI node");
        var target = request.TargetNode is not null
            ? fci.FirstOrDefault(n => string.Equals(n.Name, request.TargetNode, StringComparison.OrdinalIgnoreCase))
            : fci.First(n => !string.Equals(n.Name, currentOwner.Name, StringComparison.Ordinal));
        if (target is null) return Result.Fail<FailoverResult>($"target node '{request.TargetNode}' is not an FCI node");
        if (string.Equals(target.Name, currentOwner.Name, StringComparison.Ordinal))
            return Result.Fail<FailoverResult>($"{target.Name} already owns the FCI; nothing to fail over");

        // Pre-flight: target node must be Up so it can host the instance.
        if (!await _c.NodeStateUpAsync(probeIp, target.Name, ct).ConfigureAwait(false))
            return Result.Fail<FailoverResult>($"target {target.Name} is not Up in WSFC; refusing to fail over");
        var preFlightAt = sw.Elapsed;

        // Inject: Move-ClusterGroup to the target node (blocks until moved or fails).
        var move = await _c.WinPsAsync(probeIp,
            "$ErrorActionPreference='Stop';" +
            $"try {{ Move-ClusterGroup -Name '{SqlServerControl.SqlServerGroup.Replace("'", "''")}' -Node {target.Name} -Wait 120 | Out-Null; Write-Output 'MOVED' }}" +
            "catch { Write-Output ('MOVEERR:' + $_.Exception.Message) }", ct, TimeSpan.FromSeconds(150)).ConfigureAwait(false);
        if (move.IsFail) return Result.Fail<FailoverResult>($"Move-ClusterGroup failed: {move.Error}");
        if (!move.Value!.Contains("MOVED", StringComparison.Ordinal))
            return Result.Fail<FailoverResult>($"Move-ClusterGroup did not complete: {move.Value}");
        var injectedAt = sw.Elapsed;

        // Poll until the FCI virtual server answers again on the new owner.
        var newOwnerAt = TimeSpan.Zero;
        var answered = false;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(SqlServerControl.PollInterval, ct).ConfigureAwait(false);
            var grp = await _c.ClusterGroupAsync(probeIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
            if (grp.IsOk && grp.Value.State.Equals("Online", StringComparison.OrdinalIgnoreCase)
                && string.Equals(grp.Value.Owner, target.Name, StringComparison.OrdinalIgnoreCase))
            {
                var who = await _c.SqlAsync(probeIp, SqlServerControl.FciVirtualServer, "SET NOCOUNT ON; SELECT @@SERVERNAME", ct).ConfigureAwait(false);
                if (who.IsOk && who.Value!.Trim().Length > 0) { newOwnerAt = sw.Elapsed; answered = true; break; }
            }
        }
        var rto = answered ? newOwnerAt - injectedAt : TimeSpan.Zero;

        // Recovery: move back to the original owner (unless NoRecover).
        var recovery = "skipped";
        if (!request.NoRecover && answered)
        {
            var back = await _c.WinPsAsync(probeIp,
                "$ErrorActionPreference='Continue';" +
                $"try {{ Move-ClusterGroup -Name '{SqlServerControl.SqlServerGroup.Replace("'", "''")}' -Node {currentOwner.Name} -Wait 120 | Out-Null; Write-Output 'BACK' }} catch {{ Write-Output 'BACKERR' }}",
                ct, TimeSpan.FromSeconds(150)).ConfigureAwait(false);
            var settled = false;
            var rdeadline = sw.Elapsed + TimeSpan.FromSeconds(120);
            while (sw.Elapsed < rdeadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var grp = await _c.ClusterGroupAsync(probeIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
                if (grp.IsOk && grp.Value.State.Equals("Online", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(grp.Value.Owner, currentOwner.Name, StringComparison.OrdinalIgnoreCase)) { settled = true; break; }
            }
            recovery = (back.IsOk && back.Value!.Contains("BACK", StringComparison.Ordinal) && settled) ? "recovered" : "failed";
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "fci-move-clustergroup",
            OriginalPrimary: currentOwner.Name,
            NewPrimary: answered ? target.Name : null,
            Rto: rto,
            Recovery: recovery,
            RecoveryHint: answered ? null : $"FCI did not come Online on {target.Name} within the deadline; check the SQL Server cluster resource + iSCSI session on {target.Name}",
            Timeline: new FailoverTimeline(preFlightAt, injectedAt, newOwnerAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOut (not tractable for a fixed 2-node FCI) ===================
    public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Ok(new ScaleOutResult("add", Array.Empty<string>(), "skipped",
            "An FCI is a fixed 2-node shared-storage instance — you don't add FCI nodes at runtime (that's a setup.exe /ACTION=AddNode rebuild). To grow READ capacity add an AG replica via `nexus scale-out add sqlserver-ag`; to grow the FCI nodes' resources use `nexus scale-up`.",
            TimeSpan.Zero, DateTimeOffset.UtcNow)));

    public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Ok(new ScaleOutResult("remove", Array.Empty<string>(), "skipped",
            "An FCI is a fixed 2-node shared-storage instance — removing a node would break the failover pair. Use the sqlserver-ag adapter to remove an AG replica instead.",
            TimeSpan.Zero, DateTimeOffset.UtcNow)));

    // === Backup (BACKUP/RESTORE DATABASE nexus_demo round-trip) ============
    private const string BackupDir = "S:\\Backups";

    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<BackupResult>(split.Error!);
        var probeIp = split.Value.Fci[0].Vmnet11;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var label = string.IsNullOrWhiteSpace(request.Tag)
            ? $"nexus_demo_{startedAt:yyyyMMdd_HHmmss}"
            : $"{Sanitize(request.Tag)}_{startedAt:yyyyMMdd_HHmmss}";
        var bak = $"{BackupDir}\\{label}.bak";

        // COPY_ONLY so the backup doesn't disturb the AG log chain on nexus_demo.
        var sql =
            $"SET NOCOUNT ON; EXEC xp_create_subdir N'{BackupDir}'; " +
            $"BACKUP DATABASE [{SqlServerControl.AgDb}] TO DISK = N'{bak}' WITH COPY_ONLY, INIT, COMPRESSION, FORMAT; " +
            $"SELECT 'SIZE=' + CONVERT(varchar(30), (SELECT CAST(backup_size AS bigint) FROM msdb.dbo.backupset WHERE backup_set_id = (SELECT MAX(backup_set_id) FROM msdb.dbo.backupset WHERE database_name='{SqlServerControl.AgDb}')))";
        var r = await _c.SqlAsync(probeIp, SqlServerControl.FciVirtualServer, sql, ct, timeout: BackupTimeout).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<BackupResult>($"BACKUP DATABASE {SqlServerControl.AgDb} failed: {r.Error}");
        long size = 0;
        var m = System.Text.RegularExpressions.Regex.Match(r.Value!, @"SIZE=(\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var sz)) size = sz;

        return Result.Ok(new BackupResult(
            BackupId: $"{label}.bak",
            Destination: $"{bak} (BACKUP DATABASE {SqlServerControl.AgDb} WITH COPY_ONLY; on the shared iSCSI LUN)",
            SizeBytes: size,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id (the .bak filename from `backup take`)");
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<RestoreResult>(split.Error!);
        var probeIp = split.Value.Fci[0].Vmnet11;
        var bak = request.BackupId.Contains('\\') ? request.BackupId : $"{BackupDir}\\{request.BackupId}";
        const string verifyDb = "nexus_demo_restore_verify";

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Discover logical file names, then RESTORE as a throwaway verify DB WITH
        // MOVE (so it doesn't collide with the live nexus_demo files), count rows,
        // drop. A genuine round-trip proof of the backup's restorability.
        var sql =
            "SET NOCOUNT ON; " +
            "IF DB_ID(N'" + verifyDb + "') IS NOT NULL BEGIN ALTER DATABASE [" + verifyDb + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + verifyDb + "]; END " +
            "DECLARE @data sysname, @log sysname; " +
            "DECLARE @fl TABLE (LogicalName sysname, PhysicalName nvarchar(260), Type char(1), FileGroupName sysname NULL, Size numeric(20,0), MaxSize numeric(20,0), FileID bigint, CreateLSN numeric(25,0), DropLSN numeric(25,0) NULL, UniqueID uniqueidentifier, ReadOnlyLSN numeric(25,0) NULL, ReadWriteLSN numeric(25,0) NULL, BackupSizeInBytes bigint, SourceBlockSize int, FileGroupID int, LogGroupGUID uniqueidentifier NULL, DifferentialBaseLSN numeric(25,0) NULL, DifferentialBaseGUID uniqueidentifier NULL, IsReadOnly bit, IsPresent bit, TDEThumbprint varbinary(32) NULL, SnapshotURL nvarchar(360) NULL); " +
            $"INSERT INTO @fl EXEC ('RESTORE FILELISTONLY FROM DISK = N''{bak}'''); " +
            "SELECT @data = LogicalName FROM @fl WHERE Type='D'; SELECT @log = LogicalName FROM @fl WHERE Type='L'; " +
            "DECLARE @cmd nvarchar(max) = 'RESTORE DATABASE [" + verifyDb + "] FROM DISK = N''" + bak + "'' WITH MOVE '''+@data+''' TO N''" + BackupDir + "\\" + verifyDb + ".mdf'', MOVE '''+@log+''' TO N''" + BackupDir + "\\" + verifyDb + ".ldf'', REPLACE, RECOVERY'; " +
            "EXEC (@cmd); " +
            "DECLARE @rows bigint = (SELECT ISNULL(SUM(p.rows),0) FROM [" + verifyDb + "].sys.partitions p WHERE p.index_id IN (0,1)); " +
            "ALTER DATABASE [" + verifyDb + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + verifyDb + "]; " +
            "SELECT 'ROWS=' + CONVERT(varchar(30), @rows)";
        var r = await _c.SqlAsync(probeIp, SqlServerControl.FciVirtualServer, sql, ct, timeout: BackupTimeout).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<RestoreResult>($"RESTORE round-trip failed: {r.Error}");
        var m = System.Text.RegularExpressions.Regex.Match(r.Value!, @"ROWS=(\d+)");
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out var rows))
            return Result.Fail<RestoreResult>($"restore did not confirm a row count: {SqlServerControl.Tail(r.Value!, 200)}");

        return Result.Ok(new RestoreResult(BackupId: request.BackupId, ItemsRestored: rows, Duration: sw.Elapsed, StartedAtUtc: startedAt));
    }

    private static string Sanitize(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[^A-Za-z0-9_]", "_");

    // === RotateCertAsync (ONE shared cert on BOTH FCI nodes; single checkpoint) ===
    // The FCI checkpoints a SINGLE SuperSocketNetLib\Certificate thumbprint applied
    // to whichever node hosts it — so both nodes must carry the SAME cert. A per-node
    // rotate (different thumbprints) would break failover (live-caught bug, 2026-06-12).
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<CertRotationResult>(split.Error!);
        var (fci, _) = split.Value;
        if (_c.Vault is null) return Result.Fail<CertRotationResult>("cert-rotate issues certs via Vault PKI; set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var probeIp = fci[0].Vmnet11;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        var group = await _c.ClusterGroupAsync(probeIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
        var owner = group.IsOk ? group.Value.Owner : fci[0].Name;
        var activeNode = fci.FirstOrDefault(n => string.Equals(n.Name, owner, StringComparison.OrdinalIgnoreCase)) ?? fci[0];

        // 1) Issue ONE shared cert carrying the FCI virtual name + BOTH node names
        //    + the cluster CNO + the AG listener (so either node can present it).
        const string sharedCn = "sqlfci.nexus.lab";
        var f1 = fci.First(n => n.Name.EndsWith("-1", StringComparison.Ordinal));
        var f2 = fci.First(n => n.Name.EndsWith("-2", StringComparison.Ordinal));
        var alt = $"sqlfci,sqlfci.nexus.lab,sql-fci-cluster,sql-fci-cluster.nexus.lab,sql-ag-listener,sql-ag-listener.nexus.lab,{f1.Name},{f1.Name}.nexus.lab,{f2.Name},{f2.Name}.nexus.lab,localhost";
        var ipsan = $"192.168.70.16,192.168.70.17,{f1.Vmnet11},{f2.Vmnet11},{f1.Vmnet10},{f2.Vmnet10},127.0.0.1";
        var art = await SqlServerCert.IssueAsync(_c, sharedCn, alt, ipsan, ct).ConfigureAwait(false);
        if (art.IsFail) return Result.Fail<CertRotationResult>(art.Error!);

        // 2) Import the SAME cert on both FCI nodes (with key + service-account grant),
        //    WITHOUT writing the per-node registry (the FCI checkpoint owns it).
        string newThumb = "(unknown)";
        foreach (var node in fci)
        {
            var cnNode = $"{node.Name}.sqlserver.nexus.lab";
            var oldSerial = await SqlServerCert.OldSerialAsync(_c, node, cnNode, ct).ConfigureAwait(false);
            var svc = await SqlServerCert.ServiceAccountAsync(_c, node, ct).ConfigureAwait(false);
            var imp = await SqlServerCert.ImportOnNodeAsync(_c, node, art.Value!, svc, setCheckpoint: false, ct).ConfigureAwait(false);
            if (imp.IsOk) newThumb = imp.Value!;
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, imp.IsOk ? art.Value!.Serial : "(unchanged)", Error: imp.IsFail ? imp.Error : null));
        }

        // 3) Set the single cluster checkpoint to the new thumbprint (on the active
        //    node; the cluster replicates it), then cycle the SQL resource so the
        //    active node restarts with the new shared cert.
        if (rotated.Any(r => r.Error is null) && newThumb != "(unknown)")
        {
            await SqlServerCert.SetCheckpointAsync(_c, activeNode.Vmnet11, newThumb, ct).ConfigureAwait(false);
            await _c.WinPsAsync(probeIp,
                "$ErrorActionPreference='Continue';" +
                $"try {{ Stop-ClusterResource -Name '{SqlServerControl.SqlServerGroup.Replace("'", "''")}' -EA Stop | Out-Null; Start-Sleep -Seconds 2; Start-ClusterResource -Name '{SqlServerControl.SqlServerGroup.Replace("'", "''")}' -EA Stop | Out-Null; Write-Output 'CYCLED' }} catch {{ Write-Output ('CYCLEERR:'+$_.Exception.Message) }}",
                ct, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            var deadline = sw.Elapsed + TimeSpan.FromSeconds(90);
            while (sw.Elapsed < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                var who = await _c.SqlAsync(probeIp, SqlServerControl.FciVirtualServer, "SET NOCOUNT ON; SELECT 1", ct).ConfigureAwait(false);
                if (who.IsOk) break;
            }
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === AclAsync (SQL logins + server-role membership) ====================
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<AclSnapshot>(split.Error!);
        var probeIp = split.Value.Fci[0].Vmnet11;
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var filter = verb == "describe" && !string.IsNullOrWhiteSpace(operation.User)
                ? $"AND sp.name = '{operation.User.Replace("'", "''")}' " : "";
            // Server principals (SQL + Windows logins) + their fixed-server-role memberships.
            var sql =
                "SET NOCOUNT ON; SELECT sp.name COLLATE DATABASE_DEFAULT + '|' + sp.type_desc COLLATE DATABASE_DEFAULT + '|' + CONVERT(varchar(2),sp.is_disabled) + '|' + " +
                "ISNULL(STUFF((SELECT ',' + r.name COLLATE DATABASE_DEFAULT FROM sys.server_role_members m JOIN sys.server_principals r ON m.role_principal_id=r.principal_id WHERE m.member_principal_id=sp.principal_id FOR XML PATH('')),1,1,''),'') " +
                $"FROM sys.server_principals sp WHERE sp.type IN ('S','U','G') AND sp.name NOT LIKE '##%' {filter}ORDER BY sp.name";
            var r = await _c.SqlAsync(probeIp, SqlServerControl.FciVirtualServer, sql, ct).ConfigureAwait(false);
            if (r.IsFail) return Result.Fail<AclSnapshot>(r.Error!);
            var users = new List<AclUser>();
            foreach (var row in SqlServerControl.PipeRows(r.Value!))
            {
                var name = row[0].Trim();
                var typ = row.Length > 1 ? row[1].Trim() : "";
                var disabled = row.Length > 2 && row[2].Trim() == "1";
                var roles = row.Length > 3 && row[3].Trim().Length > 0
                    ? row[3].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : Array.Empty<string>();
                var perms = new List<string> { typ };
                perms.AddRange(roles.Select(x => "role:" + x));
                users.Add(new AclUser(name, perms, Enabled: !disabled));
            }
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user (a server login name).");
            // Permissions list is interpreted as fixed-server-role names (e.g.
            // dbcreator, securityadmin); default = dbcreator.
            var roles = operation.Permissions is { Count: > 0 } ? operation.Permissions : new[] { "dbcreator" };
            var u = operation.User.Replace("'", "''").Replace("]", "]]");
            var sb = new StringBuilder("SET NOCOUNT ON; ");
            if (verb == "grant")
                sb.Append("IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name='" + u + "') CREATE LOGIN [" + u + "] WITH PASSWORD='Chg" + Guid.NewGuid().ToString("N") + "Aa9!', CHECK_POLICY=OFF; ");
            foreach (var role in roles)
            {
                var rl = role.Replace("'", "''").Replace("]", "]]");
                sb.Append(verb == "grant"
                    ? "ALTER SERVER ROLE [" + rl + "] ADD MEMBER [" + u + "]; "
                    : "ALTER SERVER ROLE [" + rl + "] DROP MEMBER [" + u + "]; ");
            }
            sb.Append("SELECT 'ACLOK'");
            var r = await _c.SqlAsync(probeIp, SqlServerControl.FciVirtualServer, sb.ToString(), ct).ConfigureAwait(false);
            if (r.IsFail) return Result.Fail<AclSnapshot>($"acl {verb} failed: {r.Error}");
            return await AclAsync(new AclOperation("describe", operation.User), ct).ConfigureAwait(false);
        }
        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    // === ApplyChaosAsync (process-kill SQL on the active node → WSFC failover) ===
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");
        var split = _c.Split();
        if (split.IsFail) return Result.Fail<ChaosOutcome>(split.Error!);
        var (fci, _) = split.Value;
        var probeIp = fci[0].Vmnet11;

        var pre = await _c.ClusterGroupAsync(probeIp, SqlServerControl.SqlServerGroup, ct).ConfigureAwait(false);
        if (pre.IsFail) return Result.Fail<ChaosOutcome>(pre.Error!);
        var victim = fci.FirstOrDefault(n => string.Equals(n.Name, pre.Value.Owner, StringComparison.OrdinalIgnoreCase));
        if (victim is null) return Result.Fail<ChaosOutcome>("could not determine the active FCI node to target");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        // Inject: hard-kill sqlservr.exe on the active node → WSFC detects the SQL
        // resource failure and fails the group over to the passive node.
        var kill = await _c.WinPsAsync(victim.Vmnet11,
            "$ErrorActionPreference='Continue'; Stop-Process -Name sqlservr -Force -EA SilentlyContinue; Write-Output 'KILLED'",
            ct).ConfigureAwait(false);
        if (kill.IsFail) return Result.Fail<ChaosOutcome>($"failed to kill sqlservr on {victim.Name}: {kill.Error}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(scenario.DurationSeconds <= 0 ? 15 : scenario.DurationSeconds, 25)), ct).ConfigureAwait(false);
        var impact = await HealthAsync(ct).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        // Recover: WSFC auto-restarts/fails over the SQL resource. Poll until the
        // FCI virtual server answers again (on either node).
        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(150);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var st = await GetStatusAsync(ct).ConfigureAwait(false);
            if (st.IsOk && st.Value!.OverallHealth != "red") { recovered = true; break; }
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

    // === CanResizeVm =======================================================
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false;
        var member = _lastStatus.Members.FirstOrDefault(m => string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        return member.Role != "fci-active"; // refuse the active FCI node (resize → outage)
    }
}
