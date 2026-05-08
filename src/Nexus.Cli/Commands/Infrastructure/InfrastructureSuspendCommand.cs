using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

public sealed class InfrastructureSuspendCommand : AsyncCommand<InfrastructureSuspendSettings>
{
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
