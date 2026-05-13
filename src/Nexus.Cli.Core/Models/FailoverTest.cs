namespace Nexus.Cli.Core.Models;

public enum FailoverScenario
{
    ConsulLeader = 0,
    NomadLeader = 1,
    SwarmManager = 2
}

public enum FailoverRecoveryStatus
{
    Recovered = 0,
    RecoveryFailed = 1,
    NotAttempted = 2
}

/// <summary>
/// Per-step wall-clock timing for a failover-test run. All offsets are
/// measured from <see cref="FailoverTestReport.StartedAtUtc"/>.
/// </summary>
public sealed record FailoverTimeline(
    TimeSpan PreFlightCompleted,
    TimeSpan FailureInjected,
    TimeSpan NewLeaderObserved,
    TimeSpan RecoveryAttempted,
    TimeSpan ClusterHealthyAgain);

public sealed record FailoverTestReport(
    FailoverScenario Scenario,
    DateTimeOffset StartedAtUtc,
    string OriginalLeader,
    string? NewLeader,
    TimeSpan Rto,
    FailoverRecoveryStatus Recovery,
    string? RecoveryHint,
    FailoverTimeline Timeline);
