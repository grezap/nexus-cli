using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

/// <summary>Implements <c>backup restore &lt;cluster&gt; &lt;backup-id&gt;</c>: restores a cluster from a prior backup (optionally point-in-time). Destructive; guarded by <c>--yes</c>.</summary>
public sealed class ClusterBackupRestoreCommand : AsyncCommand<ClusterBackupRestoreSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterBackupRestoreSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine($"[red]DESTRUCTIVE OP:[/] restore backup [bold]{Markup.Escape(settings.BackupId)}[/] onto cluster [bold]{Markup.Escape(settings.Cluster)}[/]. Existing data will be overwritten.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] restore requires interactive confirmation; pass --yes for non-interactive use.");
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
            cts.CancelAfter(TimeSpan.FromMinutes(60));
            var request = new RestoreRequest(settings.BackupId, settings.At, settings.ConfirmDestructive);
            var r = await adapterResult.Value!.BackupRestoreAsync(request, cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            if (settings.Json) ClusterRender.EmitRestoreJson(r.Value!);
            else ClusterRender.EmitRestoreHuman(r.Value!);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
