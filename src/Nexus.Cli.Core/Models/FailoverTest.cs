namespace Nexus.Cli.Core.Models;

/// <summary>Which control-plane leader a failover test kills to prove HA.</summary>
public enum FailoverScenario
{
    /// <summary>Fail over the Consul raft leader.</summary>
    ConsulLeader = 0,

    /// <summary>Fail over the Nomad raft leader.</summary>
    NomadLeader = 1,

    /// <summary>Fail over the Swarm manager leader.</summary>
    SwarmManager = 2
}

/// <summary>Outcome of the post-failover recovery attempt.</summary>
public enum FailoverRecoveryStatus
{
    /// <summary>The killed node was recovered and rejoined healthy.</summary>
    Recovered = 0,

    /// <summary>Recovery was attempted but the node did not return healthy.</summary>
    RecoveryFailed = 1,

    /// <summary>Recovery was intentionally not attempted.</summary>
    NotAttempted = 2
}

/// <summary>
/// Per-step wall-clock timing for a failover-test run. All offsets are
/// measured from <see cref="FailoverTestReport.StartedAtUtc"/>.
/// </summary>
/// <param name="PreFlightCompleted">Offset at which pre-flight checks finished.</param>
/// <param name="FailureInjected">Offset at which the leader was killed.</param>
/// <param name="NewLeaderObserved">Offset at which a new leader was observed.</param>
/// <param name="RecoveryAttempted">Offset at which recovery of the killed node began.</param>
/// <param name="ClusterHealthyAgain">Offset at which the cluster returned fully healthy.</param>
public sealed record FailoverTimeline(
    TimeSpan PreFlightCompleted,
    TimeSpan FailureInjected,
    TimeSpan NewLeaderObserved,
    TimeSpan RecoveryAttempted,
    TimeSpan ClusterHealthyAgain);

/// <summary>Result of a single control-plane failover test, including RTO and recovery outcome.</summary>
/// <param name="Scenario">Which leader was failed over.</param>
/// <param name="StartedAtUtc">Instant the test began; timeline offsets are relative to this.</param>
/// <param name="OriginalLeader">Node that held leadership before the kill.</param>
/// <param name="NewLeader">Node that took leadership after the kill, or <c>null</c> if none was observed.</param>
/// <param name="Rto">Recovery time objective: elapsed time until a new leader served.</param>
/// <param name="Recovery">Outcome of the recovery attempt on the killed node.</param>
/// <param name="RecoveryHint">Operator remediation hint when recovery failed, or <c>null</c>.</param>
/// <param name="Timeline">Wall-clock offsets of each phase of the run.</param>
public sealed record FailoverTestReport(
    FailoverScenario Scenario,
    DateTimeOffset StartedAtUtc,
    string OriginalLeader,
    string? NewLeader,
    TimeSpan Rto,
    FailoverRecoveryStatus Recovery,
    string? RecoveryHint,
    FailoverTimeline Timeline);
