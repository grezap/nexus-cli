using System.Net.Http;
using System.Net.Http.Json;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Consul;

public sealed class ConsulClient : INexusConsulClient, IDisposable
{
    public sealed record Settings(string BaseAddress, string MgmtToken);

    private readonly Settings _settings;
    private readonly HttpClient _http;

    public ConsulClient(Settings settings, NexusHttpClientFactory httpFactory)
    {
        _settings = settings;
        _http = httpFactory.Create();
        _http.DefaultRequestHeaders.Add("X-Consul-Token", _settings.MgmtToken);
    }

    public async Task<Result<ConsulHealth>> GetHealthAsync(CancellationToken cancellationToken)
    {
        // /v1/agent/self under deny-mode returns 403 if the token is bad — robust auth probe.
        // /v1/agent/members under deny-mode returns 200 with filtered output if the token
        // lacks node:read, so we don't rely on it for auth verification.
        try
        {
            var membersUrl = $"{_settings.BaseAddress.TrimEnd('/')}/v1/agent/members";
            var resp = await _http.GetAsync(membersUrl, cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Result.Fail<ConsulHealth>(
                    "Consul rejected the mgmt token (403). Check nexus/swarm/consul-bootstrap-token in Vault.");
            }
            if (!resp.IsSuccessStatusCode)
            {
                return Result.Fail<ConsulHealth>(
                    $"Consul {(int)resp.StatusCode} on /v1/agent/members: {resp.ReasonPhrase}");
            }

            var members = await resp.Content.ReadFromJsonAsync(
                NexusJsonContext.Default.ListConsulMemberDto,
                cancellationToken).ConfigureAwait(false) ?? [];

            var leaderUrl = $"{_settings.BaseAddress.TrimEnd('/')}/v1/status/leader";
            var leaderResp = await _http.GetAsync(leaderUrl, cancellationToken).ConfigureAwait(false);
            string? leader = null;
            if (leaderResp.IsSuccessStatusCode)
            {
                var raw = (await leaderResp.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false)).Trim('"', ' ', '\n', '\r');
                if (!string.IsNullOrEmpty(raw)) leader = raw;
            }

            var mapped = members.Select(m => new ConsulMember(
                Name: m.Name,
                Addr: m.Addr,
                Port: m.Port,
                Status: TranslateStatus(m.Status),
                Role: m.Tags is not null && m.Tags.TryGetValue("role", out var r) ? r : "",
                Datacenter: m.Tags is not null && m.Tags.TryGetValue("dc", out var dc) ? dc : ""))
                .ToList();

            int alive = mapped.Count(m => m.Status == "alive");
            int failed = mapped.Count(m => m.Status == "failed" || m.Status == "left");

            return Result.Ok(new ConsulHealth(mapped, leader, alive, failed));
        }
        catch (HttpRequestException ex)
        {
            return Result.Fail<ConsulHealth>($"Consul transport error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return Result.Fail<ConsulHealth>("Consul timeout.");
        }
    }

    private static string TranslateStatus(int s) => s switch
    {
        0 => "none",
        1 => "alive",
        2 => "leaving",
        3 => "left",
        4 => "failed",
        _ => $"unknown({s})"
    };

    public void Dispose() => _http.Dispose();
}
