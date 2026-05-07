using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface IClusterStatusService
{
    Task<ClusterStatusReport> GetStatusAsync(CancellationToken cancellationToken);
}
