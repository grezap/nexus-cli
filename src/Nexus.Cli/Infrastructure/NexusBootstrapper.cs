using Microsoft.Extensions.DependencyInjection;
using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Adapters.Consul;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Nomad;
using Nexus.Cli.Adapters.Portainer;
using Nexus.Cli.Adapters.Vault;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// On-demand resolver for the cluster-status command's dependencies. We don't
/// pre-build the HTTP factory + service graph in <c>Program.cs</c> because
/// stubbed commands (infrastructure / failover-test / etc.) shouldn't need a
/// Vault token to print their not-yet-implemented banner.
/// </summary>
public sealed class NexusBootstrapper : IDisposable
{
    private readonly IVaultTokenResolver _tokenResolver;
    private NexusHttpClientFactory? _httpFactory;
    private VaultClient? _vault;
    private ConsulClient? _consul;
    private NomadClient? _nomad;
    private PortainerClient? _portainer;

    // Vault KV paths (frozen 0.E.4 close-out canon).

    /// <summary>KV path (under the <c>nexus</c> mount) holding the Consul management token.</summary>
    public const string ConsulMgmtTokenPath = "swarm/consul-bootstrap-token";

    /// <summary>KV path (under the <c>nexus</c> mount) holding the Nomad management token.</summary>
    public const string NomadMgmtTokenPath = "swarm/nomad-bootstrap-token";

    /// <summary>KV path (under the <c>nexus</c> mount) holding the Portainer admin credential.</summary>
    public const string PortainerAdminPath = "portainer/admin-bcrypt";

    // Endpoint defaults for the lab. Override via env vars NEXUS_CONSUL_ADDR,
    // NEXUS_NOMAD_ADDR, NEXUS_PORTAINER_ADDR (a v0.2-friendly seam, but already
    // honoured here to keep secrets out of the binary).
    private const string ConsulDefault = "https://192.168.70.111:8501";
    private const string NomadDefault = "https://192.168.70.111:4646";
    private const string PortainerDefault = "https://portainer.nexus.lab:9443";

    /// <summary>Creates the bootstrapper with the resolver used to obtain the Vault token/context.</summary>
    /// <param name="tokenResolver">Resolves the Vault address/token/CA bundle from the process environment.</param>
    public NexusBootstrapper(IVaultTokenResolver tokenResolver)
        => _tokenResolver = tokenResolver;

    /// <summary>
    /// Resolves the Vault context, reads the Consul/Nomad management tokens + Portainer admin password
    /// from KV, and wires the Consul/Nomad/Portainer HTTP clients into an <see cref="IClusterStatusService"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the Vault KV reads.</param>
    public async Task<IClusterStatusService> BuildClusterStatusServiceAsync(
        CancellationToken cancellationToken)
    {
        var ctx = _tokenResolver.Resolve();
        if (ctx.IsFail)
            throw new InvalidOperationException(ctx.Error);

        _httpFactory = new NexusHttpClientFactory(ctx.Value!.CaBundlePath);
        _vault = new VaultClient(ctx.Value, _httpFactory);

        // Consul + Nomad bootstrap tokens are persisted under the `management_token`
        // field (per Phase 0.E.2.3 / 0.E.3.2 close-out canon — same field name the
        // master plan's pre-flight uses: `vault kv get -field=management_token ...`).
        var consulToken = await _vault.ReadKvFieldAsync("nexus", ConsulMgmtTokenPath, "management_token", cancellationToken)
            .ConfigureAwait(false);
        if (consulToken.IsFail)
            throw new InvalidOperationException(consulToken.Error);

        var nomadToken = await _vault.ReadKvFieldAsync("nexus", NomadMgmtTokenPath, "management_token", cancellationToken)
            .ConfigureAwait(false);
        if (nomadToken.IsFail)
            throw new InvalidOperationException(nomadToken.Error);

        var portainerPassword = await _vault.ReadKvFieldAsync("nexus", PortainerAdminPath, "plaintext", cancellationToken)
            .ConfigureAwait(false);
        // Portainer admin pwd is optional at v0.1 — /api/system/status is unauthenticated.
        var portainerPwd = portainerPassword.IsOk ? portainerPassword.Value! : "";

        _consul = new ConsulClient(
            new ConsulClient.Settings(
                BaseAddress: ResolveAddr("NEXUS_CONSUL_ADDR", ConsulDefault),
                MgmtToken: consulToken.Value!),
            _httpFactory);

        _nomad = new NomadClient(
            new NomadClient.Settings(
                BaseAddress: ResolveAddr("NEXUS_NOMAD_ADDR", NomadDefault),
                MgmtToken: nomadToken.Value!),
            _httpFactory);

        _portainer = new PortainerClient(
            new PortainerClient.Settings(
                BaseAddress: ResolveAddr("NEXUS_PORTAINER_ADDR", PortainerDefault),
                AdminUser: "admin",
                AdminPassword: portainerPwd),
            _httpFactory);

        return new ClusterStatusService(_consul, _nomad, _portainer);
    }

    private static string ResolveAddr(string envVar, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _portainer?.Dispose();
        _nomad?.Dispose();
        _consul?.Dispose();
        _vault?.Dispose();
        _httpFactory?.Dispose();
    }
}
