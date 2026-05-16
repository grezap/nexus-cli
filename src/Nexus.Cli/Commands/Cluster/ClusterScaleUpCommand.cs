using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

public sealed class ClusterScaleUpCommand : AsyncCommand<ClusterScaleUpSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterScaleUpSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (!settings.Cpu.HasValue && !settings.RamMb.HasValue && !settings.DiskGb.HasValue)
        {
            AnsiConsole.MarkupLine("[red]error:[/] at least one of --cpu / --ram / --disk is required.");
            return 2;
        }

        if (!settings.Yes)
        {
            var changes = string.Join(", ",
                new[]
                {
                    settings.Cpu.HasValue ? $"cpu={settings.Cpu.Value}" : null,
                    settings.RamMb.HasValue ? $"ram={settings.RamMb.Value} MB" : null,
                    settings.DiskGb.HasValue ? $"disk={settings.DiskGb.Value} GB" : null,
                }.Where(s => s is not null));
            AnsiConsole.MarkupLine($"[yellow]Stateful op:[/] resize VM [bold]{Markup.Escape(settings.Vm)}[/] -> {changes}.");
            AnsiConsole.MarkupLine("This will power off the VM, edit its .vmx, and restart it. Disk grows are guest-side post-start.");
            if (settings.ForcePrimary)
                AnsiConsole.MarkupLine("[yellow]--force-primary[/] is set: cluster adapter's refusal will be overridden.");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("[red]aborted:[/] scale-up requires interactive confirmation; pass --yes for non-interactive use.");
                return 3;
            }
            if (!AnsiConsole.Confirm("Proceed?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]aborted by user.[/]");
                return 3;
            }
        }

        var registry = ClusterBootstrapper.BuildRegistry();
        var resizer = ClusterBootstrapper.BuildVmResizer(registry);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            var request = new ScaleUpRequest(settings.Vm, settings.Cpu, settings.RamMb, settings.DiskGb, settings.ForcePrimary);
            var r = await resizer.ScaleUpAsync(request, cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            if (settings.Json) ClusterRender.EmitScaleUpJson(r.Value!);
            else ClusterRender.EmitScaleUpHuman(r.Value!);
            return r.Value!.Outcome == "ok" ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
