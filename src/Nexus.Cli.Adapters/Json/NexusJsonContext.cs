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
public partial class NexusJsonContext : JsonSerializerContext;
