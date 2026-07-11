using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Kafka cross-region DR meta-cluster (ClusterId <c>kafka</c>) — the unified view over
/// the two real per-region clusters <c>kafka-east</c> + <c>kafka-west</c> (each a 3-node
/// combined broker+controller KRaft cluster) joined by MirrorMaker 2.
/// <para>
/// <b>FailoverAsync</b> is the region-level DR drill (east↔west MM2 cutover) via the v0.5
/// <see cref="IKafkaFailoverService"/>. Every OTHER verb <b>delegates to the two
/// <see cref="KafkaClusterAdapter"/> instances and merges</b> — status/health/topology/
/// backup/cert-rotate/acl all run real per-region work (no external <c>kafka.ps1</c> /
/// <c>kafka-acls.sh</c> hop). scale-out routes the operator to the per-region ClusterId
/// (broker drain/rejoin lives there; a net-new broker is apply-on-demand terraform,
/// ADR-0010). chaos delegates to the region owning the target node.
/// </para>
/// </summary>
public sealed class KafkaAdapter : IClusterAdapter
{
    private readonly IKafkaFailoverService _kafkaFailover;
    private readonly IClusterAdapter _east;
    private readonly IClusterAdapter _west;

    /// <summary>
    /// Constructs the meta-cluster over the region-level DR service and the two
    /// per-region <see cref="KafkaClusterAdapter"/> instances (<paramref name="east"/> /
    /// <paramref name="west"/>) that every non-failover verb delegates to.
    /// </summary>
    public KafkaAdapter(IKafkaFailoverService kafkaFailover, IClusterAdapter east, IClusterAdapter west)
    {
        _kafkaFailover = kafkaFailover;
        _east = east;
        _west = west;
    }

    /// <inheritdoc />
    public string ClusterId => "kafka";
    /// <inheritdoc />
    public string DisplayName => "Apache Kafka cross-region DR (kafka-east + kafka-west, MirrorMaker 2)";

    private (IClusterAdapter Adapter, string Tag)[] Regions => [(_east, "east"), (_west, "west")];

    /// <summary>Returns the worse (more severe) of two health colors (green &lt; yellow &lt; red).</summary>
    // green < yellow < red — the merged health is the worst of the two regions.
    internal static string WorseOf(string a, string b)
    {
        static int Rank(string s) => s switch { "red" => 2, "yellow" => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }

    // === GetStatusAsync (merge both regions) ===============================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var e = await _east.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var w = await _west.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (e.IsFail && w.IsFail)
            return Result.Fail<ClusterStatus>($"both kafka regions unreachable: east='{e.Error}'; west='{w.Error}'.");

        var members = new List<ClusterMember>();
        if (e.IsOk) members.AddRange(e.Value!.Members);
        if (w.IsOk) members.AddRange(w.Value!.Members);
        var overall = WorseOf(
            e.IsOk ? e.Value!.OverallHealth : "red",
            w.IsOk ? w.Value!.OverallHealth : "red");
        return Result.Ok(new ClusterStatus(ClusterId, DisplayName, overall, members, Leader: null, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (region-level MM2 DR; unchanged) ====================
    /// <inheritdoc />
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var direction = ParseDirection(request.Direction);
        if (direction is null)
            return Result.Fail<FailoverResult>(
                $"kafka failover requires --direction east-to-west|west-to-east (got '{request.Direction ?? "(null)"}').");

        var report = await _kafkaFailover.RunAsync(direction.Value, cancellationToken).ConfigureAwait(false);
        if (report.IsFail)
            return Result.Fail<FailoverResult>(report.Error!);

        return Result.Ok(new FailoverResult(
            Scenario: $"kafka-{direction.Value.ToString().ToLowerInvariant()}",
            OriginalPrimary: report.Value!.SourceCluster,
            NewPrimary: report.Value.TargetCluster,
            Rto: report.Value.Rto,
            Recovery: report.Value.Recovery.ToString().ToLowerInvariant(),
            RecoveryHint: report.Value.RecoveryHint,
            Timeline: new FailoverTimeline(
                PreFlightCompleted: report.Value.Timeline.PreFlightCompleted,
                FailureInjected: report.Value.Timeline.FailureInjected,
                NewLeaderObserved: report.Value.Timeline.TargetHealthy,
                RecoveryAttempted: report.Value.Timeline.RecoveryAttempted,
                ClusterHealthyAgain: report.Value.Timeline.SourceHealthyAgain),
            StartedAtUtc: report.Value.StartedAtUtc));
    }

    // === Scale-out (route to the per-region ClusterId) =====================
    /// <inheritdoc />
    public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(ScaleOutRoutingMessage));

    /// <inheritdoc />
    public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(ScaleOutRoutingMessage));

