namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Wrapper over the VMware Workstation <c>vmrun</c>/<c>vmware-vdiskmanager</c> CLIs:
/// enumerates and controls VM power state and grows backing disks for the fleet.
/// </summary>
public interface IVmrunClient
{
    /// <summary>Whether the <c>vmrun</c> binary is present and usable on this host.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns the set of <c>.vmx</c> paths for all currently running VMs.</summary>
    Task<Result<IReadOnlySet<string>>> ListRunningVmxPathsAsync(CancellationToken cancellationToken);

    /// <summary>Suspends the VM at <paramref name="vmxPath"/> (memory preserved).</summary>
    Task<Result<bool>> SuspendAsync(string vmxPath, CancellationToken cancellationToken);

    /// <summary>Resumes the suspended VM at <paramref name="vmxPath"/>.</summary>
    Task<Result<bool>> ResumeAsync(string vmxPath, CancellationToken cancellationToken);

    /// <summary>
    /// Fully power off the VM (<c>vmrun stop &lt;vmx&gt; soft|hard</c>). <c>soft</c>
    /// asks the guest (VMware Tools) to shut down cleanly; <c>hard</c> pulls the
    /// virtual power. Used by the <c>scale-up</c> resizer, which needs a cold
    /// power-off (not a suspend) before editing <c>memsize</c>/<c>numvcpus</c> or
    /// growing the backing <c>.vmdk</c>.
    /// </summary>
    Task<Result<bool>> StopAsync(string vmxPath, bool hard, CancellationToken cancellationToken);

    /// <summary>Cold-start the VM headless (<c>vmrun start &lt;vmx&gt; nogui</c>).</summary>
    Task<Result<bool>> StartAsync(string vmxPath, CancellationToken cancellationToken);

    /// <summary>
    /// Grow a virtual disk to <paramref name="newSizeGb"/> GB via
    /// <c>vmware-vdiskmanager -x &lt;n&gt;GB &lt;vmdk&gt;</c>. The VM MUST be powered
    /// off and free of snapshots. Grow-only: vmware-vdiskmanager refuses to shrink.
    /// The guest filesystem does NOT auto-grow — the caller extends it in-guest
    /// afterward (growpart/resize2fs on Linux, Resize-Partition on Windows).
    /// </summary>
    Task<Result<bool>> GrowVirtualDiskAsync(string vmdkPath, int newSizeGb, CancellationToken cancellationToken);
}
