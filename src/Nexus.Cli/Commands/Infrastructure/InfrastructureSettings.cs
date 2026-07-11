using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Infrastructure;

/// <summary>Shared base carrying the <c>--json</c> / <c>--no-color</c> flags for the infrastructure (VM lifecycle) verbs.</summary>
public abstract class InfrastructureSettingsBase : CommandSettings
{
    /// <summary>Emit JSON to stdout instead of the human table view.</summary>
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human table view.")]
    public bool Json { get; set; }

    /// <summary>Disable ANSI color in the human view.</summary>
    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }
}

/// <summary>Settings for the <c>infra list</c> verb (all VMs declared in vms.yaml).</summary>
public sealed class InfrastructureListSettings : InfrastructureSettingsBase;

/// <summary>Settings for the <c>infra status</c> verb (per-cluster or per-node VM state).</summary>
public sealed class InfrastructureStatusSettings : InfrastructureSettingsBase
{
    /// <summary>Cluster name as listed in vms.yaml (e.g. foundation, swarm).</summary>
    [CommandArgument(0, "<cluster>")]
    [Description("Cluster name as listed in vms.yaml (e.g. foundation, swarm).")]
    public string Cluster { get; set; } = "";

    /// <summary>Restrict to a single node name within the cluster.</summary>
    [CommandOption("-n|--node")]
    [Description("Restrict to a single node name within the cluster.")]
    public string? Node { get; set; }
}

/// <summary>Shared base for the mutating infrastructure verbs (suspend/resume): adds cluster/node targeting plus <c>--yes</c>.</summary>
public abstract class InfrastructureMutationSettingsBase : InfrastructureSettingsBase
{
    /// <summary>Cluster name as listed in vms.yaml.</summary>
    [CommandArgument(0, "<cluster>")]
    [Description("Cluster name as listed in vms.yaml.")]
    public string Cluster { get; set; } = "";

    /// <summary>Restrict the operation to a single node within the cluster.</summary>
    [CommandOption("-n|--node")]
    [Description("Restrict to a single node within the cluster.")]
    public string? Node { get; set; }

    /// <summary>Skip the interactive confirmation prompt.</summary>
    [CommandOption("-y|--yes")]
    [Description("Skip the interactive confirmation prompt.")]
    public bool Yes { get; set; }
}

/// <summary>Settings for the <c>infra suspend</c> verb.</summary>
public sealed class InfrastructureSuspendSettings : InfrastructureMutationSettingsBase;

/// <summary>Settings for the <c>infra resume</c> verb.</summary>
public sealed class InfrastructureResumeSettings : InfrastructureMutationSettingsBase;
