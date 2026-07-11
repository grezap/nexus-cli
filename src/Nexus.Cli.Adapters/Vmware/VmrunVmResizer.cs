using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Vmware;

/// <summary>
/// <see cref="IVmResizer"/> implementation that vertically resizes a VM via
/// <c>vmrun</c> (power off) + an atomic <c>.vmx</c> edit (<c>memsize</c> /
/// <c>numvcpus</c>) + optional <c>vmware-vdiskmanager -x</c> disk grow, then a
/// cold restart and — for disk grows — an in-guest filesystem extend over SSH.
/// <para>
/// Cluster-aware: resolves the owning cluster adapter for the target VM, warms
/// its status, and consults <see cref="IClusterAdapter.CanResizeVm"/>. If the VM
/// is the current write-primary/leader (or the cluster can't be reached to prove
/// otherwise), the resize is refused unless <see cref="ScaleUpRequest.ForcePrimary"/>
/// is set. VMs with no owning data-cluster adapter (edge/workstations) skip the gate.
/// </para>
/// </summary>
public sealed class VmrunVmResizer : IVmResizer
{
    private readonly IVmsCatalog _catalog;
    private readonly IVmrunClient _vmrun;
    private readonly IClusterRegistry _registry;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;

    private static readonly TimeSpan StopStartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan GuestReadyDeadline = TimeSpan.FromMinutes(4);

    /// <summary>Creates the resizer with its collaborators and the SSH identity used for in-guest filesystem extends.</summary>
    /// <param name="catalog">vms.yaml catalog used to resolve a VM name to its cluster + on-disk location.</param>
    /// <param name="vmrun">vmrun/vdiskmanager client that performs power ops and disk grows.</param>
    /// <param name="registry">Cluster-adapter registry consulted for the write-primary safety gate.</param>
    /// <param name="ssh">SSH client used to read guest disk size and run the in-guest grow scripts.</param>
    /// <param name="sshUsername">Username for guest SSH (the lab-canonical <c>nexusadmin</c>).</param>
    /// <param name="sshKeyPath">Path to the private key used for guest SSH.</param>
    public VmrunVmResizer(
        IVmsCatalog catalog,
        IVmrunClient vmrun,
        IClusterRegistry registry,
        ISshClient ssh,
        string sshUsername,
        string sshKeyPath)
    {
        _catalog = catalog;
        _vmrun = vmrun;
        _registry = registry;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
    }

