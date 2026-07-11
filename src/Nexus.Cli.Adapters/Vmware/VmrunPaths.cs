namespace Nexus.Cli.Adapters.Vmware;

/// <summary>
/// Path helpers for the VMware Workstation toolchain: locates <c>vmrun.exe</c> and
/// its sibling <c>vmware-vdiskmanager.exe</c>, derives <c>.vmx</c>/<c>.vmss</c>/<c>.vmem</c>
/// sidecar paths, and detects suspended-VM state on disk. Windows-only for the
/// live tools (the lab build host is win-x64); non-Windows callers get a clear
/// unavailable message rather than a spawn attempt.
/// </summary>
public static class VmrunPaths
{
    /// <summary>Environment variable that, when set to an existing file, overrides the default vmrun.exe search.</summary>
    public const string PathEnvVar = "NEXUS_VMRUN_PATH";

    private static readonly string[] WindowsDefaults =
    {
        @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe",
        @"C:\Program Files\VMware\VMware Workstation\vmrun.exe"
    };

    /// <summary>Resolves the vmrun.exe path (env override, then Workstation defaults), or <c>null</c> off-Windows / when absent.</summary>
    public static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        if (!OperatingSystem.IsWindows())
            return null;

        foreach (var candidate in WindowsDefaults)
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>True when vmrun.exe can be resolved on this host.</summary>
    public static bool IsAvailable() => Resolve() is not null;

    /// <summary>Environment variable that, when set to an existing file, overrides the vmware-vdiskmanager.exe search.</summary>
    public const string VdiskManagerEnvVar = "NEXUS_VDISKMANAGER_PATH";

    /// <summary>
    /// Locate <c>vmware-vdiskmanager.exe</c> (ships in the same VMware Workstation
    /// install dir as vmrun.exe; used by <c>scale-up --disk</c> to grow a .vmdk).
    /// Honours <c>NEXUS_VDISKMANAGER_PATH</c>, else derives it from the resolved
    /// vmrun.exe directory, else probes the Workstation defaults.
    /// </summary>
    public static string? ResolveVdiskManager()
    {
        var env = Environment.GetEnvironmentVariable(VdiskManagerEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        if (!OperatingSystem.IsWindows())
            return null;

        var vmrun = Resolve();
        if (vmrun is not null)
        {
            var dir = Path.GetDirectoryName(vmrun);
            if (!string.IsNullOrEmpty(dir))
            {
                var sibling = Path.Combine(dir, "vmware-vdiskmanager.exe");
                if (File.Exists(sibling))
                    return sibling;
            }
        }

        foreach (var candidate in WindowsDefaults)
        {
            var dir = Path.GetDirectoryName(candidate);
            if (string.IsNullOrEmpty(dir))
                continue;
            var vdm = Path.Combine(dir, "vmware-vdiskmanager.exe");
            if (File.Exists(vdm))
                return vdm;
        }
        return null;
    }

    /// <summary>Operator-facing guidance shown when vmware-vdiskmanager.exe is required but unavailable.</summary>
    public static string VdiskManagerUnavailableMessage()
        => OperatingSystem.IsWindows()
            ? $"vmware-vdiskmanager.exe not found (needed for --disk grows). Set {VdiskManagerEnvVar} or install VMware Workstation Pro."
            : "vmware-vdiskmanager.exe is Windows-only; --disk grows require the win-x64 build host.";

    /// <summary>Builds the canonical <c>&lt;dir&gt;/&lt;name&gt;.vmx</c> path for a VM.</summary>
    public static string GetVmxPath(string dir, string name)
        => Path.Combine(dir, name + ".vmx");

    /// <summary>Canonical un-suffixed .vmss path. Older Workstation versions emit this.</summary>
    public static string GetVmssSidecar(string vmxPath)
        => Path.ChangeExtension(vmxPath, ".vmss");

    /// <summary>Canonical un-suffixed .vmem path. Older Workstation versions emit this.</summary>
    public static string GetVmemSidecar(string vmxPath)
        => Path.ChangeExtension(vmxPath, ".vmem");

    /// <summary>
    /// True if the VM has on-disk evidence of preserved memory state
    /// (suspended, not stopped). Workstation Pro 17.5+ session-suffixes the
    /// memory paging file: e.g. <c>vault-3-3c85c1f6.vmem</c> rather than
    /// <c>vault-3.vmem</c>. Directory-prefix search catches both shapes
    /// plus the canonical un-suffixed .vmss/.vmem from older versions.
    /// Combined with "not in vmrun list" (= not currently running), the
    /// presence of ANY <c>&lt;basename&gt;*.vmem</c> or <c>&lt;basename&gt;*.vmss</c>
    /// indicates a suspended VM.
    /// </summary>
    public static bool HasSuspendedStateSidecar(string vmxPath)
    {
        var dir = Path.GetDirectoryName(vmxPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return false;
        var baseName = Path.GetFileNameWithoutExtension(vmxPath);
        if (string.IsNullOrEmpty(baseName))
            return false;
        return Directory.EnumerateFiles(dir, $"{baseName}*.vmem").Any()
            || Directory.EnumerateFiles(dir, $"{baseName}*.vmss").Any();
    }

    /// <summary>Operator-facing guidance shown when vmrun.exe is required but unavailable.</summary>
    public static string UnavailableMessage()
        => OperatingSystem.IsWindows()
            ? $"vmrun.exe not found. Install VMware Workstation Pro or set {PathEnvVar}."
            : "vmrun.exe is Windows-only; this command requires the win-x64 build host.";
}
