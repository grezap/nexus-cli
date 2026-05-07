using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Nexus.Cli.Adapters.Http;

/// <summary>
/// Builds <see cref="HttpClient"/> instances pinned to the operator's CA bundle.
/// The chain build relays intermediates through <see cref="X509ChainPolicy.ExtraStore"/>
/// (per memory feedback_smoke_gate_probe_robustness.md — Schannel doesn't auto-resolve
/// intermediates from the bundle, so they must be staged into ExtraStore).
/// </summary>
public sealed class NexusHttpClientFactory : IDisposable
{
    private readonly X509Certificate2Collection _roots = new();
    private readonly X509Certificate2Collection _intermediates = new();
    private readonly TimeSpan _timeout;
    private readonly List<HttpClient> _clients = new();

    public NexusHttpClientFactory(string caBundlePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(caBundlePath);
        if (!File.Exists(caBundlePath))
            throw new FileNotFoundException("CA bundle not found.", caBundlePath);

        // Load every cert from the PEM and split by self-signed vs intermediate.
        // Custom trust mode requires only roots in CustomTrustStore;
        // intermediates must go to ExtraStore for the chain builder to use.
        var bundle = new X509Certificate2Collection();
        bundle.ImportFromPemFile(caBundlePath);
        foreach (var cert in bundle)
        {
            if (string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal))
                _roots.Add(cert);
            else
                _intermediates.Add(cert);
        }

        if (_roots.Count == 0)
            throw new InvalidOperationException(
                $"CA bundle '{caBundlePath}' contained no self-signed roots; cannot anchor TLS validation.");

        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = ValidateChain
            }
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = _timeout
        };
        _clients.Add(client);
        return client;
    }

    private bool ValidateChain(
        object _,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        if (certificate is null) return false;

        // Build a fresh chain with our trust roots; ignore the system store.
        using var serverCert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var policyChain = new X509Chain
        {
            ChainPolicy =
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                VerificationFlags = X509VerificationFlags.NoFlag
            }
        };
        policyChain.ChainPolicy.CustomTrustStore.AddRange(_roots);
        policyChain.ChainPolicy.ExtraStore.AddRange(_intermediates);

        // Also stage any intermediates from the inbound chain so partial bundles still validate.
        if (chain is not null)
        {
            foreach (var element in chain.ChainElements)
            {
                policyChain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return policyChain.Build(serverCert);
    }

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
        _clients.Clear();
        foreach (var cert in _roots) cert.Dispose();
        foreach (var cert in _intermediates) cert.Dispose();
    }
}
