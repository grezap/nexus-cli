using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface INexusPortainerClient
{
    Task<Result<PortainerStatus>> GetStatusAsync(CancellationToken cancellationToken);
}
