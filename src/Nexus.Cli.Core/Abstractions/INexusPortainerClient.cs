using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>Thin Portainer HTTP-API client scoped to fetching the endpoint/stack status.</summary>
public interface INexusPortainerClient
{
    /// <summary>Queries Portainer and returns its endpoint and stack status.</summary>
    Task<Result<PortainerStatus>> GetStatusAsync(CancellationToken cancellationToken);
}
