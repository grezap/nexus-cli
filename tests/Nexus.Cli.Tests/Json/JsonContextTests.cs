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

    [Fact]
    public void FailoverTestJsonOutput_RoundTrips()
    {
        var dto = new FailoverTestJsonOutput
        {
            Scenario = "consul-leader",
            StartedAtUtc = "2026-05-08 09:30:00Z",
            OriginalLeader = "swarm-manager-2",
            NewLeader = "swarm-manager-1",
            RtoSeconds = 7.42,
            Recovery = "recovered",
            RecoveryHint = null,
            Timeline = new FailoverTimelineJson
            {
                PreFlightCompletedSec = 1.1,
                FailureInjectedSec = 1.3,
                NewLeaderObservedSec = 8.72,
                RecoveryAttemptedSec = 8.9,
                ClusterHealthyAgainSec = 14.5
            }
        };

        var json = JsonSerializer.Serialize(dto, NexusJsonContext.Default.FailoverTestJsonOutput);
        json.Should().Contain("\"consul-leader\"")
            .And.Contain("\"swarm-manager-2\"")
            .And.Contain("\"rtoSeconds\": 7.42");

        var round = JsonSerializer.Deserialize(json, NexusJsonContext.Default.FailoverTestJsonOutput);
        round.Should().NotBeNull();
        round!.Recovery.Should().Be("recovered");
        round.Timeline.Should().NotBeNull();
        round.Timeline!.NewLeaderObservedSec.Should().Be(8.72);
    }

    [Fact]
    public void InfrastructureListJsonOutput_RoundTrips()
    {
        var dto = new InfrastructureListJsonOutput
        {
            CapturedAtUtc = "2026-05-08 12:00:00Z",
            Vms =
            {
                new VmStatusJson
                {
                    Cluster = "foundation",
                    Name = "vault-1",
                    State = "running",
                    Os = "deb13",
                    Vmnet10 = "192.168.10.121",
                    Vmnet11 = "192.168.70.121",
                    Vmx = @"H:\VMS\NexusPlatform\01-foundation\vault-1\vault-1.vmx",
                    Role = "Vault Raft node 1"
                }
            }
        };
        var json = JsonSerializer.Serialize(dto, NexusJsonContext.Default.InfrastructureListJsonOutput);
        json.Should().Contain("\"vault-1\"").And.Contain("\"running\"");

        var rt = JsonSerializer.Deserialize(json, NexusJsonContext.Default.InfrastructureListJsonOutput);
        rt!.Vms.Single().Cluster.Should().Be("foundation");
    }

    [Fact]
    public void InfrastructureStatusJsonOutput_Serializes_Without_Reflection()
    {
        var dto = new InfrastructureStatusJsonOutput
        {
            CapturedAtUtc = "2026-05-08 12:00:00Z",
            Cluster = "foundation",
            Node = "vault-1"
        };
        var json = JsonSerializer.Serialize(dto, NexusJsonContext.Default.InfrastructureStatusJsonOutput);
        json.Should().Contain("\"foundation\"").And.Contain("\"vault-1\"");
    }

    [Fact]
    public void InfrastructureOpsJsonOutput_Serializes_Without_Reflection()
    {
        var dto = new InfrastructureOpsJsonOutput
        {
            CapturedAtUtc = "2026-05-08 12:00:00Z",
            Cluster = "foundation",
            Verb = "suspend",
            Ops =
            {
                new OpResultJson { Node = "vault-1", Success = true, Message = "suspended" }
            }
        };
        var json = JsonSerializer.Serialize(dto, NexusJsonContext.Default.InfrastructureOpsJsonOutput);
        json.Should().Contain("\"suspend\"").And.Contain("\"vault-1\"").And.Contain("\"success\": true");
    }
}
