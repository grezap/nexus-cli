using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

/// <summary>Implements <c>infra status &lt;cluster&gt;</c>: reports the runtime state of a cluster's VMs (optionally a single node).</summary>
public sealed class InfrastructureStatusCommand : AsyncCommand<InfrastructureStatusSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        InfrastructureStatusSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        using var bootstrapper = new InfrastructureBootstrapper();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var service = bootstrapper.BuildService();
        var rows = await service.StatusAsync(settings.Cluster, settings.Node, cts.Token).ConfigureAwait(false);
        if (rows.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {rows.Error}");
            return 2;
        }

        if (settings.Json)
            InfrastructureRender.EmitStatusJson(settings.Cluster, settings.Node, rows.Value!);
        else
            InfrastructureRender.EmitStatusHuman(settings.Cluster, settings.Node, rows.Value!);

        return 0;
    }
}
