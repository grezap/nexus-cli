namespace Nexus.Cli.Core.Models;

/// <summary>One node in a Consul serf gossip pool as reported by <c>consul members</c>.</summary>
/// <param name="Name">Agent node name.</param>
/// <param name="Addr">Serf LAN advertise address.</param>
/// <param name="Port">Serf LAN port.</param>
/// <param name="Status">Membership state (e.g. <c>alive</c>, <c>failed</c>, <c>left</c>).</param>
/// <param name="Role">Agent role: <c>server</c> or <c>client</c>.</param>
/// <param name="Datacenter">Consul datacenter the agent belongs to.</param>
public sealed record ConsulMember(
    string Name,
    string Addr,
    int Port,
    string Status,
    string Role,
    string Datacenter);

/// <summary>Rolled-up Consul cluster health: membership plus elected leader and alive/failed counts.</summary>
/// <param name="Members">All known serf pool members.</param>
/// <param name="Leader">Address of the raft leader, or <c>null</c> when none is elected.</param>
/// <param name="Alive">Count of members reporting <c>alive</c>.</param>
/// <param name="Failed">Count of members reporting <c>failed</c>.</param>
public sealed record ConsulHealth(
    IReadOnlyList<ConsulMember> Members,
    string? Leader,
    int Alive,
    int Failed);
