using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Core.Models;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="MongoShardedAdapter"/> (Phase 0.N,
/// nexus-cli v0.7.1). Fixtures are verbatim from the LIVE sharded cluster
/// (MongoDB 8.0, keyFile auth + 0.N.1 wire mTLS) captured during the v0.7.1
/// contract probe 2026-06-16, so a parser regression surfaces here rather than
/// mid-verb.
/// </summary>
public class MongoShardedAdapterParseTests
{
    // === Classify (name -> role/rs/port) ====================================

    [Theory]
    [InlineData("mongo-cfg-1", "configsvr", "config", 27019)]
    [InlineData("mongo-cfg-3", "configsvr", "config", 27019)]
    [InlineData("mongo-shard-1-1", "shardsvr", "shard-1", 27018)]
    [InlineData("mongo-shard-1-3", "shardsvr", "shard-1", 27018)]
    [InlineData("mongo-shard-2-2", "shardsvr", "shard-2", 27018)]
    [InlineData("mongo-mongos-1", "mongos", "", 27017)]
    [InlineData("mongo-mongos-2", "mongos", "", 27017)]
    public void Classify_derives_role_rs_and_port_from_name(string name, string role, string rs, int port)
    {
        var (gotRole, gotRs, gotPort) = MongoShardedAdapter.Classify(name);
        gotRole.Should().Be(role);
        gotRs.Should().Be(rs);
        gotPort.Should().Be(port);
    }

    // === ParseRsStatusJson ==================================================

    private const string ConfigRsFixture = """
        {"set":"config","members":[
          {"n":"192.168.70.74:27019","s":"PRIMARY","h":1,"o":1718553600000},
          {"n":"192.168.70.75:27019","s":"SECONDARY","h":1,"o":1718553598000},
          {"n":"192.168.70.76:27019","s":"SECONDARY","h":1,"o":1718553600000}
        ]}
        """;

    private static Dictionary<string, NodeRecord> ConfigEndpoints() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["192.168.70.74:27019"] = new NodeRecord("mongo-cfg-1", "deb13", "192.168.10.74", "192.168.70.74", "", "config-server"),
        ["192.168.70.75:27019"] = new NodeRecord("mongo-cfg-2", "deb13", "192.168.10.75", "192.168.70.75", "", "config-server"),
        ["192.168.70.76:27019"] = new NodeRecord("mongo-cfg-3", "deb13", "192.168.10.76", "192.168.70.76", "", "config-server"),
    };

    [Fact]
    public void ParseRsStatusJson_maps_members_leader_and_shardid()
    {
        var (members, leader) = MongoShardedAdapter.ParseRsStatusJson(ConfigRsFixture, ConfigEndpoints(), "config");

        members.Should().HaveCount(3);
        leader.Should().Be("mongo-cfg-1");
        members.Should().OnlyContain(m => m.ShardId == "config");
        members.Count(m => m.Role == "primary").Should().Be(1);
        members.Count(m => m.Role == "secondary").Should().Be(2);
        members.Should().OnlyContain(m => m.Status == "alive");
        // Endpoint -> friendly hostname mapping.
        members.Single(m => m.Role == "primary").Hostname.Should().Be("mongo-cfg-1");
    }

    [Fact]
    public void ParseRsStatusJson_computes_secondary_lag_against_primary_optime()
    {
        var (members, _) = MongoShardedAdapter.ParseRsStatusJson(ConfigRsFixture, ConfigEndpoints(), "config");
        // cfg-2 optime is 2s behind the primary; cfg-3 is caught up.
        var cfg2 = members.Single(m => m.Hostname == "mongo-cfg-2");
        cfg2.ReplicationLagSeconds.Should().BeApproximately(2.0, 0.001);
        var cfg3 = members.Single(m => m.Hostname == "mongo-cfg-3");
        cfg3.ReplicationLagSeconds.Should().Be(0);
    }

    [Fact]
    public void ParseRsStatusJson_marks_unhealthy_member_failed()
    {
        const string degraded = """
            {"set":"shard-1","members":[
              {"n":"192.168.70.77:27018","s":"PRIMARY","h":1,"o":100},
              {"n":"192.168.70.78:27018","s":"SECONDARY","h":1,"o":100},
              {"n":"192.168.70.79:27018","s":"(not reachable/healthy)","h":0,"o":0}
            ]}
            """;
        var byEndpoint = new Dictionary<string, NodeRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["192.168.70.77:27018"] = new NodeRecord("mongo-shard-1-1", "deb13", "192.168.10.77", "192.168.70.77", "", ""),
            ["192.168.70.78:27018"] = new NodeRecord("mongo-shard-1-2", "deb13", "192.168.10.78", "192.168.70.78", "", ""),
            ["192.168.70.79:27018"] = new NodeRecord("mongo-shard-1-3", "deb13", "192.168.10.79", "192.168.70.79", "", ""),
        };
        var (members, leader) = MongoShardedAdapter.ParseRsStatusJson(degraded, byEndpoint, "shard-1");
        leader.Should().Be("mongo-shard-1-1");
        members.Single(m => m.Hostname == "mongo-shard-1-3").Status.Should().Be("failed");
        members.Should().OnlyContain(m => m.ShardId == "shard-1");
    }

    [Fact]
    public void ParseRsStatusJson_returns_empty_on_garbage()
    {
        var (members, leader) = MongoShardedAdapter.ParseRsStatusJson("not json at all", ConfigEndpoints(), "config");
        members.Should().BeEmpty();
        leader.Should().BeNull();
    }

    // === ParseRerender (0.N.1 cert-rotate force-rerender serial probe) ========
    [Fact]
    public void ParseRerender_extracts_old_and_new_serials()
    {
        var (o, n) = MongoShardedAdapter.ParseRerender("OLD=2FEE8AA653BF NEW=192DF4558AA2");
        o.Should().Be("2FEE8AA653BF");
        n.Should().Be("192DF4558AA2");
    }

    [Fact]
    public void ParseRerender_handles_a_first_install_with_empty_old()
    {
        var (o, n) = MongoShardedAdapter.ParseRerender("noise\nOLD= NEW=ABCDEF01");
        o.Should().BeEmpty();
        n.Should().Be("ABCDEF01");
    }

    [Fact]
    public void ParseRerender_returns_empty_when_no_marker()
    {
        var (o, n) = MongoShardedAdapter.ParseRerender("vault-agent restart failed");
        o.Should().BeEmpty();
        n.Should().BeEmpty();
    }
}
