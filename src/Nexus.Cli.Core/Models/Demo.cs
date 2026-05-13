namespace Nexus.Cli.Core.Models;

public sealed record DemoStep(
    string Command,
    double WaitAfterSeconds);

public sealed record DemoSpec(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<DemoStep> Steps);

public sealed record DemoStepResult(
    int StepIndex,
    string Command,
    int ExitCode,
    string StdoutTail,
    string StderrTail,
    TimeSpan Duration);

public enum DemoStatus
{
    Ok = 0,
    StepFailed = 1,
    Aborted = 2
}

public sealed record DemoRunReport(
    string DemoId,
    string Title,
    DateTimeOffset StartedAtUtc,
    DemoStatus Status,
    IReadOnlyList<DemoStepResult> Steps,
    TimeSpan TotalDuration);

public sealed record DemoRecordReport(
    string DemoId,
    string Title,
    DateTimeOffset StartedAtUtc,
    string TapeFilePath,
    string? OutputFilePath,
    bool VhsAvailable,
    string? VhsUnavailableMessage,
    TimeSpan Duration);
