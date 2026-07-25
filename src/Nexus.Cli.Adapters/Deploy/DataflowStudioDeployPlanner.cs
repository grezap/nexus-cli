using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Deploy;

/// <summary>
/// Builds the end-to-end deploy plan for the <c>dataflow-studio</c> application project: build the
/// container images, run the three schema migrations (OltpDb / StarRocks / ClickHouse), then deploy the
/// always-on Api tier to Kubernetes. The steps mirror the project's committed <c>deploy/</c> recipes so
/// the plan is exactly what an operator would run by hand — nothing hidden.
/// </summary>
public sealed class DataflowStudioDeployPlanner : IDeployPlanner
{
    /// <summary>The only project this planner knows how to deploy.</summary>
    public const string ProjectId = "dataflow-studio";

    private static readonly string[] Known = [ProjectId];

    /// <inheritdoc />
    public Result<DeployPlan> BuildPlan(string project, string repoPath)
    {
        if (string.IsNullOrWhiteSpace(project))
            return Result.Fail<DeployPlan>("project is required (e.g. dataflow-studio).");

        if (!string.Equals(project, ProjectId, StringComparison.OrdinalIgnoreCase))
            return Result.Fail<DeployPlan>($"unknown project '{project}'. Known projects: {string.Join(", ", Known)}.");

        var steps = new List<DeployStep>
        {
            new(
                "build-images",
                "docker compose -f deploy/docker-compose.yml build",
                "Build the container images (Api + pipeline jobs) from deploy/Dockerfile."),
            new(
                "migrate-oltp",
                "docker compose -f deploy/docker-compose.yml --profile migrate run --rm migrate-oltp",
                "Apply the OltpDb FluentMigrator migrations (reversible; the E1 gate)."),
            new(
                "migrate-starrocks",
                "docker compose -f deploy/docker-compose.yml --profile migrate run --rm migrate-starrocks",
                "Apply the StarRocks DWH DbUp migrations (forward-only, idempotent)."),
            new(
                "migrate-clickhouse",
                "docker compose -f deploy/docker-compose.yml --profile migrate run --rm migrate-clickhouse",
                "Apply the ClickHouse analytics DbUp migrations (forward-only, idempotent)."),
            new(
                "deploy-k8s",
                "kubectl apply -k deploy/k8s",
                "Deploy the always-on Api tier (Deployment + Service) to Kubernetes."),
        };

        return Result.Ok(new DeployPlan(ProjectId, repoPath, steps));
    }
}
