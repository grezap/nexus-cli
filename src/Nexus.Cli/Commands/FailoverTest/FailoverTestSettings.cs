using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.FailoverTest;

/// <summary>Shared base carrying the <c>--json</c> / <c>--no-color</c> / <c>--yes</c> flags for the Swarm failover-test verbs.</summary>
public abstract class FailoverTestSettingsBase : CommandSettings
{
    /// <summary>Emit JSON to stdout instead of the human view.</summary>
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human view.")]
    public bool Json { get; set; }

    /// <summary>Disable ANSI color in the human view.</summary>
    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }

    /// <summary>Skip the interactive confirmation prompt for this destructive operation.</summary>
    [CommandOption("--yes")]
    [Description("Skip the interactive confirmation prompt (destructive op).")]
    public bool Yes { get; set; }
}

/// <summary>Settings for the <c>failover-test consul-leader</c> verb.</summary>
public sealed class FailoverTestConsulLeaderSettings : FailoverTestSettingsBase
{
    /// <summary>Assert the current Consul leader is NAME before injecting failure; abort otherwise.</summary>
    [CommandOption("--node <NAME>")]
    [Description("Only proceed if the current Consul leader is NAME; abort otherwise. Use to assert which node you expect to be the leader before injecting failure.")]
    public string? Node { get; set; }
}

/// <summary>Settings for the <c>failover-test nomad-leader</c> verb.</summary>
public sealed class FailoverTestNomadLeaderSettings : FailoverTestSettingsBase
{
    /// <summary>Assert the current Nomad leader is NAME before injecting failure; abort otherwise.</summary>
    [CommandOption("--node <NAME>")]
    [Description("Only proceed if the current Nomad leader is NAME; abort otherwise. Use to assert which node you expect to be the leader before injecting failure.")]
    public string? Node { get; set; }
}

/// <summary>Settings for the <c>failover-test swarm-manager</c> verb.</summary>
public sealed class FailoverTestSwarmManagerSettings : FailoverTestSettingsBase
{
    /// <summary>Assert the current Docker Swarm raft leader is NAME before suspending its VM; abort otherwise.</summary>
    [CommandOption("--node <NAME>")]
    [Description("Only proceed if the current Docker Swarm raft leader is NAME; abort otherwise. Use to assert which manager you expect to be the leader before vmrun-suspending its VM.")]
    public string? Node { get; set; }
}
