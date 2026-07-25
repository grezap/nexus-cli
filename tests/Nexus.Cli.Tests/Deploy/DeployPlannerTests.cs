using FluentAssertions;
using Nexus.Cli.Adapters.Deploy;
using Xunit;

namespace Nexus.Cli.Tests.Deploy;

/// <summary>The deploy planner builds the correct ordered plan for the known project and fails cleanly otherwise.</summary>
public sealed class DeployPlannerTests
{
    [Fact]
    public void BuildPlan_for_dataflow_studio_has_the_ordered_steps()
    {
        var result = new DataflowStudioDeployPlanner().BuildPlan("dataflow-studio", "/repo");

        result.IsOk.Should().BeTrue();
        var plan = result.Value!;
        plan.Project.Should().Be("dataflow-studio");
        plan.RepoPath.Should().Be("/repo");
        plan.Steps.Select(s => s.Name).Should()
            .ContainInOrder("build-images", "migrate-oltp", "migrate-starrocks", "migrate-clickhouse", "deploy-k8s");
        plan.Steps.Should().OnlyContain(s => s.Command.Length > 0 && s.Description.Length > 0);
    }

    [Fact]
    public void BuildPlan_is_case_insensitive_on_the_project_id()
    {
        new DataflowStudioDeployPlanner().BuildPlan("DataFlow-Studio", ".").IsOk.Should().BeTrue();
    }

    [Fact]
    public void BuildPlan_fails_for_an_unknown_project()
    {
        var result = new DataflowStudioDeployPlanner().BuildPlan("nope", ".");
        result.IsFail.Should().BeTrue();
        result.Error.Should().Contain("unknown project");
    }

    [Fact]
    public void BuildPlan_fails_when_the_project_is_empty()
    {
        new DataflowStudioDeployPlanner().BuildPlan("", ".").IsFail.Should().BeTrue();
    }
}
