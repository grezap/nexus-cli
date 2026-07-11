using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Resolves the ambient Vault context (address + token) from the environment/agent
/// so clients can authenticate without hard-coded credentials.
/// </summary>
public interface IVaultTokenResolver
{
    /// <summary>Resolves the current Vault address and token, failing when none is available.</summary>
    Result<VaultContext> Resolve();
}
