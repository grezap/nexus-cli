namespace Nexus.Cli.Core.Models;

/// <summary>Connection context for talking to a Vault server: address, token and CA trust.</summary>
/// <param name="Address">Base URL of the Vault API.</param>
/// <param name="Token">Vault token used to authenticate requests.</param>
/// <param name="CaBundlePath">Path to the CA bundle that validates Vault's TLS certificate.</param>
public sealed record VaultContext(
    string Address,
    string Token,
    string CaBundlePath);
