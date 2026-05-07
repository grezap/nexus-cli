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
    private readonly X509Certificate2Collection _trustRoots;
    private readonly TimeSpan _timeout;
    private readonly List<HttpClient> _clients = new();

    public NexusHttpClientFactory(string caBundlePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(caBundlePath);
        if (!File.Exists(caBundlePath))
            throw new FileNotFoundException("CA bundle not found.", caBundlePath);

        _trustRoots = new X509Certificate2Collection();
        _trustRoots.ImportFromPemFile(caBundlePath);
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
        policyChain.ChainPolicy.CustomTrustStore.AddRange(_trustRoots);

        // Stage any intermediates from the inbound chain so partial bundles still validate.
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
        foreach (var cert in _trustRoots) cert.Dispose();
    }
}
