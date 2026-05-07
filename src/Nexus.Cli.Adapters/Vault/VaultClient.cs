using System.Net.Http;
using System.Net.Http.Json;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Vault;

public sealed class VaultClient : INexusVaultClient, IDisposable
{
    private readonly VaultContext _context;
    private readonly HttpClient _http;

    public VaultClient(VaultContext context, NexusHttpClientFactory httpFactory)
    {
        _context = context;
        _http = httpFactory.Create();
        _http.DefaultRequestHeaders.Add("X-Vault-Token", _context.Token);
    }

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

    public void Dispose() => _http.Dispose();
}
