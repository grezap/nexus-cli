using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Orchestrates the Kafka MirrorMaker2 cross-cluster DR failover/failback drill
/// (the v0.5 kafka-failover logic, later absorbed by the KafkaAdapter).
/// </summary>
public interface IKafkaFailoverService
{
    /// <summary>Runs the Kafka DR drill in the given <paramref name="direction"/> (failover or failback).</summary>
    Task<Result<KafkaFailoverReport>> RunAsync(
        KafkaFailoverDirection direction,
        CancellationToken cancellationToken);
}
