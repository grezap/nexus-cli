using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Vault;

/// <summary>
/// Reads VAULT_TOKEN / VAULT_ADDR / VAULT_CACERT (or NEXUS_CA_BUNDLE) from the
/// process environment. No login flow at v0.1 — operator manages the token
/// externally via <c>vault login</c> (per ADR-0004).
/// </summary>
public sealed class VaultTokenResolver : IVaultTokenResolver
{
    private readonly IEnvironmentReader _env;

    public VaultTokenResolver(IEnvironmentReader env) => _env = env;

    public Result<VaultContext> Resolve()
    {
        var token = _env.GetVariable("VAULT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            return Result.Fail<VaultContext>("VAULT_TOKEN is not set. Run `vault login` first.");

        var addr = _env.GetVariable("VAULT_ADDR");
        if (string.IsNullOrWhiteSpace(addr))
            return Result.Fail<VaultContext>("VAULT_ADDR is not set (e.g. https://192.168.70.121:8200).");

        var ca = _env.GetVariable("NEXUS_CA_BUNDLE");
        if (string.IsNullOrWhiteSpace(ca))
            ca = _env.GetVariable("VAULT_CACERT");

        if (string.IsNullOrWhiteSpace(ca))
            return Result.Fail<VaultContext>(
                "Neither NEXUS_CA_BUNDLE nor VAULT_CACERT is set. Point to the lab root CA bundle (e.g. $HOME\\.nexus\\vault-ca-bundle.crt).");

        if (!File.Exists(ca))
            return Result.Fail<VaultContext>($"CA bundle not found at '{ca}'.");

        return Result.Ok(new VaultContext(addr.TrimEnd('/'), token, ca));
    }
}

public interface IEnvironmentReader
{
    string? GetVariable(string name);
}

public sealed class ProcessEnvironmentReader : IEnvironmentReader
{
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}
