using System.Diagnostics;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;
using Renci.SshNet;

namespace Nexus.Cli.Adapters.Ssh;

/// <summary>
/// SSH.NET (Renci.SshNet 2025.1.0) implementation of <see cref="Nexus.Cli.Core.Abstractions.ISshClient"/>.
/// Stateless: each <see cref="ExecuteAsync"/> opens a fresh connection, runs
/// one command, and disconnects. Acceptable for failover-test's ~5-10 command
/// budget; if a future verb needs many commands per session, add an
/// <c>OpenSessionAsync</c> path. Key auth only -- password auth is intentionally
/// not exposed (operator's lab uses key-only nexusadmin per canon).
/// ADR-0007 records the rationale for SSH.NET over ssh.exe shell-out.
/// </summary>
public sealed class SshNetClient : Nexus.Cli.Core.Abstractions.ISshClient
{
    /// <inheritdoc />
    public async Task<Result<SshExecResult>> ExecuteAsync(
        SshTarget target,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target.PrivateKeyPath))
            return Result.Fail<SshExecResult>($"SSH private key not found at '{target.PrivateKeyPath}'.");

        try
        {
            var keyFile = new PrivateKeyFile(target.PrivateKeyPath);
            var auth = new PrivateKeyAuthenticationMethod(target.Username, keyFile);
            var connectionInfo = new ConnectionInfo(target.Host, target.Port, target.Username, auth)
            {
                Timeout = timeout
            };

            using var client = new SshClient(connectionInfo);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = timeout;

            var sw = Stopwatch.StartNew();
            // SSH.NET 2025.x: ExecuteAsync(ct) is void-returning; stdout is read
            // back via cmd.Result, ExitStatus is nullable until the command
            // completes.
            await cmd.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            var result = new SshExecResult(
                cmd.ExitStatus ?? -1,
                cmd.Result ?? string.Empty,
                cmd.Error ?? string.Empty,
                sw.Elapsed);
            client.Disconnect();
            return Result.Ok(result);
        }
        catch (OperationCanceledException)
        {
            return Result.Fail<SshExecResult>($"SSH to {target.Host} cancelled.");
        }
        catch (Exception ex)
        {
            return Result.Fail<SshExecResult>($"SSH to {target.Username}@{target.Host}:{target.Port} failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> UploadBytesAsync(
        SshTarget target,
        byte[] content,
        string remotePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target.PrivateKeyPath))
            return Result.Fail<bool>($"SSH private key not found at '{target.PrivateKeyPath}'.");
        try
        {
            var keyFile = new PrivateKeyFile(target.PrivateKeyPath);
            var auth = new PrivateKeyAuthenticationMethod(target.Username, keyFile);
            var connectionInfo = new ConnectionInfo(target.Host, target.Port, target.Username, auth)
            {
                Timeout = timeout
            };
            using var sftp = new SftpClient(connectionInfo);
            await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            using (var ms = new MemoryStream(content))
                sftp.UploadFile(ms, remotePath, true);
            sftp.Disconnect();
            return Result.Ok(true);
        }
        catch (OperationCanceledException)
        {
            return Result.Fail<bool>($"SFTP to {target.Host} cancelled.");
        }
        catch (Exception ex)
        {
            return Result.Fail<bool>($"SFTP upload to {target.Username}@{target.Host}:{remotePath} failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> DownloadBytesAsync(
        SshTarget target,
        string remotePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(target.PrivateKeyPath))
            return Result.Fail<byte[]>($"SSH private key not found at '{target.PrivateKeyPath}'.");
        try
        {
            var keyFile = new PrivateKeyFile(target.PrivateKeyPath);
            var auth = new PrivateKeyAuthenticationMethod(target.Username, keyFile);
            var connectionInfo = new ConnectionInfo(target.Host, target.Port, target.Username, auth)
            {
                Timeout = timeout
            };
            using var sftp = new SftpClient(connectionInfo);
            await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream();
            sftp.DownloadFile(remotePath, ms);
            sftp.Disconnect();
            return Result.Ok(ms.ToArray());
        }
        catch (OperationCanceledException)
        {
            return Result.Fail<byte[]>($"SFTP from {target.Host} cancelled.");
        }
        catch (Exception ex)
        {
            return Result.Fail<byte[]>($"SFTP download from {target.Username}@{target.Host}:{remotePath} failed: {ex.Message}");
        }
    }
}
