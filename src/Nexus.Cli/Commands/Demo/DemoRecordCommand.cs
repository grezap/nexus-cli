using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Demo;

/// <summary>Implements <c>demo record &lt;demo-id&gt;</c>: writes a VHS .tape for a demo and renders it to a GIF when <c>vhs</c> is available.</summary>
public sealed class DemoRecordCommand : AsyncCommand<DemoRecordSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        DemoRecordSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        if (string.IsNullOrWhiteSpace(settings.DemoId))
        {
            AnsiConsole.MarkupLine("[red]error:[/] demo id is required. Use `nexus demo list` to see available demos.");
            return 2;
        }

        var outDir = string.IsNullOrWhiteSpace(settings.OutputDir)
            ? Path.Combine(Environment.CurrentDirectory, "demos-out")
            : settings.OutputDir;

        using var bootstrapper = new DemoBootstrapper();
        var catalog = DemoBootstrapper.BuildCatalog();
        var spec = catalog.GetDemo(settings.DemoId);
        if (spec.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {spec.Error}");
            return 2;
        }

        var runner = DemoBootstrapper.BuildRunner();
        DemoRecordReport report;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(20));
            var r = await runner.RecordAsync(spec.Value!, outDir, cts.Token).ConfigureAwait(false);
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

        if (settings.Json) DemoRender.EmitRecordJson(report);
        else DemoRender.EmitRecordHuman(report);

        // Exit 0 if a GIF was produced; 1 if only the tape exists (vhs missing);
        // 2 only if the underlying RecordAsync actually failed.
        return report.OutputFilePath is not null ? 0 : 1;
    }
}
