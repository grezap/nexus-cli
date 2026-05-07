namespace Nexus.Cli.Core.Abstractions;

public interface INexusVaultClient
{
    /// <summary>
    /// Read a single field from a Vault KV-v2 secret. Path is the secret name
    /// underneath the mount, e.g. <c>swarm/consul-bootstrap-token</c> on mount
    /// <c>nexus/</c>.
    /// </summary>
    Task<Result<string>> ReadKvFieldAsync(
        string mount,
        string path,
        string field,
        CancellationToken cancellationToken);
}
