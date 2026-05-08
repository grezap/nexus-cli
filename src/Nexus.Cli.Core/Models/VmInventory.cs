namespace Nexus.Cli.Core.Models;

public sealed record NodeRecord(
    string Name,
    string Os,
    string Vmnet10,
    string Vmnet11,
    string Dir,
    string Role);

public sealed record ClusterRecord(
    string Name,
    string Purpose,
    string Phase,
    IReadOnlyList<NodeRecord> Nodes);

public enum VmRuntimeState
{
    Running,
    Suspended,
    Stopped,
    Missing,
    Unknown
}

public sealed record VmStatus(
    string ClusterName,
    NodeRecord Node,
    VmRuntimeState State,
    string VmxPath);

public sealed record OpResult(
    string ClusterName,
    string NodeName,
    bool Success,
    string Message);
