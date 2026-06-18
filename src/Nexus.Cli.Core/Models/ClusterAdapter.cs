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

public sealed record ClusterStatus(
    string ClusterId,
    string DisplayName,
    string OverallHealth,           // "green" | "yellow" | "red"
    IReadOnlyList<ClusterMember> Members,
    string? Leader,                 // some clusters have a single leader (Patroni, Mongo); others are leader-per-shard (Redis Cluster)
    DateTimeOffset CapturedAtUtc);

public sealed record ClusterMember(
    string Hostname,
    string IpAddress,
    string Role,                    // "primary" | "replica" | "controller" | "router" | etc. (cluster-specific)
    string Status,                  // "alive" | "failed" | "draining" | "syncing" | etc.
    string? ShardId = null,         // populated for sharded clusters (Redis, ClickHouse, StarRocks BE)
    double? ReplicationLagSeconds = null);

// --- Failover (FailoverAsync) ---------------------------------------------

public sealed record FailoverRequest(
    string? TargetNode = null,      // explicit node to fail over (if null: adapter chooses, typically the current primary)
    string? Direction = null,       // some clusters have direction (kafka east-to-west); ignored when N/A
    bool NoRecover = false);        // if true, leave the cluster in the failed-over state (skip auto-recovery)

// NOTE: reuses the existing `FailoverTimeline` (TimeSpan-based) defined in
// FailoverTest.cs -- both record the same five canonical instants of a
// failover run (pre-flight done / failure injected / new leader observed /
// recovery attempted / cluster healthy again). DRY per
// feedback_dry_single_source_of_truth.md.
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

public sealed record ScaleOutAddRequest(
    string Role,                    // cluster-specific: "primary" | "replica" | "broker" | "controller" | "follower" | "backend"
    int Count = 1,                  // most adds are +1; some clusters (Redis shard add) need primary+replica together
    string? ShardId = null);        // for clusters that shard, ScaleOutAdd may target an existing shard or create a new one

public sealed record ScaleOutRemoveRequest(
    string NodeName,
    bool Drain = true);             // drain data/connections before removal; false = forceful

public sealed record ScaleOutResult(
    string OperationType,           // "add" | "remove"
    IReadOnlyList<string> AffectedNodes,
    string Outcome,                 // "ok" | "partial" | "failed"
    string? OutcomeReason,
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

// --- Vertical resize (IVmResizer / scale-up) ------------------------------

public sealed record ScaleUpRequest(
    string VmName,
    int? CpuCount = null,
    int? RamMb = null,
    int? DiskGb = null,
    bool ForcePrimary = false);     // override the adapter's CanResizeVm refusal-for-primary check

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

public sealed record HealthReport(
    string ClusterId,
    string OverallHealth,
    IReadOnlyList<HealthProbe> Probes,
    DateTimeOffset CapturedAtUtc);

public sealed record HealthProbe(
    string Name,                    // "replication-lag" | "disk-free" | "memory-pressure" | "quorum-size" | ...
    string Target,                  // node or shard the probe ran against
    string Status,                  // "green" | "yellow" | "red"
    string? Value = null,           // human-readable value (e.g. "2.4s lag", "12% disk free")
    string? Threshold = null);      // human-readable threshold (e.g. "<10s lag", ">15% disk free")

// --- Topology (TopologyAsync) ---------------------------------------------

public sealed record TopologySnapshot(
    string ClusterId,
    IReadOnlyList<TopologyNode> Nodes,
    IReadOnlyList<TopologyShard>? Shards,   // null for non-sharded (Mongo RS, Patroni)
    DateTimeOffset CapturedAtUtc);

public sealed record TopologyNode(
    string Hostname,
    string Role,
    string Status,
    double? ReplicationLagSeconds = null);

public sealed record TopologyShard(
    string ShardId,
    string Primary,
    IReadOnlyList<string> Replicas,
    string? SlotRange = null);              // Redis Cluster uses slot ranges; ClickHouse uses shard ID

// --- Backup (BackupTakeAsync / BackupRestoreAsync) ------------------------

public sealed record BackupRequest(
    string? Tag = null,                     // operator label; if null, adapter generates one
    string? Destination = null);            // optional remote destination URI (s3://..., nfs://...); if null, adapter uses cluster-default

public sealed record BackupResult(
    string BackupId,                        // generated unique ID (uuid-or-similar)
    string Destination,                     // resolved destination path
    long SizeBytes,
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

public sealed record RestoreRequest(
    string BackupId,
    string? AtTimestamp = null);            // for point-in-time-recovery; null = restore to backup completion time

public sealed record RestoreResult(
    string BackupId,
    long ItemsRestored,                     // rows / documents / keys -- cluster-specific
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

// --- Cert rotation (RotateCertAsync) --------------------------------------

public sealed record CertRotationResult(
    IReadOnlyList<CertRotatedNode> RotatedNodes,
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

public sealed record CertRotatedNode(
    string Hostname,
    string OldSerial,
    string NewSerial,
    string? Error = null);

// --- Chaos (ApplyChaosAsync) ----------------------------------------------

public sealed record ChaosScenario(
    string ScenarioType,                    // "network-partition" | "slow-disk" | "cpu-starve" | "memory-pressure" | "packet-loss"
    string? Target = null,                  // node or shard the scenario targets; null = adapter chooses
    int DurationSeconds = 30,
    int? IntensityPercent = null);          // 0..100; semantics scenario-specific

public sealed record ChaosOutcome(
    string ScenarioApplied,
    string Target,
    IReadOnlyList<HealthProbe> ObservedImpact,
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc,
    bool Recovered);                        // did the cluster return to green after the scenario lifted

// --- ACL (AclAsync) -------------------------------------------------------

public sealed record AclOperation(
    string Verb,                            // "list" | "grant" | "revoke" | "describe"
    string? User = null,                    // required for grant/revoke/describe; ignored for list
    IReadOnlyList<string>? Permissions = null);

public sealed record AclSnapshot(
    string ClusterId,
    string Verb,
    IReadOnlyList<AclUser> Users,
    DateTimeOffset CapturedAtUtc);

public sealed record AclUser(
    string Name,
    IReadOnlyList<string> Permissions,
    bool Enabled = true);

// --- Recover-HA (IRecoverableCluster.RecoverHaAsync; v0.8.1, ADR-0022) -----
// The declarative Vault-HA boot-race recovery: unseal vault-transit from the
// operator's Shamir key file, restart the HA nodes, poll until unsealed.

public sealed record RecoverHaResult(
    string ClusterId,
    bool TransitUnsealed,               // vault-transit (Shamir seal-key custodian) is unsealed
    IReadOnlyList<RecoverNodeResult> Nodes,
    bool AllUnsealed,                   // every HA node reported sealed=false
    string? Leader,                     // active node after recovery (leaders drift)
    TimeSpan Duration,
    DateTimeOffset StartedAtUtc);

public sealed record RecoverNodeResult(
    string Hostname,
    bool Sealed,                        // post-recovery seal state (false = recovered)
    string Outcome);                    // "unsealed" | "restarted" | "already-up" | "failed: <why>"
