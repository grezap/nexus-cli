namespace Nexus.Cli.Core.Models;

/// <summary>
/// A freshly-issued PKI leaf from Vault's <c>pki_int/issue/&lt;role&gt;</c>
/// endpoint. Used by the SQL Server adapters' <c>cert-rotate</c> verb: ws2025
/// has no openssl + .NET Framework 4.8 on-node can't import a PKCS#1 PEM key,
/// so the cert is issued on the build host (where the CLI runs under .NET 10),
/// turned into a PFX via <c>X509Certificate2.CreateFromPem</c>, and shipped to
/// the node for <c>Import-PfxCertificate</c> (mirrors role-overlay-sqlserver-tls.tf).
/// </summary>
/// <param name="Certificate">The issued leaf certificate in PEM form.</param>
/// <param name="PrivateKey">The leaf's private key in PEM form.</param>
/// <param name="IssuingCa">The issuing CA certificate in PEM form.</param>
/// <param name="CaChain">The full CA chain, root-last, in PEM form.</param>
/// <param name="SerialNumber">Serial number of the issued leaf.</param>
public sealed record PkiIssueData(
    string Certificate,
    string PrivateKey,
    string IssuingCa,
    IReadOnlyList<string> CaChain,
    string SerialNumber);
