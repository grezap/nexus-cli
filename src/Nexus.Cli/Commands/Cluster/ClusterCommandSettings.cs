using System.ComponentModel;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

// ===========================================================================
// Settings for the 13 cluster verb groups (ADR-0009 IClusterAdapter SPI).
// Each verb shares the --json / --no-color / --yes flags via the base class;
// per-verb settings add positional args and verb-specific options.
// ===========================================================================

/// <summary>Shared base carrying the <c>--json</c> / <c>--no-color</c> / <c>--yes</c> flags common to every cluster verb.</summary>
public abstract class ClusterCommandSettingsBase : CommandSettings
{
    /// <summary>Emit JSON to stdout instead of the human view.</summary>
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human view.")]
    public bool Json { get; set; }

    /// <summary>Disable ANSI color in the human view.</summary>
    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }

    /// <summary>Skip the interactive confirmation prompt for destructive operations.</summary>
    [CommandOption("--yes")]
    [Description("Skip the interactive confirmation prompt (destructive op).")]
    public bool Yes { get; set; }
}

/// <summary>Base for verbs that take a single positional &lt;cluster&gt; argument.</summary>
public abstract class ClusterTargetSettingsBase : ClusterCommandSettingsBase
{
    /// <summary>Target cluster id (redis, mongo, percona, patroni, clickhouse, starrocks, sql-fci, sql-ag, kafka, ...).</summary>
    [CommandArgument(0, "<cluster>")]
    [Description("Cluster id (redis, mongo, percona, patroni, clickhouse, starrocks, sql-fci, sql-ag, kafka, ...).")]
    public string Cluster { get; set; } = string.Empty;
}

// --- cluster-status -------------------------------------------------------

/// <summary>Settings for the per-cluster <c>cluster-status</c> verb.</summary>
public sealed class ClusterStatusForClusterSettings : ClusterTargetSettingsBase
{
}

// --- failover-test <cluster> ----------------------------------------------

/// <summary>Settings for the <c>failover-test</c> verb (controlled primary/leader failover + RTO measurement).</summary>
public sealed class ClusterFailoverTestSettings : ClusterTargetSettingsBase
{
    /// <summary>Target node hostname; the adapter picks one if omitted.</summary>
    [CommandOption("--node <NAME>")]
    [Description("Target node hostname (replica for redis, secondary for mongo, replica for ag, etc.). Adapter chooses if omitted.")]
    public string? Node { get; set; }

    /// <summary>Direction for directional (kafka) failovers; ignored when not applicable.</summary>
    [CommandOption("--direction <DIR>")]
    [Description("For directional failovers (kafka east-to-west / west-to-east). Ignored when not applicable.")]
    public string? Direction { get; set; }

    /// <summary>Leave the cluster in the failed-over state (skip auto-recovery).</summary>
    [CommandOption("--no-recover")]
    [Description("Leave the cluster in the failed-over state (skip auto-recovery).")]
    public bool NoRecover { get; set; }
}

// --- scale-out add <cluster> ----------------------------------------------

/// <summary>Settings for the <c>scale-out add</c> verb (add nodes to a cluster).</summary>
public sealed class ClusterScaleOutAddSettings : ClusterTargetSettingsBase
{
    /// <summary>Role of the node(s) to add (cluster-specific: primary, replica, broker, controller, ...).</summary>
    [CommandOption("--role <ROLE>")]
    [Description("Node role (cluster-specific: primary, replica, broker, controller, follower, backend, ...).")]
    public string Role { get; set; } = string.Empty;

    /// <summary>How many nodes to add (default 1).</summary>
    [CommandOption("--count <N>")]
    [Description("How many nodes to add (default 1).")]
    public int Count { get; set; } = 1;

    /// <summary>Existing shard to add to; omit to create a new shard for sharded clusters.</summary>
    [CommandOption("--shard <ID>")]
    [Description("Existing shard to add to (or omit to create a new shard for sharded clusters).")]
    public string? Shard { get; set; }
}

// --- scale-out remove <cluster> <node> ------------------------------------

/// <summary>Settings for the <c>scale-out remove</c> verb (drain and remove a node).</summary>
public sealed class ClusterScaleOutRemoveSettings : ClusterTargetSettingsBase
{
    /// <summary>Hostname of the node to drain and remove.</summary>
    [CommandArgument(1, "<node>")]
    [Description("Node hostname to drain + remove.")]
    public string Node { get; set; } = string.Empty;

    /// <summary>Force removal without draining (data loss risk).</summary>
    [CommandOption("--force")]
    [Description("Force removal without draining (data loss risk).")]
    public bool Force { get; set; }
}

// --- scale-up <vm-name> ---------------------------------------------------

/// <summary>Settings for the <c>scale-up</c> verb (vertically resize a VM's CPU/RAM/disk).</summary>
public sealed class ClusterScaleUpSettings : ClusterCommandSettingsBase
{
    /// <summary>Hostname of the VM to resize.</summary>
    [CommandArgument(0, "<vm>")]
    [Description("VM hostname to resize.")]
    public string Vm { get; set; } = string.Empty;

    /// <summary>New CPU count; omit to keep current.</summary>
    [CommandOption("--cpu <N>")]
    [Description("New CPU count (omit to keep current).")]
    public int? Cpu { get; set; }

    /// <summary>New RAM in MB; omit to keep current.</summary>
    [CommandOption("--ram <MB>")]
    [Description("New RAM in MB (omit to keep current).")]
    public int? RamMb { get; set; }

    /// <summary>New disk size in GB; omit to keep current (grow-only).</summary>
    [CommandOption("--disk <GB>")]
    [Description("New disk size in GB (omit to keep current; grow-only).")]
    public int? DiskGb { get; set; }

