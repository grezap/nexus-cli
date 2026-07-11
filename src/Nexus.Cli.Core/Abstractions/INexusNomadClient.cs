using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>Thin Nomad HTTP-API client scoped to fetching the cluster's health.</summary>
public interface INexusNomadClient
{
    /// <summary>Queries the Nomad cluster and returns its raft/allocation health.</summary>
    Task<Result<NomadHealth>> GetHealthAsync(CancellationToken cancellationToken);
}
