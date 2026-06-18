using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="CitusAdapter"/> (Phase 0.P, nexus-cli
/// v0.7.3). Fixtures are verbatim from the LIVE citus cluster (Patroni 4.0.5
/// `patronictl list --format json` per scope) captured during the v0.7.3
/// contract probe 2026-06-18, so a parser regression surfaces here rather than
/// mid-verb against the running cluster.
/// </summary>
public class CitusAdapterParseTests
{
    // === GroupOf / IsEtcd (name -> node-group) ==============================

    [Theory]
    [InlineData("citus-etcd-1")]
    [InlineData("citus-etcd-3")]
    public void IsEtcd_true_for_etcd_nodes(string name) => CitusAdapter.IsEtcd(name).Should().BeTrue();

    [Theory]
    [InlineData("citus-coord-1")]
    [InlineData("citus-worker1-2")]
    public void IsEtcd_false_for_pg_nodes(string name) => CitusAdapter.IsEtcd(name).Should().BeFalse();

    [Theory]
    [InlineData("citus-coord-1", "citus-coord", 0)]
    [InlineData("citus-coord-2", "citus-coord", 0)]
    [InlineData("citus-worker1-1", "citus-worker1", 1)]
    [InlineData("citus-worker1-2", "citus-worker1", 1)]
    [InlineData("citus-worker2-1", "citus-worker2", 2)]
    [InlineData("citus-worker2-2", "citus-worker2", 2)]
    public void GroupOf_maps_pg_node_to_scope_and_groupid(string name, string scope, int groupId)
    {
        var g = CitusAdapter.GroupOf(name);
        g.Should().NotBeNull();
        g!.Scope.Should().Be(scope);
        g.GroupId.Should().Be(groupId);
    }

    [Theory]
    [InlineData("citus-etcd-1")]
    [InlineData("not-a-node")]
    public void GroupOf_null_for_non_pg(string name) => CitusAdapter.GroupOf(name).Should().BeNull();

    // === ParsePatroniList (patronictl list --format json) ===================
    // Verbatim shape from Patroni 4.0.5: the coordinator scope with the leader
    // drifted to citus-coord-1 and a streaming replica with 0 lag.
    private const string CoordFixture = """
        [
          {"Cluster": "citus-coord", "Member": "citus-coord-1", "Host": "192.168.70.205", "Role": "Leader", "State": "running", "TL": 2},
          {"Cluster": "citus-coord", "Member": "citus-coord-2", "Host": "192.168.70.206", "Role": "Replica", "State": "streaming", "TL": 2, "Lag in MB": 0}
        ]
        """;

    // worker1 -- LEADER drifted to citus-worker1-2 (NOT the lowest member name);
    // proves we must read the role from patronictl, never assume.
    private const string Worker1Fixture = """
        [
          {"Cluster": "citus-worker1", "Member": "citus-worker1-1", "Host": "192.168.70.207", "Role": "Replica", "State": "streaming", "TL": 3, "Lag in MB": 0},
          {"Cluster": "citus-worker1", "Member": "citus-worker1-2", "Host": "192.168.70.208", "Role": "Leader", "State": "running", "TL": 3}
        ]
        """;

    [Fact]
    public void ParsePatroniList_maps_member_role_state_lag()
    {
        var members = CitusAdapter.ParsePatroniList(CoordFixture);
        members.Should().HaveCount(2);
        members.Should().ContainSingle(m => CitusAdapter.RoleOf(m) == "primary");

        var leader = members.Single(m => CitusAdapter.RoleOf(m) == "primary");
        leader.Member.Should().Be("citus-coord-1");
        leader.Scope.Should().Be("citus-coord");
        CitusAdapter.StatusOf(leader).Should().Be("alive");

        var replica = members.Single(m => CitusAdapter.RoleOf(m) == "replica");
        replica.State.Should().Be("streaming");
        replica.LagMb.Should().Be(0);
        CitusAdapter.StatusOf(replica).Should().Be("alive");
    }

    [Fact]
    public void ParsePatroniList_reads_drifted_worker_leader()
    {
        var members = CitusAdapter.ParsePatroniList(Worker1Fixture);
        var leader = members.Single(m => CitusAdapter.RoleOf(m) == "primary");
        leader.Member.Should().Be("citus-worker1-2");   // drifted off the lowest name
        leader.Host.Should().Be("192.168.70.208");
    }

    [Fact]
    public void ParsePatroniList_returns_empty_on_garbage()
    {
        CitusAdapter.ParsePatroniList("not json").Should().BeEmpty();
        CitusAdapter.ParsePatroniList("").Should().BeEmpty();
        CitusAdapter.ParsePatroniList("patronictl: error\n").Should().BeEmpty();
    }

    [Fact]
    public void ParsePatroniList_tolerates_leading_warning_noise()
    {
        var noisy = "WARNING: ...\n" + CoordFixture;
        CitusAdapter.ParsePatroniList(noisy).Should().HaveCount(2);
    }

    // === RoleOf / StatusOf classification ===================================

    [Theory]
    [InlineData("Leader", "primary")]
    [InlineData("Replica", "replica")]
    [InlineData("Sync Standby", "replica")]
    public void RoleOf_maps_patroni_roles(string role, string expected)
    {
        var m = new CitusAdapter.PgMember("citus-coord", "x", "h", role, "running", null);
        CitusAdapter.RoleOf(m).Should().Be(expected);
    }

    [Theory]
    [InlineData("running", "alive")]
    [InlineData("streaming", "alive")]
    [InlineData("starting", "syncing")]
    [InlineData("stopped", "failed")]
    public void StatusOf_maps_patroni_states(string state, string expected)
    {
        var m = new CitusAdapter.PgMember("citus-coord", "x", "h", "Replica", state, null);
        CitusAdapter.StatusOf(m).Should().Be(expected);
    }
}
