using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.FailoverTest;

/// <summary>Implements <c>failover-test nomad-leader</c>: stops the current Nomad leader, measures raft re-election RTO, then auto-recovers. Destructive; guarded by <c>--yes</c>.</summary>
public sealed class FailoverTestNomadLeaderCommand : AsyncCommand<FailoverTestNomadLeaderSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        FailoverTestNomadLeaderSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine("[yellow]Destructive op:[/] stop the current Nomad leader, measure RTO of raft re-election, then auto-recover.");
            AnsiConsole.MarkupLine("Vault Raft + Consul are unaffected (separate clusters). Running Nomad allocations are unaffected; only the scheduler is briefly leaderless.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] nomad-leader failover requires interactive confirmation; pass --yes for non-interactive use.");
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
            var r = await service.RunNomadLeaderAsync(settings.Node, cts.Token).ConfigureAwait(false);
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

        // Same exit-code policy as consul-leader:
        //   0  new leader observed within deadline + recovery ok
        //   1  no new leader observed within deadline
        //   2  recovery failed
        if (report.NewLeader is null) return 1;
        if (report.Recovery == FailoverRecoveryStatus.RecoveryFailed) return 2;
        return 0;
    }
}
