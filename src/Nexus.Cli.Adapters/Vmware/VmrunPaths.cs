namespace Nexus.Cli.Adapters.Vmware;

public static class VmrunPaths
{
    public const string PathEnvVar = "NEXUS_VMRUN_PATH";

    private static readonly string[] WindowsDefaults =
    {
        @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe",
        @"C:\Program Files\VMware\VMware Workstation\vmrun.exe"
    };

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

    public static bool IsAvailable() => Resolve() is not null;

    public static string GetVmxPath(string dir, string name)
        => Path.Combine(dir, name + ".vmx");

    public static string GetVmssSidecar(string vmxPath)
        => Path.ChangeExtension(vmxPath, ".vmss");

    public static string UnavailableMessage()
        => OperatingSystem.IsWindows()
            ? $"vmrun.exe not found. Install VMware Workstation Pro or set {PathEnvVar}."
            : "vmrun.exe is Windows-only; this command requires the win-x64 build host.";
}
