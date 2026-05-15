using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Vmware;

/// <summary>
/// <see cref="IVmResizer"/> implementation that uses <c>vmrun</c> to power off
/// the target VM, edits the <c>.vmx</c> file (<c>memsize</c> / <c>numvcpus</c>),
/// then restarts. Disk resize is more complex (involves <c>vmware-vdiskmanager</c>
/// + guest-side <c>lvextend</c> / <c>resize2fs</c>) and lands in 0.G.1.x.
/// <para>
/// Cluster-aware via <see cref="IClusterRegistry"/>: looks up the owning cluster
/// adapter for the target VM, calls <see cref="IClusterAdapter.CanResizeVm"/>,
/// and refuses the operation if false unless
/// <see cref="ScaleUpRequest.ForcePrimary"/> is set.
/// </para>
/// <para>
/// Implementation status (0.G.1 framework ship): SKELETON. The full vmrun stop
/// + .vmx edit + vmrun start sequence + per-cluster CanResizeVm consultation
/// lands in 0.G.1.x once at least one live cluster is up to validate against.
/// </para>
/// </summary>
public sealed class VmrunVmResizer : IVmResizer
{
    private readonly IVmsCatalog _catalog;
    private readonly IVmrunClient _vmrun;
    private readonly IClusterRegistry _registry;

    public VmrunVmResizer(IVmsCatalog catalog, IVmrunClient vmrun, IClusterRegistry registry)
    {
        _catalog = catalog;
        _vmrun = vmrun;
        _registry = registry;
    }

    public Task<Result<ScaleUpResult>> ScaleUpAsync(ScaleUpRequest request, CancellationToken cancellationToken)
    {
        // TODO 0.G.1.x: full implementation.
        //
        // Apply-flow:
        //   1. Resolve vm via _catalog -- get its ClusterName + NodeRecord (Dir contains the .vmx path).
        //   2. Resolve owning adapter via _registry.Get(clusterName); call CanResizeVm(vmName, role).
        //      If false and !request.ForcePrimary, refuse with a clear message.
        //   3. Capture current memsize / numvcpus / disk size from the .vmx file (parse "key = value" lines).
        //   4. vmrun stop <vmx> soft (graceful guest shutdown).
        //   5. Edit .vmx atomically (write to .vmx.new, rename).
        //   6. vmrun start <vmx> nogui.
        //   7. If disk grew: SSH in + lvextend + resize2fs (guest-side; needs SSH client).
        //   8. Return ScaleUpResult with old/new values + duration.
        //
        // Until that lands, return a clear-failure with the request shape so
        // the command class can render an actionable error.
        var hasChange = request.CpuCount.HasValue || request.RamMb.HasValue || request.DiskGb.HasValue;
        if (!hasChange)
            return Task.FromResult(Result.Fail<ScaleUpResult>(
                "scale-up requires at least one of --cpu / --ram / --disk."));
        return Task.FromResult(Result.Fail<ScaleUpResult>(
            $"VmrunVmResizer.ScaleUpAsync (VM '{request.VmName}') is a skeleton in the 0.G.1 framework ship; "
            + "full vmrun stop + .vmx edit + vmrun start (+ optional guest-side disk grow) lands in 0.G.1.x. "
            + "Cluster-aware refusal-for-primary will consult IClusterRegistry once implemented."));
    }
}
