using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

public sealed class ClusterScaleOutAddCommand : AsyncCommand<ClusterScaleOutAddSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterScaleOutAddSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (string.IsNullOrWhiteSpace(settings.Role))
        {
            AnsiConsole.MarkupLine("[red]error:[/] --role <ROLE> is required (cluster-specific: primary, replica, broker, controller, follower, backend, ...).");
            return 2;
        }

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine($"[yellow]Stateful op:[/] add {settings.Count} node(s) of role [bold]{Markup.Escape(settings.Role)}[/] to cluster [bold]{Markup.Escape(settings.Cluster)}[/].");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] scale-out add requires interactive confirmation; pass --yes for non-interactive use.");
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
            var request = new ScaleOutAddRequest(settings.Role, settings.Count, settings.Shard);
            var r = await adapterResult.Value!.ScaleOutAddAsync(request, cts.Token).ConfigureAwait(false);
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
