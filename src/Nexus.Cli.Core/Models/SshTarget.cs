namespace Nexus.Cli.Core.Models;

/// <summary>
/// Endpoint + credentials for one SSH session. PrivateKeyPath is operator-
/// owned; the CLI never reads or stores the key contents itself.
/// </summary>
/// <param name="Host">Target hostname or IP address.</param>
/// <param name="Port">TCP port of the SSH daemon.</param>
/// <param name="Username">Login user for the session.</param>
/// <param name="PrivateKeyPath">Path to the operator-owned private key file.</param>
public sealed record SshTarget(
    string Host,
    int Port,
    string Username,
    string PrivateKeyPath);

/// <summary>Captured result of one remote command executed over SSH.</summary>
/// <param name="ExitCode">Remote process exit code.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
/// <param name="Duration">Wall-clock time the command took.</param>
public sealed record SshExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration);
