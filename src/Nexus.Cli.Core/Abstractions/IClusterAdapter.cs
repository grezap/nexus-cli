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

    Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken);

    Task<Result<FailoverResult>> FailoverAsync(
        FailoverRequest request,
        CancellationToken cancellationToken);

    Task<Result<ScaleOutResult>> ScaleOutAddAsync(
        ScaleOutAddRequest request,
        CancellationToken cancellationToken);

    Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(
        ScaleOutRemoveRequest request,
        CancellationToken cancellationToken);

    Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken);

    Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken);

    Task<Result<BackupResult>> BackupTakeAsync(
        BackupRequest request,
        CancellationToken cancellationToken);

    Task<Result<RestoreResult>> BackupRestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken);

    Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken);

    Task<Result<ChaosOutcome>> ApplyChaosAsync(
        ChaosScenario scenario,
        CancellationToken cancellationToken);

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