    private const string ScaleOutRoutingMessage =
        "scale-out on the cross-region kafka meta-cluster is ambiguous — target a region directly: "
        + "`nexus scale-out remove kafka-east <node>` / `kafka-west <node>` (the per-region adapters implement "
        + "the quorum-guarded broker drain/rejoin). Adding a net-new broker/controller is apply-on-demand "
        + "terraform in nexus-infra-kafka (the KRaft quorum size is fixed at format time; ADR-0010).";

    // === HealthAsync (merge both regions; probe names region-prefixed) =====
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var e = await _east.HealthAsync(cancellationToken).ConfigureAwait(false);
        var w = await _west.HealthAsync(cancellationToken).ConfigureAwait(false);
        if (e.IsFail && w.IsFail)
            return Result.Fail<HealthReport>($"both kafka regions unreachable: east='{e.Error}'; west='{w.Error}'.");

        var probes = new List<HealthProbe>();
        if (e.IsOk) probes.AddRange(e.Value!.Probes.Select(p => p with { Name = $"east/{p.Name}" }));
        else probes.Add(new HealthProbe("east/region", "kafka-east", "red", e.Error, "reachable"));
        if (w.IsOk) probes.AddRange(w.Value!.Probes.Select(p => p with { Name = $"west/{p.Name}" }));
        else probes.Add(new HealthProbe("west/region", "kafka-west", "red", w.Error, "reachable"));

        var overall = WorseOf(
            e.IsOk ? e.Value!.OverallHealth : "red",
            w.IsOk ? w.Value!.OverallHealth : "red");
        return Result.Ok(new HealthReport(ClusterId, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync (merge both regions) ================================
    /// <inheritdoc />
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var e = await _east.TopologyAsync(cancellationToken).ConfigureAwait(false);
        var w = await _west.TopologyAsync(cancellationToken).ConfigureAwait(false);
        if (e.IsFail && w.IsFail)
            return Result.Fail<TopologySnapshot>($"both kafka regions unreachable: east='{e.Error}'; west='{w.Error}'.");

        var nodes = new List<TopologyNode>();
        if (e.IsOk) nodes.AddRange(e.Value!.Nodes);
        if (w.IsOk) nodes.AddRange(w.Value!.Nodes);
        nodes.Add(new TopologyNode("mirrormaker-2", "east↔west replication (the DR link; `failover kafka --direction east-to-west|west-to-east`)", "info"));
        return Result.Ok(new TopologySnapshot(ClusterId, nodes, Shards: null, DateTimeOffset.UtcNow));
    }

    // === Backup (take/restore on BOTH regions; combined id) ================
    /// <inheritdoc />
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var e = await _east.BackupTakeAsync(request, cancellationToken).ConfigureAwait(false);
        if (e.IsFail) return Result.Fail<BackupResult>($"kafka-east backup failed: {e.Error}");
        var w = await _west.BackupTakeAsync(request, cancellationToken).ConfigureAwait(false);
        if (w.IsFail) return Result.Fail<BackupResult>($"kafka-west backup failed: {w.Error}");

        // The combined id encodes BOTH per-region backup ids (split on '||' at restore).
        var combinedId = $"{e.Value!.BackupId}||{w.Value!.BackupId}";
        var dur = e.Value.Duration + w.Value.Duration;
        return Result.Ok(new BackupResult(
            combinedId,
            $"east:[{e.Value.Destination}] + west:[{w.Value.Destination}]",
            e.Value.SizeBytes + w.Value.SizeBytes,
            dur,
            startedAt));
    }

    /// <inheritdoc />
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        var (eId, wId) = SplitBackupId(request.BackupId);
        if (eId is null || wId is null)
            return Result.Fail<RestoreResult>(
                "kafka restore needs the combined backup-id from `backup take kafka` (shape '<east-id>||<west-id>'). "
                + "To restore one region only, use `nexus backup restore kafka-east <id>` / `kafka-west <id>`.");

        var startedAt = DateTimeOffset.UtcNow;
        var e = await _east.BackupRestoreAsync(new RestoreRequest(eId, request.AtTimestamp), cancellationToken).ConfigureAwait(false);
        if (e.IsFail) return Result.Fail<RestoreResult>($"kafka-east restore failed: {e.Error}");
        var w = await _west.BackupRestoreAsync(new RestoreRequest(wId, request.AtTimestamp), cancellationToken).ConfigureAwait(false);
        if (w.IsFail) return Result.Fail<RestoreResult>($"kafka-west restore failed: {w.Error}");

