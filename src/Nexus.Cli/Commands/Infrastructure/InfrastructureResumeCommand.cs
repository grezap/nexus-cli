using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

/// <summary>Implements <c>infra resume &lt;cluster&gt;</c>: vmrun-resumes a cluster's VMs (optionally a single node) after confirmation.</summary>
public sealed class InfrastructureResumeCommand : AsyncCommand<InfrastructureResumeSettings>
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(
        CommandContext context,
        InfrastructureResumeSettings settings,
        CancellationToken cancellationToken)
        => InfrastructureMutate.RunAsync(
            "resume",
            settings,
            (svc, ct) => svc.ResumeAsync(settings.Cluster, settings.Node, ct),
            cancellationToken);
}
