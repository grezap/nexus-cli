using FluentAssertions;
using Nexus.Cli.Adapters.Ssh;
using Xunit;

namespace Nexus.Cli.Tests.Ssh;

public class SshKeyDiscoveryTests
{
    [Fact]
    public void Resolve_Honours_Env_Var_When_File_Exists()
    {
        var prev = Environment.GetEnvironmentVariable(SshKeyDiscovery.KeyEnvVar);
        var tmp = Path.Combine(Path.GetTempPath(), $"nexus-fake-key-{Guid.NewGuid():N}");
        File.WriteAllText(tmp, "fake-key-bytes");
        try
        {
            Environment.SetEnvironmentVariable(SshKeyDiscovery.KeyEnvVar, tmp);
            SshKeyDiscovery.Resolve().Should().Be(tmp);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SshKeyDiscovery.KeyEnvVar, prev);
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Resolve_Falls_Through_When_Env_Var_Points_At_Missing_File()
    {
        var prev = Environment.GetEnvironmentVariable(SshKeyDiscovery.KeyEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                SshKeyDiscovery.KeyEnvVar,
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"));
            // Returns either ~/.ssh/id_ed25519, ~/.ssh/id_rsa, or null
            // depending on operator's machine. Must not throw and must not
            // return the bogus env-var path.
            var resolved = SshKeyDiscovery.Resolve();
            (resolved is null || File.Exists(resolved)).Should().BeTrue();
            resolved.Should().NotStartWith(Path.GetTempPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SshKeyDiscovery.KeyEnvVar, prev);
        }
    }

    [Fact]
    public void UnavailableMessage_Mentions_Env_Var_And_Canonical_Paths()
    {
        var msg = SshKeyDiscovery.UnavailableMessage();
        msg.Should().Contain(SshKeyDiscovery.KeyEnvVar);
        msg.Should().Contain("id_ed25519");
        msg.Should().Contain("id_rsa");
    }
}
