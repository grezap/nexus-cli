using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// The cluster-adapter SPI introduced for Phase 0.G's data-tier verb
/// expansion (ADR-0009 in nexus-cli; cross-tier rationale in
/// nexus-platform-plan ADR-0024).
/// <para>
/// One implementation per cluster type (RedisAdapter, MongoAdapter,
/// PerconaAdapter, PatroniAdapter, ClickHouseAdapter, StarRocksAdapter,
/// SqlFciAdapter, SqlAgAdapter, plus a KafkaAdapter retrofit absorbing
/// the v0.5 kafka-failover logic). Concrete adapters dispatch via SSH
/// to on-node native CLIs (redis-cli / mongosh / mysql / patronictl /
/// clickhouse-client / sqlcmd) -- no managed DB drivers are linked,
/// keeping AOT footprint flat per adapter (~150-300 KB).
/// </para>
/// <para>
/// Architectural constraints (verified by NetArchTest in
/// Nexus.Cli.Tests/Architecture/ClusterAdapterTests.cs): every concrete
/// *Adapter implements this interface; no *Adapter references a managed
/// DB-driver type (StackExchange.Redis, MongoDB.Driver, Npgsql,
/// MySqlConnector, Microsoft.Data.SqlClient, ClickHouse.Client).
/// </para>
/// </summary>
public interface IClusterAdapter
{
    /// <summary>Stable identifier used by commands + the cluster registry.</summary>
    string ClusterId { get; }

    /// <summary>Human-readable name for status/topology rendering.</summary>
    string DisplayName { get; }

    /// <summary>Probes the live cluster and returns its current role/health topology.</summary>
    Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Triggers a controlled primary/leader failover per <paramref name="request"/>.</summary>
    Task<Result<FailoverResult>> FailoverAsync(
        FailoverRequest request,
        CancellationToken cancellationToken);

    /// <summary>Horizontally scales the cluster out by adding a node (the <c>scale-out add</c> verb).</summary>
    Task<Result<ScaleOutResult>> ScaleOutAddAsync(
        ScaleOutAddRequest request,
        CancellationToken cancellationToken);

    /// <summary>Horizontally scales the cluster in by removing a node (the <c>scale-out remove</c> verb).</summary>
    Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(
        ScaleOutRemoveRequest request,
        CancellationToken cancellationToken);

    /// <summary>Runs the adapter's deep health checks and returns a per-node report.</summary>
    Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken);

    /// <summary>Captures a point-in-time snapshot of the cluster's membership and roles.</summary>
    Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken);

    /// <summary>Takes a backup of the cluster per <paramref name="request"/>.</summary>
    Task<Result<BackupResult>> BackupTakeAsync(
        BackupRequest request,
        CancellationToken cancellationToken);

    /// <summary>Restores the cluster from a prior backup per <paramref name="request"/>.</summary>
    Task<Result<RestoreResult>> BackupRestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken);

    /// <summary>Rotates the cluster's TLS/mTLS leaf certificates, online where the engine supports it.</summary>
    Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken);

    /// <summary>Injects a chaos <paramref name="scenario"/> (fault/kill/partition) to exercise resilience.</summary>
    Task<Result<ChaosOutcome>> ApplyChaosAsync(
        ChaosScenario scenario,
        CancellationToken cancellationToken);

    /// <summary>Reads or mutates the cluster's access-control state per <paramref name="operation"/>.</summary>
    Task<Result<AclSnapshot>> AclAsync(
        AclOperation operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Synchronous heuristic: can the given VM be vertically resized right
    /// now without disrupting the cluster's write window? Returns false if
    /// the VM is the current primary and ForcePrimary is required to
    /// override. Consulted by <see cref="IVmResizer"/>.
    /// </summary>
    bool CanResizeVm(string vmName, string role);
}