    /// <summary>Override the cluster adapter's refusal to resize the current primary.</summary>
    [CommandOption("--force-primary")]
    [Description("Override the cluster adapter's refusal to resize the current primary.")]
    public bool ForcePrimary { get; set; }
}

// --- backup take <cluster> ------------------------------------------------

/// <summary>Settings for the <c>backup take</c> verb.</summary>
public sealed class ClusterBackupTakeSettings : ClusterTargetSettingsBase
{
    /// <summary>Operator label for the backup; the adapter generates one if omitted.</summary>
    [CommandOption("--tag <TAG>")]
    [Description("Operator label for the backup; adapter generates one if omitted.")]
    public string? Tag { get; set; }

    /// <summary>Remote destination URI; the adapter uses the cluster default if omitted.</summary>
    [CommandOption("--destination <URI>")]
    [Description("Remote destination URI (e.g. nfs://nexus-gateway:/srv/nfs/backups/...); adapter uses cluster-default if omitted.")]
    public string? Destination { get; set; }
}

// --- backup restore <cluster> <backup-id> ---------------------------------

/// <summary>Settings for the <c>backup restore</c> verb.</summary>
public sealed class ClusterBackupRestoreSettings : ClusterTargetSettingsBase
{
    /// <summary>Backup id returned by a prior <c>backup take</c>.</summary>
    [CommandArgument(1, "<backup-id>")]
    [Description("Backup id returned by a prior `backup take`.")]
    public string BackupId { get; set; } = string.Empty;

    /// <summary>Point-in-time-recovery target; restores to backup completion time if omitted.</summary>
    [CommandOption("--at <TIMESTAMP>")]
    [Description("Point-in-time-recovery target (cluster-specific format); restores to backup completion time if omitted.")]
    public string? At { get; set; }

    /// <summary>Extra opt-in required by adapters whose restore overwrites live state in place.</summary>
    [CommandOption("--confirm-destructive")]
    [Description("Extra opt-in required by adapters whose restore OVERWRITES live state in place (e.g. swarm consul/nomad snapshot restore).")]
    public bool ConfirmDestructive { get; set; }
}

// --- health <cluster> -----------------------------------------------------

/// <summary>Settings for the <c>health</c> verb (deep per-node health probes).</summary>
public sealed class ClusterHealthSettings : ClusterTargetSettingsBase
{
}

// --- topology <cluster> ---------------------------------------------------

/// <summary>Settings for the <c>topology</c> verb (membership/role snapshot).</summary>
public sealed class ClusterTopologySettings : ClusterTargetSettingsBase
{
    /// <summary>Re-poll and redraw every 2s until interrupted.</summary>
    [CommandOption("--watch")]
    [Description("Re-poll + redraw every 2s until interrupted.")]
    public bool Watch { get; set; }
}

// --- cert-rotate <cluster> ------------------------------------------------

/// <summary>Settings for the <c>cert-rotate</c> verb (rotate TLS/mTLS leaf certificates).</summary>
public sealed class ClusterCertRotateSettings : ClusterTargetSettingsBase
{
}

// --- recover-ha <cluster> (v0.8.1; IRecoverableCluster) -------------------

/// <summary>Settings for the <c>recover-ha</c> verb (HA recovery for recoverable clusters).</summary>
public sealed class ClusterRecoverHaSettings : ClusterTargetSettingsBase
{
}

// --- chaos <cluster> <scenario> -------------------------------------------

/// <summary>Settings for the <c>chaos</c> verb (inject a resilience-testing fault scenario).</summary>
public sealed class ClusterChaosSettings : ClusterTargetSettingsBase
{
    /// <summary>Scenario type (network-partition, slow-disk, cpu-starve, memory-pressure, packet-loss).</summary>
    [CommandArgument(1, "<scenario>")]
    [Description("Scenario type (network-partition, slow-disk, cpu-starve, memory-pressure, packet-loss).")]
    public string Scenario { get; set; } = string.Empty;

    /// <summary>Node to target; the adapter chooses if omitted.</summary>
    [CommandOption("--target <NODE>")]
    [Description("Node to target (adapter chooses if omitted).")]
    public string? Target { get; set; }

    /// <summary>Scenario duration in seconds (default 30).</summary>
    [CommandOption("--duration <SEC>")]
    [Description("Scenario duration in seconds (default 30).")]
    public int Duration { get; set; } = 30;

    /// <summary>Scenario intensity 0..100 (scenario-specific semantics).</summary>
    [CommandOption("--intensity <PCT>")]
    [Description("Scenario intensity 0..100 (scenario-specific semantics).")]
    public int? Intensity { get; set; }
}

// --- acl <cluster> <verb> -------------------------------------------------

/// <summary>Settings for the <c>acl</c> verb (list/describe/grant/revoke cluster access control).</summary>
public sealed class ClusterAclSettings : ClusterTargetSettingsBase
{
    /// <summary>ACL sub-verb: list, describe, grant, or revoke.</summary>
    [CommandArgument(1, "<verb>")]
    [Description("ACL verb: list, describe, grant, revoke.")]
    public string Verb { get; set; } = string.Empty;

    /// <summary>User to act on (required for describe/grant/revoke).</summary>
    [CommandOption("--user <NAME>")]
    [Description("User (required for describe/grant/revoke).")]
    public string? User { get; set; }

    /// <summary>Comma-separated permission list (for grant/revoke).</summary>
    [CommandOption("--permissions <PERMS>")]
    [Description("Comma-separated permission list (for grant/revoke).")]
    public string? Permissions { get; set; }
}
