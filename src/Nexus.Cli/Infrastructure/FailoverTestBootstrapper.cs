using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Inventory;
using Nexus.Cli.Adapters.Ssh;
using Nexus.Cli.Adapters.Vault;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// DI bootstrap for the v0.3 failover-test verb. Reuses VaultTokenResolver +
/// NexusHttpClientFactory + VaultClient from cluster-status to fetch the
/// Consul mgmt token from KV, then wires the VmsYamlCatalog + SshNetClient
/// into FailoverTestService.
/// </summary>
public sealed class FailoverTestBootstrapper : IDisposable
{
    public const string SshUserEnvVar = "NEXUS_SSH_USER";
    public const string DefaultSshUser = "nexusadmin";

    private readonly IVaultTokenResolver _tokenResolver;
    private NexusHttpClientFactory? _httpFactory;
    private VaultClient? _vault;

    public FailoverTestBootstrapper(IVaultTokenResolver tokenResolver)
        => _tokenResolver = tokenResolver;

    public async Task<IFailoverTestService> BuildAsync(CancellationToken cancellationToken)
    {
        var ctx = _tokenResolver.Resolve();
        if (ctx.IsFail)
            throw new InvalidOperationException(ctx.Error);

        _httpFactory = new NexusHttpClientFactory(ctx.Value!.CaBundlePath);
        _vault = new VaultClient(ctx.Value, _httpFactory);

        var consulMgmt = await _vault.ReadKvFieldAsync(
            "nexus",
            NexusBootstrapper.ConsulMgmtTokenPath,
            "management_token",
            cancellationToken).ConfigureAwait(false);
        if (consulMgmt.IsFail)
            throw new InvalidOperationException(consulMgmt.Error);

        var nomadMgmt = await _vault.ReadKvFieldAsync(
            "nexus",
            NexusBootstrapper.NomadMgmtTokenPath,
            "management_token",
            cancellationToken).ConfigureAwait(false);
        if (nomadMgmt.IsFail)
            throw new InvalidOperationException(nomadMgmt.Error);

        var catalog = new VmsYamlCatalog();
        var ssh = new SshNetClient();
        var sshKey = SshKeyDiscovery.Resolve()
            ?? throw new InvalidOperationException(SshKeyDiscovery.UnavailableMessage());
        var sshUser = Environment.GetEnvironmentVariable(SshUserEnvVar);
        if (string.IsNullOrWhiteSpace(sshUser))
            sshUser = DefaultSshUser;

        return new FailoverTestService(
            catalog,
            ssh,
            _httpFactory,
            consulMgmt.Value!,
            nomadMgmt.Value!,
            sshUser,
            sshKey);
    }

    public void Dispose()
    {
        _vault?.Dispose();
        _httpFactory?.Dispose();
    }
}
