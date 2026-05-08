using Nexus.Cli.Adapters.Vmware;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Orchestrates IVmsCatalog (canonical fleet) + IVmrunClient (live VMware
/// state) to satisfy the infrastructure verbs. State inference rules are
/// in <see cref="ClassifyState"/>; suspend/resume are idempotent — a VM
/// already in the target state returns Ok with an "already X" message.
/// </summary>
public sealed class InfrastructureService : IInfrastructureService
{
    private readonly IVmsCatalog _catalog;
    private readonly IVmrunClient _vmrun;

    public InfrastructureService(IVmsCatalog catalog, IVmrunClient vmrun)
    {
        _catalog = catalog;
        _vmrun = vmrun;
    }

    public async Task<Result<IReadOnlyList<VmStatus>>> ListAsync(CancellationToken cancellationToken)
    {
        var loaded = _catalog.Load();
        if (loaded.IsFail)
            return Result.Fail<IReadOnlyList<VmStatus>>(loaded.Error!);

        var running = await ResolveRunningSetAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<VmStatus>();
        foreach (var kv in loaded.Value!.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var node in kv.Value.Nodes)
                rows.Add(BuildStatus(kv.Key, node, running));
        }
        return Result.Ok<IReadOnlyList<VmStatus>>(rows);
    }

    public async Task<Result<IReadOnlyList<VmStatus>>> StatusAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(clusterName);
        if (cluster.IsFail)
            return Result.Fail<IReadOnlyList<VmStatus>>(cluster.Error!);

        IReadOnlyList<NodeRecord> nodes = cluster.Value!.Nodes;
        if (nodeName is not null)
        {
            nodes = nodes.Where(n => string.Equals(n.Name, nodeName, StringComparison.Ordinal)).ToList();
            if (nodes.Count == 0)
            {
                var known = string.Join(", ", cluster.Value.Nodes.Select(n => n.Name));
                return Result.Fail<IReadOnlyList<VmStatus>>(
                    $"unknown node '{nodeName}' in cluster '{clusterName}'. Known: {known}");
            }
        }

        var running = await ResolveRunningSetAsync(cancellationToken).ConfigureAwait(false);
        var rows = nodes.Select(n => BuildStatus(clusterName, n, running)).ToList();
        return Result.Ok<IReadOnlyList<VmStatus>>(rows);
    }

    public async Task<Result<IReadOnlyList<OpResult>>> SuspendAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken)
    {
        if (!_vmrun.IsAvailable)
            return Result.Fail<IReadOnlyList<OpResult>>(VmrunPaths.UnavailableMessage());

        var statuses = await StatusAsync(clusterName, nodeName, cancellationToken).ConfigureAwait(false);
        if (statuses.IsFail)
            return Result.Fail<IReadOnlyList<OpResult>>(statuses.Error!);

        var ops = new List<OpResult>();
        foreach (var s in statuses.Value!)
        {
            switch (s.State)
            {
                case VmRuntimeState.Missing:
                    ops.Add(new OpResult(clusterName, s.Node.Name, false, "vmx file not on disk (planned, not deployed)"));
                    break;
                case VmRuntimeState.Stopped:
                    ops.Add(new OpResult(clusterName, s.Node.Name, true, "already stopped"));
                    break;
                case VmRuntimeState.Suspended:
                    ops.Add(new OpResult(clusterName, s.Node.Name, true, "already suspended"));
                    break;
                case VmRuntimeState.Running:
                    var r = await _vmrun.SuspendAsync(s.VmxPath, cancellationToken).ConfigureAwait(false);
                    ops.Add(new OpResult(clusterName, s.Node.Name, r.IsOk, r.IsOk ? "suspended" : r.Error!));
                    break;
                default:
                    ops.Add(new OpResult(clusterName, s.Node.Name, false, $"state '{s.State}' is not actionable"));
                    break;
            }
        }
        return Result.Ok<IReadOnlyList<OpResult>>(ops);
    }

    public async Task<Result<IReadOnlyList<OpResult>>> ResumeAsync(
        string clusterName,
        string? nodeName,
        CancellationToken cancellationToken)
    {
        if (!_vmrun.IsAvailable)
            return Result.Fail<IReadOnlyList<OpResult>>(VmrunPaths.UnavailableMessage());

        var statuses = await StatusAsync(clusterName, nodeName, cancellationToken).ConfigureAwait(false);
        if (statuses.IsFail)
            return Result.Fail<IReadOnlyList<OpResult>>(statuses.Error!);

        var ops = new List<OpResult>();
        foreach (var s in statuses.Value!)
        {
            switch (s.State)
            {
                case VmRuntimeState.Missing:
                    ops.Add(new OpResult(clusterName, s.Node.Name, false, "vmx file not on disk (planned, not deployed)"));
                    break;
                case VmRuntimeState.Running:
                    ops.Add(new OpResult(clusterName, s.Node.Name, true, "already running"));
                    break;
                case VmRuntimeState.Stopped:
                case VmRuntimeState.Suspended:
                    var r = await _vmrun.ResumeAsync(s.VmxPath, cancellationToken).ConfigureAwait(false);
                    ops.Add(new OpResult(clusterName, s.Node.Name, r.IsOk, r.IsOk ? "resumed" : r.Error!));
                    break;
                default:
                    ops.Add(new OpResult(clusterName, s.Node.Name, false, $"state '{s.State}' is not actionable"));
                    break;
            }
        }
        return Result.Ok<IReadOnlyList<OpResult>>(ops);
    }

    private async Task<IReadOnlySet<string>?> ResolveRunningSetAsync(CancellationToken cancellationToken)
    {
        if (!_vmrun.IsAvailable)
            return null;
        var r = await _vmrun.ListRunningVmxPathsAsync(cancellationToken).ConfigureAwait(false);
        return r.IsOk
            ? r.Value
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static VmStatus BuildStatus(string clusterName, NodeRecord node, IReadOnlySet<string>? running)
    {
        var vmx = VmrunPaths.GetVmxPath(node.Dir, node.Name);
        var state = ClassifyState(
            vmrunAvailable: running is not null,
            vmxExists: File.Exists(vmx),
            vmssExists: File.Exists(VmrunPaths.GetVmssSidecar(vmx)),
            inRunningSet: running is not null && running.Contains(vmx));
        return new VmStatus(clusterName, node, state, vmx);
    }

    internal static VmRuntimeState ClassifyState(bool vmrunAvailable, bool vmxExists, bool vmssExists, bool inRunningSet)
    {
        if (!vmrunAvailable)
            return VmRuntimeState.Unknown;
        if (!vmxExists)
            return VmRuntimeState.Missing;
        if (inRunningSet)
            return VmRuntimeState.Running;
        if (vmssExists)
            return VmRuntimeState.Suspended;
        return VmRuntimeState.Stopped;
    }
}
