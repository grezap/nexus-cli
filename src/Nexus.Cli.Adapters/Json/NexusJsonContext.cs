using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Cli.Adapters.Json;

// === Vault KV-v2 ============================================================

public sealed class VaultKvV2Response
{
    [JsonPropertyName("data")]
    public VaultKvV2Envelope? Data { get; set; }
}

public sealed class VaultKvV2Envelope
{
    [JsonPropertyName("data")]
    public Dictionary<string, string>? Data { get; set; }
}

// === Consul =================================================================

public sealed class ConsulMemberDto
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("Addr")] public string Addr { get; set; } = "";
    [JsonPropertyName("Port")] public int Port { get; set; }
    [JsonPropertyName("Status")] public int Status { get; set; }
    [JsonPropertyName("Tags")] public Dictionary<string, string>? Tags { get; set; }
}

public sealed class ConsulAgentSelfDto
{
    [JsonPropertyName("Config")] public ConsulAgentConfigDto? Config { get; set; }
    [JsonPropertyName("Stats")] public Dictionary<string, Dictionary<string, string>>? Stats { get; set; }
}

public sealed class ConsulAgentConfigDto
{
    [JsonPropertyName("Datacenter")] public string Datacenter { get; set; } = "";
    [JsonPropertyName("NodeName")] public string NodeName { get; set; } = "";
}

// === Nomad ==================================================================

public sealed class NomadServerMemberDto
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("Addr")] public string Addr { get; set; } = "";
    [JsonPropertyName("Port")] public int Port { get; set; }
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("Tags")] public Dictionary<string, string>? Tags { get; set; }
}

public sealed class NomadServerMembersDto
{
    [JsonPropertyName("ServerName")] public string ServerName { get; set; } = "";
    [JsonPropertyName("Members")] public List<NomadServerMemberDto> Members { get; set; } = new();
}

public sealed class NomadNodeListDto
{
    [JsonPropertyName("ID")] public string Id { get; set; } = "";
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("Address")] public string Address { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("NodeClass")] public string NodeClass { get; set; } = "";
}

// === Portainer ==============================================================

public sealed class PortainerSystemStatusDto
{
    [JsonPropertyName("Version")] public string Version { get; set; } = "";
    [JsonPropertyName("InstanceID")] public string InstanceId { get; set; } = "";
}

public sealed class PortainerAuthRequestDto
{
    [JsonPropertyName("Username")] public string Username { get; set; } = "";
    [JsonPropertyName("Password")] public string Password { get; set; } = "";
}

public sealed class PortainerAuthResponseDto
{
    [JsonPropertyName("jwt")] public string Jwt { get; set; } = "";
}

// === ClusterStatusReport JSON output =======================================

public sealed class ClusterStatusJsonOutput
{
    public string Overall { get; set; } = "";
    public string CapturedAtUtc { get; set; } = "";
    public ConsulSection? Consul { get; set; }
    public NomadSection? Nomad { get; set; }
    public PortainerSection? Portainer { get; set; }
}

public sealed class ConsulSection
{
    public string? Error { get; set; }
    public int? Alive { get; set; }
    public int? Failed { get; set; }
    public string? Leader { get; set; }
    public List<ConsulMemberJson>? Members { get; set; }
}

public sealed class ConsulMemberJson
{
    public string Name { get; set; } = "";
    public string Addr { get; set; } = "";
    public string Status { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class NomadSection
{
    public string? Error { get; set; }
    public List<NomadServerJson>? Servers { get; set; }
    public List<NomadClientJson>? Clients { get; set; }
    public string? LeaderAddress { get; set; }
}

public sealed class NomadServerJson
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public bool IsLeader { get; set; }
}

public sealed class NomadClientJson
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Status { get; set; } = "";
    public string NodeClass { get; set; } = "";
}

public sealed class PortainerSection
{
    public string? Error { get; set; }
    public string? Version { get; set; }
    public string? InstanceId { get; set; }
    public bool? Reachable { get; set; }
}

// === Infrastructure (v0.2) JSON output =====================================

