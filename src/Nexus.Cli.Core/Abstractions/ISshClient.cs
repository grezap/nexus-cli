using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// SSH/SFTP transport to fleet nodes: runs remote commands and moves raw files in
/// both directions, the backbone every adapter uses to reach on-node native CLIs.
/// </summary>
public interface ISshClient
{
    /// <summary>Executes <paramref name="command"/> on <paramref name="target"/> under <paramref name="timeout"/> and returns its exit code and output.</summary>
    Task<Result<SshExecResult>> ExecuteAsync(
        SshTarget target,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Upload raw bytes to a remote path over SFTP (overwrites). Used by the SQL
    /// Server cert-rotate to ship a PFX without blowing past the Windows
    /// command-line length limit that an inline base64 EncodedCommand would hit.
    /// </summary>
    Task<Result<bool>> UploadBytesAsync(
        SshTarget target,
        byte[] content,
        string remotePath,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Download a remote file over SFTP and return its bytes. Used by the SQL
    /// Server AG scale-out to ferry a backup base (.bak/.trn) from the active FCI
    /// node to the build host and on to a re-joining replica — the only viable
    /// transfer when the FCI's shared S:\ has no path on the standalone replicas
    /// and a plain-SSH session (local nexusadmin) has no network identity to reach
    /// a peer's admin share (manual seeding, mirroring role-overlay-ag-bootstrap).
    /// </summary>
    Task<Result<byte[]>> DownloadBytesAsync(
        SshTarget target,
        string remotePath,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
