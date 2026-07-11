using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Aggregates the control-plane cluster health (Consul, Nomad, Portainer) into a
/// single report for the <c>status</c> verb.
/// </summary>
public interface IClusterStatusService
{
    /// <summary>Gathers and returns the composite control-plane status report.</summary>
    Task<ClusterStatusReport> GetStatusAsync(CancellationToken cancellationToken);
}
