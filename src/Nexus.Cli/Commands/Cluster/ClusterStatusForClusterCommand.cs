using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

/// <summary>Implements <c>cluster-status &lt;cluster&gt;</c>: probes a single data-tier cluster via its adapter and renders its role/health topology.</summary>
public sealed class ClusterStatusForClusterCommand : AsyncCommand<ClusterStatusForClusterSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterStatusForClusterSettings settings,
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

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            var r = await adapterResult.Value!.GetStatusAsync(cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            if (settings.Json) ClusterRender.EmitClusterStatusJson(r.Value!);
            else ClusterRender.EmitClusterStatusHuman(r.Value!);
            return r.Value!.OverallHealth == "red" ? 1 : 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
