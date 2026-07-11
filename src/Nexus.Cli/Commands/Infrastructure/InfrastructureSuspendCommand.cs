using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

/// <summary>Implements <c>infra suspend &lt;cluster&gt;</c>: vmrun-suspends a cluster's VMs (optionally a single node) after confirmation.</summary>
public sealed class InfrastructureSuspendCommand : AsyncCommand<InfrastructureSuspendSettings>
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(
        CommandContext context,
        InfrastructureSuspendSettings settings,
        CancellationToken cancellationToken)
        => InfrastructureMutate.RunAsync(
            "suspend",
            settings,
            (svc, ct) => svc.SuspendAsync(settings.Cluster, settings.Node, ct),
            cancellationToken);
}
