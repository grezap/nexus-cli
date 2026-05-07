using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface IVaultTokenResolver
{
    Result<VaultContext> Resolve();
}
