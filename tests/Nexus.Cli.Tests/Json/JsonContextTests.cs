using System.Text.Json;
using FluentAssertions;
using Nexus.Cli.Adapters.Json;
using Xunit;

namespace Nexus.Cli.Tests.Json;

public class JsonContextTests
{
    [Fact]
    public void VaultKvV2Response_RoundTrips()
    {
        const string body = """
        {"data":{"data":{"value":"abc-123"}}}
        """;

        var dto = JsonSerializer.Deserialize(body, NexusJsonContext.Default.VaultKvV2Response);
        dto.Should().NotBeNull();
        dto!.Data!.Data.Should().ContainKey("value").WhoseValue.Should().Be("abc-123");
    }

    [Fact]
    public void ConsulMembers_RoundTrips()
    {
        const string body = """
        [
          {"Name":"swarm-manager-1","Addr":"192.168.10.111","Port":8301,"Status":1,"Tags":{"role":"consul","dc":"dc1"}},
          {"Name":"swarm-manager-2","Addr":"192.168.10.112","Port":8301,"Status":1,"Tags":{"role":"consul","dc":"dc1"}}
        ]
        """;

        var dto = JsonSerializer.Deserialize(body, NexusJsonContext.Default.ListConsulMemberDto);
        dto.Should().HaveCount(2);
        dto![0].Name.Should().Be("swarm-manager-1");
        dto[0].Status.Should().Be(1);
        dto[0].Tags.Should().ContainKey("role").WhoseValue.Should().Be("consul");
    }

    [Fact]
    public void NomadServerMembers_RoundTrips()
    {
        const string body = """
        {"ServerName":"swarm-manager-1.global","Members":[
          {"Name":"swarm-manager-1.global","Addr":"192.168.10.111","Port":4648,"Status":"alive","Tags":{}}
        ]}
        """;

        var dto = JsonSerializer.Deserialize(body, NexusJsonContext.Default.NomadServerMembersDto);
        dto.Should().NotBeNull();
        dto!.ServerName.Should().Be("swarm-manager-1.global");
        dto.Members.Should().ContainSingle().Which.Status.Should().Be("alive");
    }

    [Fact]
    public void NomadNodes_RoundTrips()
    {
        const string body = """
        [
          {"ID":"abc","Name":"swarm-worker-1","Address":"192.168.10.131","Status":"ready","NodeClass":""}
        ]
        """;

        var dto = JsonSerializer.Deserialize(body, NexusJsonContext.Default.ListNomadNodeListDto);
        dto.Should().ContainSingle();
        dto![0].Status.Should().Be("ready");
    }

    [Fact]
    public void PortainerSystemStatus_RoundTrips()
    {
        const string body = """
        {"Version":"2.21.0","InstanceID":"e3b0c442-...-1c149afb"}
        """;

        var dto = JsonSerializer.Deserialize(body, NexusJsonContext.Default.PortainerSystemStatusDto);
        dto!.Version.Should().Be("2.21.0");
    }

    [Fact]
    public void ClusterStatusJsonOutput_Serializes_Without_Reflection()
    {
        var dto = new ClusterStatusJsonOutput
        {
            Overall = "green",
            CapturedAtUtc = "2026-05-07 12:34:56Z",
            Consul = new ConsulSection { Alive = 6, Failed = 0, Leader = "192.168.10.111:8300" }
        };

        var json = JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterStatusJsonOutput);
        json.Should().Contain("\"green\"").And.Contain("\"alive\": 6");
    }
}
