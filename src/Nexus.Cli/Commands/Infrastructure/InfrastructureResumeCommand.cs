using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

public sealed class InfrastructureResumeCommand : AsyncCommand<InfrastructureResumeSettings>
{
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
