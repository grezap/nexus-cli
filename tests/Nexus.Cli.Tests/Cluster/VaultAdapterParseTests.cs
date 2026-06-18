using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="VaultAdapter"/> (Phase 0.A-0.D/0.M,
/// nexus-cli v0.8.1). Fixtures are verbatim from the LIVE foundation Vault HA
/// cluster captured during the v0.8.1 contract probe 2026-06-18 (Vault 1.18.4,
/// transit auto-unseal, vault-transit-init.json shape), so a parser regression
/// surfaces here rather than mid-verb against the live trust root.
/// </summary>
public class VaultAdapterParseTests
{
    // === ClassifyNode (vms.yaml name -> role) ==============================

    [Theory]
    [InlineData("vault-1", "ha")]
    [InlineData("vault-2", "ha")]
    [InlineData("vault-3", "ha")]
    [InlineData("vault-transit", "transit")]
    [InlineData("dc-nexus", "other")]
    [InlineData("nexus-gateway", "other")]
    public void ClassifyNode_maps_foundation_nodes(string name, string expected) =>
        VaultAdapter.ClassifyNode(name).Should().Be(expected);

    // === ParseSealed (vault status -format=json) ===========================
    // Verbatim from a live transit-mode HA node (leader drifted to vault-2).
    private const string HaStatusFixture = """
        {
          "type": "transit",
          "initialized": true,
          "sealed": false,
          "version": "1.18.4",
          "cluster_name": "nexus-vault",
          "storage_type": "raft",
          "ha_enabled": true,
          "leader_address": "https://192.168.70.122:8200"
        }
        """;

    private const string TransitSealedFixture = """
        {"type":"shamir","initialized":true,"sealed":true,"version":"1.18.4"}
        """;

    [Fact]
    public void ParseSealed_reads_false_from_unsealed_node() =>
        VaultAdapter.ParseSealed(HaStatusFixture).Should().BeFalse();

    [Fact]
    public void ParseSealed_reads_true_from_sealed_transit() =>
        VaultAdapter.ParseSealed(TransitSealedFixture).Should().BeTrue();

    [Fact]
    public void ParseSealed_tolerates_leading_sudo_noise()
    {
        // `sudo` on a node without a 127.0.1.1 /etc/hosts entry prepends a warning.
        var noisy = "sudo: unable to resolve host vault-transit\n" + TransitSealedFixture;
        VaultAdapter.ParseSealed(noisy).Should().BeTrue();
    }

    [Fact]
    public void ParseSealed_null_on_garbage()
    {
        VaultAdapter.ParseSealed("not json").Should().BeNull();
        VaultAdapter.ParseSealed("").Should().BeNull();
        VaultAdapter.ParseSealed("{\"initialized\":true}").Should().BeNull();
    }

    // === ParseTransitInit (vault-transit-init.json) ========================
    // Shape of ~/.nexus/vault-transit-init.json (keys redacted, structure real).
    private const string InitFixture = """
        {
          "unseal_keys_b64": ["a1Key==", "b2Key==", "c3Key==", "d4Key==", "e5Key=="],
          "unseal_keys_hex": ["aa","bb","cc","dd","ee"],
          "unseal_shares": 5,
          "unseal_threshold": 3,
          "root_token": "hvs.redacted"
        }
        """;

    [Fact]
    public void ParseTransitInit_extracts_keys_and_threshold()
    {
        var r = VaultAdapter.ParseTransitInit(InitFixture);
        r.IsOk.Should().BeTrue();
        r.Value.Keys.Should().HaveCount(5);
        r.Value.Threshold.Should().Be(3);
        r.Value.Keys[0].Should().Be("a1Key==");
    }

    [Fact]
    public void ParseTransitInit_defaults_threshold_to_3_when_absent()
    {
        var json = """{"unseal_keys_b64":["k1","k2","k3"]}""";
        var r = VaultAdapter.ParseTransitInit(json);
        r.IsOk.Should().BeTrue();
        r.Value.Threshold.Should().Be(3);
    }

    [Fact]
    public void ParseTransitInit_fails_when_keys_below_threshold()
    {
        var json = """{"unseal_keys_b64":["only-one"],"unseal_threshold":3}""";
        VaultAdapter.ParseTransitInit(json).IsFail.Should().BeTrue();
    }

    [Fact]
    public void ParseTransitInit_fails_on_missing_keys_array()
    {
        VaultAdapter.ParseTransitInit("""{"root_token":"x"}""").IsFail.Should().BeTrue();
        VaultAdapter.ParseTransitInit("not json").IsFail.Should().BeTrue();
    }
}
