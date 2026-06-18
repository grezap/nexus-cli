using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

/// <summary>
/// <c>recover-ha &lt;cluster&gt;</c> — the bespoke high-availability recovery verb
/// (nexus-cli v0.8.1, ADR-0022). Only clusters implementing
/// <see cref="IRecoverableCluster"/> support it; today that is the foundation
/// <c>vault</c> cluster, whose adapter wraps the boot-race recovery
/// (unseal vault-transit from the operator Shamir key file → restart vault-1/2/3
/// → poll until unsealed). For any other cluster the command returns a graceful,
/// actionable "not applicable".
/// </summary>
public sealed class RecoverHaCommand : AsyncCommand<ClusterRecoverHaSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterRecoverHaSettings settings,
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

        if (adapterResult.Value is not IRecoverableCluster recoverable)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]not applicable:[/] cluster [bold]{settings.Cluster}[/] has no recover-ha step. recover-ha wraps the Vault auto-unseal boot-race recovery and is implemented only for the foundation [bold]vault[/] cluster.");
            return 2;
        }

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine($"[yellow]Stateful op:[/] drive HA recovery on cluster [bold]{Markup.Escape(settings.Cluster)}[/] — unseal vault-transit from the operator Shamir key file, then restart + poll the HA nodes. Idempotent (already-unsealed = no-op).");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] recover-ha requires interactive confirmation; pass --yes for non-interactive use.");
                return 3;
            }
            if (!AnsiConsole.Confirm("Proceed?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]aborted by user.[/]");
                return 3;
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));
            var r = await recoverable.RecoverHaAsync(cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            if (settings.Json) ClusterRender.EmitRecoverHaJson(r.Value!);
            else ClusterRender.EmitRecoverHaHuman(r.Value!);
            return r.Value!.AllUnsealed ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
