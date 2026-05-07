namespace Nexus.Cli.Core.Models;

public sealed record VaultContext(
    string Address,
    string Token,
    string CaBundlePath);
