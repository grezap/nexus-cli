using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Demo;

/// <summary>Implements <c>demo list</c>: loads the demo catalog and renders the available DEMO-NN-* specs.</summary>
public sealed class DemoListCommand : AsyncCommand<DemoListSettings>
{
    /// <inheritdoc />
    protected override Task<int> ExecuteAsync(
        CommandContext context,
        DemoListSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        using var bootstrapper = new DemoBootstrapper();
        var catalog = DemoBootstrapper.BuildCatalog();
        var loaded = catalog.Load();
        if (loaded.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {loaded.Error}");
            return Task.FromResult(2);
        }

        if (settings.Json) DemoRender.EmitListJson(loaded.Value!.Values);
        else DemoRender.EmitListHuman(loaded.Value!.Values);
        return Task.FromResult(0);
    }
}
