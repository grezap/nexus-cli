using System.Net.Http;
using System.Net.Http.Json;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Nomad;

/// <summary>
/// CA-pinned HTTP <see cref="INexusNomadClient"/> for the Swarm-tier Nomad
/// cluster. Fans out to <c>/v1/agent/members</c> (server raft), <c>/v1/status/leader</c>,
/// and <c>/v1/nodes</c> (client nodes) with the mgmt token in the <c>X-Nomad-Token</c>
/// header, marking the server whose <c>addr:port</c> prefixes the leader address as
/// leader. HTTP-from-the-build-host; no nomad binary is linked.
/// </summary>
public sealed class NomadClient : INexusNomadClient, IDisposable
{
    /// <summary>Connection settings for the Nomad HTTP API.</summary>
    /// <param name="BaseAddress">Base URL of the Nomad agent (e.g. <c>https://host:4646</c>).</param>
    /// <param name="MgmtToken">Nomad management/bootstrap ACL token sent in <c>X-Nomad-Token</c>.</param>
    public sealed record Settings(string BaseAddress, string MgmtToken);

    private readonly Settings _settings;
    private readonly HttpClient _http;

    /// <summary>Creates a client bound to <paramref name="settings"/>, minting a CA-pinned <see cref="HttpClient"/> from <paramref name="httpFactory"/> and pre-seeding the ACL-token header.</summary>
    /// <param name="settings">Base address + mgmt token for the target Nomad agent.</param>
    /// <param name="httpFactory">Factory that produces the CA-bundle-pinned HTTP client.</param>
    public NomadClient(Settings settings, NexusHttpClientFactory httpFactory)
    {
        _settings = settings;
        _http = httpFactory.Create();
        _http.DefaultRequestHeaders.Add("X-Nomad-Token", _settings.MgmtToken);
    }

    /// <inheritdoc />
    public async Task<Result<NomadHealth>> GetHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Server members
            var membersUrl = $"{_settings.BaseAddress.TrimEnd('/')}/v1/agent/members";
            var membersResp = await _http.GetAsync(membersUrl, cancellationToken).ConfigureAwait(false);
            if (membersResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Result.Fail<NomadHealth>(
                    "Nomad rejected the mgmt token (403). Check nexus/swarm/nomad-bootstrap-token in Vault.");
            }
            if (!membersResp.IsSuccessStatusCode)
            {
                return Result.Fail<NomadHealth>(
                    $"Nomad {(int)membersResp.StatusCode} on /v1/agent/members: {membersResp.ReasonPhrase}");
            }
            var membersDto = await membersResp.Content.ReadFromJsonAsync(
                NexusJsonContext.Default.NomadServerMembersDto,
                cancellationToken).ConfigureAwait(false);

            // Leader
            var leaderUrl = $"{_settings.BaseAddress.TrimEnd('/')}/v1/status/leader";
            var leaderResp = await _http.GetAsync(leaderUrl, cancellationToken).ConfigureAwait(false);
            string? leaderAddr = null;
            if (leaderResp.IsSuccessStatusCode)
            {
                leaderAddr = (await leaderResp.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false)).Trim('"', ' ', '\n', '\r');
                if (string.IsNullOrEmpty(leaderAddr)) leaderAddr = null;
            }

            // Client nodes
            var nodesUrl = $"{_settings.BaseAddress.TrimEnd('/')}/v1/nodes";
            var nodesResp = await _http.GetAsync(nodesUrl, cancellationToken).ConfigureAwait(false);
            if (!nodesResp.IsSuccessStatusCode)
            {
                return Result.Fail<NomadHealth>(
                    $"Nomad {(int)nodesResp.StatusCode} on /v1/nodes: {nodesResp.ReasonPhrase}");
            }
            var nodes = await nodesResp.Content.ReadFromJsonAsync(
                NexusJsonContext.Default.ListNomadNodeListDto,
                cancellationToken).ConfigureAwait(false) ?? [];

            var servers = (membersDto?.Members ?? []).Select(m => new NomadServer(
                Name: m.Name,
                Address: $"{m.Addr}:{m.Port}",
                IsLeader: leaderAddr is not null && leaderAddr.StartsWith($"{m.Addr}:", StringComparison.Ordinal)))
                .ToList();

            var clients = nodes.Select(n => new NomadClientNode(
                Name: n.Name,
                Address: n.Address,
                Status: n.Status,
                NodeClass: string.IsNullOrEmpty(n.NodeClass) ? "default" : n.NodeClass))
                .ToList();

            return Result.Ok(new NomadHealth(servers, clients, leaderAddr));
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail<NomadHealth>($"Nomad transport error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return Result.Fail<NomadHealth>("Nomad timeout.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
