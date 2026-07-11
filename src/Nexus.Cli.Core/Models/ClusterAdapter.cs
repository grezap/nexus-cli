namespace Nexus.Cli.Core.Models;

// ===========================================================================
// IClusterAdapter SPI domain types -- request + result records for the 12
// adapter methods declared by IClusterAdapter (ADR-0009 in nexus-cli;
// nexus-platform-plan ADR-0024 has the cross-tier rationale).
//
// Conventions:
//   - Requests are records with `? = null` optional members so callers can
//     pass only what they need.
//   - Results record what HAPPENED + when + how long, plus operation-specific
//     payload. Every result includes a CapturedAtUtc or StartedAtUtc + a
//     Duration so timelines are reconstructable.
//   - String enum-likes (RoleName, ScenarioType, AclVerb) stay as strings
//     rather than C# enums so per-cluster vocabulary can extend without
//     enum-version churn.
// ===========================================================================

// --- ClusterStatus (GetStatusAsync) ---------------------------------------

/// <summary>Point-in-time health and membership snapshot of a data-tier cluster.</summary>
/// <param name="ClusterId">Stable cluster identifier.</param>
/// <param name="DisplayName">Human-readable cluster name for rendering.</param>
/// <param name="OverallHealth">Rolled-up traffic-light health (<c>green</c>, <c>yellow</c> or <c>red</c>).</param>
/// <param name="Members">All observed cluster members.</param>
/// <param name="Leader">Single cluster leader, or <c>null</c> for leaderless / leader-per-shard clusters.</param>
/// <param name="CapturedAtUtc">Instant the snapshot was captured.</param>
public sealed record ClusterStatus(
    string ClusterId,
    string DisplayName,
    string OverallHealth,           // "green" | "yellow" | "red"
    IReadOnlyList<ClusterMember> Members,
    string? Leader,                 // some clusters have a single leader (Patroni, Mongo); others are leader-per-shard (Redis Cluster)
    DateTimeOffset CapturedAtUtc);

/// <summary>One node within a <see cref="ClusterStatus"/> membership list.</summary>
/// <param name="Hostname">Member hostname.</param>
/// <param name="IpAddress">Member IP address.</param>
/// <param name="Role">Cluster-specific role (e.g. <c>primary</c>, <c>replica</c>, <c>controller</c>, <c>router</c>).</param>
/// <param name="Status">Cluster-specific liveness (e.g. <c>alive</c>, <c>failed</c>, <c>draining</c>, <c>syncing</c>).</param>
/// <param name="ShardId">Owning shard for sharded clusters, or <c>null</c>.</param>
/// <param name="ReplicationLagSeconds">Replication lag behind the primary in seconds, or <c>null</c> when N/A.</param>
public sealed record ClusterMember(
    string Hostname,
    string IpAddress,
    string Role,                    // "primary" | "replica" | "controller" | "router" | etc. (cluster-specific)
    string Status,                  // "alive" | "failed" | "draining" | "syncing" | etc.
    string? ShardId = null,         // populated for sharded clusters (Redis, ClickHouse, StarRocks BE)
    double? ReplicationLagSeconds = null);

// --- Failover (FailoverAsync) ---------------------------------------------

/// <summary>Request parameters for <c>IClusterAdapter.FailoverAsync</c>.</summary>
/// <param name="TargetNode">Explicit node to fail over, or <c>null</c> to let the adapter choose (typically the current primary).</param>
/// <param name="Direction">Directional hint for clusters that have one (e.g. Kafka east-to-west); ignored when N/A.</param>
/// <param name="NoRecover">When <c>true</c>, leave the cluster failed-over and skip auto-recovery.</param>
public sealed record FailoverRequest(
    string? TargetNode = null,      // explicit node to fail over (if null: adapter chooses, typically the current primary)
    string? Direction = null,       // some clusters have direction (kafka east-to-west); ignored when N/A
    bool NoRecover = false);        // if true, leave the cluster in the failed-over state (skip auto-recovery)

