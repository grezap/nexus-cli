using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Core.Models;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Tests for the shared <see cref="PgSslCertRotator"/> (v0.8.7 GAP #5 grafana-pg + #6
/// iceberg-pg PG-ssl cert-rotate). The safety-critical piece is the ordering: the standby
/// (any non-primary) must rotate FIRST and the write-primary LAST, so a failed standby
/// rotation never leaves the sole-serving primary rotated blind.
/// </summary>
public class PgSslCertRotatorTests
{
    private static NodeRecord Pg(string name, string vmnet11) =>
        new(name, "deb13", vmnet11.Replace("70.", "10.", StringComparison.Ordinal), vmnet11, $@"H:\VMS\{name}", name.StartsWith("grafana", StringComparison.Ordinal) ? "grafana-pg" : "iceberg-pg");

    private static readonly NodeRecord Pg1 = Pg("grafana-pg-1", "192.168.70.180");
    private static readonly NodeRecord Pg2 = Pg("grafana-pg-2", "192.168.70.181");

    [Fact]
    public void OrderStandbyFirst_puts_the_primary_last_when_pg1_is_primary()
    {
        var ordered = PgSslCertRotator.OrderStandbyFirst(new[] { Pg1, Pg2 }, "192.168.70.180");
        ordered.Select(n => n.Name).Should().Equal("grafana-pg-2", "grafana-pg-1"); // standby (pg2) first, primary (pg1) last
    }

    [Fact]
    public void OrderStandbyFirst_puts_the_primary_last_when_pg2_is_primary()
    {
        var ordered = PgSslCertRotator.OrderStandbyFirst(new[] { Pg1, Pg2 }, "192.168.70.181");
        ordered.Select(n => n.Name).Should().Equal("grafana-pg-1", "grafana-pg-2"); // standby (pg1) first, primary (pg2) last
    }

    [Fact]
    public void OrderStandbyFirst_is_order_independent_of_input()
    {
        // Even if vms.yaml lists the primary first, it still rotates last.
        var ordered = PgSslCertRotator.OrderStandbyFirst(new[] { Pg2, Pg1 }, "192.168.70.180");
        ordered.Last().Vmnet11.Should().Be("192.168.70.180"); // the primary is always last
    }

    [Fact]
    public void OrderStandbyFirst_falls_back_to_stable_name_order_when_primary_unknown()
    {
        var ordered = PgSslCertRotator.OrderStandbyFirst(new[] { Pg2, Pg1 }, primaryIp: null);
        ordered.Select(n => n.Name).Should().Equal("grafana-pg-1", "grafana-pg-2"); // deterministic by name
    }

    [Fact]
    public void OrderStandbyFirst_handles_a_single_node()
    {
        var ordered = PgSslCertRotator.OrderStandbyFirst(new[] { Pg1 }, "192.168.70.180");
        ordered.Should().ContainSingle().Which.Name.Should().Be("grafana-pg-1");
    }
}
