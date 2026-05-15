namespace Nexus.Cli.Core.Models;

/// <summary>
/// Which direction the Kafka DR failover runs in.
/// </summary>
public enum KafkaFailoverDirection
{
    /// <summary>Halt kafka-east; prove kafka-west keeps serving.</summary>
    EastToWest = 0,

    /// <summary>Halt kafka-west; prove kafka-east keeps serving.</summary>
    WestToEast = 1,
}

/// <summary>
/// Recovery outcome after the verb tries to vmrun-resume the suspended
/// source-cluster brokers.
/// </summary>
public enum KafkaFailoverRecoveryStatus
{
    Recovered = 0,
    RecoveryFailed = 1,
    NotAttempted = 2,
}

/// <summary>
/// Per-step wall-clock timing for a kafka-failover run. All offsets are
/// measured from <see cref="KafkaFailoverReport.StartedAtUtc"/>.
/// </summary>
public sealed record KafkaFailoverTimeline(
    TimeSpan PreFlightCompleted,
    TimeSpan FailureInjected,
    TimeSpan TargetHealthy,
    TimeSpan RecoveryAttempted,
    TimeSpan SourceHealthyAgain);

/// <summary>
/// Outcome of a single kafka-failover run.
/// <para>
/// The "leader" metaphor of <see cref="Models.FailoverTestReport"/> is one
/// node; for Kafka DR the unit of failover is a whole 3-broker cluster, so
/// the analogous fields name the source / target clusters and record the
/// produce-consume round-trip that proves the target is serving.
/// </para>
/// </summary>
public sealed record KafkaFailoverReport(
    KafkaFailoverDirection Direction,
    DateTimeOffset StartedAtUtc,
    string SourceCluster,
    string TargetCluster,
    IReadOnlyList<string> SuspendedBrokers,
    bool TargetServedAfterFailure,
    string? TargetProbeToken,
    TimeSpan Rto,
    KafkaFailoverRecoveryStatus Recovery,
    string? RecoveryHint,
    KafkaFailoverTimeline Timeline);
