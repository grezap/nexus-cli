using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface ISshClient
{
    Task<Result<SshExecResult>> ExecuteAsync(
        SshTarget target,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
