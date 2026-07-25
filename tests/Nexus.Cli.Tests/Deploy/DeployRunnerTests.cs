using FluentAssertions;
using Nexus.Cli.Adapters.Deploy;
using Nexus.Cli.Core.Models;
using Xunit;

namespace Nexus.Cli.Tests.Deploy;

/// <summary>The deploy runner executes plan steps via the shell and stops on the first failure.</summary>
public sealed class DeployRunnerTests
{
    private static DeployStep Echo(string name, string text) =>
        new(name, $"echo {text}", "echo step");

    private static DeployStep Fail(string name) =>
        new(name, OperatingSystem.IsWindows() ? "exit /b 3" : "exit 3", "failing step");

    [Fact]
    public async Task ExecuteAsync_runs_every_step_and_reports_ok()
    {
        var plan = new DeployPlan("test", Environment.CurrentDirectory, [Echo("a", "one"), Echo("b", "two")]);

        var result = await new DeployRunner().ExecuteAsync(plan, CancellationToken.None);

        result.IsOk.Should().BeTrue();
        var report = result.Value!;
        report.Status.Should().Be(DeployStatus.Ok);
        report.Steps.Should().HaveCount(2);
        report.Steps.Should().OnlyContain(s => s.ExitCode == 0);
    }

    [Fact]
    public async Task ExecuteAsync_stops_on_the_first_failing_step()
    {
        var plan = new DeployPlan("test", Environment.CurrentDirectory, [Fail("boom"), Echo("never", "unreached")]);

        var result = await new DeployRunner().ExecuteAsync(plan, CancellationToken.None);

        var report = result.Value!;
        report.Status.Should().Be(DeployStatus.StepFailed);
        report.Steps.Should().ContainSingle();   // execution stopped after the failing step
        report.Steps[0].ExitCode.Should().NotBe(0);
    }
}
