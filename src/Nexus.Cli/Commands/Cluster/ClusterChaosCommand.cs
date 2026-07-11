using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

/// <summary>Implements <c>chaos &lt;cluster&gt; &lt;scenario&gt;</c>: injects a fault scenario for a bounded duration and reports whether the cluster recovered. Destructive; guarded by <c>--yes</c>.</summary>
public sealed class ClusterChaosCommand : AsyncCommand<ClusterChaosSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterChaosSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine($"[yellow]Chaos op:[/] inject scenario [bold]{Markup.Escape(settings.Scenario)}[/] into cluster [bold]{Markup.Escape(settings.Cluster)}[/] for {settings.Duration}s.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] chaos injection requires interactive confirmation; pass --yes for non-interactive use.");
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
            // chaos op is duration-bound; allow some buffer
            cts.CancelAfter(TimeSpan.FromSeconds(settings.Duration + 60));
            var scenario = new ChaosScenario(settings.Scenario, settings.Target, settings.Duration, settings.Intensity);
            var r = await adapterResult.Value!.ApplyChaosAsync(scenario, cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            if (settings.Json) ClusterRender.EmitChaosJson(r.Value!);
            else ClusterRender.EmitChaosHuman(r.Value!);
            return r.Value!.Recovered ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
