namespace Nexus.Cli.Core.Models;

/// <summary>One ordered step of a project deploy plan.</summary>
/// <param name="Name">Short stable step name (e.g. <c>build-images</c>, <c>migrate-starrocks</c>).</param>
/// <param name="Command">The shell command line the step runs (relative to the project repo).</param>
/// <param name="Description">One-line human description of what the step does.</param>
public sealed record DeployStep(
    string Name,
    string Command,
    string Description);

/// <summary>An ordered, reviewable plan to deploy an application project end-to-end.</summary>
/// <param name="Project">The project id (e.g. <c>dataflow-studio</c>).</param>
/// <param name="RepoPath">Filesystem path the steps run from (the project repo working copy).</param>
/// <param name="Steps">The ordered deploy steps (build → migrate → deploy).</param>
public sealed record DeployPlan(
    string Project,
    string RepoPath,
    IReadOnlyList<DeployStep> Steps);

/// <summary>Outcome of executing one <see cref="DeployStep"/>.</summary>
/// <param name="Name">The step name.</param>
/// <param name="ExitCode">Process exit code observed.</param>
/// <param name="OutputTail">Tail of captured stdout+stderr.</param>
/// <param name="Duration">Wall-clock time the step took.</param>
public sealed record DeployStepResult(
    string Name,
    int ExitCode,
    string OutputTail,
    TimeSpan Duration);

/// <summary>Terminal status of a deploy execution.</summary>
public enum DeployStatus
{
    /// <summary>Every step exited zero.</summary>
    Ok = 0,

    /// <summary>A step exited non-zero; execution stopped.</summary>
    StepFailed = 1
}

/// <summary>Report of executing a deploy plan.</summary>
/// <param name="Project">The deployed project id.</param>
/// <param name="Status">Terminal status.</param>
/// <param name="Steps">Per-step results in execution order.</param>
/// <param name="TotalDuration">Total wall-clock time.</param>
public sealed record DeployReport(
    string Project,
    DeployStatus Status,
    IReadOnlyList<DeployStepResult> Steps,
    TimeSpan TotalDuration);
