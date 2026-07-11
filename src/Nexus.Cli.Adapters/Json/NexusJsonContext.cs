using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Cli.Adapters.Json;

// === Vault KV-v2 ============================================================

/// <summary>KV-v2 read response: outer <c>data</c> wrapper.</summary>
public sealed class VaultKvV2Response
{
    /// <summary>The KV-v2 envelope holding the inner secret data.</summary>
    [JsonPropertyName("data")]
    public VaultKvV2Envelope? Data { get; set; }
}

/// <summary>KV-v2 envelope: the inner <c>data</c> map of secret key/values.</summary>
public sealed class VaultKvV2Envelope
{
    /// <summary>The secret's field name/value pairs.</summary>
    [JsonPropertyName("data")]
    public Dictionary<string, string>? Data { get; set; }
}

// === Vault PKI issue (cert-rotate, v0.6.6) ==================================

/// <summary>PKI <c>issue</c> response: outer <c>data</c> wrapper.</summary>
public sealed class VaultPkiIssueResponse
{
    /// <summary>The issued-certificate payload.</summary>
    [JsonPropertyName("data")]
    public VaultPkiIssueData? Data { get; set; }
}

/// <summary>The certificate material returned by a PKI <c>issue</c> call.</summary>
public sealed class VaultPkiIssueData
{
    /// <summary>The issued leaf certificate (PEM).</summary>
    [JsonPropertyName("certificate")] public string Certificate { get; set; } = "";
    /// <summary>The leaf's private key (PEM).</summary>
    [JsonPropertyName("private_key")] public string PrivateKey { get; set; } = "";
    /// <summary>The issuing CA certificate (PEM).</summary>
    [JsonPropertyName("issuing_ca")] public string IssuingCa { get; set; } = "";
    /// <summary>The full CA chain (PEM), leaf-to-root, when present.</summary>
    [JsonPropertyName("ca_chain")] public List<string>? CaChain { get; set; }
    /// <summary>The issued certificate's serial number.</summary>
    [JsonPropertyName("serial_number")] public string SerialNumber { get; set; } = "";
}

// === Consul =================================================================

