using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Inventory;
using Nexus.Cli.Adapters.Ssh;
using Nexus.Cli.Adapters.Vault;
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
///     <item>0.H.7 -- KafkaClusterAdapter x2 + KafkaEcosystemAdapter</item>
///     <item>0.7.1 -- MongoShardedAdapter (Phase 0.N sharded cluster)</item>
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

        // Optional Vault client for adapters whose engines authenticate with a
        // password held ONLY in Vault KV (the credential model locked 2026-06-05:
        // Mongo/Percona/Patroni/SQL operator passwords live in nexus/oltp/.../...,
        // never on nodes). Resolved from VAULT_ADDR/VAULT_TOKEN/VAULT_CACERT exactly
        // like cluster-status + failover-test (VaultTokenResolver). When those env
        // vars are absent the client is null; password-needing verbs then return a
        // clear "set VAULT_TOKEN/ADDR/CACERT" error rather than failing obscurely.
        // mTLS-only adapters (Redis, Kafka) ignore it. The HttpClient/factory live
        // for the process lifetime (short-lived CLI; reclaimed at exit) -- matching
        // how NexusBootstrapper holds its Vault client.
        var vault = TryBuildVaultClient();

        var adapters = new IClusterAdapter[]
        {
            new RedisAdapter(catalog, ssh, sshUser, sshKey),
            new KafkaAdapter(kafkaFailover),                                  // 0.5 DR meta-cluster (ClusterId kafka; east<->west MM2 failover)
            new KafkaClusterAdapter("kafka-east", catalog, ssh, sshUser, sshKey), // 0.6.7 (full per-cluster verb surface)
            new KafkaClusterAdapter("kafka-west", catalog, ssh, sshUser, sshKey), // 0.6.7
            new KafkaEcosystemAdapter(catalog, ssh, sshUser, sshKey),         // 0.6.7 (ClusterId kafka-ecosystem; observe)
            new MongoAdapter(catalog, ssh, sshUser, sshKey, vault),
            new MongoShardedAdapter(catalog, ssh, sshUser, sshKey, vault), // 0.7.1 (ClusterId mongo-sharded; Phase 0.N)
            new PerconaAdapter(catalog, ssh, sshUser, sshKey, vault),
            new PatroniAdapter(catalog, ssh, sshUser, sshKey, vault),
            new ClickHouseAdapter(catalog, ssh, sshUser, sshKey, vault),
            new StarRocksAdapter(catalog, ssh, sshUser, sshKey, vault),
            new SqlFciAdapter(catalog, ssh, sshUser, sshKey, vault),     // 0.G.7 (ClusterId sqlserver)
            new SqlAgAdapter(catalog, ssh, sshUser, sshKey, vault),      // 0.G.7 (ClusterId sqlserver-ag)
        };
        return new ClusterRegistry(adapters);
    }

    /// <summary>
    /// Best-effort build of an <see cref="INexusVaultClient"/> from the process
    /// environment (VAULT_ADDR / VAULT_TOKEN / VAULT_CACERT, via
    /// <see cref="VaultTokenResolver"/>). Returns null when the env isn't set or the
    /// CA bundle is missing -- the adapters degrade gracefully with an actionable
    /// error on the verbs that actually need it. Never throws.
    /// </summary>
    private static VaultClient? TryBuildVaultClient()
    {
        try
        {
            var resolver = new VaultTokenResolver(new ProcessEnvironmentReader());
            var ctx = resolver.Resolve();
            if (ctx.IsFail) return null;
            var httpFactory = new NexusHttpClientFactory(ctx.Value!.CaBundlePath);
            return new VaultClient(ctx.Value, httpFactory);
        }
        catch
        {
            return null;
        }
    }

    public static IVmResizer BuildVmResizer(IClusterRegistry registry)
    {
        var catalog = new VmsYamlCatalog();
        var vmrun = new VmrunProcessClient();
        return new VmrunVmResizer(catalog, vmrun, registry);
    }
}
