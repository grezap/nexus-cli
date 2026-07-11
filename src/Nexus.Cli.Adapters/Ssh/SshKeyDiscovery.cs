namespace Nexus.Cli.Adapters.Ssh;

/// <summary>
/// Resolves the operator's SSH private key path without touching the key contents.
/// Lookup order: <c>NEXUS_SSH_KEY</c> env var, then the canonical OpenSSH paths
/// under the user's home directory.
/// <para>
/// <c>nexus_gateway_ed25519</c> is preferred: it is the lab-canonical key name
/// referenced by <c>~/.ssh/config</c>'s <c>Host 192.168.70.*</c> stanza on the
/// build host. The fleet's <c>authorized_keys</c> files trust it under the
/// comment <c>nexusadmin@nexus-gateway</c>. A user's personal/GitHub
/// <c>id_ed25519</c> is NOT authorized on the lab VMs even when present, so
/// preferring it (as v0.4.x did) silently breaks every SSH-using verb against
/// the kafka + later tiers. Falls back to <c>id_ed25519</c> / <c>id_rsa</c>
/// for environments where the user has aliased their lab key to one of those
/// canonical names.
/// </para>
/// </summary>
public static class SshKeyDiscovery
{
    /// <summary>Environment variable that, when set to an existing file, overrides the default key search.</summary>
    public const string KeyEnvVar = "NEXUS_SSH_KEY";

    private static readonly string[] DefaultRelativePaths =
    {
        Path.Combine(".ssh", "nexus_gateway_ed25519"),
        Path.Combine(".ssh", "id_ed25519"),
        Path.Combine(".ssh", "id_rsa"),
    };

    /// <summary>
    /// Resolves the SSH private-key path: <see cref="KeyEnvVar"/> first, then the
    /// preference-ordered <c>~/.ssh</c> candidates. Returns <c>null</c> when none exist.
    /// </summary>
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

    /// <summary>Operator-facing guidance shown when <see cref="Resolve"/> finds no usable key.</summary>
    public static string UnavailableMessage()
        => $"no SSH private key found. Set {KeyEnvVar} to an absolute path, or place a key at " +
           $"{Path.Combine("~", ".ssh", "nexus_gateway_ed25519")} (preferred — the lab-canonical name), " +
           $"{Path.Combine("~", ".ssh", "id_ed25519")}, or {Path.Combine("~", ".ssh", "id_rsa")}.";
}