/// <summary>A member row from Consul <c>/v1/agent/members</c>.</summary>
public sealed class ConsulMemberDto
{
    /// <summary>Node name.</summary>
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    /// <summary>Gossip advertise address.</summary>
    [JsonPropertyName("Addr")] public string Addr { get; set; } = "";
    /// <summary>Serf LAN port.</summary>
    [JsonPropertyName("Port")] public int Port { get; set; }
    /// <summary>Serf member-status enum (see status translation).</summary>
    [JsonPropertyName("Status")] public int Status { get; set; }
    /// <summary>Serf tags (carries <c>role</c>, <c>dc</c>, etc.).</summary>
    [JsonPropertyName("Tags")] public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>Response of Consul <c>/v1/agent/self</c> (used as an auth probe).</summary>
public sealed class ConsulAgentSelfDto
{
    /// <summary>The agent's configuration block.</summary>
    [JsonPropertyName("Config")] public ConsulAgentConfigDto? Config { get; set; }
    /// <summary>Nested runtime stats keyed by subsystem.</summary>
    [JsonPropertyName("Stats")] public Dictionary<string, Dictionary<string, string>>? Stats { get; set; }
}

/// <summary>The <c>Config</c> subset of Consul <c>/v1/agent/self</c>.</summary>
public sealed class ConsulAgentConfigDto
{
    /// <summary>The agent's datacenter.</summary>
    [JsonPropertyName("Datacenter")] public string Datacenter { get; set; } = "";
    /// <summary>The agent's node name.</summary>
    [JsonPropertyName("NodeName")] public string NodeName { get; set; } = "";
}

// === Nomad ==================================================================

/// <summary>A server member row from Nomad <c>/v1/agent/members</c>.</summary>
public sealed class NomadServerMemberDto
{
    /// <summary>Server member name.</summary>
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    /// <summary>Serf advertise address.</summary>
    [JsonPropertyName("Addr")] public string Addr { get; set; } = "";
    /// <summary>Serf port.</summary>
    [JsonPropertyName("Port")] public int Port { get; set; }
    /// <summary>Member status (e.g. <c>alive</c>).</summary>
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    /// <summary>Serf tags for the member.</summary>
    [JsonPropertyName("Tags")] public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>Envelope of Nomad <c>/v1/agent/members</c>.</summary>
public sealed class NomadServerMembersDto
{
    /// <summary>Name of the server that answered the request.</summary>
    [JsonPropertyName("ServerName")] public string ServerName { get; set; } = "";
    /// <summary>The server member set.</summary>
    [JsonPropertyName("Members")] public List<NomadServerMemberDto> Members { get; set; } = new();
}

/// <summary>A client-node row from Nomad <c>/v1/nodes</c>.</summary>
public sealed class NomadNodeListDto
{
    /// <summary>Node id.</summary>
    [JsonPropertyName("ID")] public string Id { get; set; } = "";
    /// <summary>Node name.</summary>
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    /// <summary>Node HTTP/RPC address.</summary>
    [JsonPropertyName("Address")] public string Address { get; set; } = "";
    /// <summary>Node status (e.g. <c>ready</c>).</summary>
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    /// <summary>Operator-assigned node class.</summary>
    [JsonPropertyName("NodeClass")] public string NodeClass { get; set; } = "";
}

// === Portainer ==============================================================

/// <summary>Response of Portainer <c>/api/system/status</c>.</summary>
public sealed class PortainerSystemStatusDto
{
    /// <summary>Portainer server version.</summary>
    [JsonPropertyName("Version")] public string Version { get; set; } = "";
    /// <summary>Portainer instance identifier.</summary>
    [JsonPropertyName("InstanceID")] public string InstanceId { get; set; } = "";
}

/// <summary>Request body for Portainer <c>/api/auth</c> (JWT login).</summary>
public sealed class PortainerAuthRequestDto
{
    /// <summary>Admin username.</summary>
    [JsonPropertyName("Username")] public string Username { get; set; } = "";
    /// <summary>Admin password.</summary>
    [JsonPropertyName("Password")] public string Password { get; set; } = "";
}

/// <summary>Response of Portainer <c>/api/auth</c>.</summary>
public sealed class PortainerAuthResponseDto
{
    /// <summary>The issued JWT bearer token.</summary>
    [JsonPropertyName("jwt")] public string Jwt { get; set; } = "";
}

// === ClusterStatusReport JSON output =======================================

/// <summary>Top-level JSON shape for the Swarm-tier <c>cluster status</c> report.</summary>
public sealed class ClusterStatusJsonOutput
{
    /// <summary>Aggregate health verdict.</summary>
    public string Overall { get; set; } = "";
    /// <summary>UTC capture timestamp (ISO-8601).</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>Consul section, if probed.</summary>
    public ConsulSection? Consul { get; set; }
    /// <summary>Nomad section, if probed.</summary>
    public NomadSection? Nomad { get; set; }
    /// <summary>Portainer section, if probed.</summary>
    public PortainerSection? Portainer { get; set; }
}

/// <summary>Consul portion of <see cref="ClusterStatusJsonOutput"/>.</summary>
public sealed class ConsulSection
{
    /// <summary>Error message if the Consul probe failed; null on success.</summary>
    public string? Error { get; set; }
    /// <summary>Count of alive members.</summary>
    public int? Alive { get; set; }
    /// <summary>Count of failed/left members.</summary>
    public int? Failed { get; set; }
    /// <summary>Current Raft leader address, if known.</summary>
    public string? Leader { get; set; }
    /// <summary>Member rows, if the probe succeeded.</summary>
    public List<ConsulMemberJson>? Members { get; set; }
}

/// <summary>A Consul member as rendered in JSON output.</summary>
public sealed class ConsulMemberJson
{
    /// <summary>Node name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Advertise address.</summary>
    public string Addr { get; set; } = "";
    /// <summary>Translated member status.</summary>
    public string Status { get; set; } = "";
    /// <summary>Role tag, if present.</summary>
    public string Role { get; set; } = "";
}

/// <summary>Nomad portion of <see cref="ClusterStatusJsonOutput"/>.</summary>
public sealed class NomadSection
{
    /// <summary>Error message if the Nomad probe failed; null on success.</summary>
    public string? Error { get; set; }
    /// <summary>Server rows.</summary>
    public List<NomadServerJson>? Servers { get; set; }
    /// <summary>Client-node rows.</summary>
    public List<NomadClientJson>? Clients { get; set; }
    /// <summary>Current leader address, if known.</summary>
    public string? LeaderAddress { get; set; }
}

/// <summary>A Nomad server as rendered in JSON output.</summary>
public sealed class NomadServerJson
{
    /// <summary>Server name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Server <c>addr:port</c>.</summary>
    public string Address { get; set; } = "";
    /// <summary>True when this server is the leader.</summary>
    public bool IsLeader { get; set; }
}

/// <summary>A Nomad client node as rendered in JSON output.</summary>
public sealed class NomadClientJson
{
    /// <summary>Node name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Node address.</summary>
    public string Address { get; set; } = "";
    /// <summary>Node status.</summary>
    public string Status { get; set; } = "";
    /// <summary>Node class.</summary>
    public string NodeClass { get; set; } = "";
}

/// <summary>Portainer portion of <see cref="ClusterStatusJsonOutput"/>.</summary>
public sealed class PortainerSection
{
    /// <summary>Error message if the Portainer probe failed; null on success.</summary>
    public string? Error { get; set; }
    /// <summary>Portainer version.</summary>
    public string? Version { get; set; }
    /// <summary>Portainer instance id.</summary>
    public string? InstanceId { get; set; }
    /// <summary>True when Portainer responded.</summary>
    public bool? Reachable { get; set; }
}

// === Infrastructure (v0.2) JSON output =====================================

/// <summary>A single VM's status row in infrastructure JSON output.</summary>
public sealed class VmStatusJson
{
    /// <summary>Owning cluster name.</summary>
    public string Cluster { get; set; } = "";
    /// <summary>VM name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Power state.</summary>
    public string State { get; set; } = "";
    /// <summary>Guest OS tag.</summary>
    public string Os { get; set; } = "";
    /// <summary>VMnet10 (backplane) address.</summary>
    public string Vmnet10 { get; set; } = "";
    /// <summary>VMnet11 (host-reachable) address.</summary>
    public string Vmnet11 { get; set; } = "";
    /// <summary>On-disk <c>.vmx</c> path.</summary>
    public string Vmx { get; set; } = "";
    /// <summary>Node role.</summary>
    public string Role { get; set; } = "";
}

/// <summary>The outcome of one infrastructure power operation on a node.</summary>
public sealed class OpResultJson
{
    /// <summary>Target node name.</summary>
    public string Node { get; set; } = "";
    /// <summary>True when the operation succeeded.</summary>
    public bool Success { get; set; }
    /// <summary>Human-readable outcome message.</summary>
    public string Message { get; set; } = "";
}

/// <summary>JSON shape for <c>infra list</c> (all VMs).</summary>
public sealed class InfrastructureListJsonOutput
{
    /// <summary>UTC capture timestamp.</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>The VM rows.</summary>
    public List<VmStatusJson> Vms { get; set; } = new();
}

/// <summary>JSON shape for <c>infra status</c> (one cluster, optionally one node).</summary>
public sealed class InfrastructureStatusJsonOutput
{
    /// <summary>UTC capture timestamp.</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>Target cluster name.</summary>
    public string Cluster { get; set; } = "";
    /// <summary>Target node name, if scoped to one.</summary>
    public string? Node { get; set; }
    /// <summary>The matching VM rows.</summary>
    public List<VmStatusJson> Vms { get; set; } = new();
}

/// <summary>JSON shape for an infrastructure power-verb run (start/stop/suspend).</summary>
public sealed class InfrastructureOpsJsonOutput
{
    /// <summary>UTC capture timestamp.</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>Target cluster name.</summary>
    public string Cluster { get; set; } = "";
    /// <summary>The verb applied.</summary>
    public string Verb { get; set; } = "";
    /// <summary>Per-node operation outcomes.</summary>
    public List<OpResultJson> Ops { get; set; } = new();
}

// === FailoverTestReport JSON output (v0.3) ==================================

/// <summary>Relative-seconds timeline of a generic failover test.</summary>
public sealed class FailoverTimelineJson
{
    /// <summary>Seconds to complete pre-flight checks.</summary>
    public double PreFlightCompletedSec { get; set; }
    /// <summary>Seconds at which the failure was injected.</summary>
    public double FailureInjectedSec { get; set; }
    /// <summary>Seconds at which a new leader was observed.</summary>
    public double NewLeaderObservedSec { get; set; }
    /// <summary>Seconds at which recovery was attempted.</summary>
    public double RecoveryAttemptedSec { get; set; }
    /// <summary>Seconds at which the cluster was healthy again.</summary>
    public double ClusterHealthyAgainSec { get; set; }
}

/// <summary>JSON shape of a generic failover-test report.</summary>
public sealed class FailoverTestJsonOutput
{
    /// <summary>Scenario identifier.</summary>
    public string Scenario { get; set; } = "";
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
    /// <summary>Leader before the injection.</summary>
    public string OriginalLeader { get; set; } = "";
    /// <summary>Leader observed after failover, if any.</summary>
    public string? NewLeader { get; set; }
    /// <summary>Measured recovery time objective (seconds).</summary>
    public double RtoSeconds { get; set; }
    /// <summary>Recovery outcome.</summary>
    public string Recovery { get; set; } = "";
    /// <summary>Operator hint when recovery was incomplete.</summary>
    public string? RecoveryHint { get; set; }
    /// <summary>The relative-seconds timeline.</summary>
    public FailoverTimelineJson? Timeline { get; set; }
}

// === KafkaFailoverReport JSON output (v0.5) ================================

/// <summary>Relative-seconds timeline of a Kafka MM2 DR failover.</summary>
public sealed class KafkaFailoverTimelineJson
{
    /// <summary>Seconds to complete pre-flight checks.</summary>
    public double PreFlightCompletedSec { get; set; }
    /// <summary>Seconds at which the failure was injected.</summary>
    public double FailureInjectedSec { get; set; }
    /// <summary>Seconds at which the DR target became healthy.</summary>
    public double TargetHealthySec { get; set; }
    /// <summary>Seconds at which recovery was attempted.</summary>
    public double RecoveryAttemptedSec { get; set; }
    /// <summary>Seconds at which the source became healthy again.</summary>
    public double SourceHealthyAgainSec { get; set; }
}

/// <summary>JSON shape of a Kafka failover report.</summary>
public sealed class KafkaFailoverJsonOutput
{
    /// <summary>Failover direction (source-to-target).</summary>
    public string Direction { get; set; } = "";
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
    /// <summary>Source cluster name.</summary>
    public string SourceCluster { get; set; } = "";
    /// <summary>Target (DR) cluster name.</summary>
    public string TargetCluster { get; set; } = "";
    /// <summary>Brokers suspended to inject the failure.</summary>
    public List<string> SuspendedBrokers { get; set; } = new();
    /// <summary>True if the target served traffic after the failure.</summary>
    public bool TargetServedAfterFailure { get; set; }
    /// <summary>Probe token round-tripped through the target, if used.</summary>
    public string? TargetProbeToken { get; set; }
    /// <summary>Measured recovery time objective (seconds).</summary>
    public double RtoSeconds { get; set; }
    /// <summary>Recovery outcome.</summary>
    public string Recovery { get; set; } = "";
    /// <summary>Operator hint when recovery was incomplete.</summary>
    public string? RecoveryHint { get; set; }
    /// <summary>The relative-seconds timeline.</summary>
    public KafkaFailoverTimelineJson? Timeline { get; set; }
}

// === Demo (v0.4) ===========================================================
// Extended in v0.6 (ADR-0009) with 5 optional fields on the spec/step DTOs.
// All additions are nullable -- DefaultIgnoreCondition.WhenWritingNull on the
// source-gen context means absent fields don't appear in emitted JSON, so the
// existing DEMO-01 / DEMO-02 specs deserialize + reserialize unchanged.

/// <summary>An observation annotation on a demo step (where to look, what to expect).</summary>
public sealed class DemoObservationJson
{
    /// <summary>Where the observation applies (component/log/output).</summary>
    public string? Where { get; set; }
    /// <summary>What the operator should expect to see.</summary>
    public string? What { get; set; }
}

/// <summary>Prerequisites a demo needs before it can run.</summary>
public sealed class DemoPrerequisitesJson
{
    /// <summary>VM names that must be alive.</summary>
    public List<string>? VmsAlive { get; set; }
    /// <summary>Environment variables that must be set.</summary>
    public List<string>? EnvVars { get; set; }
}

/// <summary>One step of a demo spec.</summary>
public sealed class DemoStepJson
{
    /// <summary>The shell command line to run.</summary>
    public string? Command { get; set; }
    /// <summary>Seconds to wait after the step (recording pacing / settle time).</summary>
    public double WaitAfterSeconds { get; set; }
    /// <summary>Expected exit code, if the step asserts one.</summary>
    public int? ExpectedExitCode { get; set; }
    /// <summary>Substrings the combined output must contain, if asserted.</summary>
    public List<string>? ExpectedOutputContains { get; set; }
    /// <summary>Observation annotations for the step.</summary>
    public List<DemoObservationJson>? Observe { get; set; }
}

/// <summary>A full demo spec as loaded from a <c>&lt;id&gt;.json</c> file.</summary>
public sealed class DemoSpecJson
{
    /// <summary>Stable demo id (also the file name).</summary>
    public string? Id { get; set; }
    /// <summary>Human-readable title.</summary>
    public string? Title { get; set; }
    /// <summary>Longer description.</summary>
    public string? Description { get; set; }
    /// <summary>Ordered demo steps.</summary>
    public List<DemoStepJson>? Steps { get; set; }
    /// <summary>Run prerequisites.</summary>
    public DemoPrerequisitesJson? Prerequisites { get; set; }
    /// <summary>One-line statement of what the demo proves.</summary>
    public string? WhatProves { get; set; }
}

/// <summary>The result of running one demo step.</summary>
public sealed class DemoStepResultJson
{
    /// <summary>Zero-based step index.</summary>
    public int StepIndex { get; set; }
    /// <summary>The command that ran.</summary>
    public string Command { get; set; } = "";
    /// <summary>Process exit code.</summary>
    public int ExitCode { get; set; }
    /// <summary>Trailing lines of stdout.</summary>
    public string StdoutTail { get; set; } = "";
    /// <summary>Trailing lines of stderr.</summary>
    public string StderrTail { get; set; } = "";
    /// <summary>Step duration in seconds.</summary>
    public double DurationSec { get; set; }
    /// <summary>Whether the step's expectations were met (null if none set).</summary>
    public bool? ExpectationMet { get; set; }
    /// <summary>Why the expectation failed, if it did.</summary>
    public string? ExpectationFailureReason { get; set; }
}

/// <summary>JSON shape of a demo run report.</summary>
public sealed class DemoRunReportJson
{
    /// <summary>Demo id.</summary>
    public string DemoId { get; set; } = "";
    /// <summary>Demo title.</summary>
    public string Title { get; set; } = "";
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
    /// <summary>Overall run status.</summary>
    public string Status { get; set; } = "";
    /// <summary>Total run duration in seconds.</summary>
    public double TotalDurationSec { get; set; }
    /// <summary>Per-step results.</summary>
    public List<DemoStepResultJson> Steps { get; set; } = new();
}

/// <summary>JSON shape of a demo record (VHS) report.</summary>
public sealed class DemoRecordReportJson
{
    /// <summary>Demo id.</summary>
    public string DemoId { get; set; } = "";
    /// <summary>Demo title.</summary>
    public string Title { get; set; } = "";
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
    /// <summary>Path to the generated <c>.tape</c> file.</summary>
    public string TapeFilePath { get; set; } = "";
    /// <summary>Path to the rendered output (GIF), if VHS ran.</summary>
    public string? OutputFilePath { get; set; }
    /// <summary>True when the VHS binary was available.</summary>
    public bool VhsAvailable { get; set; }
    /// <summary>Guidance shown when VHS was unavailable.</summary>
    public string? VhsUnavailableMessage { get; set; }
    /// <summary>Duration in seconds.</summary>
    public double DurationSec { get; set; }
}

// === Cluster verb outputs (v0.6 / ADR-0009) ================================

/// <summary>A cluster member row in <c>cluster status</c> JSON.</summary>
public sealed class ClusterMemberJson
{
    /// <summary>Member hostname.</summary>
    public string Hostname { get; set; } = "";
    /// <summary>Member IP.</summary>
    public string Ip { get; set; } = "";
    /// <summary>Member role (primary/replica/etc.).</summary>
    public string Role { get; set; } = "";
    /// <summary>Member health status.</summary>
    public string Status { get; set; } = "";
    /// <summary>Shard id, for sharded topologies.</summary>
    public string? ShardId { get; set; }
    /// <summary>Replication lag in seconds, if measured.</summary>
    public double? ReplicationLagSeconds { get; set; }
}

/// <summary>JSON shape of a <c>cluster status</c> report.</summary>
public sealed class ClusterStatusOutputJson
{
    /// <summary>Cluster id.</summary>
    public string ClusterId { get; set; } = "";
    /// <summary>Display name.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Aggregate health verdict.</summary>
    public string OverallHealth { get; set; } = "";
    /// <summary>Current leader/primary, if known.</summary>
    public string? Leader { get; set; }
    /// <summary>UTC capture timestamp.</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>Member rows.</summary>
    public List<ClusterMemberJson> Members { get; set; } = new();
}

/// <summary>Relative-seconds timeline of a cluster failover.</summary>
public sealed class FailoverTimelineSecondsJson
{
    /// <summary>Seconds to complete pre-flight checks.</summary>
    public double PreFlightCompletedSec { get; set; }
    /// <summary>Seconds at which the failure was injected.</summary>
    public double FailureInjectedSec { get; set; }
    /// <summary>Seconds at which a new leader was observed.</summary>
    public double NewLeaderObservedSec { get; set; }
    /// <summary>Seconds at which recovery was attempted.</summary>
    public double RecoveryAttemptedSec { get; set; }
    /// <summary>Seconds at which the cluster was healthy again.</summary>
    public double ClusterHealthyAgainSec { get; set; }
}

/// <summary>JSON shape of a <c>cluster failover</c> report.</summary>
public sealed class ClusterFailoverOutputJson
{
    /// <summary>Scenario identifier.</summary>
    public string Scenario { get; set; } = "";
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
    /// <summary>Primary before the failover.</summary>
    public string OriginalPrimary { get; set; } = "";
    /// <summary>Primary after the failover, if any.</summary>
    public string? NewPrimary { get; set; }
    /// <summary>Measured recovery time objective (seconds).</summary>
    public double RtoSeconds { get; set; }
    /// <summary>Recovery outcome.</summary>
    public string Recovery { get; set; } = "";
    /// <summary>Operator hint when recovery was incomplete.</summary>
    public string? RecoveryHint { get; set; }
    /// <summary>The relative-seconds timeline.</summary>
    public FailoverTimelineSecondsJson? Timeline { get; set; }
}

/// <summary>JSON shape of a <c>scale-out</c> (add/remove node) report.</summary>
public sealed class ClusterScaleOutOutputJson
{
    /// <summary>Operation type (add/remove).</summary>
    public string OperationType { get; set; } = "";
    /// <summary>Outcome verdict.</summary>
    public string Outcome { get; set; } = "";
    /// <summary>Reason behind the outcome, if noteworthy.</summary>
    public string? OutcomeReason { get; set; }
    /// <summary>Nodes affected by the operation.</summary>
    public List<string> AffectedNodes { get; set; } = new();
    /// <summary>Duration in seconds.</summary>
    public double DurationSec { get; set; }
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
}

/// <summary>JSON shape of a <c>scale-up</c> (vertical resize) report.</summary>
public sealed class ClusterScaleUpOutputJson
{
    /// <summary>Target VM name.</summary>
    public string VmName { get; set; } = "";
    /// <summary>Outcome verdict.</summary>
    public string Outcome { get; set; } = "";
    /// <summary>Reason behind the outcome, if noteworthy.</summary>
    public string? OutcomeReason { get; set; }
    /// <summary>vCPU count before the change.</summary>
    public int? OldCpu { get; set; }
    /// <summary>vCPU count after the change.</summary>
    public int? NewCpu { get; set; }
    /// <summary>RAM (MB) before the change.</summary>
    public int? OldRamMb { get; set; }
    /// <summary>RAM (MB) after the change.</summary>
    public int? NewRamMb { get; set; }
    /// <summary>Disk (GB) before the change.</summary>
    public int? OldDiskGb { get; set; }
    /// <summary>Disk (GB) after the change.</summary>
    public int? NewDiskGb { get; set; }
    /// <summary>Duration in seconds.</summary>
    public double DurationSec { get; set; }
}

/// <summary>A single health probe result.</summary>
public sealed class HealthProbeJson
{
    /// <summary>Probe name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Probe target (node/endpoint).</summary>
    public string Target { get; set; } = "";
    /// <summary>Probe status verdict.</summary>
    public string Status { get; set; } = "";
    /// <summary>Observed value, if any.</summary>
    public string? Value { get; set; }
    /// <summary>Threshold the value was compared against, if any.</summary>
    public string? Threshold { get; set; }
}

/// <summary>JSON shape of a <c>cluster health</c> report.</summary>
public sealed class ClusterHealthOutputJson
{
    /// <summary>Cluster id.</summary>
    public string ClusterId { get; set; } = "";
    /// <summary>Aggregate health verdict.</summary>
    public string OverallHealth { get; set; } = "";
    /// <summary>UTC capture timestamp.</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>Individual probe results.</summary>
    public List<HealthProbeJson> Probes { get; set; } = new();
}

/// <summary>A node row in a <c>cluster topology</c> report.</summary>
public sealed class TopologyNodeJson
{
    /// <summary>Node hostname.</summary>
    public string Hostname { get; set; } = "";
    /// <summary>Node role.</summary>
    public string Role { get; set; } = "";
    /// <summary>Node status.</summary>
    public string Status { get; set; } = "";
    /// <summary>Replication lag in seconds, if measured.</summary>
    public double? ReplicationLagSeconds { get; set; }
}

/// <summary>A shard row in a <c>cluster topology</c> report.</summary>
public sealed class TopologyShardJson
{
    /// <summary>Shard id.</summary>
    public string ShardId { get; set; } = "";
    /// <summary>Shard primary hostname.</summary>
    public string Primary { get; set; } = "";
    /// <summary>Replica hostnames.</summary>
    public List<string> Replicas { get; set; } = new();
    /// <summary>Key/slot range served by the shard, if applicable.</summary>
    public string? SlotRange { get; set; }
}

/// <summary>JSON shape of a <c>cluster topology</c> report.</summary>
public sealed class ClusterTopologyOutputJson
{
    /// <summary>Cluster id.</summary>
    public string ClusterId { get; set; } = "";
    /// <summary>UTC capture timestamp.</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>Node rows.</summary>
    public List<TopologyNodeJson> Nodes { get; set; } = new();
    /// <summary>Shard rows, for sharded topologies.</summary>
    public List<TopologyShardJson>? Shards { get; set; }
}

/// <summary>JSON shape of a <c>backup take</c> report.</summary>
public sealed class ClusterBackupOutputJson
{
    /// <summary>Backup identifier.</summary>
    public string BackupId { get; set; } = "";
    /// <summary>Backup destination path/URI.</summary>
    public string Destination { get; set; } = "";
    /// <summary>Backup size in bytes.</summary>
    public long SizeBytes { get; set; }
    /// <summary>Duration in seconds.</summary>
    public double DurationSec { get; set; }
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
}

/// <summary>JSON shape of a <c>backup restore</c> report.</summary>
public sealed class ClusterRestoreOutputJson
{
    /// <summary>Backup identifier restored from.</summary>
    public string BackupId { get; set; } = "";
    /// <summary>Count of items restored.</summary>
    public long ItemsRestored { get; set; }
    /// <summary>Duration in seconds.</summary>
    public double DurationSec { get; set; }
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
}

/// <summary>Per-node result of a certificate rotation.</summary>
public sealed class CertRotatedNodeJson
{
    /// <summary>Node hostname.</summary>
    public string Hostname { get; set; } = "";
    /// <summary>Serial before rotation.</summary>
    public string OldSerial { get; set; } = "";
    /// <summary>Serial after rotation.</summary>
    public string NewSerial { get; set; } = "";
    /// <summary>Error for this node, if rotation failed.</summary>
    public string? Error { get; set; }
}

/// <summary>JSON shape of a <c>cert-rotate</c> report.</summary>
public sealed class ClusterCertRotationOutputJson
{
    /// <summary>Duration in seconds.</summary>
    public double DurationSec { get; set; }
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
    /// <summary>Per-node rotation results.</summary>
    public List<CertRotatedNodeJson> RotatedNodes { get; set; } = new();
}

/// <summary>JSON shape of a <c>chaos</c> report.</summary>
public sealed class ClusterChaosOutputJson
{
    /// <summary>The chaos scenario applied.</summary>
    public string ScenarioApplied { get; set; } = "";
    /// <summary>The chaos target.</summary>
    public string Target { get; set; } = "";
    /// <summary>True if the cluster recovered.</summary>
    public bool Recovered { get; set; }
    /// <summary>Duration in seconds.</summary>
    public double DurationSec { get; set; }
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
    /// <summary>Probes capturing the observed impact.</summary>
    public List<HealthProbeJson> ObservedImpact { get; set; } = new();
}

/// <summary>An ACL user row in a <c>cluster acl</c> report.</summary>
public sealed class AclUserJson
{
    /// <summary>User name.</summary>
    public string Name { get; set; } = "";
    /// <summary>True when the user is enabled.</summary>
    public bool Enabled { get; set; }
    /// <summary>Granted permissions.</summary>
    public List<string> Permissions { get; set; } = new();
}

/// <summary>JSON shape of a <c>cluster acl</c> report.</summary>
public sealed class ClusterAclOutputJson
{
    /// <summary>Cluster id.</summary>
    public string ClusterId { get; set; } = "";
    /// <summary>The ACL verb applied.</summary>
    public string Verb { get; set; } = "";
    /// <summary>UTC capture timestamp.</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>ACL user rows.</summary>
    public List<AclUserJson> Users { get; set; } = new();
}

// === Vault admin / raft (v0.8.1 VaultAdapter, ADR-0022) ====================

/// <summary>Response of Vault <c>/v1/sys/seal-status</c>.</summary>
public sealed class VaultSealStatusDto
{
    /// <summary>Seal type.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    /// <summary>True when the node is initialized.</summary>
    [JsonPropertyName("initialized")] public bool Initialized { get; set; }
    /// <summary>True when the node is sealed.</summary>
    [JsonPropertyName("sealed")] public bool Sealed { get; set; }
    /// <summary>Vault version string.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    /// <summary>Raft cluster name.</summary>
    [JsonPropertyName("cluster_name")] public string ClusterName { get; set; } = "";
}

/// <summary>Response of Vault <c>/v1/sys/leader</c>.</summary>
public sealed class VaultLeaderDto
{
    /// <summary>True when HA is enabled.</summary>
    [JsonPropertyName("ha_enabled")] public bool HaEnabled { get; set; }
    /// <summary>True when the queried node is itself the active node.</summary>
    [JsonPropertyName("is_self")] public bool IsSelf { get; set; }
    /// <summary>Address of the active node.</summary>
    [JsonPropertyName("leader_address")] public string LeaderAddress { get; set; } = "";
}

/// <summary>A raft peer row from <c>/v1/sys/storage/raft/configuration</c>.</summary>
public sealed class VaultRaftServerDto
{
    /// <summary>Raft node id.</summary>
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    /// <summary>Raft advertise address.</summary>
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    /// <summary>True when this peer is the raft leader.</summary>
    [JsonPropertyName("leader")] public bool Leader { get; set; }
    /// <summary>True when this peer is a voting member.</summary>
    [JsonPropertyName("voter")] public bool Voter { get; set; }
}

/// <summary>Inner <c>config</c> object of the raft-configuration response.</summary>
public sealed class VaultRaftConfigInnerDto
{
    /// <summary>The raft peer set.</summary>
    [JsonPropertyName("servers")] public List<VaultRaftServerDto>? Servers { get; set; }
}

/// <summary>The <c>data</c> wrapper of the raft-configuration response.</summary>
public sealed class VaultRaftConfigDataDto
{
    /// <summary>The inner raft config.</summary>
    [JsonPropertyName("config")] public VaultRaftConfigInnerDto? Config { get; set; }
}

/// <summary>Top-level raft-configuration response envelope.</summary>
public sealed class VaultRaftConfigResponse
{
    /// <summary>The response data wrapper.</summary>
    [JsonPropertyName("data")] public VaultRaftConfigDataDto? Data { get; set; }
}

/// <summary>The <c>data</c> wrapper of a Vault LIST response.</summary>
public sealed class VaultKeysDataDto
{
    /// <summary>The listed keys.</summary>
    [JsonPropertyName("keys")] public List<string>? Keys { get; set; }
}

/// <summary>Top-level envelope of a Vault LIST response.</summary>
public sealed class VaultListKeysResponse
{
    /// <summary>The response data wrapper.</summary>
    [JsonPropertyName("data")] public VaultKeysDataDto? Data { get; set; }
}

/// <summary>The <c>data</c> wrapper of a policy read response.</summary>
public sealed class VaultPolicyDataDto
{
    /// <summary>Policy name.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Policy HCL body.</summary>
    [JsonPropertyName("policy")] public string Policy { get; set; } = "";
}

/// <summary>Top-level envelope of a policy read response.</summary>
public sealed class VaultPolicyReadResponse
{
    /// <summary>The response data wrapper.</summary>
    [JsonPropertyName("data")] public VaultPolicyDataDto? Data { get; set; }
}

// Raft snapshot meta.json (gzip(tar) entry) -- the non-destructive "inspect".
/// <summary>The <c>meta.json</c> entry inside a raft snapshot archive.</summary>
public sealed class VaultSnapshotMetaDto
{
    /// <summary>Snapshot id.</summary>
    [JsonPropertyName("ID")] public string Id { get; set; } = "";
    /// <summary>Raft log index captured.</summary>
    [JsonPropertyName("Index")] public long Index { get; set; }
    /// <summary>Raft term captured.</summary>
    [JsonPropertyName("Term")] public long Term { get; set; }
    /// <summary>Snapshot format version.</summary>
    [JsonPropertyName("Version")] public int Version { get; set; }
    /// <summary>Snapshot payload size in bytes.</summary>
    [JsonPropertyName("Size")] public long Size { get; set; }
}

// === Recover-HA output (v0.8.1; ADR-0022) ==================================

/// <summary>Per-node outcome of a Vault HA recovery run.</summary>
public sealed class RecoverHaNodeJson
{
    /// <summary>Node hostname.</summary>
    public string Hostname { get; set; } = "";
    /// <summary>True if the node was sealed at the start.</summary>
    public bool Sealed { get; set; }
    /// <summary>Recovery outcome for the node.</summary>
    public string Outcome { get; set; } = "";
}

/// <summary>JSON shape of a Vault <c>recover-ha</c> report.</summary>
public sealed class RecoverHaOutputJson
{
    /// <summary>Cluster id.</summary>
    public string ClusterId { get; set; } = "";
    /// <summary>True if the transit auto-unseal node was unsealed.</summary>
    public bool TransitUnsealed { get; set; }
    /// <summary>True if every HA node ended unsealed.</summary>
    public bool AllUnsealed { get; set; }
    /// <summary>Resulting leader address, if known.</summary>
    public string? Leader { get; set; }
    /// <summary>Duration in seconds.</summary>
    public double DurationSec { get; set; }
    /// <summary>UTC start timestamp.</summary>
    public string StartedAtUtc { get; set; } = "";
    /// <summary>Per-node recovery outcomes.</summary>
    public List<RecoverHaNodeJson> Nodes { get; set; } = new();
}

// === Source-gen context ====================================================

/// <summary>
/// System.Text.Json source-generation context for every nexus-cli DTO and
/// report shape (camelCase, indented, null-omitting). Compile-time metadata
/// keeps JSON reflection-free and AOT-clean; the generated members need no docs.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(VaultKvV2Response))]
[JsonSerializable(typeof(VaultPkiIssueResponse))]
[JsonSerializable(typeof(ConsulAgentSelfDto))]
[JsonSerializable(typeof(List<ConsulMemberDto>))]
[JsonSerializable(typeof(NomadServerMembersDto))]
[JsonSerializable(typeof(List<NomadNodeListDto>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(PortainerSystemStatusDto))]
[JsonSerializable(typeof(PortainerAuthRequestDto))]
[JsonSerializable(typeof(PortainerAuthResponseDto))]
[JsonSerializable(typeof(ClusterStatusJsonOutput))]
[JsonSerializable(typeof(InfrastructureListJsonOutput))]
[JsonSerializable(typeof(InfrastructureStatusJsonOutput))]
[JsonSerializable(typeof(InfrastructureOpsJsonOutput))]
[JsonSerializable(typeof(FailoverTestJsonOutput))]
[JsonSerializable(typeof(KafkaFailoverJsonOutput))]
[JsonSerializable(typeof(DemoSpecJson))]
[JsonSerializable(typeof(DemoRunReportJson))]
[JsonSerializable(typeof(DemoRecordReportJson))]
[JsonSerializable(typeof(ClusterStatusOutputJson))]
[JsonSerializable(typeof(ClusterFailoverOutputJson))]
[JsonSerializable(typeof(ClusterScaleOutOutputJson))]
[JsonSerializable(typeof(ClusterScaleUpOutputJson))]
[JsonSerializable(typeof(ClusterHealthOutputJson))]
[JsonSerializable(typeof(ClusterTopologyOutputJson))]
[JsonSerializable(typeof(ClusterBackupOutputJson))]
[JsonSerializable(typeof(ClusterRestoreOutputJson))]
[JsonSerializable(typeof(ClusterCertRotationOutputJson))]
[JsonSerializable(typeof(ClusterChaosOutputJson))]
[JsonSerializable(typeof(ClusterAclOutputJson))]
[JsonSerializable(typeof(VaultSealStatusDto))]
[JsonSerializable(typeof(VaultLeaderDto))]
[JsonSerializable(typeof(VaultRaftConfigResponse))]
[JsonSerializable(typeof(VaultListKeysResponse))]
[JsonSerializable(typeof(VaultPolicyReadResponse))]
[JsonSerializable(typeof(VaultSnapshotMetaDto))]
[JsonSerializable(typeof(RecoverHaOutputJson))]
public partial class NexusJsonContext : JsonSerializerContext;
