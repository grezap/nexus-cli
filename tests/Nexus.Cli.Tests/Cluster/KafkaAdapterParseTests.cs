using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for the v0.6.7 Kafka adapters
/// (<see cref="KafkaClusterAdapter"/> + <see cref="KafkaEcosystemAdapter"/>).
/// Fixtures are verbatim captures from the LIVE kafka tier (KRaft 3.8.1)
/// taken during the v0.6.7 contract probe, so a parser regression surfaces here
/// rather than mid-verb against a cluster.
/// </summary>
public class KafkaAdapterParseTests
{
    // === ParseVoters ========================================================

    [Fact]
    public void ParseVoters_maps_backplane_ip_to_node_id()
    {
        const string line = "controller.quorum.voters=1@192.168.10.21:9093,2@192.168.10.22:9093,3@192.168.10.23:9093";
        var map = KafkaClusterAdapter.ParseVoters(line);
        map.Should().HaveCount(3);
        map["192.168.10.21"].Should().Be(1);
        map["192.168.10.22"].Should().Be(2);
        map["192.168.10.23"].Should().Be(3);
    }

    // === ParseQuorum (combined sectioned probe output) ======================

    private const string QuorumFixture = """
        ===QSTATUS===
        ClusterId:              QcN54jrfRTCubAMew8j25A
        LeaderId:               1
        LeaderEpoch:            51
        HighWatermark:          831727
        MaxFollowerLag:         0
        MaxFollowerLagTimeMs:   0
        CurrentVoters:          [1,2,3]
        CurrentObservers:       []
        ===QREPL===
        NodeId	LogEndOffset	Lag	LastFetchTimestamp	LastCaughtUpTimestamp	Status
        1     	831777      	0  	1781557044219     	1781557044219        	Leader
        2     	831777      	0  	1781557044128     	1781557044128        	Follower
        3     	831777      	0  	1781557044127     	1781557044127        	Follower
        ===UNDERREP===
        0
        ===OFFLINE===
        0
        ===END===
        """;

    [Fact]
    public void ParseQuorum_extracts_leader_voters_replicas_and_counts()
    {
        var q = KafkaClusterAdapter.ParseQuorum(QuorumFixture);
        q.LeaderId.Should().Be(1);
        q.Voters.Should().BeEquivalentTo([1, 2, 3]);
        q.Replicas.Should().HaveCount(3);
        q.Replicas.Should().OnlyContain(r => r.Lag == 0);
        q.Replicas.Single(r => r.NodeId == 1).Status.Should().Be("Leader");
        q.UnderReplicated.Should().Be(0);
        q.Offline.Should().Be(0);
    }

    [Fact]
    public void ParseQuorum_counts_under_replicated_partition_lines()
    {
        // grep -c Partition over the --under-replicated-partitions output yields a count.
        var fixture = QuorumFixture.Replace("===UNDERREP===\n0", "===UNDERREP===\n2");
        var q = KafkaClusterAdapter.ParseQuorum(fixture);
        q.UnderReplicated.Should().Be(2);
    }

    // === ParseTopics ========================================================

    private const string TopicsFixture = """
        Topic: dr-gate-test	TopicId: hnQQR19YRpCjWfJVdxQrdQ	PartitionCount: 3	ReplicationFactor: 3	Configs: min.insync.replicas=2
        	Topic: dr-gate-test	Partition: 0	Leader: 3	Replicas: 3,1,2	Isr: 3,1,2	Elr: 	LastKnownElr:
        	Topic: dr-gate-test	Partition: 1	Leader: 1	Replicas: 1,2,3	Isr: 3,1,2	Elr: 	LastKnownElr:
        	Topic: dr-gate-test	Partition: 2	Leader: 2	Replicas: 2,3,1	Isr: 3,1,2	Elr: 	LastKnownElr:
        Topic: heartbeats	TopicId: abc	PartitionCount: 1	ReplicationFactor: 3	Configs:
        	Topic: heartbeats	Partition: 0	Leader: 1	Replicas: 1,2,3	Isr: 1,2,3	Elr: 	LastKnownElr:
        """;

    [Fact]
    public void ParseTopics_builds_one_shard_per_topic_with_partition_and_rf()
    {
        var map = new Dictionary<int, string> { [1] = "kafka-east-1", [2] = "kafka-east-2", [3] = "kafka-east-3" };
        var shards = KafkaClusterAdapter.ParseTopics(TopicsFixture, map);

        shards.Should().HaveCount(2);
        var dr = shards.Single(s => s.ShardId == "dr-gate-test");
        dr.SlotRange.Should().Be("3p RF3");
        dr.Primary.Should().Be("kafka-east-3");           // partition-0 leader = node 3
        dr.Replicas.Should().BeEquivalentTo(["kafka-east-1", "kafka-east-2", "kafka-east-3"]);

        var hb = shards.Single(s => s.ShardId == "heartbeats");
        hb.SlotRange.Should().Be("1p RF3");
        hb.Primary.Should().Be("kafka-east-1");
    }

