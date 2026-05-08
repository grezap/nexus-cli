using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;

namespace Nexus.Cli.Commands.Infrastructure;

internal static class InfrastructureMutate
{
    public static async Task<int> RunAsync(
        string verb,
        InfrastructureMutationSettingsBase settings,
        Func<IInfrastructureService, CancellationToken, Task<Result<IReadOnlyList<OpResult>>>> action)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        using var bootstrapper = new InfrastructureBootstrapper();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var service = bootstrapper.BuildService();

        var preview = await service.StatusAsync(settings.Cluster, settings.Node, cts.Token).ConfigureAwait(false);
        if (preview.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {preview.Error}");
            return 2;
        }

        var actionable = preview.Value!.Where(s => s.State != VmRuntimeState.Missing).ToList();
        if (actionable.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[grey]nothing to {verb}[/] in {settings.Cluster} (every row is missing or unknown).");
            return 0;
        }

        if (!settings.Yes)
        {
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLineInterpolated(
                    $"[yellow]aborted:[/] {verb} of {settings.Cluster} requires interactive confirmation; pass --yes for non-interactive use.");
                return 3;
            }
            AnsiConsole.MarkupLineInterpolated(
                $"[bold]About to {verb}:[/] {settings.Cluster} ({actionable.Count} VMs)");
            foreach (var s in actionable)
            {
                AnsiConsole.MarkupLine(
                    $"  - {Markup.Escape(s.Node.Name)} {InfrastructureRender.ColorState(s.State)}");
            }
            if (!AnsiConsole.Confirm($"Proceed with {verb}?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[yellow]aborted by user.[/]");
                return 3;
            }
        }

        var ops = await action(service, cts.Token).ConfigureAwait(false);
        if (ops.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ops.Error}");
            return 2;
        }

        if (settings.Json)
            InfrastructureRender.EmitOpsJson(verb, settings.Cluster, ops.Value!);
        else
            InfrastructureRender.EmitOpsHuman(verb, settings.Cluster, ops.Value!);

        return ops.Value!.Any(o => !o.Success) ? 1 : 0;
    }
}
