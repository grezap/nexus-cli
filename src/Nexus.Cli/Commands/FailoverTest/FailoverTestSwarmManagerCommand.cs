using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.FailoverTest;

/// <summary>Implements <c>failover-test swarm-manager</c>: vmrun-suspends the current Docker Swarm raft leader VM (a host-level outage), measures RTO, then vmrun-resumes. Destructive; guarded by <c>--yes</c>.</summary>
public sealed class FailoverTestSwarmManagerCommand : AsyncCommand<FailoverTestSwarmManagerSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        FailoverTestSwarmManagerSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine("[yellow]Destructive op (HOST-LEVEL):[/] vmrun-suspend the current Docker Swarm raft leader VM, measure RTO, then vmrun-resume.");
            AnsiConsole.MarkupLine("This is heavier than the consul/nomad scenarios -- the host outage kills Docker, Consul agent, Nomad agent, Portainer agent, and Vault Agent on that node simultaneously.");
            AnsiConsole.MarkupLine("Recovery includes a ~30-60 second VM cold boot; total scenario time ~2-3 minutes.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] swarm-manager failover requires interactive confirmation; pass --yes for non-interactive use.");
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
            // VM boot + Swarm rejoin needs more headroom than service restart.
            cts.CancelAfter(TimeSpan.FromMinutes(6));
            var service = await bootstrapper.BuildAsync(cts.Token).ConfigureAwait(false);
            var r = await service.RunSwarmManagerAsync(settings.Node, cts.Token).ConfigureAwait(false);
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

        if (report.NewLeader is null) return 1;
        if (report.Recovery == FailoverRecoveryStatus.RecoveryFailed) return 2;
        return 0;
    }
}
