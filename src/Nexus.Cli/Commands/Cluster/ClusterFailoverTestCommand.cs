using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

/// <summary>Implements <c>failover-test &lt;cluster&gt;</c>: drives a controlled primary/leader failover on a cluster and measures RTO. Destructive; guarded by <c>--yes</c>.</summary>
public sealed class ClusterFailoverTestCommand : AsyncCommand<ClusterFailoverTestSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterFailoverTestSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine($"[yellow]Destructive op:[/] drive a failover scenario on cluster [bold]{Markup.Escape(settings.Cluster)}[/] and measure RTO.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] failover requires interactive confirmation; pass --yes for non-interactive use.");
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
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            var request = new FailoverRequest(
                TargetNode: settings.Node,
                Direction: settings.Direction,
                NoRecover: settings.NoRecover);
            var r = await adapterResult.Value!.FailoverAsync(request, cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            if (settings.Json) ClusterRender.EmitFailoverJson(r.Value!);
            else ClusterRender.EmitFailoverHuman(r.Value!);
            return r.Value!.NewPrimary is null ? 1 : 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
