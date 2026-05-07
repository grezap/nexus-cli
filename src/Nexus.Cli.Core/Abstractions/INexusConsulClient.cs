using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface INexusConsulClient
{
    Task<Result<ConsulHealth>> GetHealthAsync(CancellationToken cancellationToken);
}