public sealed class VmStatusJson
{
    public string Cluster { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string Os { get; set; } = "";
    public string Vmnet10 { get; set; } = "";
    public string Vmnet11 { get; set; } = "";
    public string Vmx { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class OpResultJson
{
    public string Node { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public sealed class InfrastructureListJsonOutput
{
    public string CapturedAtUtc { get; set; } = "";
    public List<VmStatusJson> Vms { get; set; } = new();
}

public sealed class InfrastructureStatusJsonOutput
{
    public string CapturedAtUtc { get; set; } = "";
    public string Cluster { get; set; } = "";
    public string? Node { get; set; }
    public List<VmStatusJson> Vms { get; set; } = new();
}

public sealed class InfrastructureOpsJsonOutput
{
    public string CapturedAtUtc { get; set; } = "";
    public string Cluster { get; set; } = "";
    public string Verb { get; set; } = "";
    public List<OpResultJson> Ops { get; set; } = new();
}

// === FailoverTestReport JSON output (v0.3) ==================================

public sealed class FailoverTimelineJson
{
    public double PreFlightCompletedSec { get; set; }
    public double FailureInjectedSec { get; set; }
    public double NewLeaderObservedSec { get; set; }
    public double RecoveryAttemptedSec { get; set; }
    public double ClusterHealthyAgainSec { get; set; }
}

public sealed class FailoverTestJsonOutput
{
    public string Scenario { get; set; } = "";
    public string StartedAtUtc { get; set; } = "";
    public string OriginalLeader { get; set; } = "";
    public string? NewLeader { get; set; }
    public double RtoSeconds { get; set; }
    public string Recovery { get; set; } = "";
    public string? RecoveryHint { get; set; }
    public FailoverTimelineJson? Timeline { get; set; }
}

// === KafkaFailoverReport JSON output (v0.5) ================================

public sealed class KafkaFailoverTimelineJson
{
    public double PreFlightCompletedSec { get; set; }
    public double FailureInjectedSec { get; set; }
    public double TargetHealthySec { get; set; }
    public double RecoveryAttemptedSec { get; set; }
    public double SourceHealthyAgainSec { get; set; }
}

public sealed class KafkaFailoverJsonOutput
{
    public string Direction { get; set; } = "";
    public string StartedAtUtc { get; set; } = "";
    public string SourceCluster { get; set; } = "";
    public string TargetCluster { get; set; } = "";
    public List<string> SuspendedBrokers { get; set; } = new();
    public bool TargetServedAfterFailure { get; set; }
    public string? TargetProbeToken { get; set; }
    public double RtoSeconds { get; set; }
    public string Recovery { get; set; } = "";
    public string? RecoveryHint { get; set; }
    public KafkaFailoverTimelineJson? Timeline { get; set; }
}

// === Demo (v0.4) ===========================================================
// Extended in v0.6 (ADR-0009) with 5 optional fields on the spec/step DTOs.
// All additions are nullable -- DefaultIgnoreCondition.WhenWritingNull on the
// source-gen context means absent fields don't appear in emitted JSON, so the
// existing DEMO-01 / DEMO-02 specs deserialize + reserialize unchanged.

public sealed class DemoObservationJson
{
    public string? Where { get; set; }
    public string? What { get; set; }
}

public sealed class DemoPrerequisitesJson
{
    public List<string>? VmsAlive { get; set; }
    public List<string>? EnvVars { get; set; }
}

public sealed class DemoStepJson
{
    public string? Command { get; set; }
    public double WaitAfterSeconds { get; set; }
    public int? ExpectedExitCode { get; set; }
    public List<string>? ExpectedOutputContains { get; set; }
    public List<DemoObservationJson>? Observe { get; set; }
}

public sealed class DemoSpecJson
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<DemoStepJson>? Steps { get; set; }
    public DemoPrerequisitesJson? Prerequisites { get; set; }
    public string? WhatProves { get; set; }
}

public sealed class DemoStepResultJson
{
    public int StepIndex { get; set; }
    public string Command { get; set; } = "";
    public int ExitCode { get; set; }
    public string StdoutTail { get; set; } = "";
    public string StderrTail { get; set; } = "";
    public double DurationSec { get; set; }
    public bool? ExpectationMet { get; set; }
    public string? ExpectationFailureReason { get; set; }
}

public sealed class DemoRunReportJson
{
    public string DemoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string StartedAtUtc { get; set; } = "";
    public string Status { get; set; } = "";
    public double TotalDurationSec { get; set; }
    public List<DemoStepResultJson> Steps { get; set; } = new();
}

public sealed class DemoRecordReportJson
{
    public string DemoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string StartedAtUtc { get; set; } = "";
    public string TapeFilePath { get; set; } = "";
    public string? OutputFilePath { get; set; }
    public bool VhsAvailable { get; set; }
    public string? VhsUnavailableMessage { get; set; }
    public double DurationSec { get; set; }
}

// === Source-gen context ====================================================

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(VaultKvV2Response))]
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
public partial class NexusJsonContext : JsonSerializerContext;
