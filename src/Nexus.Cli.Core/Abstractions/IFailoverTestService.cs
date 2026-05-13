using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface IFailoverTestService
{
    Task<Result<FailoverTestReport>> RunConsulLeaderAsync(
        string? targetNode,
        CancellationToken cancellationToken);
}
