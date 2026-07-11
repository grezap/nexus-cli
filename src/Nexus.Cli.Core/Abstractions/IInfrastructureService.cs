using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// VM lifecycle facade over the vmrun/catalog layer — enumerates fleet VMs and
/// suspends/resumes them by cluster or node for the infrastructure verbs.
/// </summary>
public interface IInfrastructureService
{
    /// <summary>Lists every VM in the fleet catalog with its live power state.</summary>
    Task<Result<IReadOnlyList<VmStatus>>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reports the power state of a cluster, or a single <paramref name="nodeName"/> when supplied.</summary>
    Task<Result<IReadOnlyList<VmStatus>>> StatusAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken);

    /// <summary>Suspends a whole cluster, or a single <paramref name="nodeName"/> when supplied.</summary>
    Task<Result<IReadOnlyList<OpResult>>> SuspendAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken);

    /// <summary>Resumes a whole cluster, or a single <paramref name="nodeName"/> when supplied.</summary>
    Task<Result<IReadOnlyList<OpResult>>> ResumeAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken);
}
