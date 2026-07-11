using System.Net.Http;
using System.Net.Http.Json;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Portainer;

/// <summary>
/// CA-pinned HTTP <see cref="INexusPortainerClient"/> for the Swarm-tier
/// Portainer instance. Reads <c>/api/system/status</c> (unauthenticated on
/// Portainer CE 2.x) for a basic up-or-down + version probe; admin credentials
/// are carried for a future authenticated surface but are not required for the
/// current status read. HTTP-from-the-build-host; no Portainer agent is linked.
/// </summary>
public sealed class PortainerClient : INexusPortainerClient, IDisposable
{
    /// <summary>Connection settings for the Portainer HTTP API.</summary>
    /// <param name="BaseAddress">Base URL of the Portainer server (e.g. <c>https://host:9443</c>).</param>
    /// <param name="AdminUser">Admin username (reserved for the authenticated surface).</param>
    /// <param name="AdminPassword">Admin password (reserved for the authenticated surface).</param>
    public sealed record Settings(string BaseAddress, string AdminUser, string AdminPassword);

    private readonly Settings _settings;
    private readonly HttpClient _http;

    /// <summary>Creates a client bound to <paramref name="settings"/>, minting a CA-pinned <see cref="HttpClient"/> from <paramref name="httpFactory"/>.</summary>
    /// <param name="settings">Base address + admin credentials for the target Portainer server.</param>
    /// <param name="httpFactory">Factory that produces the CA-bundle-pinned HTTP client.</param>
    public PortainerClient(Settings settings, NexusHttpClientFactory httpFactory)
    {
        _settings = settings;
        _http = httpFactory.Create();
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
