namespace Nexus.Cli.Core.Models;

public sealed record PortainerStatus(
    string Version,
    string InstanceId,
    bool Reachable,
    int? AgentTaskCount);
