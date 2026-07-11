namespace Nexus.Cli.Core.Models;

/// <summary>One Nomad server (control-plane) node in the raft peer set.</summary>
/// <param name="Name">Server node name.</param>
/// <param name="Address">RPC advertise address.</param>
/// <param name="IsLeader">True when this server currently holds raft leadership.</param>
public sealed record NomadServer(
    string Name,
    string Address,
    bool IsLeader);

/// <summary>One Nomad client (worker) node that runs allocations.</summary>
/// <param name="Name">Client node name.</param>
/// <param name="Address">Client HTTP/RPC address.</param>
/// <param name="Status">Node readiness (e.g. <c>ready</c>, <c>down</c>, <c>ineligible</c>).</param>
/// <param name="NodeClass">Scheduling class the node advertises for constraint matching.</param>
public sealed record NomadClientNode(
    string Name,
    string Address,
    string Status,
    string NodeClass);

/// <summary>Rolled-up Nomad cluster health: servers, clients and the elected leader.</summary>
/// <param name="Servers">Control-plane server peers.</param>
/// <param name="Clients">Worker client nodes.</param>
/// <param name="LeaderAddress">Address of the raft leader, or <c>null</c> when none is elected.</param>
public sealed record NomadHealth(
    IReadOnlyList<NomadServer> Servers,
    IReadOnlyList<NomadClientNode> Clients,
    string? LeaderAddress);
