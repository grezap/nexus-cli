using System.Net.Http;
using System.Net.Http.Json;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Vault;

/// <summary>
/// CA-pinned HTTP <see cref="INexusVaultClient"/> for KV-v2 reads and PKI leaf
/// issuance. Sends the operator's token in the <c>X-Vault-Token</c> header (the
/// locked auth model, ADR-0004: the token stays on the build host) and decodes
/// responses via the source-gen <see cref="NexusJsonContext"/> (AOT-clean, no
/// reflection). The vault binary is never linked.
/// </summary>
public sealed class VaultClient : INexusVaultClient, IDisposable
{
    private readonly VaultContext _context;
    private readonly HttpClient _http;

    /// <summary>Creates a client bound to <paramref name="context"/>, minting a CA-pinned <see cref="HttpClient"/> from <paramref name="httpFactory"/> and pre-seeding the token header.</summary>
    /// <param name="context">Resolved Vault address + token + CA-bundle path.</param>
    /// <param name="httpFactory">Factory that produces the CA-bundle-pinned HTTP client.</param>
    public VaultClient(VaultContext context, NexusHttpClientFactory httpFactory)
    {
        _context = context;
        _http = httpFactory.Create();
        _http.DefaultRequestHeaders.Add("X-Vault-Token", _context.Token);
    }

    /// <inheritdoc />
    public async Task<Result<string>> ReadKvFieldAsync(
        string mount,
        string path,
        string field,
        CancellationToken cancellationToken)
    {
        var url = $"{_context.Address}/v1/{mount.Trim('/')}/data/{path.TrimStart('/')}";
        try
        {
            var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Result.Fail<string>(
                    $"Vault {(int)resp.StatusCode} reading {mount}/{path}: {resp.ReasonPhrase}");
            }

            var body = await resp.Content.ReadFromJsonAsync(
                NexusJsonContext.Default.VaultKvV2Response,
                cancellationToken).ConfigureAwait(false);

            if (body?.Data?.Data is null || !body.Data.Data.TryGetValue(field, out var value))
            {
                return Result.Fail<string>(
                    $"Vault KV at {mount}/{path} has no field '{field}'.");
            }

            return Result.Ok(value);
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail<string>($"Vault transport error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return Result.Fail<string>($"Vault timeout reading {mount}/{path}.");
        }
    }

    /// <inheritdoc />
    public async Task<Result<PkiIssueData>> IssuePkiCertAsync(
        string pkiMount,
        string role,
        string commonName,
        string altNames,
        string ipSans,
        string ttl,
        CancellationToken cancellationToken)
    {
        var url = $"{_context.Address}/v1/{pkiMount.Trim('/')}/issue/{role.TrimStart('/')}";
        // Hand-built JSON (4 string fields; values are hostnames/IPs/duration --
        // no embedded quotes) avoids registering a request DTO for the source-gen
        // context.
        var json = "{"
            + $"\"common_name\":\"{commonName}\","
            + $"\"alt_names\":\"{altNames}\","
            + $"\"ip_sans\":\"{ipSans}\","
            + $"\"ttl\":\"{ttl}\""
            + "}";
        try
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return Result.Fail<PkiIssueData>(
                    $"Vault {(int)resp.StatusCode} issuing {pkiMount}/issue/{role}: {resp.ReasonPhrase} {Trunc(detail, 200)}");
            }

            var body = await resp.Content.ReadFromJsonAsync(
                NexusJsonContext.Default.VaultPkiIssueResponse,
                cancellationToken).ConfigureAwait(false);

            if (body?.Data is null || string.IsNullOrEmpty(body.Data.Certificate))
                return Result.Fail<PkiIssueData>($"Vault PKI issue {pkiMount}/issue/{role} returned no certificate.");

            var d = body.Data;
            return Result.Ok(new PkiIssueData(
                d.Certificate, d.PrivateKey, d.IssuingCa,
                d.CaChain ?? new List<string>(), d.SerialNumber));
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail<PkiIssueData>($"Vault transport error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return Result.Fail<PkiIssueData>($"Vault timeout issuing {pkiMount}/issue/{role}.");
        }
    }

    // Cap an untrusted Vault error body before surfacing it in a failure message.
    private static string Trunc(string s, int n) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s[..n]);

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
