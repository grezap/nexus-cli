using FluentAssertions;
using Nexus.Cli.Adapters.Demos;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;
using Xunit;

namespace Nexus.Cli.Tests.Demos;

/// <summary>
/// Covers ADR-0009 (DemoRunner step-expectation enforcement). Tests use
/// cross-platform shell commands (`echo`, `exit`) so they run on Windows
/// (cmd.exe /c) and Linux (/bin/sh -c) without conditional logic.
/// </summary>
public class DemoRunnerTests
{
    private static DemoRunner CreateRunner() => new(new StubVhsClient());

    [Fact]
    public async Task RunAsync_StepWithoutExpectations_PassesOnExitZero()
    {
        var runner = CreateRunner();
        var spec = new DemoSpec(
            "DEMO-T-RUN-01",
            "no expectations",
            "step succeeds on exit 0",
            new List<DemoStep>
            {
                new("echo hello", WaitAfterSeconds: 0)
            });

        var result = await runner.RunAsync(spec, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(DemoStatus.Ok);
        var step = result.Value.Steps.Single();
        step.ExpectationMet.Should().BeNull();
        step.ExpectationFailureReason.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_ExpectedExitCodeMet_RecordsExpectationMet()
    {
        var runner = CreateRunner();
        var spec = new DemoSpec(
            "DEMO-T-RUN-02",
            "expectedExitCode=0 met",
            "step exit matches expectation",
            new List<DemoStep>
            {
                new("echo hello", 0, ExpectedExitCode: 0)
            });

        var result = await runner.RunAsync(spec, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(DemoStatus.Ok);
        result.Value.Steps.Single().ExpectationMet.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ExpectedExitCodeMissed_StepFailed()
    {
        var runner = CreateRunner();
        // `echo` exits 0, but we expect exit code 1 -> mismatch.
        var spec = new DemoSpec(
            "DEMO-T-RUN-03",
            "expectedExitCode mismatch",
            "step exit does not match expectation",
            new List<DemoStep>
            {
                new("echo hello", 0, ExpectedExitCode: 1)
            });

        var result = await runner.RunAsync(spec, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(DemoStatus.StepFailed);
        var step = result.Value.Steps.Single();
        step.ExpectationMet.Should().BeFalse();
        step.ExpectationFailureReason.Should().NotBeNull().And.Contain("expected exit code 1");
    }

    [Fact]
    public async Task RunAsync_ExpectedOutputContainsMet_RecordsExpectationMet()
    {
        var runner = CreateRunner();
        var spec = new DemoSpec(
            "DEMO-T-RUN-04",
            "expectedOutputContains met",
            "step output contains expected substring",
            new List<DemoStep>
            {
                new("echo expected-token", 0,
                    ExpectedOutputContains: new[] { "expected-token" })
            });

        var result = await runner.RunAsync(spec, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(DemoStatus.Ok);
        result.Value.Steps.Single().ExpectationMet.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_ExpectedOutputContainsMissing_StepFailed()
    {
        var runner = CreateRunner();
        var spec = new DemoSpec(
            "DEMO-T-RUN-05",
            "expectedOutputContains missed",
            "step output does not contain expected substring",
            new List<DemoStep>
            {
                new("echo hello", 0,
                    ExpectedOutputContains: new[] { "missing-token" })
            });

        var result = await runner.RunAsync(spec, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(DemoStatus.StepFailed);
        var step = result.Value.Steps.Single();
        step.ExpectationMet.Should().BeFalse();
        step.ExpectationFailureReason.Should().NotBeNull().And.Contain("missing-token");
    }

    [Fact]
    public async Task RunAsync_MultipleExpectedTokensSomeMissing_StepFailed()
    {
        var runner = CreateRunner();
        var spec = new DemoSpec(
            "DEMO-T-RUN-06",
            "partial output match",
            "first token present, second token missing",
            new List<DemoStep>
            {
                new("echo expected-token", 0,
                    ExpectedOutputContains: new[] { "expected-token", "other-token" })
            });

        var result = await runner.RunAsync(spec, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(DemoStatus.StepFailed);
        var step = result.Value.Steps.Single();
        step.ExpectationMet.Should().BeFalse();
        step.ExpectationFailureReason.Should().Contain("other-token");
    }

    [Fact]
    public async Task RunAsync_LaterStepNotExecutedAfterExpectationFailure()
    {
        var runner = CreateRunner();
        var spec = new DemoSpec(
            "DEMO-T-RUN-07",
            "stops on first expectation failure",
            "second step must not run after first fails",
            new List<DemoStep>
            {
                new("echo hello", 0,
                    ExpectedOutputContains: new[] { "missing-token" }),
                new("echo unreachable", 0)
            });

        var result = await runner.RunAsync(spec, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value!.Status.Should().Be(DemoStatus.StepFailed);
        result.Value.Steps.Should().ContainSingle();
        result.Value.Steps[0].ExpectationMet.Should().BeFalse();
    }

    private sealed class StubVhsClient : IVhsClient
    {
        public bool IsAvailable => false;
        public string UnavailableMessage() => "vhs not installed (test stub)";
        public Task<Result<int>> RenderAsync(string tapeFilePath, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Fail<int>("RenderAsync not exercised in DemoRunnerTests"));
    }
}
