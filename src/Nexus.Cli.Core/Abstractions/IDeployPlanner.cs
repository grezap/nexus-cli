using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>Builds the ordered, reviewable deploy plan for an application project (build → migrate → deploy).</summary>
public interface IDeployPlanner
{
    /// <summary>Builds the deploy plan for <paramref name="project"/> rooted at <paramref name="repoPath"/>; fails on an unknown project.</summary>
    Result<DeployPlan> BuildPlan(string project, string repoPath);
}

/// <summary>Executes a deploy plan's steps in order (shelling out), stopping on the first failure.</summary>
public interface IDeployRunner
{
    /// <summary>Runs every step of <paramref name="plan"/> from its repo path; stops on the first non-zero exit.</summary>
    Task<Result<DeployReport>> ExecuteAsync(DeployPlan plan, CancellationToken cancellationToken);
}
