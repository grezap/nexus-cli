using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Demo;

/// <summary>Shared base carrying the <c>--json</c> / <c>--no-color</c> flags for the demo verbs.</summary>
public abstract class DemoSettingsBase : CommandSettings
{
    /// <summary>Emit JSON to stdout instead of the human view.</summary>
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human view.")]
    public bool Json { get; set; }

    /// <summary>Disable ANSI color in the human view.</summary>
    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }
}

/// <summary>Settings for the <c>demo list</c> verb.</summary>
public sealed class DemoListSettings : DemoSettingsBase
{
}

/// <summary>Settings for the <c>demo run</c> verb.</summary>
public sealed class DemoRunSettings : DemoSettingsBase
{
    /// <summary>DEMO-NN-* identifier to run; use <c>nexus demo list</c> to enumerate.</summary>
    [CommandArgument(0, "<demo-id>")]
    [Description("DEMO-NN-* identifier. Use `nexus demo list` to see available demos.")]
    public string? DemoId { get; set; }
}

/// <summary>Settings for the <c>demo record</c> verb.</summary>
public sealed class DemoRecordSettings : DemoSettingsBase
{
    /// <summary>DEMO-NN-* identifier to record (via VHS) to a GIF.</summary>
    [CommandArgument(0, "<demo-id>")]
    [Description("DEMO-NN-* identifier to record via VHS to a GIF.")]
    public string? DemoId { get; set; }

    /// <summary>Directory to write the .tape file and rendered GIF (default: ./demos-out).</summary>
    [CommandOption("--output-dir <DIR>")]
    [Description("Directory to write the .tape file and rendered GIF (default: ./demos-out).")]
    public string? OutputDir { get; set; }
}
