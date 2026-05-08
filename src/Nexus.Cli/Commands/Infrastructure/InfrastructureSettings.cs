using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

public abstract class InfrastructureSettingsBase : CommandSettings
{
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human table view.")]
    public bool Json { get; set; }

    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }
}

public sealed class InfrastructureListSettings : InfrastructureSettingsBase;

public sealed class InfrastructureStatusSettings : InfrastructureSettingsBase
{
    [CommandArgument(0, "<cluster>")]
    [Description("Cluster name as listed in vms.yaml (e.g. foundation, swarm).")]
    public string Cluster { get; set; } = "";

    [CommandOption("-n|--node")]
    [Description("Restrict to a single node name within the cluster.")]
    public string? Node { get; set; }
}
