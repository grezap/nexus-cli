using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Demo;

public abstract class DemoSettingsBase : CommandSettings
{
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human view.")]
    public bool Json { get; set; }

    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }
}

public sealed class DemoListSettings : DemoSettingsBase
{
}

public sealed class DemoRunSettings : DemoSettingsBase
{
    [CommandArgument(0, "<demo-id>")]
    [Description("DEMO-NN-* identifier. Use `nexus demo list` to see available demos.")]
    public string? DemoId { get; set; }
}

public sealed class DemoRecordSettings : DemoSettingsBase
{
    [CommandArgument(0, "<demo-id>")]
    [Description("DEMO-NN-* identifier to record via VHS to a GIF.")]
    public string? DemoId { get; set; }

    [CommandOption("--output-dir <DIR>")]
    [Description("Directory to write the .tape file and rendered GIF (default: ./demos-out).")]
    public string? OutputDir { get; set; }
}
