using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.FailoverTest;

public abstract class FailoverTestSettingsBase : CommandSettings
{
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human view.")]
    public bool Json { get; set; }

    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }

    [CommandOption("--yes")]
    [Description("Skip the interactive confirmation prompt (destructive op).")]
    public bool Yes { get; set; }
}

public sealed class FailoverTestConsulLeaderSettings : FailoverTestSettingsBase
{
    [CommandOption("--node <NAME>")]
    [Description("Only proceed if the current Consul leader is NAME; abort otherwise. Use to assert which node you expect to be the leader before injecting failure.")]
    public string? Node { get; set; }
}