    // === ParseAcls ==========================================================

    private const string AclsFixture = """
        Current ACLs for resource `ResourcePattern(resourceType=TOPIC, name=nexus-acl-demo, patternType=LITERAL)`:
         	(principal=User:CN=nexus-test-app, host=*, operation=READ, permissionType=ALLOW)
        	(principal=User:CN=nexus-test-app, host=*, operation=WRITE, permissionType=ALLOW)
        """;

    [Fact]
    public void ParseAcls_groups_operations_by_principal()
    {
        var users = KafkaAdapterParse_ParseAcls(AclsFixture);
        users.Should().HaveCount(1);
        users[0].Name.Should().Be("User:CN=nexus-test-app");
        users[0].Permissions.Should().BeEquivalentTo(["ALLOW:READ", "ALLOW:WRITE"]);
    }

    // ParseAcls is internal static on KafkaClusterAdapter; tiny shim keeps the
    // test readable + survives a signature tweak.
    private static IReadOnlyList<Core.Models.AclUser> KafkaAdapterParse_ParseAcls(string s)
        => KafkaClusterAdapter.ParseAcls(s);

    // === NormalizePrincipal =================================================

    [Theory]
    [InlineData("nexus-app", "User:CN=nexus-app")]
    [InlineData("CN=nexus-app", "User:CN=nexus-app")]
    [InlineData("User:CN=nexus-app", "User:CN=nexus-app")]
    [InlineData("user:cn=already", "user:cn=already")] // already-prefixed (any case) passes through
    public void NormalizePrincipal_yields_full_user_cn_form(string input, string expected)
        => KafkaClusterAdapter.NormalizePrincipal(input).Should().Be(expected);

    // === KafkaEcosystemAdapter.ServiceFor ===================================

    [Theory]
    [InlineData("schema-registry-1", "schema-registry", "schema-registry.service", 8081, "/subjects")]
    [InlineData("kafka-connect-2", "kafka-connect", "connect-distributed.service", 8083, "/")]
    [InlineData("ksqldb-1", "ksqldb", "ksqldb-server.service", 8088, "/healthcheck")]
    [InlineData("kafka-rest-1", "kafka-rest", "kafka-rest.service", 8082, "/v3/clusters")]
    public void ServiceFor_maps_http_services(string host, string kind, string unit, int port, string path)
    {
        var svc = KafkaEcosystemAdapter.ServiceFor(host);
        svc.Kind.Should().Be(kind);
        svc.Unit.Should().Be(unit);
        svc.HttpPort.Should().Be(port);
        svc.HealthPath.Should().Be(path);
    }

    [Theory]
    [InlineData("mm2-1")]
    [InlineData("mm2-2")]
    public void ServiceFor_mm2_has_no_http_surface(string host)
    {
        var svc = KafkaEcosystemAdapter.ServiceFor(host);
        svc.Kind.Should().Be("mirrormaker2");
        svc.Unit.Should().Be("mm2.service");
        svc.HttpPort.Should().Be(0);
        svc.HealthPath.Should().BeNull();
    }

    // === KafkaAdapter meta delegation helpers (v0.8.6) ======================
    // The `kafka` meta-cluster merges the two per-region adapters; the merged
    // health is the WORST of the two regions, and backup ids are combined.

    [Theory]
    [InlineData("green", "green", "green")]
    [InlineData("green", "yellow", "yellow")]
    [InlineData("yellow", "green", "yellow")]
    [InlineData("green", "red", "red")]
    [InlineData("red", "yellow", "red")]
    [InlineData("yellow", "red", "red")]
    public void WorseOf_returns_the_worse_health(string a, string b, string expected) =>
        KafkaAdapter.WorseOf(a, b).Should().Be(expected);

    [Fact]
    public void SplitBackupId_splits_the_combined_east_west_id()
    {
        var (east, west) = KafkaAdapter.SplitBackupId("east-topics-20260626||west-topics-20260626");
        east.Should().Be("east-topics-20260626");
        west.Should().Be("west-topics-20260626");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-combined-id")]      // no '||' separator
    [InlineData("||west-only")]            // empty east half
    [InlineData("east-only||")]            // empty west half
    public void SplitBackupId_returns_nulls_for_non_combined_ids(string? id)
    {
        var (east, west) = KafkaAdapter.SplitBackupId(id);
        east.Should().BeNull();
        west.Should().BeNull();
    }
}