    /// <inheritdoc />
    public async Task<Result<ScaleUpResult>> ScaleUpAsync(ScaleUpRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (request.CpuCount is null && request.RamMb is null && request.DiskGb is null)
            return Result.Fail<ScaleUpResult>("scale-up requires at least one of --cpu / --ram / --disk.");
        if (request.CpuCount is <= 0)
            return Result.Fail<ScaleUpResult>("--cpu must be a positive integer.");
        if (request.RamMb is { } ram && (ram <= 0 || ram % 4 != 0))
            return Result.Fail<ScaleUpResult>("--ram (MB) must be a positive multiple of 4 (VMware memsize constraint).");
        if (request.DiskGb is <= 0)
            return Result.Fail<ScaleUpResult>("--disk (GB) must be a positive integer.");

        if (!_vmrun.IsAvailable)
            return Result.Fail<ScaleUpResult>(VmrunPaths.UnavailableMessage());

        // 1. Resolve the VM -> owning vms.yaml cluster + NodeRecord.
        var resolved = ResolveVm(request.VmName);
        if (resolved.IsFail)
            return Result.Fail<ScaleUpResult>(resolved.Error!);
        var (catalogCluster, node) = resolved.Value;

        var vmxPath = VmrunPaths.GetVmxPath(node.Dir, node.Name);
        if (!File.Exists(vmxPath))
            return Result.Fail<ScaleUpResult>($"VM '{node.Name}' is not deployed on disk (no .vmx at {vmxPath}).");

        // 2. Cluster-safety gate (refuse the write-primary unless --force-primary).
        if (!request.ForcePrimary)
        {
            var gate = await CheckResizeAllowedAsync(catalogCluster, node, cancellationToken).ConfigureAwait(false);
            if (gate is not null)
                return Result.Fail<ScaleUpResult>(gate);
        }

        // 3. Parse the current .vmx for cpu/ram + the primary disk .vmdk.
        string[] vmxLines;
        try { vmxLines = await File.ReadAllLinesAsync(vmxPath, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return Result.Fail<ScaleUpResult>($"failed to read {vmxPath}: {ex.Message}"); }

        var oldCpu = ParseVmxInt(vmxLines, "numvcpus") ?? 1;
        var oldRamMb = ParseVmxInt(vmxLines, "memsize");
        var diskFile = ParsePrimaryDiskFile(vmxLines);

        var newCpu = request.CpuCount ?? oldCpu;
        var newRamMb = request.RamMb ?? oldRamMb;
        var wantDiskGb = request.DiskGb;

        // Best-effort current disk size from the running guest (for reporting only;
        // vmware-vdiskmanager enforces grow-only regardless).
        int? oldDiskGb = null;
        var wasRunning = await IsRunningAsync(vmxPath, cancellationToken).ConfigureAwait(false);
        if (wantDiskGb is not null && wasRunning)
            oldDiskGb = await TryReadGuestDiskGbAsync(node, cancellationToken).ConfigureAwait(false);

        var cpuChange = request.CpuCount is not null && newCpu != oldCpu;
        var ramChange = request.RamMb is not null && newRamMb != oldRamMb;
        var diskChange = wantDiskGb is not null && (oldDiskGb is null || wantDiskGb > oldDiskGb);

        if (!cpuChange && !ramChange && !diskChange)
        {
            return Result.Ok(new ScaleUpResult(
                node.Name, oldCpu, oldCpu, oldRamMb, oldRamMb, oldDiskGb, oldDiskGb ?? wantDiskGb,
                "skipped", "requested values already match current; nothing to do.", sw.Elapsed));
        }
        if (wantDiskGb is not null && oldDiskGb is not null && wantDiskGb < oldDiskGb)
            return Result.Fail<ScaleUpResult>($"disk shrink not supported: current ~{oldDiskGb} GB, requested {wantDiskGb} GB (vmware-vdiskmanager only grows).");
        if (diskChange && diskFile is null)
            return Result.Fail<ScaleUpResult>("could not locate the primary disk (.vmdk fileName) in the .vmx; cannot grow disk.");

        // 4. Power off (cold — a suspend would not apply memsize/numvcpus edits).
        if (wasRunning)
        {
            var stop = await StopAndConfirmAsync(vmxPath, cancellationToken).ConfigureAwait(false);
            if (stop is not null)
                return Result.Fail<ScaleUpResult>(stop);
        }

        // 5. Atomic .vmx edit for cpu/ram.
        if (cpuChange || ramChange)
        {
            var edited = vmxLines;
            if (cpuChange) edited = SetVmxValue(edited, "numvcpus", newCpu.ToString(CultureInfo.InvariantCulture));
            if (ramChange && newRamMb is not null) edited = SetVmxValue(edited, "memsize", newRamMb.Value.ToString(CultureInfo.InvariantCulture));
            var wrote = await WriteVmxAtomicAsync(vmxPath, edited, cancellationToken).ConfigureAwait(false);
            if (wrote is not null)
            {
                if (wasRunning) await _vmrun.StartAsync(vmxPath, cancellationToken).ConfigureAwait(false);
                return Result.Fail<ScaleUpResult>(wrote);
            }
        }

        // 6. Grow the .vmdk (offline) if requested.
        var reason = new StringBuilder();
        if (diskChange)
        {
            var vmdkPath = Path.Combine(node.Dir, diskFile!);
            var grow = await _vmrun.GrowVirtualDiskAsync(vmdkPath, wantDiskGb!.Value, cancellationToken).ConfigureAwait(false);
            if (grow.IsFail)
            {
                if (wasRunning) await _vmrun.StartAsync(vmxPath, cancellationToken).ConfigureAwait(false);
                return Result.Fail<ScaleUpResult>($"disk grow failed: {grow.Error} (vmware-vdiskmanager needs the VM off, no snapshots, and a size larger than current).");
            }
        }

        // 7. Start (if it was running, or if a disk grow needs an in-guest extend).
        var mustStart = wasRunning || diskChange;
        var guestGrowFailed = false;
        if (mustStart)
        {
            var start = await _vmrun.StartAsync(vmxPath, cancellationToken).ConfigureAwait(false);
            if (start.IsFail)
                return Result.Fail<ScaleUpResult>($"VM edited but failed to restart: {start.Error}");

            // 8. In-guest filesystem extend for a disk grow (best-effort, OS-branched).
            //    Ok(null) = FS extended; Ok(msg) = vmdk grew but FS safely left alone
            //    (layout can't be extended in place); Fail = genuine error.
            if (diskChange)
            {
                var guest = await GrowGuestFilesystemAsync(node, cancellationToken).ConfigureAwait(false);
                if (guest.IsFail)
                {
                    guestGrowFailed = true;
                    reason.Append(CultureInfo.InvariantCulture, $"vmdk grown to {wantDiskGb} GB but the in-guest filesystem extend errored: {guest.Error}. Extend manually or re-run scale-up.");
                }
                else if (guest.Value is not null)
                {
                    // Safe + accurate: the disk grew, but the guest root FS was NOT
                    // auto-extended (no live repartitioning). Report honestly.
                    reason.Append(CultureInfo.InvariantCulture, $"vmdk grown to {wantDiskGb} GB, but the in-guest root filesystem was NOT auto-extended: {guest.Value.Trim()} The space is present at the disk level; extend it manually, or use a growable-root template (deb13 root-last / swapfile — infra follow-up).");
                }
            }
        }
        else if (cpuChange || ramChange)
        {
            reason.Append("VM was powered off; cpu/ram changes apply on next boot (left powered off).");
        }

        var outcome = guestGrowFailed ? "failed" : "ok";
        var newDiskGb = diskChange ? wantDiskGb : oldDiskGb;
        return Result.Ok(new ScaleUpResult(
            node.Name, oldCpu, newCpu, oldRamMb, newRamMb, oldDiskGb, newDiskGb,
            outcome, reason.Length == 0 ? null : reason.ToString(), sw.Elapsed));
    }

    // === VM resolution =====================================================
    // Find the vms.yaml cluster + NodeRecord that owns vmName (case-insensitive);
    // failure lists all known VM names to aid the operator.
    private Result<(string ClusterName, NodeRecord Node)> ResolveVm(string vmName)
    {
        var loaded = _catalog.Load();
        if (loaded.IsFail)
            return Result.Fail<(string, NodeRecord)>(loaded.Error!);
        foreach (var (clusterName, cluster) in loaded.Value!)
        {
            var node = cluster.Nodes.FirstOrDefault(n => string.Equals(n.Name, vmName, StringComparison.OrdinalIgnoreCase));
            if (node is not null)
                return Result.Ok((clusterName, node));
        }
        var all = loaded.Value!.Values.SelectMany(c => c.Nodes.Select(n => n.Name)).OrderBy(n => n, StringComparer.Ordinal);
        return Result.Fail<(string, NodeRecord)>($"unknown VM '{vmName}'. Known VMs: {string.Join(", ", all)}");
    }

    /// <summary>Returns null if the resize is allowed, else a refusal message.</summary>
    private async Task<string?> CheckResizeAllowedAsync(string catalogCluster, NodeRecord node, CancellationToken ct)
    {
        var adapterId = ResolveOwningAdapterId(catalogCluster, node.Name);
        if (adapterId is null)
            return null; // no data-cluster adapter owns this VM (edge/workstations/jumpbox) — no write-window to protect.
        var adapter = _registry.GetAdapter(adapterId);
        if (adapter.IsFail)
            return null; // adapter not registered — nothing to consult; proceed.

        // CanResizeVm reads a cache populated by GetStatusAsync — warm it first.
        var status = await adapter.Value!.GetStatusAsync(ct).ConfigureAwait(false);
        if (status.IsFail)
            return $"could not reach cluster '{adapterId}' to verify '{node.Name}' is not the current write-primary ({status.Error}). Bring the cluster up, or pass --force-primary to resize anyway.";
        if (!adapter.Value.CanResizeVm(node.Name, node.Role))
            return $"'{node.Name}' is the current primary/leader of cluster '{adapterId}'; resizing it now would disrupt the write window. Fail over first, or pass --force-primary to override.";
        return null;
    }

    /// <summary>
    /// Map a vms.yaml cluster name + VM name to the adapter ClusterId that owns
    /// the VM. Most tiers are 1:1 (adapter ClusterId == vms.yaml cluster). These
    /// are the documented splits where one vms.yaml cluster is served by more than
    /// one adapter (or a differently-named one), distinguished by node-name prefix.
    /// </summary>
    internal static string? ResolveOwningAdapterId(string catalogCluster, string vmName) => catalogCluster switch
    {
        "sqlserver" => vmName.StartsWith("sql-ag", StringComparison.OrdinalIgnoreCase) ? "sqlserver-ag" : "sqlserver",
        "foundation" => vmName.StartsWith("vault", StringComparison.OrdinalIgnoreCase) ? "vault"
            : vmName.StartsWith("dc-nexus", StringComparison.OrdinalIgnoreCase) ? "foundation-ad"
            : null,
        "platform-tools" => "registry",
        "edge" or "windows-workstations" => null,
        _ => catalogCluster,
    };

    // === vmrun power state =================================================
    private async Task<bool> IsRunningAsync(string vmxPath, CancellationToken ct)
    {
        var running = await _vmrun.ListRunningVmxPathsAsync(ct).ConfigureAwait(false);
        return running.IsOk && running.Value!.Contains(vmxPath);
    }

    /// <summary>Stop the VM (soft, then hard) and confirm it left the running set. Null on success.</summary>
    private async Task<string?> StopAndConfirmAsync(string vmxPath, CancellationToken ct)
    {
        var soft = await _vmrun.StopAsync(vmxPath, hard: false, ct).ConfigureAwait(false);
        if (soft.IsFail)
        {
            var hard = await _vmrun.StopAsync(vmxPath, hard: true, ct).ConfigureAwait(false);
            if (hard.IsFail)
                return $"failed to power off '{Path.GetFileNameWithoutExtension(vmxPath)}': {hard.Error}";
        }
        var deadline = DateTimeOffset.UtcNow + StopStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!await IsRunningAsync(vmxPath, ct).ConfigureAwait(false))
                return null;
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        return $"'{Path.GetFileNameWithoutExtension(vmxPath)}' did not power off within {StopStartTimeout.TotalSeconds:F0}s.";
    }