        return Result.Ok(new RestoreResult(
            request.BackupId,
            e.Value!.ItemsRestored + w.Value!.ItemsRestored,
            e.Value.Duration + w.Value.Duration,
            startedAt));
    }

    /// <summary>Split a combined kafka backup-id '&lt;east&gt;||&lt;west&gt;' into its two parts (null,null if not combined).</summary>
    internal static (string? East, string? West) SplitBackupId(string? combined)
    {
        if (string.IsNullOrWhiteSpace(combined)) return (null, null);
        var idx = combined.IndexOf("||", StringComparison.Ordinal);
        if (idx < 0) return (null, null);
        var east = combined[..idx].Trim();
        var west = combined[(idx + 2)..].Trim();
        return (east.Length > 0 && west.Length > 0) ? (east, west) : (null, null);
    }

    // === RotateCertAsync (rotate both regions; merge nodes) ================
    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var e = await _east.RotateCertAsync(cancellationToken).ConfigureAwait(false);
        var w = await _west.RotateCertAsync(cancellationToken).ConfigureAwait(false);
        if (e.IsFail && w.IsFail)
            return Result.Fail<CertRotationResult>($"both kafka regions failed cert-rotate: east='{e.Error}'; west='{w.Error}'.");

        var rotated = new List<CertRotatedNode>();
        if (e.IsOk) rotated.AddRange(e.Value!.RotatedNodes);
        else rotated.Add(new CertRotatedNode("kafka-east", "(none)", "(none)", e.Error));
        if (w.IsOk) rotated.AddRange(w.Value!.RotatedNodes);
        else rotated.Add(new CertRotatedNode("kafka-west", "(none)", "(none)", w.Error));

        var dur = (e.IsOk ? e.Value!.Duration : TimeSpan.Zero) + (w.IsOk ? w.Value!.Duration : TimeSpan.Zero);
        return Result.Ok(new CertRotationResult(rotated, dur, startedAt));
    }

    // === ApplyChaosAsync (delegate to the region owning the target) ========
    /// <inheritdoc />
    public Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        var target = scenario.Target ?? "";
        if (target.Contains("west", StringComparison.OrdinalIgnoreCase))
            return _west.ApplyChaosAsync(scenario, cancellationToken);
        if (target.Contains("east", StringComparison.OrdinalIgnoreCase))
            return _east.ApplyChaosAsync(scenario, cancellationToken);
        // No region in the target → default to east (the per-region adapter picks a safe broker).
        return _east.ApplyChaosAsync(scenario, cancellationToken);
    }

    // === AclAsync (apply to BOTH regions; merge) ===========================
    /// <inheritdoc />
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var e = await _east.AclAsync(operation, cancellationToken).ConfigureAwait(false);
        var w = await _west.AclAsync(operation, cancellationToken).ConfigureAwait(false);
        if (e.IsFail && w.IsFail)
            return Result.Fail<AclSnapshot>($"kafka acl {operation.Verb} failed on both regions: east='{e.Error}'; west='{w.Error}'.");
        if (e.IsFail) return Result.Fail<AclSnapshot>($"kafka-east acl {operation.Verb} failed: {e.Error}");
        if (w.IsFail) return Result.Fail<AclSnapshot>($"kafka-west acl {operation.Verb} failed: {w.Error}");

        // Merge the two regions' principals, deduped by name (a principal is granted on both regions).
        var byName = new Dictionary<string, AclUser>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in e.Value!.Users.Concat(w.Value!.Users))
            byName[u.Name] = u;
        return Result.Ok(new AclSnapshot(ClusterId, operation.Verb, byName.Values.ToList(), DateTimeOffset.UtcNow));
    }

    // === CanResizeVm (delegate to the region owning the VM) ================
    /// <inheritdoc />
    public bool CanResizeVm(string vmName, string role)
    {
        if (vmName.Contains("west", StringComparison.OrdinalIgnoreCase)) return _west.CanResizeVm(vmName, role);
        if (vmName.Contains("east", StringComparison.OrdinalIgnoreCase)) return _east.CanResizeVm(vmName, role);
        return false;
    }

    /// <summary>Parses the <c>--direction</c> flag (east-to-west / west-to-east + aliases) to a <see cref="KafkaFailoverDirection"/>; null if unrecognized.</summary>
    private static KafkaFailoverDirection? ParseDirection(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "east-to-west" or "east2west" or "e2w" => KafkaFailoverDirection.EastToWest,
            "west-to-east" or "west2east" or "w2e" => KafkaFailoverDirection.WestToEast,
            _ => null,
        };
    }
}
