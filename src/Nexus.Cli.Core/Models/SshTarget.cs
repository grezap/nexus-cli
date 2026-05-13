namespace Nexus.Cli.Core.Models;

/// <summary>
/// Endpoint + credentials for one SSH session. PrivateKeyPath is operator-
/// owned; the CLI never reads or stores the key contents itself.
/// </summary>
public sealed record SshTarget(
    string Host,
    int Port,
    string Username,
    string PrivateKeyPath);

public sealed record SshExecResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration);
