using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

public sealed class InfrastructureResumeCommand : AsyncCommand<InfrastructureResumeSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, InfrastructureResumeSettings settings)
        => InfrastructureMutate.RunAsync(
            "resume",
            settings,
            (svc, ct) => svc.ResumeAsync(settings.Cluster, settings.Node, ct));
}
