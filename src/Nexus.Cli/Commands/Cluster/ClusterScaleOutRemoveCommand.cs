using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

public sealed class ClusterScaleOutRemoveCommand : AsyncCommand<ClusterScaleOutRemoveSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterScaleOutRemoveSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            var drainNote = settings.Force ? "[red]NO DRAIN -- data loss risk[/]" : "drain first";
            AnsiConsole.MarkupLine($"[yellow]Destructive op:[/] remove node [bold]{Markup.Escape(settings.Node)}[/] from cluster [bold]{Markup.Escape(settings.Cluster)}[/] ({drainNote}).");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] scale-out remove requires interactive confirmation; pass --yes for non-interactive use.");
                return 3;
            }
            if (!AnsiConsole.Confirm("Proceed?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]aborted by user.[/]");
                return 3;
            }
        }

        var registry = ClusterBootstrapper.BuildRegistry();
        var adapterResult = registry.GetAdapter(settings.Cluster);
        if (adapterResult.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {adapterResult.Error}");
            return 2;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(15));
            var request = new ScaleOutRemoveRequest(settings.Node, Drain: !settings.Force);
            var r = await adapterResult.Value!.ScaleOutRemoveAsync(request, cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            if (settings.Json) ClusterRender.EmitScaleOutJson(r.Value!);
            else ClusterRender.EmitScaleOutHuman(r.Value!);
            return r.Value!.Outcome == "ok" ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
