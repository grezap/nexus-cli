using FluentAssertions;
using Nexus.Cli.Adapters.Inventory;
using Xunit;

namespace Nexus.Cli.Tests.Inventory;

public class VmsYamlCatalogTests
{
    // Hand-crafted fixture mirroring the canonical vms.yaml structural quirks:
    //   - two top-level `clusters:` blocks (edge first, foundation+swarm second)
    //   - free-form phase/purpose strings with punctuation
    //   - quoted role strings (with internal commas to test the splitter)
    //   - a `virtual_ips:` sub-block that the reader must skip
    //   - inline `# ...` comments and blank lines around the structure
    private const string Fixture = """
        # vms.yaml fixture for VmsYamlCatalogTests
        metadata:
          updated: 2026-05-08

        networks:
          vmnet10:
            mode: host-only

        clusters:

          edge:
            purpose: Internet egress, DHCP, DNS
            phase: 0.B
            nodes:
              - { name: nexus-gateway, os: deb13, vmnet10: 192.168.10.1, vmnet11: 192.168.70.1, dir: H:\VMS\NexusPlatform\00-edge\nexus-gateway, role: "Bridged NIC0 + VMnet11 NIC1, gateway, dnsmasq" }

        storage:
          active: H:\VMS

        clusters:

          foundation:
            purpose: AD, DNS, Vault
            phase: 0.A-0.D / 0.I
            nodes:
              # comment between fields
              - { name: dc-nexus, os: ws2025-desktop, vmnet10: 192.168.10.10, vmnet11: 192.168.70.10, dir: H:\VMS\NexusPlatform\01-foundation\dc-nexus, role: "AD DC + DNS" }
              - { name: vault-1, os: deb13, vmnet10: 192.168.10.121, vmnet11: 192.168.70.121, dir: H:\VMS\NexusPlatform\01-foundation\vault-1, role: "Vault Raft node 1" }

          sqlserver:
            purpose: SQL Server FCI + AG
            phase: 0.G / 1
            nodes:
              - { name: sql-fci-1, os: ws2025-desktop, vmnet10: 192.168.10.11, vmnet11: 192.168.70.11, dir: H:\VMS\NexusPlatform\02-sqlserver\sql-fci-1, role: "FCI node 1 (WSFC)" }
              - { name: sql-fci-2, os: ws2025-desktop, vmnet10: 192.168.10.12, vmnet11: 192.168.70.12, dir: H:\VMS\NexusPlatform\02-sqlserver\sql-fci-2, role: "FCI node 2 (WSFC)" }
            virtual_ips:
              wsfc: 192.168.70.15
              fci: 192.168.70.16
              ag_listener: 192.168.70.17
        """;

    [Fact]
    public void Parse_Reads_Both_Clusters_Roots_And_All_Three_Clusters()
    {
        var lines = Fixture.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var clusters = VmsYamlCatalog.Parse(lines);

        clusters.Should().HaveCount(3);
        clusters.Keys.Should().BeEquivalentTo("edge", "foundation", "sqlserver");
    }

    [Fact]
    public void Parse_Captures_Cluster_Metadata()
    {
        var lines = Fixture.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var clusters = VmsYamlCatalog.Parse(lines);

        clusters["foundation"].Purpose.Should().Be("AD, DNS, Vault");
        clusters["foundation"].Phase.Should().Be("0.A-0.D / 0.I");
        clusters["edge"].Phase.Should().Be("0.B");
    }

    [Fact]
    public void Parse_Reads_Node_Flow_Mappings()
    {
        var lines = Fixture.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var clusters = VmsYamlCatalog.Parse(lines);

        var foundation = clusters["foundation"];
        foundation.Nodes.Should().HaveCount(2);

        var dc = foundation.Nodes[0];
        dc.Name.Should().Be("dc-nexus");
        dc.Os.Should().Be("ws2025-desktop");
        dc.Vmnet10.Should().Be("192.168.10.10");
        dc.Dir.Should().Be(@"H:\VMS\NexusPlatform\01-foundation\dc-nexus");
        dc.Role.Should().Be("AD DC + DNS");

        foundation.Nodes[1].Name.Should().Be("vault-1");
    }

    [Fact]
    public void Parse_Skips_Virtual_Ips_Subblock()
    {
        var lines = Fixture.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var clusters = VmsYamlCatalog.Parse(lines);

        var sql = clusters["sqlserver"];
        sql.Nodes.Should().HaveCount(2);
        sql.Nodes[0].Name.Should().Be("sql-fci-1");
        sql.Nodes[1].Name.Should().Be("sql-fci-2");
    }

    [Fact]
    public void Parse_Splits_Quoted_Role_Containing_Commas_Correctly()
    {
        var lines = Fixture.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var clusters = VmsYamlCatalog.Parse(lines);

        var gw = clusters["edge"].Nodes.Single();
        gw.Role.Should().Be("Bridged NIC0 + VMnet11 NIC1, gateway, dnsmasq");
        gw.Vmnet10.Should().Be("192.168.10.1");
        gw.Vmnet11.Should().Be("192.168.70.1");
    }

    [Fact]
    public void Load_Returns_Fail_When_Path_Cannot_Be_Resolved()
    {
        var prevEnv = Environment.GetEnvironmentVariable(VmsYamlCatalog.PathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(VmsYamlCatalog.PathEnvVar, "");
            var bogus = Path.Combine(Path.GetTempPath(), $"nexus-cli-test-missing-{Guid.NewGuid():N}.yaml");
            var catalog = new VmsYamlCatalog(bogus);
            var result = catalog.Load();
            result.IsFail.Should().BeTrue();
            result.Error.Should().Contain("vms.yaml not found").And.Contain(VmsYamlCatalog.PathEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VmsYamlCatalog.PathEnvVar, prevEnv);
        }
    }

    [Fact]
    public void Load_Reads_Fixture_From_Disk_And_Caches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-cli-test-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, Fixture);
        try
        {
            var catalog = new VmsYamlCatalog(path);
            var first = catalog.Load();
            first.IsOk.Should().BeTrue();
            first.Value!.Should().ContainKey("foundation");

            var second = catalog.Load();
            ReferenceEquals(first.Value, second.Value).Should().BeTrue("cached dict reused on second Load()");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetCluster_Returns_Fail_With_Known_Names_For_Unknown_Cluster()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-cli-test-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, Fixture);
        try
        {
            var catalog = new VmsYamlCatalog(path);
            var miss = catalog.GetCluster("nope");
            miss.IsFail.Should().BeTrue();
            miss.Error.Should().Contain("unknown cluster 'nope'")
                .And.Contain("edge").And.Contain("foundation").And.Contain("sqlserver");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
