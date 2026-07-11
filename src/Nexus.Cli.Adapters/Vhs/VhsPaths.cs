namespace Nexus.Cli.Adapters.Vhs;

/// <summary>
/// Locates the charmbracelet/vhs binary used by <c>demo record</c>. Prefers the
/// <see cref="PathEnvVar"/> override, else walks <c>PATH</c> (adding the <c>.exe</c>
/// suffix on Windows, since manual resolution does not benefit from PATHEXT).
/// </summary>
public static class VhsPaths
{
    /// <summary>Environment variable that, when set to an existing file, overrides the PATH walk.</summary>
    public const string PathEnvVar = "NEXUS_VHS_PATH";
    private const string ExeName = "vhs";

    /// <summary>Resolves the vhs executable path, or <c>null</c> if it cannot be found.</summary>
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

    /// <summary>True when a vhs binary can be resolved on this host.</summary>
    public static bool IsAvailable() => Resolve() is not null;

    /// <summary>Operator-facing install guidance shown when vhs is unavailable.</summary>
    public static string UnavailableMessage()
        => $"vhs not found on PATH. Install from https://github.com/charmbracelet/vhs " +
           $"(winget install charmbracelet.vhs / brew install vhs / scoop install vhs) " +
           $"or set {PathEnvVar} to an absolute path.";
}
