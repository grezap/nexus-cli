using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Adapters.Inventory;
using Nexus.Cli.Adapters.Ssh;
using Nexus.Cli.Adapters.Vmware;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// DI bootstrap for the v0.6 cluster-adapter SPI (ADR-0009). Builds each
/// cluster adapter, composes them into an <see cref="IClusterRegistry"/>,
/// and wires the generic <see cref="IVmResizer"/> (<c>scale-up</c> verb).
/// <para>
/// Per Phase 0.G.N (one cluster per sub-phase), new adapters get registered
/// here as they ship:
///   <list type="bullet">
///     <item>0.G.1 -- RedisAdapter (+ KafkaAdapter retrofit)</item>
///     <item>0.G.2 -- MongoAdapter</item>
///     <item>0.G.3 -- PerconaAdapter</item>
///     <item>0.G.4 -- PatroniAdapter</item>
///     <item>0.G.5 -- ClickHouseAdapter</item>
///     <item>0.G.6 -- StarRocksAdapter</item>
///     <item>0.G.7 -- SqlFciAdapter + SqlAgAdapter</item>
///   </list>
/// </para>
/// </summary>
public static class ClusterBootstrapper
{
    public const string SshUserEnvVar = "NEXUS_SSH_USER";
    public const string DefaultSshUser = "nexusadmin";

    public static IClusterRegistry BuildRegistry()
    {
        var catalog = new VmsYamlCatalog();
        var ssh = new SshNetClient();
        var sshKey = SshKeyDiscovery.Resolve()
            ?? throw new InvalidOperationException(SshKeyDiscovery.UnavailableMessage());
        var sshUser = Environment.GetEnvironmentVariable(SshUserEnvVar);
        if (string.IsNullOrWhiteSpace(sshUser))
            sshUser = DefaultSshUser;

        // Kafka adapter is a retrofit -- it delegates to the existing
        // IKafkaFailoverService. Built via the existing bootstrapper to
        // preserve the v0.5 wiring.
        var kafkaFailover = KafkaFailoverBootstrapper.Build();

        var adapters = new IClusterAdapter[]
        {
            new RedisAdapter(catalog, ssh, sshUser, sshKey),
            new KafkaAdapter(kafkaFailover),
            // 0.G.2+: new MongoAdapter(catalog, ssh, sshUser, sshKey),
            // 0.G.3+: new PerconaAdapter(catalog, ssh, sshUser, sshKey),
            // 0.G.4+: new PatroniAdapter(catalog, ssh, sshUser, sshKey),
            // 0.G.5+: new ClickHouseAdapter(catalog, ssh, sshUser, sshKey),
            // 0.G.6+: new StarRocksAdapter(catalog, ssh, sshUser, sshKey),
            // 0.G.7+: new SqlFciAdapter(catalog, ssh, sshUser, sshKey),
            // 0.G.7+: new SqlAgAdapter(catalog, ssh, sshUser, sshKey),
        };
        return new ClusterRegistry(adapters);
    }

    public static IVmResizer BuildVmResizer(IClusterRegistry registry)
    {
        var catalog = new VmsYamlCatalog();
        var vmrun = new VmrunProcessClient();
        return new VmrunVmResizer(catalog, vmrun, registry);
    }
}
