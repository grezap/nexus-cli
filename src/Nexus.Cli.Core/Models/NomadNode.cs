namespace Nexus.Cli.Core.Models;

public sealed record NomadServer(
    string Name,
    string Address,
    bool IsLeader);

public sealed record NomadClientNode(
    string Name,
    string Address,
    string Status,
    string NodeClass);

public sealed record NomadHealth(
    IReadOnlyList<NomadServer> Servers,
    IReadOnlyList<NomadClientNode> Clients,
    string? LeaderAddress);
