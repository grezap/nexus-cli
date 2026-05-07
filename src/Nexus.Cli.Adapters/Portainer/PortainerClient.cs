using System.Net.Http;
using System.Net.Http.Json;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Portainer;

public sealed class PortainerClient : INexusPortainerClient, IDisposable
{
    public sealed record Settings(string BaseAddress, string AdminUser, string AdminPassword);

    private readonly Settings _settings;
    private readonly HttpClient _http;

    public PortainerClient(Settings settings, NexusHttpClientFactory httpFactory)
    {
        _settings = settings;
        _http = httpFactory.Create();
    }

    public async Task<Result<PortainerStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            // /api/system/status is unauthenticated as of Portainer CE 2.x — no JWT needed for basic up-or-down.
            // We attempt auth too so we can populate richer fields if/when we expand the surface (e.g. agent counts).
            var statusUrl = $"{_settings.BaseAddress.TrimEnd('/')}/api/system/status";
            var resp = await _http.GetAsync(statusUrl, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Result.Fail<PortainerStatus>(
                    $"Portainer {(int)resp.StatusCode} on /api/system/status: {resp.ReasonPhrase}");
            }

            var dto = await resp.Content.ReadFromJsonAsync(
                NexusJsonContext.Default.PortainerSystemStatusDto,
                cancellationToken).ConfigureAwait(false);

            return Result.Ok(new PortainerStatus(
                Version: dto?.Version ?? "",
                InstanceId: dto?.InstanceId ?? "",
                Reachable: true,
                AgentTaskCount: null));
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail<PortainerStatus>($"Portainer transport error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return Result.Fail<PortainerStatus>("Portainer timeout.");
        }
    }

    public void Dispose() => _http.Dispose();
}
