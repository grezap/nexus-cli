using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface INexusNomadClient
{
    Task<Result<NomadHealth>> GetHealthAsync(CancellationToken cancellationToken);
}
