using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Kafka cluster retrofit into the <see cref="IClusterAdapter"/> SPI
/// introduced in Phase 0.G (ADR-0009). Wraps the existing v0.5
/// <see cref="IKafkaFailoverService"/> for FailoverAsync; cluster status +
/// topology + scale-out + backup + chaos are deferred -- the kafka tier's
/// operational surface is the <c>kafka.ps1</c> + <c>smoke-0.H.&lt;N&gt;.ps1</c>
/// scripts in <c>nexus-infra-kafka</c>, NOT this SPI. This adapter exists
/// so <c>nexus failover-test kafka</c> works through the unified verb
/// pattern without breaking the existing <c>nexus kafka failover</c>
/// surface.
/// </summary>
public sealed class KafkaAdapter : IClusterAdapter
{
    private readonly IKafkaFailoverService _kafkaFailover;

    public KafkaAdapter(IKafkaFailoverService kafkaFailover)
    {
        _kafkaFailover = kafkaFailover;
    }

    public string ClusterId => "kafka";
    public string DisplayName => "Apache Kafka (KRaft, east + west clusters)";

    public Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        // The cluster-status surface for kafka is the existing `nexus cluster-status`
        // verb (covers Consul + Nomad + Portainer) + the kafka-tier-specific smoke
        // gates 0.H.{2,3,4,5}. A kafka-specific status query through this SPI is
        // a future ergonomics improvement.
        return Task.FromResult(Result.Fail<ClusterStatus>(
            "KafkaAdapter.GetStatusAsync is deferred. For kafka health, use the kafka-tier smoke gates: "
            + "`pwsh -File nexus-infra-kafka/scripts/kafka.ps1 smoke -Phase 0.H.5`."));
    }

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

    public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(
            "KafkaAdapter.ScaleOutAddAsync is deferred. Kafka tier expansion (adding a 4th broker / 4th controller) is a multi-step operation managed via nexus-infra-kafka's terraform overlays + the operator handbook."));

    public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(
            "KafkaAdapter.ScaleOutRemoveAsync is deferred. Kafka tier contraction is managed via nexus-infra-kafka's terraform overlays."));

    public Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<HealthReport>(
            "KafkaAdapter.HealthAsync is deferred. For kafka health, use `pwsh -File nexus-infra-kafka/scripts/kafka.ps1 smoke`."));

    public Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<TopologySnapshot>(
            "KafkaAdapter.TopologyAsync is deferred. For kafka topology, use `kafka-metadata-quorum.sh ... describe --status` on a broker."));

    public Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<BackupResult>(
            "KafkaAdapter.BackupTakeAsync is deferred. Kafka topic backup is MirrorMaker-2-based (already running between east + west); per-topic snapshotting is out of v0.G scope."));

    public Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<RestoreResult>(
            "KafkaAdapter.BackupRestoreAsync is deferred. See note on BackupTakeAsync."));

    public Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<CertRotationResult>(
            "KafkaAdapter.RotateCertAsync is deferred. The kafka tier uses Vault Agent for cert rotation (per-host nexus-vault-agent.service); to force a rotation, restart the agent on the target broker via SSH."));

    public Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ChaosOutcome>(
            "KafkaAdapter.ApplyChaosAsync is deferred. Region-loss chaos is already covered by the existing `nexus kafka failover` verb."));

    public Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<AclSnapshot>(
            "KafkaAdapter.AclAsync is deferred. Kafka ACL management is via `kafka-acls.sh` on a broker."));

    public bool CanResizeVm(string vmName, string role) =>
        // Kafka tier sizing is managed via the kafka-node Packer template's memory_mb +
        // terraform module.vm's memory_mb. Mid-flight vmrun-based resize is outside
        // the kafka tier's operational surface in v0.6.
        false;

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
