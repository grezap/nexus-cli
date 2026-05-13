using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands;

internal abstract class StubCommandBase<TSettings> : Command<TSettings>
    where TSettings : CommandSettings
{
    protected abstract string Name { get; }
    protected abstract string PlannedVersion { get; }

    protected override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]{Name}[/] is not yet implemented — planned for [bold]{PlannedVersion}[/]. See the roadmap in README.md.");
        return 0;
    }
}

public sealed class KafkaFailoverSettings : CommandSettings
{
    [CommandOption("--cluster")]
    [Description("Source cluster (east|west)")]
    public string? Cluster { get; set; }
}

internal sealed class KafkaFailoverCommand : StubCommandBase<KafkaFailoverSettings>
{
    protected override string Name => "kafka failover";
    protected override string PlannedVersion => "v0.5.0 (paired with Phase 0.H)";
}

