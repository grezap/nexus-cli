namespace Nexus.Cli.Adapters.Ssh;

/// <summary>
/// Resolves the operator's SSH private key path without touching the key contents.
/// Lookup order: <c>NEXUS_SSH_KEY</c> env var, then the canonical OpenSSH paths
/// under the user's home directory.
/// </summary>
public static class SshKeyDiscovery
{
    public const string KeyEnvVar = "NEXUS_SSH_KEY";

    private static readonly string[] DefaultRelativePaths =
    {
        Path.Combine(".ssh", "id_ed25519"),
        Path.Combine(".ssh", "id_rsa")
    };

    public static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable(KeyEnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return null;

        foreach (var rel in DefaultRelativePaths)
        {
            var candidate = Path.Combine(home, rel);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static string UnavailableMessage()
        => $"no SSH private key found. Set {KeyEnvVar} to an absolute path, or place a key at " +
           $"{Path.Combine("~", ".ssh", "id_ed25519")} or {Path.Combine("~", ".ssh", "id_rsa")}.";
}
