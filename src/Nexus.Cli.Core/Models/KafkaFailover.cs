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
    /// <summary>The suspended source brokers were resumed and rejoined healthy.</summary>
    Recovered = 0,

    /// <summary>Resume was attempted but the source cluster did not return healthy.</summary>
    RecoveryFailed = 1,

    /// <summary>Recovery was intentionally not attempted.</summary>
    NotAttempted = 2,
}

/// <summary>
/// Per-step wall-clock timing for a kafka-failover run. All offsets are
/// measured from <see cref="KafkaFailoverReport.StartedAtUtc"/>.
/// </summary>
/// <param name="PreFlightCompleted">Offset at which pre-flight checks finished.</param>
/// <param name="FailureInjected">Offset at which the source-cluster brokers were suspended.</param>
/// <param name="TargetHealthy">Offset at which the target cluster was confirmed serving.</param>
/// <param name="RecoveryAttempted">Offset at which resume of the source brokers began.</param>
/// <param name="SourceHealthyAgain">Offset at which the source cluster returned healthy.</param>
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
/// <param name="Direction">Which cluster was halted and which was proven to keep serving.</param>
/// <param name="StartedAtUtc">Instant the run began; timeline offsets are relative to this.</param>
/// <param name="SourceCluster">Cluster that was suspended (the DR failure source).</param>
/// <param name="TargetCluster">Cluster expected to keep serving after the failure.</param>
/// <param name="SuspendedBrokers">Broker hosts that were suspended to inject the failure.</param>
/// <param name="TargetServedAfterFailure">Whether the target cluster served a produce-consume round-trip after the failure.</param>
/// <param name="TargetProbeToken">Unique token round-tripped through the target to prove serving, or <c>null</c>.</param>
/// <param name="Rto">Recovery time objective: elapsed time until the target was confirmed serving.</param>
/// <param name="Recovery">Outcome of resuming the suspended source brokers.</param>
/// <param name="RecoveryHint">Operator remediation hint when recovery failed, or <c>null</c>.</param>
/// <param name="Timeline">Wall-clock offsets of each phase of the run.</param>
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
