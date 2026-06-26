using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="RegistryAdapter"/> (Phase 0.L.4 Harbor
/// registry HA, nexus-cli v0.8.5). Fixtures mirror the LIVE shapes the adapter
/// parses — Harbor <c>/api/v2.0/health</c>, <c>/api/v2.0/systeminfo</c>,
/// <c>/api/v2.0/users</c>, and <c>redis-cli INFO replication</c> — so a parser
/// regression surfaces here rather than mid-verb against the running tier.
/// </summary>
public class RegistryAdapterParseTests
{
    // === ClassifyRole (platform-tools name -> registry role) ===============
    // registry-pg-N must win over registry-N (prefix ordering matters); the
    // unbuilt future platform tools (prefect/unleash/marquez/backstage) -> other.

    [Theory]
    [InlineData("registry-1", "harbor")]
    [InlineData("registry-2", "harbor")]
    [InlineData("registry-pg-1", "registry-pg")]
    [InlineData("registry-pg-2", "registry-pg")]
    [InlineData("prefect-server", "other")]
    [InlineData("unleash-1", "other")]
    [InlineData("marquez", "other")]
    [InlineData("backstage", "other")]
    [InlineData("vault-1", "other")]
    public void ClassifyRole_maps_registry_nodes(string name, string expected) =>
        RegistryAdapter.ClassifyRole(name).Should().Be(expected);

    // === ParseHarborHealth (/api/v2.0/health) ==============================
    private const string HarborHealthyFixture = """
        {"status":"healthy","components":[
          {"name":"core","status":"healthy"},
          {"name":"database","status":"healthy"},
          {"name":"redis","status":"healthy"},
          {"name":"registry","status":"healthy"},
          {"name":"registryctl","status":"healthy"},
          {"name":"jobservice","status":"healthy"},
          {"name":"portal","status":"healthy"},
          {"name":"trivy","status":"healthy"}
        ]}
        """;

    private const string HarborDegradedFixture = """
        {"status":"unhealthy","components":[
          {"name":"core","status":"healthy"},
          {"name":"database","status":"healthy"},
          {"name":"trivy","status":"unhealthy"}
        ]}
        """;

    [Fact]
    public void ParseHarborHealth_counts_healthy_components()
    {
        var (status, healthy, total) = RegistryAdapter.ParseHarborHealth(HarborHealthyFixture);
        status.Should().Be("healthy");
        healthy.Should().Be(8);
        total.Should().Be(8);
    }

    [Fact]
    public void ParseHarborHealth_reports_degraded_subset()
    {
        var (status, healthy, total) = RegistryAdapter.ParseHarborHealth(HarborDegradedFixture);
        status.Should().Be("unhealthy");
        healthy.Should().Be(2);
        total.Should().Be(3);
    }

    [Fact]
    public void ParseHarborHealth_empty_on_garbage()
    {
        var (status, healthy, total) = RegistryAdapter.ParseHarborHealth("not json");
        status.Should().BeEmpty();
        healthy.Should().Be(0);
        total.Should().Be(0);
    }

    // === ParseHarborSystemInfo (/api/v2.0/systeminfo) ======================
    [Fact]
    public void ParseHarborSystemInfo_reads_version_and_auth_mode()
    {
        var (ver, auth) = RegistryAdapter.ParseHarborSystemInfo(
            """{"harbor_version":"v2.11.0-abcdef","auth_mode":"oidc_auth","registry_url":"registry.nexus.lab","external_url":"https://registry.nexus.lab"}""");
        ver.Should().Be("v2.11.0-abcdef");
        auth.Should().Be("oidc_auth");
    }

    // === ParseHarborUsers (/api/v2.0/users) ================================
    private const string HarborUsersFixture = """
        [
          {"user_id":1,"username":"admin","email":"admin@nexus.lab","sysadmin_flag":true},
          {"user_id":2,"username":"greg","email":"greg@nexus.lab","sysadmin_flag":false},
          {"user_id":3,"username":"ci-bot","email":"ci@nexus.lab","sysadmin_flag":false}
        ]
        """;

    [Fact]
    public void ParseHarborUsers_reads_userid_username_sysadmin()
    {
        var users = RegistryAdapter.ParseHarborUsers(HarborUsersFixture);
        users.Should().HaveCount(3);
        users.Should().ContainSingle(u => u.UserId == 1 && u.Username == "admin" && u.SysAdmin);
        users.Should().ContainSingle(u => u.UserId == 2 && u.Username == "greg" && !u.SysAdmin);
    }

    [Fact]
    public void ParseHarborUsers_empty_on_non_array() =>
        RegistryAdapter.ParseHarborUsers("""{"errors":[{"code":"UNAUTHORIZED"}]}""").Should().BeEmpty();

    // === ParseRedisReplication (redis-cli INFO replication) ================
    private const string RedisMasterFixture =
        "# Replication\r\nrole:master\r\nconnected_slaves:1\r\nslave0:ip=192.168.10.118,port=6379,state=online,offset=616,lag=0\r\nmaster_repl_offset:616\r\n";

    private const string RedisSlaveFixture =
        "# Replication\r\nrole:slave\r\nmaster_host:192.168.10.117\r\nmaster_port:6379\r\nmaster_link_status:up\r\nslave_repl_offset:616\r\n";

    [Fact]
    public void ParseRedisReplication_reads_master_with_connected_slave()
    {
        var (role, connected) = RegistryAdapter.ParseRedisReplication(RedisMasterFixture);
        role.Should().Be("master");
        connected.Should().Be(1);
    }

    [Fact]
    public void ParseRedisReplication_reads_slave_link_up()
    {
        var (role, connected) = RegistryAdapter.ParseRedisReplication(RedisSlaveFixture);
        role.Should().Be("slave");
        connected.Should().Be(1); // master_link_status:up
    }

    [Fact]
    public void ParseRedisReplication_empty_on_blank() =>
        RegistryAdapter.ParseRedisReplication("").Role.Should().BeEmpty();

    // === CountJsonArray (projects / robots) ================================
    [Fact]
    public void CountJsonArray_counts_elements() =>
        RegistryAdapter.CountJsonArray("""[{"project_id":1},{"project_id":2},{"project_id":3}]""").Should().Be(3);

    [Fact]
    public void CountJsonArray_zero_on_non_array() =>
        RegistryAdapter.CountJsonArray("""{"message":"forbidden"}""").Should().Be(0);
}