// NOTE: reuses the existing `FailoverTimeline` (TimeSpan-based) defined in
// FailoverTest.cs -- both record the same five canonical instants of a
// failover run (pre-flight done / failure injected / new leader observed /
// recovery attempted / cluster healthy again). DRY per
// feedback_dry_single_source_of_truth.md.
/// <summary>Result of a <c>IClusterAdapter.FailoverAsync</c> run, including RTO and recovery outcome.</summary>
/// <param name="Scenario">Description of the failover scenario that was exercised.</param>
/// <param name="OriginalPrimary">Node that held the primary role before failover.</param>
/// <param name="NewPrimary">Node promoted to primary, or <c>null</c> if none was observed.</param>
/// <param name="Rto">Recovery time objective: elapsed time until a new primary served.</param>
/// <param name="Recovery">Recovery outcome (<c>recovered</c>, <c>skipped</c> or <c>failed</c>).</param>
/// <param name="RecoveryHint">Operator remediation hint when recovery failed, or <c>null</c>.</param>
/// <param name="Timeline">Wall-clock offsets of each failover phase.</param>
/// <param name="StartedAtUtc">Instant the run began; timeline offsets are relative to this.</param>
public sealed record FailoverResult(
    string Scenario,
    string OriginalPrimary,
    string? NewPrimary,
    TimeSpan Rto,
    string Recovery,                // "recovered" | "skipped" | "failed"
    string? RecoveryHint,
    FailoverTimeline Timeline,
    DateTimeOffset StartedAtUtc);

// --- Scale-out (ScaleOutAddAsync / ScaleOutRemoveAsync) -------------------

/// <summary>Request parameters for <c>IClusterAdapter.ScaleOutAddAsync</c> (add a node).</summary>
/// <param name="Role">Cluster-specific role to add (e.g. <c>primary</c>, <c>replica</c>, <c>broker</c>, <c>controller</c>, <c>backend</c>).</param>
/// <param name="Count">Number of nodes to add; usually 1, but some clusters need a primary+replica pair.</param>
/// <param name="ShardId">Existing shard to target, or <c>null</c> to let the adapter create a new shard.</param>
public sealed record ScaleOutAddRequest(
    string Role,                    // cluster-specific: "primary" | "replica" | "broker" | "controller" | "follower" | "backend"
    int Count = 1,                  // most adds are +1; some clusters (Redis shard add) need primary+replica together
    string? ShardId = null);        // for clusters that shard, ScaleOutAdd may target an existing shard or create a new one

/// <summary>Request parameters for <c>IClusterAdapter.ScaleOutRemoveAsync</c> (remove a node).</summary>
/// <param name="NodeName">Name of the node to remove.</param>
/// <param name="Drain">When <c>true</c>, drain data/connections before removal; <c>false</c> removes forcefully.</param>
public sealed record ScaleOutRemoveRequest(
    string NodeName,
    bool Drain = true);             // drain data/connections before removal; false = forceful

