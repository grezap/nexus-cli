using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands;

internal abstract class StubCommandBase<TSettings> : Command<TSettings>
    where TSettings : CommandSettings
{
    protected abstract string Name { get; }
    protected abstract string PlannedVersion { get; }

    public override int Execute(CommandContext context, TSettings settings)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]{Name}[/] is not implemented in v0.1.0 — planned for [bold]{PlannedVersion}[/]. See the roadmap in README.md.");
        return 0;
    }
}

public sealed class FailoverTestSettings : CommandSettings
{
    [CommandArgument(0, "[scenario]")]
    [Description("Scenario name (consul-leader|nomad-leader|swarm-manager)")]
    public string? Scenario { get; set; }
}

internal sealed class FailoverTestCommand : StubCommandBase<FailoverTestSettings>
{
    protected override string Name => "failover-test";
    protected override string PlannedVersion => "v0.3.0";
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

public sealed class DemoRunSettings : CommandSettings
{
    [CommandArgument(0, "<demo-id>")]
    [Description("DEMO-NN-* identifier")]
    public string? DemoId { get; set; }
}

internal sealed class DemoRunCommand : StubCommandBase<DemoRunSettings>
{
    protected override string Name => "demo run";
    protected override string PlannedVersion => "v0.4.0";
}

public sealed class DemoRecordSettings : CommandSettings
{
    [CommandArgument(0, "[demo-id]")]
    [Description("DEMO-NN-* identifier (omit for --all)")]
    public string? DemoId { get; set; }

    [CommandOption("--all")]
    [Description("Record every demo defined in the parent project's docs/demos/.")]
    public bool All { get; set; }
}

internal sealed class DemoRecordCommand : StubCommandBase<DemoRecordSettings>
{
    protected override string Name => "demo record";
    protected override string PlannedVersion => "v0.4.0";
}
