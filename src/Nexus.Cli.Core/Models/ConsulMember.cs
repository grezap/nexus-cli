namespace Nexus.Cli.Core.Models;

public sealed record ConsulMember(
    string Name,
    string Addr,
    int Port,
    string Status,
    string Role,
    string Datacenter);

public sealed record ConsulHealth(
    IReadOnlyList<ConsulMember> Members,
    string? Leader,
    int Alive,
    int Failed);