/// <summary>Result of a scale-out add or remove operation.</summary>
/// <param name="OperationType">Which operation ran (<c>add</c> or <c>remove</c>).</param>
/// <param name="AffectedNodes">Nodes added or removed by the operation.</param>
/// <param name="Outcome">Operation outcome (<c>ok</c>, <c>partial</c> or <c>failed</c>).</param>
/// <param name="OutcomeReason">Explanation when the outcome was not clean, or <c>null</c>.</param>
/// <param name="Duration">Wall-clock time the operation took.</param>
/// <param name="StartedAtUtc">Instant the operation began.</param>
public sealed record ScaleOutResult(
    string OperationType,           // "add" | "remove"
    IReadOnlyList<string> AffectedNodes,
    string Outcome,                 // "ok" | "partial" | "failed"
    string? OutcomeReason,
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

// --- Vertical resize (IVmResizer / scale-up) ------------------------------

/// <summary>Request parameters for a vertical VM resize (scale-up) via <see cref="Abstractions.IVmResizer"/>.</summary>
/// <param name="VmName">Name of the VM to resize.</param>
/// <param name="CpuCount">Target vCPU count, or <c>null</c> to leave unchanged.</param>
/// <param name="RamMb">Target RAM in megabytes, or <c>null</c> to leave unchanged.</param>
/// <param name="DiskGb">Target disk size in gigabytes, or <c>null</c> to leave unchanged.</param>
/// <param name="ForcePrimary">When <c>true</c>, override the adapter's refusal to resize the current primary.</param>
public sealed record ScaleUpRequest(
    string VmName,
    int? CpuCount = null,
    int? RamMb = null,
    int? DiskGb = null,
    bool ForcePrimary = false);     // override the adapter's CanResizeVm refusal-for-primary check

/// <summary>Result of a vertical VM resize, recording before/after resource values.</summary>
/// <param name="VmName">Name of the resized VM.</param>
/// <param name="OldCpu">vCPU count before the resize, or <c>null</c> when unchanged/unknown.</param>
/// <param name="NewCpu">vCPU count after the resize, or <c>null</c> when unchanged/unknown.</param>
/// <param name="OldRamMb">RAM in megabytes before the resize, or <c>null</c> when unchanged/unknown.</param>
/// <param name="NewRamMb">RAM in megabytes after the resize, or <c>null</c> when unchanged/unknown.</param>
/// <param name="OldDiskGb">Disk size in gigabytes before the resize, or <c>null</c> when unchanged/unknown.</param>
/// <param name="NewDiskGb">Disk size in gigabytes after the resize, or <c>null</c> when unchanged/unknown.</param>
/// <param name="Outcome">Resize outcome (<c>ok</c>, <c>skipped</c> or <c>failed</c>).</param>
/// <param name="OutcomeReason">Explanation when skipped or failed, or <c>null</c>.</param>
/// <param name="Duration">Wall-clock time the resize took.</param>
public sealed record ScaleUpResult(
    string VmName,
    int? OldCpu,
    int? NewCpu,
    int? OldRamMb,
    int? NewRamMb,
    int? OldDiskGb,
    int? NewDiskGb,
    string Outcome,                 // "ok" | "skipped" | "failed"
    string? OutcomeReason,
    TimeSpan Duration);

// --- Health (HealthAsync) -------------------------------------------------

/// <summary>Result of <c>IClusterAdapter.HealthAsync</c>: a set of per-probe health checks.</summary>
/// <param name="ClusterId">Identifier of the probed cluster.</param>
/// <param name="OverallHealth">Rolled-up traffic-light health across all probes.</param>
/// <param name="Probes">Individual health probes and their results.</param>
/// <param name="CapturedAtUtc">Instant the report was captured.</param>
public sealed record HealthReport(
    string ClusterId,
    string OverallHealth,
    IReadOnlyList<HealthProbe> Probes,
    DateTimeOffset CapturedAtUtc);

/// <summary>One health check within a <see cref="HealthReport"/> or chaos observation.</summary>
/// <param name="Name">Probe name (e.g. <c>replication-lag</c>, <c>disk-free</c>, <c>quorum-size</c>).</param>
/// <param name="Target">Node or shard the probe ran against.</param>
/// <param name="Status">Traffic-light result (<c>green</c>, <c>yellow</c> or <c>red</c>).</param>
/// <param name="Value">Human-readable measured value (e.g. <c>2.4s lag</c>), or <c>null</c>.</param>
/// <param name="Threshold">Human-readable pass threshold (e.g. <c>&lt;10s lag</c>), or <c>null</c>.</param>
public sealed record HealthProbe(
    string Name,                    // "replication-lag" | "disk-free" | "memory-pressure" | "quorum-size" | ...
    string Target,                  // node or shard the probe ran against
    string Status,                  // "green" | "yellow" | "red"
    string? Value = null,           // human-readable value (e.g. "2.4s lag", "12% disk free")
    string? Threshold = null);      // human-readable threshold (e.g. "<10s lag", ">15% disk free")

// --- Topology (TopologyAsync) ---------------------------------------------

/// <summary>Result of <c>IClusterAdapter.TopologyAsync</c>: the cluster's node and shard layout.</summary>
/// <param name="ClusterId">Identifier of the cluster.</param>
/// <param name="Nodes">All nodes in the topology.</param>
/// <param name="Shards">Shard layout, or <c>null</c> for non-sharded clusters (e.g. Mongo RS, Patroni).</param>
/// <param name="CapturedAtUtc">Instant the snapshot was captured.</param>
public sealed record TopologySnapshot(
    string ClusterId,
    IReadOnlyList<TopologyNode> Nodes,
    IReadOnlyList<TopologyShard>? Shards,   // null for non-sharded (Mongo RS, Patroni)
    DateTimeOffset CapturedAtUtc);

/// <summary>One node in a <see cref="TopologySnapshot"/>.</summary>
/// <param name="Hostname">Node hostname.</param>
/// <param name="Role">Cluster-specific role of the node.</param>
/// <param name="Status">Cluster-specific liveness of the node.</param>
/// <param name="ReplicationLagSeconds">Replication lag behind the primary in seconds, or <c>null</c> when N/A.</param>
public sealed record TopologyNode(
    string Hostname,
    string Role,
    string Status,
    double? ReplicationLagSeconds = null);

/// <summary>One shard in a <see cref="TopologySnapshot"/> with its primary and replicas.</summary>
/// <param name="ShardId">Shard identifier.</param>
/// <param name="Primary">Hostname of the shard's primary.</param>
/// <param name="Replicas">Hostnames of the shard's replicas.</param>
/// <param name="SlotRange">Owned key/slot range (Redis Cluster) or shard descriptor, or <c>null</c>.</param>
public sealed record TopologyShard(
    string ShardId,
    string Primary,
    IReadOnlyList<string> Replicas,
    string? SlotRange = null);              // Redis Cluster uses slot ranges; ClickHouse uses shard ID

// --- Backup (BackupTakeAsync / BackupRestoreAsync) ------------------------

/// <summary>Request parameters for <c>IClusterAdapter.BackupTakeAsync</c>.</summary>
/// <param name="Tag">Operator label for the backup, or <c>null</c> to let the adapter generate one.</param>
/// <param name="Destination">Remote destination URI (e.g. <c>s3://</c>, <c>nfs://</c>), or <c>null</c> for the cluster default.</param>
public sealed record BackupRequest(
    string? Tag = null,                     // operator label; if null, adapter generates one
    string? Destination = null);            // optional remote destination URI (s3://..., nfs://...); if null, adapter uses cluster-default

/// <summary>Result of a backup operation.</summary>
/// <param name="BackupId">Generated unique backup identifier.</param>
/// <param name="Destination">Resolved destination path the backup was written to.</param>
/// <param name="SizeBytes">Size of the backup in bytes.</param>
/// <param name="Duration">Wall-clock time the backup took.</param>
/// <param name="StartedAtUtc">Instant the backup began.</param>
public sealed record BackupResult(
    string BackupId,                        // generated unique ID (uuid-or-similar)
    string Destination,                     // resolved destination path
    long SizeBytes,
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

/// <summary>Request parameters for <c>IClusterAdapter.BackupRestoreAsync</c>.</summary>
/// <param name="BackupId">Identifier of the backup to restore.</param>
/// <param name="AtTimestamp">Point-in-time target for PITR, or <c>null</c> to restore to backup completion time.</param>
/// <param name="ConfirmDestructive">Required opt-in for adapters whose restore overwrites live state in place.</param>
public sealed record RestoreRequest(
    string BackupId,
    string? AtTimestamp = null,             // for point-in-time-recovery; null = restore to backup completion time
    bool ConfirmDestructive = false);       // extra opt-in for adapters whose restore overwrites LIVE state in place (e.g. Swarm consul/nomad snapshot restore)

/// <summary>Result of a restore operation.</summary>
/// <param name="BackupId">Identifier of the restored backup.</param>
/// <param name="ItemsRestored">Cluster-specific count of restored items (rows, documents or keys).</param>
/// <param name="Duration">Wall-clock time the restore took.</param>
/// <param name="StartedAtUtc">Instant the restore began.</param>
public sealed record RestoreResult(
    string BackupId,
    long ItemsRestored,                     // rows / documents / keys -- cluster-specific
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

// --- Cert rotation (RotateCertAsync) --------------------------------------

/// <summary>Result of <c>IClusterAdapter.RotateCertAsync</c>: the per-node cert rotation outcome.</summary>
/// <param name="RotatedNodes">Per-node rotation results.</param>
/// <param name="Duration">Wall-clock time the rotation took.</param>
/// <param name="StartedAtUtc">Instant the rotation began.</param>
public sealed record CertRotationResult(
    IReadOnlyList<CertRotatedNode> RotatedNodes,
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

/// <summary>Certificate rotation outcome for a single node.</summary>
/// <param name="Hostname">Node whose certificate was rotated.</param>
/// <param name="OldSerial">Serial number of the retired certificate.</param>
/// <param name="NewSerial">Serial number of the newly issued certificate.</param>
/// <param name="Error">Failure detail when rotation failed on this node, or <c>null</c> on success.</param>
public sealed record CertRotatedNode(
    string Hostname,
    string OldSerial,
    string NewSerial,
    string? Error = null);

// --- Chaos (ApplyChaosAsync) ----------------------------------------------

/// <summary>Definition of a fault-injection scenario for <c>IClusterAdapter.ApplyChaosAsync</c>.</summary>
/// <param name="ScenarioType">Fault type (e.g. <c>network-partition</c>, <c>slow-disk</c>, <c>cpu-starve</c>, <c>packet-loss</c>).</param>
/// <param name="Target">Node or shard to target, or <c>null</c> to let the adapter choose.</param>
/// <param name="DurationSeconds">How long to hold the fault, in seconds.</param>
/// <param name="IntensityPercent">Scenario-specific intensity from 0 to 100, or <c>null</c>.</param>
public sealed record ChaosScenario(
    string ScenarioType,                    // "network-partition" | "slow-disk" | "cpu-starve" | "memory-pressure" | "packet-loss"
    string? Target = null,                  // node or shard the scenario targets; null = adapter chooses
    int DurationSeconds = 30,
    int? IntensityPercent = null);          // 0..100; semantics scenario-specific

/// <summary>Result of applying a chaos scenario, including observed impact and recovery.</summary>
/// <param name="ScenarioApplied">Fault type that was applied.</param>
/// <param name="Target">Node or shard the fault was applied to.</param>
/// <param name="ObservedImpact">Health probes captured while the fault was active.</param>
/// <param name="Duration">Wall-clock time the scenario ran.</param>
/// <param name="StartedAtUtc">Instant the scenario began.</param>
/// <param name="Recovered">Whether the cluster returned to green after the fault lifted.</param>
public sealed record ChaosOutcome(
    string ScenarioApplied,
    string Target,
    IReadOnlyList<HealthProbe> ObservedImpact,
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc,
    bool Recovered);                        // did the cluster return to green after the scenario lifted

// --- ACL (AclAsync) -------------------------------------------------------

/// <summary>Request parameters for <c>IClusterAdapter.AclAsync</c> (list/grant/revoke/describe).</summary>
/// <param name="Verb">ACL verb to perform (<c>list</c>, <c>grant</c>, <c>revoke</c> or <c>describe</c>).</param>
/// <param name="User">Target user; required for grant/revoke/describe, ignored for list.</param>
/// <param name="Permissions">Permissions to grant or revoke, or <c>null</c> when not applicable.</param>
public sealed record AclOperation(
    string Verb,                            // "list" | "grant" | "revoke" | "describe"
    string? User = null,                    // required for grant/revoke/describe; ignored for list
    IReadOnlyList<string>? Permissions = null);

/// <summary>Result of an ACL operation: the cluster's users and their grants.</summary>
/// <param name="ClusterId">Identifier of the cluster.</param>
/// <param name="Verb">ACL verb that produced this snapshot.</param>
/// <param name="Users">Users and their effective permissions.</param>
/// <param name="CapturedAtUtc">Instant the snapshot was captured.</param>
public sealed record AclSnapshot(
    string ClusterId,
    string Verb,
    IReadOnlyList<AclUser> Users,
    DateTimeOffset CapturedAtUtc);

/// <summary>One ACL user and its granted permissions.</summary>
/// <param name="Name">User name.</param>
/// <param name="Permissions">Permissions granted to the user.</param>
/// <param name="Enabled">Whether the user account is enabled.</param>
public sealed record AclUser(
    string Name,
    IReadOnlyList<string> Permissions,
    bool Enabled = true);

// --- Recover-HA (IRecoverableCluster.RecoverHaAsync; v0.8.1, ADR-0022) -----
// The declarative Vault-HA boot-race recovery: unseal vault-transit from the
// operator's Shamir key file, restart the HA nodes, poll until unsealed.

/// <summary>Result of the declarative Vault-HA boot-race recovery (<c>IRecoverableCluster.RecoverHaAsync</c>).</summary>
/// <param name="ClusterId">Identifier of the recovered cluster.</param>
/// <param name="TransitUnsealed">Whether the vault-transit Shamir seal-key custodian is unsealed.</param>
/// <param name="Nodes">Per-node recovery outcomes.</param>
/// <param name="AllUnsealed">Whether every HA node reported <c>sealed=false</c>.</param>
/// <param name="Leader">Active node after recovery, or <c>null</c> if none; leadership may drift.</param>
/// <param name="Duration">Wall-clock time the recovery took.</param>
/// <param name="StartedAtUtc">Instant the recovery began.</param>
public sealed record RecoverHaResult(
    string ClusterId,
    bool TransitUnsealed,               // vault-transit (Shamir seal-key custodian) is unsealed
    IReadOnlyList<RecoverNodeResult> Nodes,
    bool AllUnsealed,                   // every HA node reported sealed=false
    string? Leader,                     // active node after recovery (leaders drift)
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

/// <summary>Recovery outcome for a single Vault HA node.</summary>
/// <param name="Hostname">Node that was recovered.</param>
/// <param name="Sealed">Post-recovery seal state (<c>false</c> means recovered).</param>
/// <param name="Outcome">Recovery action taken (<c>unsealed</c>, <c>restarted</c>, <c>already-up</c> or <c>failed: &lt;why&gt;</c>).</param>
public sealed record RecoverNodeResult(
    string Hostname,
    bool Sealed,                        // post-recovery seal state (false = recovered)
    string Outcome);                    // "unsealed" | "restarted" | "already-up" | "failed: <why>"
