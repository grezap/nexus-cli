namespace Nexus.Cli.Adapters.Vhs;

public static class VhsPaths
{
    public const string PathEnvVar = "NEXUS_VHS_PATH";
    private const string ExeName = "vhs";

    public static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        // PATH-walk for `vhs` or `vhs.exe` (Windows extends with .exe automatically
        // via PATHEXT in the shell, but Process.Start needs the explicit suffix when
        // we resolve manually).
        var pathSep = Path.PathSeparator;
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(pathSep) ?? [];
        var candidates = OperatingSystem.IsWindows()
            ? new[] { ExeName + ".exe", ExeName }
            : new[] { ExeName };
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var c in candidates)
            {
                var full = Path.Combine(dir.Trim('"'), c);
                if (File.Exists(full))
                    return full;
            }
        }
        return null;
    }

    public static bool IsAvailable() => Resolve() is not null;

    public static string UnavailableMessage()
        => $"vhs not found on PATH. Install from https://github.com/charmbracelet/vhs " +
           $"(winget install charmbracelet.vhs / brew install vhs / scoop install vhs) " +
           $"or set {PathEnvVar} to an absolute path.";
}
