using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="LakehouseAdapter"/> (Phase 0.L lakehouse
/// tier, nexus-cli v0.8.4). Fixtures mirror the LIVE shapes the adapter parses —
/// Spark master <c>/json/</c>, Nessie Quarkus <c>/q/health</c>, MinIO
/// <c>mc admin info --json</c> + <c>mc admin user ls --json</c>, and ZooKeeper
/// <c>echo srvr</c> — so a parser regression surfaces here rather than mid-verb
/// against the running tier.
/// </summary>
public class LakehouseAdapterParseTests
{
    // === ClassifyRole (vms.yaml name -> lakehouse role) ====================
    [Theory]
    [InlineData("minio-1", "minio")]
    [InlineData("minio-4", "minio")]
    [InlineData("iceberg-rest-1", "nessie")]
    [InlineData("iceberg-rest-2", "nessie")]
    [InlineData("iceberg-pg-1", "iceberg-pg")]
    [InlineData("iceberg-pg-2", "iceberg-pg")]
    [InlineData("spark-master-1", "spark-master")]
    [InlineData("spark-master-2", "spark-master")]
    [InlineData("spark-worker-1", "spark-worker")]
    [InlineData("spark-worker-3", "spark-worker")]
    [InlineData("zookeeper-1", "zookeeper")]
    [InlineData("zookeeper-3", "zookeeper")]
    [InlineData("vault-1", "other")]
    [InlineData("grafana-1", "other")]
    public void ClassifyRole_maps_lakehouse_nodes(string name, string expected) =>
        LakehouseAdapter.ClassifyRole(name).Should().Be(expected);

    // iceberg-pg must NOT be mistaken for a spark/minio prefix, and iceberg-rest
    // (nessie) and iceberg-pg are distinct.
    [Fact]
    public void ClassifyRole_distinguishes_iceberg_rest_from_iceberg_pg()
    {
        LakehouseAdapter.ClassifyRole("iceberg-rest-1").Should().Be("nessie");
        LakehouseAdapter.ClassifyRole("iceberg-pg-1").Should().Be("iceberg-pg");
    }

    // === ParseSparkStatus (/json/) =========================================
    private const string SparkAliveFixture = """
        { "url":"spark://192.168.70.140:7077", "status":"ALIVE", "aliveworkers":3, "cores":6,
          "workers":[ {"id":"w1","cores":2}, {"id":"w2","cores":2}, {"id":"w3","cores":2} ] }
        """;
    private const string SparkStandbyFixture = """{ "status":"STANDBY", "aliveworkers":0, "cores":0 }""";

    [Fact]
    public void ParseSparkStatus_reads_alive_leader_with_workers()
    {
        var (status, workers, cores) = LakehouseAdapter.ParseSparkStatus(SparkAliveFixture);
        status.Should().Be("ALIVE");
        workers.Should().Be(3);
        cores.Should().Be(6);
    }

    [Fact]
    public void ParseSparkStatus_reads_standby()
    {
        var (status, workers, _) = LakehouseAdapter.ParseSparkStatus(SparkStandbyFixture);
        status.Should().Be("STANDBY");
        workers.Should().Be(0);
    }

    [Fact]
    public void ParseSparkStatus_empty_on_garbage()
    {
        var (status, workers, cores) = LakehouseAdapter.ParseSparkStatus("not json");
        status.Should().BeEmpty();
        workers.Should().Be(0);
        cores.Should().Be(0);
    }

    // === ParseNessieHealth (/q/health) — the cross-tier CA split shows here =
    private const string NessieDownFixture = """
        { "status":"DOWN", "checks":[
            { "name":"Database connections health check", "status":"UP" },
            { "name":"Warehouses Object Stores", "status":"DOWN",
              "data": { "warehouse.warehouse.error": "PKIX path validation failed: Path does not chain with any of the trust anchors" } }
        ] }
        """;
    private const string NessieUpFixture = """
        { "status":"UP", "checks":[
            { "name":"Database connections health check", "status":"UP" },
            { "name":"Warehouses Object Stores", "status":"UP" } ] }
        """;

    [Fact]
    public void ParseNessieHealth_surfaces_the_down_objectstore_check()
    {
        var (overall, checks) = LakehouseAdapter.ParseNessieHealth(NessieDownFixture);
        overall.Should().Be("DOWN");
        checks.Should().Contain(c => c.Name.Contains("Object Store") && c.Status == "DOWN");
        checks.Should().Contain(c => c.Name.Contains("Database") && c.Status == "UP");
    }

    [Fact]
    public void ParseNessieHealth_all_up()
    {
        var (overall, checks) = LakehouseAdapter.ParseNessieHealth(NessieUpFixture);
        overall.Should().Be("UP");
        checks.Should().OnlyContain(c => c.Status == "UP");
    }

    // === ParseMcAdminInfo (mc admin info --json) ===========================
    private const string McInfoFixture = """
        {"status":"success","info":{"mode":"online","servers":[
            {"drives":[{"state":"ok"}]},
            {"drives":[{"state":"ok"}]},
            {"drives":[{"state":"ok"}]},
            {"drives":[{"state":"ok"}]} ]}}
        """;
    private const string McInfoDegradedFixture = """
        {"info":{"mode":"online","servers":[
            {"drives":[{"state":"ok"}]},
            {"drives":[{"state":"offline"}]} ]}}
        """;

    [Fact]
    public void ParseMcAdminInfo_counts_online_drives()
    {
        var (mode, online, offline) = LakehouseAdapter.ParseMcAdminInfo(McInfoFixture);
        mode.Should().Be("online");
        online.Should().Be(4);
        offline.Should().Be(0);
    }

    [Fact]
    public void ParseMcAdminInfo_counts_offline_drives()
    {
        var (_, online, offline) = LakehouseAdapter.ParseMcAdminInfo(McInfoDegradedFixture);
        online.Should().Be(1);
        offline.Should().Be(1);
    }

    // === ParseZkMode (echo srvr | nc) ======================================
    [Theory]
    [InlineData("Zookeeper version: 3.9.2\nMode: leader\nNode count: 12\n", "leader")]
    [InlineData("Mode: follower", "follower")]
    [InlineData("Mode: standalone", "standalone")]
    [InlineData("garbage", "")]
    public void ParseZkMode_reads_mode(string body, string expected) =>
        LakehouseAdapter.ParseZkMode(body).Should().Be(expected);

    // === ParseMcUsers (mc admin user ls --json) ============================
    private const string McUsersFixture = """
        {"status":"success","accessKey":"nexus-lakehouse-app","userStatus":"enabled"}
        {"status":"success","accessKey":"nexus-analytics-ro","userStatus":"disabled"}
        """;

    [Fact]
    public void ParseMcUsers_reads_accesskey_and_status()
    {
        var users = LakehouseAdapter.ParseMcUsers(McUsersFixture);
        users.Should().HaveCount(2);
        users.Should().ContainSingle(u => u.AccessKey == "nexus-lakehouse-app" && u.Status == "enabled");
        users.Should().ContainSingle(u => u.AccessKey == "nexus-analytics-ro" && u.Status == "disabled");
    }

    [Fact]
    public void ParseMcUsers_empty_on_garbage()
    {
        LakehouseAdapter.ParseMcUsers("not json\n").Should().BeEmpty();
    }

    // === ParseMcList (mc admin policy ls) ==================================
    [Fact]
    public void ParseMcList_reads_bare_policy_names()
    {
        var p = LakehouseAdapter.ParseMcList("readwrite\nreadonly\nlakehouse-app\n");
        p.Should().BeEquivalentTo(new[] { "readwrite", "readonly", "lakehouse-app" });
    }
}
