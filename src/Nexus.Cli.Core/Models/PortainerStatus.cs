namespace Nexus.Cli.Core.Models;

/// <summary>Reachability and version snapshot of the Portainer management endpoint.</summary>
/// <param name="Version">Reported Portainer server version.</param>
/// <param name="InstanceId">Portainer instance identifier.</param>
/// <param name="Reachable">Whether the endpoint responded to the status probe.</param>
/// <param name="AgentTaskCount">Number of agent-managed tasks, or <c>null</c> when unavailable.</param>
public sealed record PortainerStatus(
    string Version,
    string InstanceId,
    bool Reachable,
    int? AgentTaskCount);
