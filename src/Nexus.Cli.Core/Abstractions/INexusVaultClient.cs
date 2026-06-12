using Nexus.Cli.Core.Models;

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

    /// <summary>
    /// Issue a leaf certificate from a Vault PKI role
    /// (<c>&lt;pkiMount&gt;/issue/&lt;role&gt;</c>). Used by the SQL Server
    /// adapters' cert-rotate verb (the node can't mint its own PFX: ws2025 has
    /// no openssl). The process Vault token must carry the issue capability.
    /// </summary>
    Task<Result<PkiIssueData>> IssuePkiCertAsync(
        string pkiMount,
        string role,
        string commonName,
        string altNames,
        string ipSans,
        string ttl,
        CancellationToken cancellationToken);
}
