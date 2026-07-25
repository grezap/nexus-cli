using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Deploy;

/// <summary>Settings for <c>deploy &lt;project&gt;</c>: plan (default) or execute a project's end-to-end deploy.</summary>
public sealed class DeploySettings : CommandSettings
{
    /// <summary>The application project to deploy (currently <c>dataflow-studio</c>).</summary>
    [CommandArgument(0, "<project>")]
    [Description("Application project to deploy (e.g. dataflow-studio).")]
    public string Project { get; set; } = string.Empty;

    /// <summary>The project repo working copy the deploy steps run from (defaults to the current directory).</summary>
    [CommandOption("--path <DIR>")]
    [Description("The project repo working copy the steps run from (default: current directory).")]
    public string? RepoPath { get; set; }

    /// <summary>Execute the plan instead of only printing it (dry-run is the default).</summary>
    [CommandOption("--execute")]
    [Description("Execute the plan (build + migrate + deploy). Default is a dry-run that only prints the plan.")]
    public bool Execute { get; set; }

    /// <summary>Confirm execution; required with <c>--execute</c>.</summary>
    [CommandOption("--yes")]
    [Description("Confirm execution (required with --execute).")]
    public bool Yes { get; set; }

    /// <summary>Emit JSON to stdout instead of the human view.</summary>
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human view.")]
    public bool Json { get; set; }

    /// <summary>Disable ANSI color in the human view.</summary>
    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }
}
