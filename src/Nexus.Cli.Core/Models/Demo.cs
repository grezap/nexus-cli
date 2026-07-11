namespace Nexus.Cli.Core.Models;

/// <summary>One scripted command in a demo playbook plus its post-run wait and expectations.</summary>
/// <param name="Command">CLI command line to execute for this step.</param>
/// <param name="WaitAfterSeconds">Seconds to pause after the command before the next step.</param>
/// <param name="ExpectedExitCode">Exit code the step must return to pass, or <c>null</c> to not assert.</param>
/// <param name="ExpectedOutputContains">Substrings that must appear in stdout to pass, or <c>null</c>.</param>
/// <param name="Observations">Side observations to narrate alongside the command, or <c>null</c>.</param>
public sealed record DemoStep(
    string Command,
    double WaitAfterSeconds,
    int? ExpectedExitCode = null,
    IReadOnlyList<string>? ExpectedOutputContains = null,
    IReadOnlyList<DemoObservation>? Observations = null);

/// <summary>A narrated side-check pointing the viewer at what to watch and where.</summary>
/// <param name="Where">Location to observe (host, node, dashboard).</param>
/// <param name="What">What the viewer should expect to see there.</param>
public sealed record DemoObservation(
    string Where,
    string What);

/// <summary>Preconditions a demo needs before it can run.</summary>
/// <param name="VmsAlive">Names of VMs that must be reachable.</param>
/// <param name="EnvVars">Environment variables that must be set.</param>
public sealed record DemoPrerequisites(
    IReadOnlyList<string> VmsAlive,
    IReadOnlyList<string> EnvVars);

/// <summary>Declarative specification of a runnable demo playbook.</summary>
/// <param name="Id">Stable demo identifier.</param>
/// <param name="Title">Human-readable demo title.</param>
/// <param name="Description">Short description of what the demo walks through.</param>
/// <param name="Steps">Ordered steps executed by the demo runner.</param>
/// <param name="Prerequisites">Optional preconditions gating the run.</param>
/// <param name="WhatProves">Optional one-line claim the demo substantiates.</param>
public sealed record DemoSpec(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<DemoStep> Steps,
    DemoPrerequisites? Prerequisites = null,
    string? WhatProves = null);

/// <summary>Outcome of executing a single <see cref="DemoStep"/>.</summary>
/// <param name="StepIndex">Zero-based position of the step in the playbook.</param>
/// <param name="Command">Command line that was executed.</param>
/// <param name="ExitCode">Process exit code observed.</param>
/// <param name="StdoutTail">Tail of captured standard output.</param>
/// <param name="StderrTail">Tail of captured standard error.</param>
/// <param name="Duration">Wall-clock time the command took.</param>
/// <param name="ExpectationMet">Whether the step's expectations passed, or <c>null</c> when none were asserted.</param>
/// <param name="ExpectationFailureReason">Reason the expectation failed, or <c>null</c> on success.</param>
public sealed record DemoStepResult(
    int StepIndex,
    string Command,
    int ExitCode,
    string StdoutTail,
    string StderrTail,
    TimeSpan Duration,
    bool? ExpectationMet = null,
    string? ExpectationFailureReason = null);

/// <summary>Terminal status of a demo run.</summary>
public enum DemoStatus
{
    /// <summary>All steps ran and met their expectations.</summary>
    Ok = 0,

    /// <summary>A step ran but failed its expectation.</summary>
    StepFailed = 1,

    /// <summary>The run was aborted before completing.</summary>
    Aborted = 2
}

/// <summary>Report of a full demo playbook execution.</summary>
/// <param name="DemoId">Identifier of the executed demo.</param>
/// <param name="Title">Title of the executed demo.</param>
/// <param name="StartedAtUtc">Instant the run began.</param>
/// <param name="Status">Terminal run status.</param>
/// <param name="Steps">Per-step results in execution order.</param>
/// <param name="TotalDuration">Total wall-clock time of the run.</param>
public sealed record DemoRunReport(
    string DemoId,
    string Title,
    DateTimeOffset StartedAtUtc,
    DemoStatus Status,
    IReadOnlyList<DemoStepResult> Steps,
    TimeSpan TotalDuration);

/// <summary>Report of a demo recording session that renders a VHS tape to an asciicast/GIF.</summary>
/// <param name="DemoId">Identifier of the recorded demo.</param>
/// <param name="Title">Title of the recorded demo.</param>
/// <param name="StartedAtUtc">Instant the recording began.</param>
/// <param name="TapeFilePath">Path to the generated VHS tape script.</param>
/// <param name="OutputFilePath">Path to the rendered output, or <c>null</c> when not produced.</param>
/// <param name="VhsAvailable">Whether the <c>vhs</c> tool was found on the host.</param>
/// <param name="VhsUnavailableMessage">Guidance shown when <c>vhs</c> is missing, or <c>null</c>.</param>
/// <param name="Duration">Wall-clock time the recording took.</param>
public sealed record DemoRecordReport(
    string DemoId,
    string Title,
    DateTimeOffset StartedAtUtc,
    string TapeFilePath,
    string? OutputFilePath,
    bool VhsAvailable,
    string? VhsUnavailableMessage,
    TimeSpan Duration);
