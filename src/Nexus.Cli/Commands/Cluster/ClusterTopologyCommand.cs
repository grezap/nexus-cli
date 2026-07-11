using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

/// <summary>Implements <c>topology &lt;cluster&gt;</c>: snapshots a cluster's membership/roles, optionally re-polling in a <c>--watch</c> loop.</summary>
public sealed class ClusterTopologyCommand : AsyncCommand<ClusterTopologySettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterTopologySettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        var registry = ClusterBootstrapper.BuildRegistry();
        var adapterResult = registry.GetAdapter(settings.Cluster);
        if (adapterResult.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {adapterResult.Error}");
            return 2;
        }
        var adapter = adapterResult.Value!;

        var watchInterval = TimeSpan.FromSeconds(2);
        try
        {
            while (true)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(2));
                var r = await adapter.TopologyAsync(cts.Token).ConfigureAwait(false);
                if (r.IsFail)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                    return 2;
                }
                if (settings.Json) ClusterRender.EmitTopologyJson(r.Value!);
                else
                {
                    if (settings.Watch) AnsiConsole.Clear();
                    ClusterRender.EmitTopologyHuman(r.Value!);
                }
                if (!settings.Watch) return 0;
                await Task.Delay(watchInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return 0;       // ctrl-C while watching is the normal exit
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
