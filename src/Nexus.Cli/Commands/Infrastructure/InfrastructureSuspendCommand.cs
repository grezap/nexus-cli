using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

public sealed class InfrastructureSuspendCommand : AsyncCommand<InfrastructureSuspendSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, InfrastructureSuspendSettings settings)
        => InfrastructureMutate.RunAsync(
            "suspend",
            settings,
            (svc, ct) => svc.SuspendAsync(settings.Cluster, settings.Node, ct));
}
