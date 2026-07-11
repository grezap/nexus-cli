using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>Thin Consul HTTP-API client scoped to fetching the cluster's health.</summary>
public interface INexusConsulClient
{
    /// <summary>Queries the Consul cluster and returns its raft/service health.</summary>
    Task<Result<ConsulHealth>> GetHealthAsync(CancellationToken cancellationToken);
}
