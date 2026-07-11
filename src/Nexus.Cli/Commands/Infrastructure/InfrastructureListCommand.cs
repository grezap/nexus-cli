using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

/// <summary>Implements <c>infra list</c>: enumerates every VM declared in vms.yaml with its live runtime state.</summary>
public sealed class InfrastructureListCommand : AsyncCommand<InfrastructureListSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        InfrastructureListSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        using var bootstrapper = new InfrastructureBootstrapper();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var service = bootstrapper.BuildService();
        var rows = await service.ListAsync(cts.Token).ConfigureAwait(false);
        if (rows.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {rows.Error}");
            return 2;
        }

        if (settings.Json)
            InfrastructureRender.EmitListJson(rows.Value!);
        else
            InfrastructureRender.EmitListHuman(rows.Value!);

        return 0;
    }
}
