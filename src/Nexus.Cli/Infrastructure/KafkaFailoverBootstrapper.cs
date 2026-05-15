using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Adapters.Inventory;
using Nexus.Cli.Adapters.Ssh;
using Nexus.Cli.Adapters.Vmware;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// DI bootstrap for the v0.5 kafka-failover verb.
/// <para>
/// Lighter than <see cref="FailoverTestBootstrapper"/>: no Vault tokens are
/// needed (the verb shells out to the kafka CLI on each broker, which uses
/// the broker's own on-disk PEM keystore for mTLS; no Consul/Nomad mgmt
/// tokens are consulted). Just vms.yaml + SSH key + vmrun.exe.
/// </para>
/// </summary>
public static class KafkaFailoverBootstrapper
{
    public const string SshUserEnvVar = "NEXUS_SSH_USER";
    public const string DefaultSshUser = "nexusadmin";

    public static IKafkaFailoverService Build()
    {
        var catalog = new VmsYamlCatalog();
        var ssh = new SshNetClient();
        var vmrun = new VmrunProcessClient();
        var sshKey = SshKeyDiscovery.Resolve()
            ?? throw new InvalidOperationException(SshKeyDiscovery.UnavailableMessage());
        var sshUser = Environment.GetEnvironmentVariable(SshUserEnvVar);
        if (string.IsNullOrWhiteSpace(sshUser))
            sshUser = DefaultSshUser;

        return new KafkaFailoverService(catalog, ssh, vmrun, sshUser, sshKey);
    }
}
