using Nexus.Cli.Adapters.Deploy;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// DI bootstrap for the <c>deploy</c> verb. No Vault dependency: the planner is pure and the runner
/// shells out to the project's committed <c>deploy/</c> recipes (docker compose / kubectl).
/// </summary>
public sealed class DeployBootstrapper
{
    /// <summary>Builds the deploy planner (the pure plan builder).</summary>
    public static IDeployPlanner BuildPlanner() => new DataflowStudioDeployPlanner();

    /// <summary>Builds the deploy runner (shells out to execute a plan's steps).</summary>
    public static IDeployRunner BuildRunner() => new DeployRunner();
}
