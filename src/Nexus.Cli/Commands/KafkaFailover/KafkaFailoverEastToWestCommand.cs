using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.KafkaFailover;

public sealed class KafkaFailoverEastToWestCommand : AsyncCommand<KafkaFailoverEastToWestSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        KafkaFailoverEastToWestSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine("[yellow]Destructive op (HOST-LEVEL):[/] vmrun-suspend the entire kafka-east cluster (3 brokers), prove kafka-west keeps serving via a produce/consume round-trip, then vmrun-resume.");
            AnsiConsole.MarkupLine("Suspending all 3 source brokers simulates a region loss. The west cluster runs an independent KRaft quorum and should keep serving uninterrupted.");
            AnsiConsole.MarkupLine("Recovery includes a ~30-90 second cold boot of the 3 east brokers + KRaft quorum reform; total scenario time ~3-5 minutes.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] kafka failover east-to-west requires interactive confirmation; pass --yes for non-interactive use.");
                return 3;
            }
            if (!AnsiConsole.Confirm("Proceed?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]aborted by user.[/]");
                return 3;
            }
        }

        KafkaFailoverReport report;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(8));
            var service = KafkaFailoverBootstrapper.Build();
            var r = await service.RunAsync(KafkaFailoverDirection.EastToWest, cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            report = r.Value!;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }

        if (settings.Json) KafkaFailoverRender.EmitJson(report);
        else KafkaFailoverRender.EmitHuman(report);

        if (!report.TargetServedAfterFailure) return 1;
        if (report.Recovery == KafkaFailoverRecoveryStatus.RecoveryFailed) return 2;
        return 0;
    }
}
