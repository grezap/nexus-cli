using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface IInfrastructureService
{
    Task<Result<IReadOnlyList<VmStatus>>> ListAsync(CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<VmStatus>>> StatusAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OpResult>>> SuspendAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OpResult>>> ResumeAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken);
}
