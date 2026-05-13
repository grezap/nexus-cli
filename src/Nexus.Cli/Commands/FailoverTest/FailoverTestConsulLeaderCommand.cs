using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.FailoverTest;

public sealed class FailoverTestConsulLeaderCommand : AsyncCommand<FailoverTestConsulLeaderSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        FailoverTestConsulLeaderSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine("[yellow]Destructive op:[/] stop the current Consul leader, measure RTO of raft re-election, then auto-recover.");
            AnsiConsole.MarkupLine("Vault Raft keeps quorum on the other 2 managers throughout the suspend window.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] consul-leader failover requires interactive confirmation; pass --yes for non-interactive use.");
                return 3;
            }
            if (!AnsiConsole.Confirm("Proceed?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]aborted by user.[/]");
                return 3;
            }
        }

        using var bootstrapper = new FailoverTestBootstrapper(
            new Adapters.Vault.VaultTokenResolver(new Adapters.Vault.ProcessEnvironmentReader()));

        FailoverTestReport report;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            var service = await bootstrapper.BuildAsync(cts.Token).ConfigureAwait(false);
            var r = await service.RunConsulLeaderAsync(settings.Node, cts.Token).ConfigureAwait(false);
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

        if (settings.Json) FailoverTestRender.EmitJson(report);
        else FailoverTestRender.EmitHuman(report);

        // Exit code policy:
        //   0  new leader observed within deadline + recovery ok
        //   1  no new leader observed within deadline (RTO unbounded)
        //   2  recovery failed (cluster is one-manager-down; operator must run RecoveryHint)
        if (report.NewLeader is null) return 1;
        if (report.Recovery == FailoverRecoveryStatus.RecoveryFailed) return 2;
        return 0;
    }
}
