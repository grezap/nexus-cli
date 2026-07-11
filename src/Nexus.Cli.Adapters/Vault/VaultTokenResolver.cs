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

    /// <summary>Creates a resolver that reads environment via <paramref name="env"/> (indirection kept so tests can inject a fake environment).</summary>
    /// <param name="env">Abstraction over process environment-variable reads.</param>
    public VaultTokenResolver(IEnvironmentReader env) => _env = env;

    /// <inheritdoc />
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

/// <summary>Thin seam over environment-variable reads so token resolution is unit-testable.</summary>
public interface IEnvironmentReader
{
    /// <summary>Returns the value of environment variable <paramref name="name"/>, or <c>null</c> if unset.</summary>
    /// <param name="name">The environment-variable name.</param>
    /// <returns>The variable's value, or <c>null</c> when it is not defined.</returns>
    string? GetVariable(string name);
}

/// <summary>Production <see cref="IEnvironmentReader"/> backed by <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
public sealed class ProcessEnvironmentReader : IEnvironmentReader
{
    /// <inheritdoc />
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}
