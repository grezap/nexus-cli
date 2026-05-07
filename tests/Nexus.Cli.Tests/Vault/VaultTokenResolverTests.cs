using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Nexus.Cli.Adapters.Vault;
using Xunit;

namespace Nexus.Cli.Tests.Vault;

public class VaultTokenResolverTests
{
    private sealed class FakeEnv : IEnvironmentReader
    {
        private readonly Dictionary<string, string?> _values;
        public FakeEnv(Dictionary<string, string?> values) => _values = values;
        public string? GetVariable(string name) => _values.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void Fails_When_VAULT_TOKEN_Missing()
    {
        var resolver = new VaultTokenResolver(new FakeEnv(new()));
        var result = resolver.Resolve();
        result.IsFail.Should().BeTrue();
        result.Error.Should().Contain("VAULT_TOKEN");
    }

    [Fact]
    public void Fails_When_VAULT_ADDR_Missing()
    {
        var resolver = new VaultTokenResolver(new FakeEnv(new()
        {
            ["VAULT_TOKEN"] = "hvs.deadbeef"
        }));
        var result = resolver.Resolve();
        result.IsFail.Should().BeTrue();
        result.Error.Should().Contain("VAULT_ADDR");
    }

    [Fact]
    public void Fails_When_CA_Bundle_Path_Missing()
    {
        var resolver = new VaultTokenResolver(new FakeEnv(new()
        {
            ["VAULT_TOKEN"] = "hvs.deadbeef",
            ["VAULT_ADDR"] = "https://192.168.70.121:8200"
        }));
        var result = resolver.Resolve();
        result.IsFail.Should().BeTrue();
        result.Error.Should().Contain("NEXUS_CA_BUNDLE");
    }

    [Fact]
    public void Fails_When_CA_Bundle_File_Missing()
    {
        var resolver = new VaultTokenResolver(new FakeEnv(new()
        {
            ["VAULT_TOKEN"] = "hvs.deadbeef",
            ["VAULT_ADDR"] = "https://192.168.70.121:8200",
            ["VAULT_CACERT"] = "/nope/does/not/exist.pem"
        }));
        var result = resolver.Resolve();
        result.IsFail.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public void Succeeds_When_All_Set_And_Bundle_Exists()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "-----BEGIN CERTIFICATE-----\n...\n-----END CERTIFICATE-----\n");

            var resolver = new VaultTokenResolver(new FakeEnv(new()
            {
                ["VAULT_TOKEN"] = "hvs.deadbeef",
                ["VAULT_ADDR"] = "https://192.168.70.121:8200/",
                ["VAULT_CACERT"] = tmp
            }));

            var result = resolver.Resolve();
            result.IsOk.Should().BeTrue();
            result.Value!.Address.Should().Be("https://192.168.70.121:8200");
            result.Value.Token.Should().Be("hvs.deadbeef");
            result.Value.CaBundlePath.Should().Be(tmp);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void NEXUS_CA_BUNDLE_Takes_Precedence_Over_VAULT_CACERT()
    {
        var primary = Path.GetTempFileName();
        var secondary = Path.GetTempFileName();
        try
        {
            File.WriteAllText(primary, "primary");
            File.WriteAllText(secondary, "secondary");

            var resolver = new VaultTokenResolver(new FakeEnv(new()
            {
                ["VAULT_TOKEN"] = "hvs.deadbeef",
                ["VAULT_ADDR"] = "https://192.168.70.121:8200",
                ["NEXUS_CA_BUNDLE"] = primary,
                ["VAULT_CACERT"] = secondary
            }));

            var result = resolver.Resolve();
            result.IsOk.Should().BeTrue();
            result.Value!.CaBundlePath.Should().Be(primary);
        }
        finally
        {
            File.Delete(primary);
            File.Delete(secondary);
        }
    }
}
