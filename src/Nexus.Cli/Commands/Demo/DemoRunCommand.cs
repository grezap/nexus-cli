using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Demo;

public sealed class DemoRunCommand : AsyncCommand<DemoRunSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        DemoRunSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        if (string.IsNullOrWhiteSpace(settings.DemoId))
        {
            AnsiConsole.MarkupLine("[red]error:[/] demo id is required. Use `nexus demo list` to see available demos.");
            return 2;
        }

        using var bootstrapper = new DemoBootstrapper();
        var catalog = DemoBootstrapper.BuildCatalog();
        var spec = catalog.GetDemo(settings.DemoId);
        if (spec.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {spec.Error}");
            return 2;
        }

        var runner = DemoBootstrapper.BuildRunner();
        DemoRunReport report;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(15));
            var r = await runner.RunAsync(spec.Value!, cts.Token).ConfigureAwait(false);
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

        if (settings.Json) DemoRender.EmitRunJson(report);
        else DemoRender.EmitRunHuman(report);

        return report.Status switch
        {
            DemoStatus.Ok => 0,
            DemoStatus.StepFailed => 1,
            DemoStatus.Aborted => 3,
            _ => 2
        };
    }
}
