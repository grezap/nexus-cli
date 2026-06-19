using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="SwarmAdapter"/> (Phase 0.E orchestration
/// tier, nexus-cli v0.8.2). Fixtures mirror the LIVE shapes the adapter parses —
/// <c>docker node ls --format json</c> (NDJSON), <c>consul acl token list
/// -format=json</c>, and <c>nomad acl token list -json</c> — so a parser
/// regression surfaces here rather than mid-verb against the running cluster.
/// </summary>
public class SwarmAdapterParseTests
{
    // === ClassifyNode (vms.yaml name -> role) ==============================

    [Theory]
    [InlineData("swarm-manager-1", "manager")]
    [InlineData("swarm-manager-3", "manager")]
    [InlineData("swarm-worker-1", "worker")]
    [InlineData("swarm-worker-3", "worker")]
    [InlineData("vault-1", "other")]
    [InlineData("nexus-gateway", "other")]
    public void ClassifyNode_maps_swarm_nodes(string name, string expected) =>
        SwarmAdapter.ClassifyNode(name).Should().Be(expected);

    // === ParseDockerNodes (docker node ls --format json, NDJSON) ===========
    // Verbatim shape from Docker 29.x: one JSON object per line.
    private const string DockerNodesFixture = """
        {"Availability":"Active","EngineVersion":"29.4.3","Hostname":"swarm-manager-1","ID":"qecjgm73zb42dxz2aahilveiy","ManagerStatus":"Reachable","Self":true,"Status":"Ready","TLSStatus":"Ready"}
        {"Availability":"Active","EngineVersion":"29.4.3","Hostname":"swarm-manager-2","ID":"kw0pvi2xbzb7txvfwdc31coel","ManagerStatus":"Reachable","Self":false,"Status":"Ready","TLSStatus":"Ready"}
        {"Availability":"Active","EngineVersion":"29.4.3","Hostname":"swarm-manager-3","ID":"n1s6rl5iaz3nxpxqjaikw3nqr","ManagerStatus":"Leader","Self":false,"Status":"Ready","TLSStatus":"Ready"}
        {"Availability":"Active","EngineVersion":"29.4.3","Hostname":"swarm-worker-1","ID":"03ntlm5ggl3ny5frjiidynp6j","ManagerStatus":"","Self":false,"Status":"Ready","TLSStatus":"Ready"}
        {"Availability":"Drain","EngineVersion":"29.4.3","Hostname":"swarm-worker-2","ID":"2jx46orf4q8ohi5e74gi5fjnt","ManagerStatus":"","Self":false,"Status":"Ready","TLSStatus":"Ready"}
        {"Availability":"Active","EngineVersion":"29.4.3","Hostname":"swarm-worker-3","ID":"kbdgq32jdcozix16chxs9wptq","ManagerStatus":"","Self":false,"Status":"Down","TLSStatus":"Ready"}
        """;

    [Fact]
    public void ParseDockerNodes_reads_six_nodes()
    {
        var nodes = SwarmAdapter.ParseDockerNodes(DockerNodesFixture);
        nodes.Should().HaveCount(6);
    }

    [Fact]
    public void ParseDockerNodes_identifies_the_single_raft_leader()
    {
        var nodes = SwarmAdapter.ParseDockerNodes(DockerNodesFixture);
        nodes.Count(n => n.IsLeader).Should().Be(1);
        nodes.Single(n => n.IsLeader).Hostname.Should().Be("swarm-manager-3");
    }

    [Fact]
    public void ParseDockerNodes_classifies_managers_vs_workers()
    {
        var nodes = SwarmAdapter.ParseDockerNodes(DockerNodesFixture);
        nodes.Count(n => n.IsManager).Should().Be(3);
        nodes.Count(n => !n.IsManager).Should().Be(3);
    }

    [Fact]
    public void ParseDockerNodes_surfaces_drain_and_down_states()
    {
        var nodes = SwarmAdapter.ParseDockerNodes(DockerNodesFixture);
        nodes.Single(n => n.Hostname == "swarm-worker-2").Availability.Should().Be("Drain");
        nodes.Single(n => n.Hostname == "swarm-worker-3").Status.Should().Be("Down");
    }

    [Fact]
    public void ParseDockerNodes_tolerates_blank_and_malformed_lines()
    {
        var noisy = "\n  \nnot-json-warning-line\n" + DockerNodesFixture + "\n{ broken";
        var nodes = SwarmAdapter.ParseDockerNodes(noisy);
        nodes.Should().HaveCount(6);
    }

    // === ParseConsulAclTokens (consul acl token list -format=json) ==========
    private const string ConsulTokensFixture = """
        [
          {
            "AccessorID": "00000000-0000-0000-0000-000000000002",
            "Description": "Anonymous Token",
            "Policies": null
          },
          {
            "AccessorID": "8f2c1d4e-aaaa-bbbb-cccc-1234567890ab",
            "Description": "Bootstrap Token (Global Management)",
            "Policies": [ { "ID": "00000000-0000-0000-0000-000000000001", "Name": "global-management" } ]
          },
          {
            "AccessorID": "deadbeef-1111-2222-3333-444455556666",
            "Description": "nexus-acl-demo",
            "Policies": []
          }
        ]
        """;

    [Fact]
    public void ParseConsulAclTokens_reads_all_tokens_with_policies()
    {
        var tokens = SwarmAdapter.ParseConsulAclTokens(ConsulTokensFixture);
        tokens.Should().HaveCount(3);
        var boot = tokens.Single(t => t.Accessor == "8f2c1d4e-aaaa-bbbb-cccc-1234567890ab");
        boot.Description.Should().Be("Bootstrap Token (Global Management)");
        boot.Policies.Should().Contain("global-management");
        boot.Engine.Should().Be("consul");
    }

    [Fact]
    public void ParseConsulAclTokens_tolerates_null_policies()
    {
        var tokens = SwarmAdapter.ParseConsulAclTokens(ConsulTokensFixture);
        tokens.Single(t => t.Description == "Anonymous Token").Policies.Should().BeEmpty();
    }

    // === ParseNomadAclTokens (nomad acl token list -json) ===================
    private const string NomadTokensFixture = """
        [
          {
            "AccessorID": "11111111-2222-3333-4444-555566667777",
            "Name": "Bootstrap Token",
            "Type": "management",
            "Policies": null,
            "Global": true
          },
          {
            "AccessorID": "99998888-7777-6666-5555-444433332222",
            "Name": "nexus-reader",
            "Type": "client",
            "Policies": [ "read-only" ],
            "Global": false
          }
        ]
        """;

    [Fact]
    public void ParseNomadAclTokens_reads_name_type_and_policies()
    {
        var tokens = SwarmAdapter.ParseNomadAclTokens(NomadTokensFixture);
        tokens.Should().HaveCount(2);
        var boot = tokens.Single(t => t.Accessor == "11111111-2222-3333-4444-555566667777");
        boot.Description.Should().Be("Bootstrap Token");
        boot.Policies.Should().Contain("management");   // Type folded into policies
        boot.Engine.Should().Be("nomad");

        var reader = tokens.Single(t => t.Description == "nexus-reader");
        reader.Policies.Should().Contain("read-only");
        reader.Policies.Should().Contain("client");
    }
}
