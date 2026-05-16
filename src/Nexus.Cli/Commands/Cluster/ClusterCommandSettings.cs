using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

// ===========================================================================
// Settings for the 13 cluster verb groups (ADR-0009 IClusterAdapter SPI).
// Each verb shares the --json / --no-color / --yes flags via the base class;
// per-verb settings add positional args and verb-specific options.
// ===========================================================================

public abstract class ClusterCommandSettingsBase : CommandSettings
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

/// <summary>Base for verbs that take a single positional &lt;cluster&gt; argument.</summary>
public abstract class ClusterTargetSettingsBase : ClusterCommandSettingsBase
{
    [CommandArgument(0, "<cluster>")]
    [Description("Cluster id (redis, mongo, percona, patroni, clickhouse, starrocks, sql-fci, sql-ag, kafka, ...).")]
    public string Cluster { get; set; } = string.Empty;
}

// --- cluster-status -------------------------------------------------------

public sealed class ClusterStatusForClusterSettings : ClusterTargetSettingsBase
{
}

// --- failover-test <cluster> ----------------------------------------------

public sealed class ClusterFailoverTestSettings : ClusterTargetSettingsBase
{
    [CommandOption("--node <NAME>")]
    [Description("Target node hostname (replica for redis, secondary for mongo, replica for ag, etc.). Adapter chooses if omitted.")]
    public string? Node { get; set; }

    [CommandOption("--direction <DIR>")]
    [Description("For directional failovers (kafka east-to-west / west-to-east). Ignored when not applicable.")]
    public string? Direction { get; set; }

    [CommandOption("--no-recover")]
    [Description("Leave the cluster in the failed-over state (skip auto-recovery).")]
    public bool NoRecover { get; set; }
}

// --- scale-out add <cluster> ----------------------------------------------

public sealed class ClusterScaleOutAddSettings : ClusterTargetSettingsBase
{
    [CommandOption("--role <ROLE>")]
    [Description("Node role (cluster-specific: primary, replica, broker, controller, follower, backend, ...).")]
    public string Role { get; set; } = string.Empty;

    [CommandOption("--count <N>")]
    [Description("How many nodes to add (default 1).")]
    public int Count { get; set; } = 1;

    [CommandOption("--shard <ID>")]
    [Description("Existing shard to add to (or omit to create a new shard for sharded clusters).")]
    public string? Shard { get; set; }
}

// --- scale-out remove <cluster> <node> ------------------------------------

public sealed class ClusterScaleOutRemoveSettings : ClusterTargetSettingsBase
{
    [CommandArgument(1, "<node>")]
    [Description("Node hostname to drain + remove.")]
    public string Node { get; set; } = string.Empty;

    [CommandOption("--force")]
    [Description("Force removal without draining (data loss risk).")]
    public bool Force { get; set; }
}

// --- scale-up <vm-name> ---------------------------------------------------

public sealed class ClusterScaleUpSettings : ClusterCommandSettingsBase
{
    [CommandArgument(0, "<vm>")]
    [Description("VM hostname to resize.")]
    public string Vm { get; set; } = string.Empty;

    [CommandOption("--cpu <N>")]
    [Description("New CPU count (omit to keep current).")]
    public int? Cpu { get; set; }

    [CommandOption("--ram <MB>")]
    [Description("New RAM in MB (omit to keep current).")]
    public int? RamMb { get; set; }

    [CommandOption("--disk <GB>")]
    [Description("New disk size in GB (omit to keep current; grow-only).")]
    public int? DiskGb { get; set; }

    [CommandOption("--force-primary")]
    [Description("Override the cluster adapter's refusal to resize the current primary.")]
    public bool ForcePrimary { get; set; }
}

// --- backup take <cluster> ------------------------------------------------

public sealed class ClusterBackupTakeSettings : ClusterTargetSettingsBase
{
    [CommandOption("--tag <TAG>")]
    [Description("Operator label for the backup; adapter generates one if omitted.")]
    public string? Tag { get; set; }

    [CommandOption("--destination <URI>")]
    [Description("Remote destination URI (e.g. nfs://nexus-gateway:/srv/nfs/backups/...); adapter uses cluster-default if omitted.")]
    public string? Destination { get; set; }
}

// --- backup restore <cluster> <backup-id> ---------------------------------

public sealed class ClusterBackupRestoreSettings : ClusterTargetSettingsBase
{
    [CommandArgument(1, "<backup-id>")]
    [Description("Backup id returned by a prior `backup take`.")]
    public string BackupId { get; set; } = string.Empty;

    [CommandOption("--at <TIMESTAMP>")]
    [Description("Point-in-time-recovery target (cluster-specific format); restores to backup completion time if omitted.")]
    public string? At { get; set; }
}

// --- health <cluster> -----------------------------------------------------

public sealed class ClusterHealthSettings : ClusterTargetSettingsBase
{
}

// --- topology <cluster> ---------------------------------------------------

public sealed class ClusterTopologySettings : ClusterTargetSettingsBase
{
    [CommandOption("--watch")]
    [Description("Re-poll + redraw every 2s until interrupted.")]
    public bool Watch { get; set; }
}

// --- cert-rotate <cluster> ------------------------------------------------

public sealed class ClusterCertRotateSettings : ClusterTargetSettingsBase
{
}

// --- chaos <cluster> <scenario> -------------------------------------------

public sealed class ClusterChaosSettings : ClusterTargetSettingsBase
{
    [CommandArgument(1, "<scenario>")]
    [Description("Scenario type (network-partition, slow-disk, cpu-starve, memory-pressure, packet-loss).")]
    public string Scenario { get; set; } = string.Empty;

    [CommandOption("--target <NODE>")]
    [Description("Node to target (adapter chooses if omitted).")]
    public string? Target { get; set; }

    [CommandOption("--duration <SEC>")]
    [Description("Scenario duration in seconds (default 30).")]
    public int Duration { get; set; } = 30;

    [CommandOption("--intensity <PCT>")]
    [Description("Scenario intensity 0..100 (scenario-specific semantics).")]
    public int? Intensity { get; set; }
}

// --- acl <cluster> <verb> -------------------------------------------------

public sealed class ClusterAclSettings : ClusterTargetSettingsBase
{
    [CommandArgument(1, "<verb>")]
    [Description("ACL verb: list, describe, grant, revoke.")]
    public string Verb { get; set; } = string.Empty;

    [CommandOption("--user <NAME>")]
    [Description("User (required for describe/grant/revoke).")]
    public string? User { get; set; }

    [CommandOption("--permissions <PERMS>")]
    [Description("Comma-separated permission list (for grant/revoke).")]
    public string? Permissions { get; set; }
}
