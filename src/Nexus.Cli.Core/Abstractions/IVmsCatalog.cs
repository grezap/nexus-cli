using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Loads the fleet inventory from <c>vms.yaml</c> — the canonical map of clusters to
/// their member VMs — keyed by cluster name.
/// </summary>
public interface IVmsCatalog
{
    /// <summary>Loads all cluster records from the catalog, keyed by cluster name.</summary>
    Result<IReadOnlyDictionary<string, ClusterRecord>> Load();

    /// <summary>Resolves a single cluster record by <paramref name="name"/>, failing when unknown.</summary>
    Result<ClusterRecord> GetCluster(string name);
}
