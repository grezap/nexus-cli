using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface IVmsCatalog
{
    Result<IReadOnlyDictionary<string, ClusterRecord>> Load();

    Result<ClusterRecord> GetCluster(string name);
}
