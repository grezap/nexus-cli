using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="VitessAdapter"/> (Phase 0.O, nexus-cli
/// v0.7.2). Fixtures are verbatim from the LIVE Vitess cluster (vtctldclient
/// v24.0.1 GetTablets/GetShard JSON + the vtgate static-auth creds file)
/// captured during the v0.7.2 contract probe 2026-06-17, so a parser regression
/// surfaces here rather than mid-verb against the running cluster.
/// </summary>
public class VitessAdapterParseTests
{
    // === Classify (name -> role/shard-index) ================================

    [Theory]
    [InlineData("vitess-etcd-1", "etcd", 0)]
    [InlineData("vitess-etcd-3", "etcd", 0)]
    [InlineData("vitess-control-1", "control", 0)]
    [InlineData("vitess-vtgate-1", "vtgate", 0)]
    [InlineData("vitess-vtgate-2", "vtgate", 0)]
    [InlineData("vitess-shard1-tablet-1", "tablet", 1)]
    [InlineData("vitess-shard1-tablet-3", "tablet", 1)]
    [InlineData("vitess-shard2-tablet-1", "tablet", 2)]
    [InlineData("vitess-shard2-tablet-3", "tablet", 2)]
    public void Classify_derives_role_and_shard_index_from_name(string name, string role, int shardIndex)
    {
        var (gotRole, gotShard) = VitessAdapter.Classify(name);
        gotRole.Should().Be(role);
        gotShard.Should().Be(shardIndex);
    }

    // === ParseTabletsJson (GetTablets --format json) ========================
    // Verbatim shape from vtctldclient v24.0.1 (type: 1=primary, 2=replica).
    private const string TabletsFixture = """
        [
          { "alias": { "cell": "nexus", "uid": 100 }, "hostname": "192.168.10.196", "keyspace": "commerce", "shard": "-80", "type": 2, "mysql_hostname": "192.168.10.196", "mysql_port": 3306 },
          { "alias": { "cell": "nexus", "uid": 101 }, "hostname": "192.168.10.197", "keyspace": "commerce", "shard": "-80", "type": 1, "mysql_hostname": "192.168.10.197", "mysql_port": 3306 },
          { "alias": { "cell": "nexus", "uid": 102 }, "hostname": "192.168.10.198", "keyspace": "commerce", "shard": "-80", "type": 2, "mysql_hostname": "192.168.10.198", "mysql_port": 3306 },
          { "alias": { "cell": "nexus", "uid": 200 }, "hostname": "192.168.10.199", "keyspace": "commerce", "shard": "80-", "type": 1, "mysql_hostname": "192.168.10.199", "mysql_port": 3306 },
          { "alias": { "cell": "nexus", "uid": 201 }, "hostname": "192.168.10.200", "keyspace": "commerce", "shard": "80-", "type": 2, "mysql_hostname": "192.168.10.200", "mysql_port": 3306 },
          { "alias": { "cell": "nexus", "uid": 202 }, "hostname": "192.168.10.201", "keyspace": "commerce", "shard": "80-", "type": 2, "mysql_hostname": "192.168.10.201", "mysql_port": 3306 }
        ]
        """;

    [Fact]
    public void ParseTabletsJson_maps_uid_shard_role_and_host()
    {
        var tablets = VitessAdapter.ParseTabletsJson(TabletsFixture);

        tablets.Should().HaveCount(6);
        // One primary per shard, two replicas per shard.
        tablets.Count(t => t.Role == "primary").Should().Be(2);
        tablets.Count(t => t.Role == "replica").Should().Be(4);
        tablets.Where(t => t.Shard == "-80").Should().HaveCount(3);
        tablets.Where(t => t.Shard == "80-").Should().HaveCount(3);

        // shard -80 primary = uid 101 @ .197 (NOT the lowest uid -- it drifted via
        // a prior reparent, so we must read role from the topo, never assume).
        var p1 = tablets.Single(t => t.Shard == "-80" && t.Role == "primary");
        p1.Uid.Should().Be(101);
        p1.Vmnet10.Should().Be("192.168.10.197");

        var p2 = tablets.Single(t => t.Shard == "80-" && t.Role == "primary");
        p2.Uid.Should().Be(200);
    }

    [Fact]
    public void ParseTabletsJson_returns_empty_on_garbage()
    {
        VitessAdapter.ParseTabletsJson("not json").Should().BeEmpty();
        VitessAdapter.ParseTabletsJson("").Should().BeEmpty();
    }

    // === ParseShardPrimaryUid (GetShard) ====================================
    private const string ShardFixture = """
        {
          "keyspace": "commerce",
          "name": "-80",
          "shard": {
            "primary_alias": { "cell": "nexus", "uid": 101 },
            "primary_term_start_time": { "seconds": "1780471786", "nanoseconds": 62037707 },
            "key_range": { "start": "", "end": "gA==" },
            "is_primary_serving": true
          }
        }
        """;

    [Fact]
    public void ParseShardPrimaryUid_reads_primary_alias_uid()
    {
        VitessAdapter.ParseShardPrimaryUid(ShardFixture).Should().Be(101);
    }

    [Fact]
    public void ParseShardPrimaryUid_null_when_no_primary()
    {
        const string noPrimary = """{ "keyspace": "commerce", "name": "-80", "shard": { "is_primary_serving": false } }""";
        VitessAdapter.ParseShardPrimaryUid(noPrimary).Should().BeNull();
        VitessAdapter.ParseShardPrimaryUid("garbage").Should().BeNull();
    }

    // === ParseVtgateCreds (the vtgate static-auth file = MySQL acl users) ====
    private const string CredsFixture = """
        {
          "nexus": [
            { "Password": "3da4c2cad9c670cd0a27e20face0eb2a", "UserData": "nexus" }
          ]
        }
        """;

    [Fact]
    public void ParseVtgateCreds_lists_static_auth_users()
    {
        var users = VitessAdapter.ParseVtgateCreds(CredsFixture);
        users.Should().HaveCount(1);
        users[0].Name.Should().Be("nexus");
        users[0].Enabled.Should().BeTrue();
        users[0].Permissions.Should().ContainSingle().Which.Should().Contain("UserData=nexus");
    }

    // === MutateVtgateCreds (acl grant/revoke round-trip) ====================

    [Fact]
    public void MutateVtgateCreds_adds_a_user_and_is_reparseable()
    {
        var grown = VitessAdapter.MutateVtgateCreds(CredsFixture, "reporting", add: true);
        var users = VitessAdapter.ParseVtgateCreds(grown);
        users.Select(u => u.Name).Should().BeEquivalentTo(["nexus", "reporting"]);
        // The original operator user must survive a grant unchanged.
        grown.Should().Contain("3da4c2cad9c670cd0a27e20face0eb2a");
    }

    [Fact]
    public void MutateVtgateCreds_removes_a_user()
    {
        var grown = VitessAdapter.MutateVtgateCreds(CredsFixture, "reporting", add: true);
        var shrunk = VitessAdapter.MutateVtgateCreds(grown, "reporting", add: false);
        VitessAdapter.ParseVtgateCreds(shrunk).Select(u => u.Name).Should().BeEquivalentTo(["nexus"]);
    }

    // === ExtractJson helpers =================================================

    [Fact]
    public void ExtractJsonArray_and_object_trim_surrounding_noise()
    {
        VitessAdapter.ExtractJsonArray("warn line\n[ {\"a\":1} ]\n").Should().Be("[ {\"a\":1} ]");
        VitessAdapter.ExtractJsonObject("Using a password...\n{ \"x\": 1 }").Should().Be("{ \"x\": 1 }");
        VitessAdapter.ExtractJsonArray("no array here").Should().BeNull();
    }

    // === ParseRerender (cert-rotate force-rerender probe; GAP #12) ===========
    [Fact]
    public void ParseRerender_extracts_old_and_new_serials()
    {
        var (o, n) = VitessAdapter.ParseRerender("OLD=2FEE8AA653BF NEW=192DF4558AA2");
        o.Should().Be("2FEE8AA653BF");
        n.Should().Be("192DF4558AA2");
    }

    [Fact]
    public void ParseRerender_handles_a_first_install_with_empty_old()
    {
        var (o, n) = VitessAdapter.ParseRerender("noise\nOLD= NEW=ABCDEF01");
        o.Should().BeEmpty();
        n.Should().Be("ABCDEF01");
    }

    [Fact]
    public void ParseRerender_returns_empty_when_no_marker()
    {
        var (o, n) = VitessAdapter.ParseRerender("agent restart failed");
        o.Should().BeEmpty();
        n.Should().BeEmpty();
    }
}
