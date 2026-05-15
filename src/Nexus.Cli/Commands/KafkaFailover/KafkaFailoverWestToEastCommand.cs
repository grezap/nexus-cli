using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.KafkaFailover;

public sealed class KafkaFailoverWestToEastCommand : AsyncCommand<KafkaFailoverWestToEastSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        KafkaFailoverWestToEastSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine("[yellow]Destructive op (HOST-LEVEL):[/] vmrun-suspend the entire kafka-west cluster (3 brokers), prove kafka-east keeps serving via a produce/consume round-trip, then vmrun-resume.");
            AnsiConsole.MarkupLine("Suspending all 3 source brokers simulates a region loss. The east cluster runs an independent KRaft quorum and should keep serving uninterrupted -- this is the more demo-worthy direction since the ecosystem services (Schema Registry, Connect, ksqlDB, REST Proxy) are all east clients and remain unaffected.");
            AnsiConsole.MarkupLine("Recovery includes a ~30-90 second cold boot of the 3 west brokers + KRaft quorum reform; total scenario time ~3-5 minutes.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] kafka failover west-to-east requires interactive confirmation; pass --yes for non-interactive use.");
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
            var r = await service.RunAsync(KafkaFailoverDirection.WestToEast, cts.Token).ConfigureAwait(false);
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
