using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="ObservabilityAdapter"/> (Phase 0.I
/// observability tier, nexus-cli v0.8.3). Fixtures mirror the LIVE shapes the
/// adapter parses — Prometheus <c>/api/v1/targets</c>, Alertmanager
/// <c>/api/v2/status</c>, Loki/Tempo <c>/memberlist</c>, and Grafana
/// <c>/api/health</c> + <c>/api/admin/users</c> — so a parser regression surfaces
/// here rather than mid-verb against the running tier.
/// </summary>
public class ObservabilityAdapterParseTests
{
    // === ClassifyRole (vms.yaml name -> observability role) ================
    // grafana-pg-N must win over grafana-N (prefix ordering matters).

    [Theory]
    [InlineData("prom-1", "prometheus")]
    [InlineData("prom-2", "prometheus")]
    [InlineData("loki-1", "loki")]
    [InlineData("loki-3", "loki")]
    [InlineData("tempo-2", "tempo")]
    [InlineData("grafana-1", "grafana")]
    [InlineData("grafana-2", "grafana")]
    [InlineData("grafana-pg-1", "grafana-pg")]
    [InlineData("grafana-pg-2", "grafana-pg")]
    [InlineData("otel-collector-1", "otel")]
    [InlineData("otel-collector-2", "otel")]
    [InlineData("vault-1", "other")]
    [InlineData("minio-1", "other")]
    public void ClassifyRole_maps_observability_nodes(string name, string expected) =>
        ObservabilityAdapter.ClassifyRole(name).Should().Be(expected);

    // === ParsePromTargets (/api/v1/targets?state=active) ===================
    private const string PromTargetsFixture = """
        {"status":"success","data":{"activeTargets":[
          {"discoveredLabels":{"__address__":"prom-1.nexus.lab:9093","job":"alertmanager"},"labels":{"instance":"prom-1.nexus.lab:9093"},"scrapePool":"alertmanager","scrapeUrl":"https://prom-1.nexus.lab:9093/metrics","health":"up"},
          {"discoveredLabels":{"__address__":"loki-1.nexus.lab:9100","job":"node"},"labels":{"instance":"loki-1"},"scrapePool":"node","scrapeUrl":"https://loki-1:9100/metrics","health":"up"},
          {"discoveredLabels":{"__address__":"tempo-1.nexus.lab:9100","job":"node"},"labels":{"instance":"tempo-1"},"scrapePool":"node","scrapeUrl":"https://tempo-1:9100/metrics","health":"down"}
        ]}}
        """;

    [Fact]
    public void ParsePromTargets_counts_active_and_up()
    {
        var (active, up) = ObservabilityAdapter.ParsePromTargets(PromTargetsFixture);
        active.Should().Be(3);
        up.Should().Be(2);
    }

    [Fact]
    public void ParsePromTargets_empty_on_garbage()
    {
        var (active, up) = ObservabilityAdapter.ParsePromTargets("not json");
        active.Should().Be(0);
        up.Should().Be(0);
    }

    // === ParseAmPeers (/api/v2/status) =====================================
    private const string AmStatusFixture = """
        {"cluster":{"name":"01KVJ7XHCX1CZX4SZ8BY7B1A6S","peers":[
          {"address":"192.168.10.170:9094","name":"01KVJ7XHCX1CZX4SZ8BY7B1A6S"},
          {"address":"192.168.10.171:9094","name":"01KVJ7XJ8N7J75PFFMMW6AD4H7"}
        ],"status":"ready"},"versionInfo":{"version":"0.27.0"}}
        """;

    [Fact]
    public void ParseAmPeers_reads_two_ready_peers()
    {
        var (peers, status) = ObservabilityAdapter.ParseAmPeers(AmStatusFixture);
        peers.Should().Be(2);
        status.Should().Be("ready");
    }

    // === ParseMemberlistCount (Loki/Tempo /memberlist HTML) ================
    private const string LokiMemberlistFixture = """
        <html><head><title>Memberlist</title></head><body>
        <h1>Memberlist</h1><table>
        <tr><td>loki-1</td><td>192.168.10.172:7946</td><td>ALIVE</td></tr>
        <tr><td>loki-2</td><td>192.168.10.173:7946</td><td>ALIVE</td></tr>
        <tr><td>loki-3</td><td>192.168.10.174:7946</td><td>ALIVE</td></tr>
        </table></body></html>
        """;

    [Fact]
    public void ParseMemberlistCount_counts_distinct_ring_members()
    {
        ObservabilityAdapter.ParseMemberlistCount(LokiMemberlistFixture, "loki").Should().Be(3);
        ObservabilityAdapter.ParseMemberlistCount(LokiMemberlistFixture, "tempo").Should().Be(0);
    }

    [Fact]
    public void ParseMemberlistCount_dedupes_repeated_names()
    {
        var html = "tempo-1 tempo-1 tempo-2 tempo-2 tempo-3";
        ObservabilityAdapter.ParseMemberlistCount(html, "tempo").Should().Be(3);
    }

    // === ParseGrafanaHealth (/api/health) ==================================
    [Fact]
    public void ParseGrafanaHealth_reads_database_and_version()
    {
        var (db, ver) = ObservabilityAdapter.ParseGrafanaHealth(
            """{"database":"ok","version":"11.6.3","commit":"2187e5a58be60393219b4052f33dab22fffa8158"}""");
        db.Should().Be("ok");
        ver.Should().Be("11.6.3");
    }

    // === ParseGrafanaOrgUsers (/api/org/users) =============================
    // Verbatim shape from Grafana 11.6 /api/org/users (preferred over the
    // server-admin /api/admin/users, which 404s under basic auth).
    private const string GrafanaOrgUsersFixture = """
        [
          {"orgId":1,"userId":1,"uid":"ffpxu1a6upc74a","login":"admin","email":"admin@localhost","role":"Admin","isDisabled":false},
          {"orgId":1,"userId":2,"uid":"abc","login":"viewer","email":"v@nexus.lab","role":"Viewer","isDisabled":false}
        ]
        """;

    [Fact]
    public void ParseGrafanaOrgUsers_reads_userid_login_and_role()
    {
        var users = ObservabilityAdapter.ParseGrafanaOrgUsers(GrafanaOrgUsersFixture);
        users.Should().HaveCount(2);
        users.Should().ContainSingle(u => u.UserId == 1 && u.Login == "admin" && u.Role == "Admin");
        users.Should().ContainSingle(u => u.UserId == 2 && u.Login == "viewer" && u.Role == "Viewer");
    }

    [Fact]
    public void ParseGrafanaOrgUsers_empty_on_non_array() =>
        ObservabilityAdapter.ParseGrafanaOrgUsers("""{"message":"unauthorized"}""").Should().BeEmpty();
}
