namespace Nexus.Cli.Core.Models;

/// <summary>Declared inventory record for one VM as defined in <c>vms.yaml</c>.</summary>
/// <param name="Name">VM name.</param>
/// <param name="Os">Guest operating system identifier.</param>
/// <param name="Vmnet10">Backplane (VMnet10) IP address.</param>
/// <param name="Vmnet11">Management (VMnet11) IP address.</param>
/// <param name="Dir">Host directory holding the VM's files.</param>
/// <param name="Role">Cluster role the VM plays.</param>
public sealed record NodeRecord(
    string Name,
    string Os,
    string Vmnet10,
    string Vmnet11,
    string Dir,
    string Role);

/// <summary>A cluster grouping of VM nodes as declared in the inventory.</summary>
/// <param name="Name">Cluster name.</param>
/// <param name="Purpose">Short description of what the cluster provides.</param>
/// <param name="Phase">Platform build phase the cluster belongs to.</param>
/// <param name="Nodes">Member VM records.</param>
public sealed record ClusterRecord(
    string Name,
    string Purpose,
    string Phase,
    IReadOnlyList<NodeRecord> Nodes);

/// <summary>Observed power/existence state of a VM on the host.</summary>
public enum VmRuntimeState
{
    /// <summary>The VM is powered on.</summary>
    Running,

    /// <summary>The VM is suspended (paused to disk).</summary>
    Suspended,

    /// <summary>The VM exists but is powered off.</summary>
    Stopped,

    /// <summary>The VM's files were not found on the host.</summary>
    Missing,

    /// <summary>The VM state could not be determined.</summary>
    Unknown
}

/// <summary>Runtime state of one declared node reconciled against the host.</summary>
/// <param name="ClusterName">Name of the cluster the node belongs to.</param>
/// <param name="Node">The declared inventory record for the node.</param>
/// <param name="State">Observed runtime state on the host.</param>
/// <param name="VmxPath">Path to the VM's <c>.vmx</c> file.</param>
public sealed record VmStatus(
    string ClusterName,
    NodeRecord Node,
    VmRuntimeState State,
    string VmxPath);

/// <summary>Outcome of a per-node VM lifecycle operation (start, stop, suspend, etc.).</summary>
/// <param name="ClusterName">Name of the cluster the node belongs to.</param>
/// <param name="NodeName">Name of the targeted node.</param>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Message">Human-readable outcome or error detail.</param>
public sealed record OpResult(
    string ClusterName,
    string NodeName,
    bool Success,
    string Message);
