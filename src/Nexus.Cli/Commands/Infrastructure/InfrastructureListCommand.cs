using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

public sealed class InfrastructureListCommand : AsyncCommand<InfrastructureListSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InfrastructureListSettings settings)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        using var bootstrapper = new InfrastructureBootstrapper();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var service = bootstrapper.BuildService();
        var rows = await service.ListAsync(cts.Token).ConfigureAwait(false);
        if (rows.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {rows.Error}");
            return 2;
        }

        if (settings.Json)
            InfrastructureRender.EmitListJson(rows.Value!);
        else
            InfrastructureRender.EmitListHuman(rows.Value!);

        return 0;
    }
}
