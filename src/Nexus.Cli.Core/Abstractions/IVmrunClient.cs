namespace Nexus.Cli.Core.Abstractions;

public interface IVmrunClient
{
    bool IsAvailable { get; }

    Task<Result<IReadOnlySet<string>>> ListRunningVmxPathsAsync(CancellationToken cancellationToken);

    Task<Result<bool>> SuspendAsync(string vmxPath, CancellationToken cancellationToken);

    Task<Result<bool>> ResumeAsync(string vmxPath, CancellationToken cancellationToken);
}