    // === .vmx parse / edit =================================================
    /// <summary>Reads an integer <c>key = "n"</c> value from .vmx lines; null if missing/non-numeric.</summary>
    internal static int? ParseVmxInt(IReadOnlyList<string> lines, string key)
    {
        var val = ParseVmxValue(lines, key);
        return int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>Reads a raw <c>key = "value"</c> string from .vmx lines (quotes stripped); null if absent.</summary>
    internal static string? ParseVmxValue(IReadOnlyList<string> lines, string key)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq].Trim();
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
            return line[(eq + 1)..].Trim().Trim('"').Trim();
        }
        return null;
    }

    /// <summary>Update an existing <c>key = "value"</c> line, or append one. Returns a new array.</summary>
    internal static string[] SetVmxValue(string[] lines, string key, string value)
    {
        var outLines = new List<string>(lines.Length + 1);
        var replaced = false;
        foreach (var raw in lines)
        {
            var eq = raw.IndexOf('=');
            if (eq > 0)
            {
                var k = raw[..eq].Trim();
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                {
                    outLines.Add($"{key} = \"{value}\"");
                    replaced = true;
                    continue;
                }
            }
            outLines.Add(raw);
        }
        if (!replaced)
            outLines.Add($"{key} = \"{value}\"");
        return outLines.ToArray();
    }

    /// <summary>
    /// The primary boot disk's backing file from the .vmx. Prefers the disk whose
    /// device is present + is disk-type, scanning the canonical bus keys in order.
    /// </summary>
    internal static string? ParsePrimaryDiskFile(IReadOnlyList<string> lines)
    {
        foreach (var dev in new[] { "scsi0:0", "sata0:0", "nvme0:0", "ide0:0" })
        {
            var file = ParseVmxValue(lines, dev + ".fileName");
            if (string.IsNullOrEmpty(file)) continue;
            if (!file.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase)) continue;
            var present = ParseVmxValue(lines, dev + ".present");
            if (present is not null && present.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) continue;
            return file;
        }
        return null;
    }

    private static async Task<string?> WriteVmxAtomicAsync(string vmxPath, string[] lines, CancellationToken ct)
    {
        try
        {
            var tmp = vmxPath + ".nexus-new";
            await File.WriteAllLinesAsync(tmp, lines, ct).ConfigureAwait(false);
            File.Move(tmp, vmxPath, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"failed to write {vmxPath}: {ex.Message}";
        }
    }

    // === guest-side SSH ====================================================
    private async Task<int?> TryReadGuestDiskGbAsync(NodeRecord node, CancellationToken ct)
    {
        if (IsWindows(node)) return null; // querying Windows disk size adds little; grow is enforced by vdiskmanager.
        var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var r = await _ssh.ExecuteAsync(target, "lsblk -bdno SIZE $(findmnt -no SOURCE / | sed -E 's|/dev/||; s|[0-9]+$||; s|^mapper/.*||') 2>/dev/null | head -1", SshTimeout, ct).ConfigureAwait(false);
        if (r.IsFail || r.Value!.ExitCode != 0) return null;
        var s = r.Value.Stdout.Trim();
        if (long.TryParse(s, out var bytes) && bytes > 0)
            return (int)Math.Round(bytes / 1024d / 1024d / 1024d);
        return null;
    }

    // Exit codes the grow scripts use to signal a SAFE non-extend (vs a genuine error).
    private const int GuestExitLayoutCantGrow = 3;   // partition not last / no adjacent free space
    private const int GuestExitToolUnavailable = 4;  // growpart missing + could not install

    /// <summary>
    /// Extend the guest root filesystem after a vmdk grow.
    /// <list type="bullet">
    ///   <item>Ok(null): the FS was extended.</item>
    ///   <item>Ok(message): the disk grew but the FS was intentionally NOT extended
    ///     (layout can't grow in place / tool unavailable) — safe, honest.</item>
    ///   <item>Fail(error): a genuine failure (unreachable / unexpected error).</item>
    /// </list>
    /// </summary>
    private async Task<Result<string?>> GrowGuestFilesystemAsync(NodeRecord node, CancellationToken ct)
    {
        var ready = await WaitForSshAsync(node, ct).ConfigureAwait(false);
        if (ready.IsFail) return Result.Fail<string?>(ready.Error!);

        var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var script = IsWindows(node) ? WindowsGrowScript() : LinuxGrowScript();
        var cmd = IsWindows(node) ? $"powershell -NoProfile -Command \"{script}\"" : script;
        var r = await _ssh.ExecuteAsync(target, cmd, TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string?>(r.Error!);

        var exit = r.Value!.ExitCode;
        if (exit == 0)
            return Result.Ok<string?>(null);
        if (exit == GuestExitLayoutCantGrow || exit == GuestExitToolUnavailable)
        {
            var msg = r.Value.Stdout.Trim();
            return Result.Ok<string?>(string.IsNullOrWhiteSpace(msg) ? $"guest reported it could not extend in place (exit {exit})." : msg);
        }
        var err = string.IsNullOrWhiteSpace(r.Value.Stderr) ? r.Value.Stdout.Trim() : r.Value.Stderr.Trim();
        return Result.Fail<string?>(string.IsNullOrWhiteSpace(err) ? $"exit {exit}" : err);
    }

    private async Task<Result<bool>> WaitForSshAsync(NodeRecord node, CancellationToken ct)
    {
        var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var probe = IsWindows(node) ? "cmd /c echo NEXUS_OK" : "echo NEXUS_OK";
        var deadline = DateTimeOffset.UtcNow + GuestReadyDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var r = await _ssh.ExecuteAsync(target, probe, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (r.IsOk && r.Value!.Stdout.Contains("NEXUS_OK", StringComparison.Ordinal))
                return Result.Ok(true);
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }
        return Result.Fail<bool>($"guest '{node.Name}' did not become SSH-reachable within {GuestReadyDeadline.TotalMinutes:F0} min after restart.");
    }

    private static bool IsWindows(NodeRecord node)
        => node.Os.StartsWith("ws2025", StringComparison.OrdinalIgnoreCase)
        || node.Os.StartsWith("win", StringComparison.OrdinalIgnoreCase);

    // Extend the root filesystem to fill the grown disk. Handles plain-partition
    // ext4 (growpart + resize2fs) and LVM-on-partition (growpart PV + pvresize +
    // lvextend -r). SAFE: only grows a partition when growpart's dry-run confirms
    // free space follows it (no live repartitioning). exit 3 = can't grow in place
    // (a swap/extended partition follows root, as in the deb13 default layout);
    // exit 4 = growpart unavailable and could not be installed.
    internal static string LinuxGrowScript() =>
        "set -e; " +
        "SRC=$(findmnt -no SOURCE /); " +
        "ensure_growpart() { command -v growpart >/dev/null 2>&1 || { sudo DEBIAN_FRONTEND=noninteractive apt-get update -qq >/dev/null 2>&1 || true; sudo DEBIAN_FRONTEND=noninteractive apt-get install -y cloud-guest-utils >/dev/null 2>&1 || true; }; command -v growpart >/dev/null 2>&1; }; " +
        "case \"$SRC\" in " +
        "/dev/mapper/*) " +
        "PV=$(sudo pvs --noheadings -o pv_name 2>/dev/null | awk 'NR==1{$1=$1;print}'); " +
        "[ -n \"$PV\" ] || { echo 'no LVM PV found for root'; exit 4; }; " +
        "PVDISK=/dev/$(lsblk -no PKNAME \"$PV\" | head -1); " +
        "PVPART=$(echo \"$PV\" | grep -oE '[0-9]+$'); " +
        "if ensure_growpart && sudo growpart --dry-run \"$PVDISK\" \"$PVPART\" >/dev/null 2>&1; then sudo growpart \"$PVDISK\" \"$PVPART\" >/dev/null; fi; " +
        "sudo pvresize \"$PV\" >/dev/null; " +
        "sudo lvextend -r -l +100%FREE \"$SRC\" >/dev/null 2>&1 || { echo \"no free extents to grow the LV (PV partition $PVPART may not be the last on $PVDISK)\"; exit 3; }; " +
        ";; " +
        "*) " +
        "DISK=/dev/$(lsblk -no PKNAME \"$SRC\" | head -1); " +
        "PART=$(echo \"$SRC\" | grep -oE '[0-9]+$'); " +
        "ensure_growpart || { echo 'growpart unavailable and could not be installed (no network to fetch cloud-guest-utils?)'; exit 4; }; " +
        "if ! sudo growpart --dry-run \"$DISK\" \"$PART\" >/dev/null 2>&1; then echo \"root partition $SRC has no free space to grow into -- a swap/extended partition likely follows it (root is not the last partition on $DISK)\"; exit 3; fi; " +
        "sudo growpart \"$DISK\" \"$PART\" >/dev/null; " +
        "sudo resize2fs \"$SRC\" >/dev/null; " +
        ";; " +
        "esac; " +
        "echo GREW=$(findmnt -nbo SIZE /)";

    // Rescan the disk + extend the C: volume to its max supported size (uses only
    // adjacent free space -- never moves partitions). exit 3 = C: can't grow (a
    // recovery/other partition follows it).
    internal static string WindowsGrowScript() =>
        "$ErrorActionPreference='Stop'; " +
        "$n=(Get-Partition -DriveLetter C).DiskNumber; " +
        "Update-Disk -Number $n; " +
        "$cur=(Get-Partition -DriveLetter C).Size; " +
        "$max=(Get-PartitionSupportedSize -DriveLetter C).SizeMax; " +
        "if ($max -gt $cur) { Resize-Partition -DriveLetter C -Size $max; Write-Output ('GREW=' + (Get-Volume -DriveLetter C).Size) } " +
        "else { Write-Output 'C: has no adjacent free space to grow into (a recovery/other partition may follow it)'; exit 3 }";
}
