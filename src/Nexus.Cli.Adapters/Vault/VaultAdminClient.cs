using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Vault;

// Per-node status (sealed/role/version) + a raft peer row.
public sealed record VaultNodeStatus(
    string Address, bool Sealed, bool Initialized, string Type, string Version,
    string ClusterName, bool HaEnabled, bool IsActive, string LeaderAddress);

public sealed record VaultRaftPeer(string NodeId, string Address, bool Leader, bool Voter);

public sealed record VaultSnapshotMeta(long Index, long Term, int Version, long Size);

/// <summary>
/// Build-host HTTP control plane for the foundation Vault HA cluster (nexus-cli
/// v0.8.1 VaultAdapter, ADR-0022). DELIBERATELY HTTP-from-the-build-host rather
/// than SSH-shell-out-to-the-nodes: the operator's <c>VAULT_TOKEN</c> (the locked
/// auth model, ADR-0004) stays on the build host and is NEVER shipped to a node's
/// process table. Reuses <see cref="NexusHttpClientFactory"/> (the same CA-pinned
/// client the rest of the CLI uses) + the source-gen <see cref="NexusJsonContext"/>
/// (no reflection-based JSON; AOT-clean). The vault binary is never linked.
/// <para>
/// Vault's CA bundle covers vault-1/2/3 (.121-.123) but NOT vault-transit (.124),
/// so transit is handled by the adapter over SSH; this client only talks to the
/// three HA nodes, each addressable directly (per-node status reads bypass the
/// active-node forward).
/// </para>
/// </summary>
public sealed class VaultAdminClient : IDisposable
{
    private readonly VaultContext _ctx;
    private readonly NexusHttpClientFactory _factory;
    private readonly HttpClient _http;

    public VaultAdminClient(VaultContext ctx)
    {
        _ctx = ctx;
        // 90s: a raft snapshot stream can be a few MB; the read verbs are sub-second.
        _factory = new NexusHttpClientFactory(ctx.CaBundlePath, TimeSpan.FromSeconds(90));
        _http = _factory.Create();
        _http.DefaultRequestHeaders.Add("X-Vault-Token", ctx.Token);
    }

    public string DefaultAddress => _ctx.Address;

    /// <summary>Per-node status: seal-status (sealed/version) + leader (active/standby). addr = full https://ip:8200.</summary>
    public async Task<Result<VaultNodeStatus>> NodeStatusAsync(string addr, CancellationToken ct)
    {
        try
        {
            var seal = await _http.GetFromJsonAsync(
                $"{addr}/v1/sys/seal-status", NexusJsonContext.Default.VaultSealStatusDto, ct).ConfigureAwait(false);
            if (seal is null) return Result.Fail<VaultNodeStatus>($"{addr}: empty seal-status");

            // /v1/sys/leader is authenticated; tolerate a failure (sealed node) by defaulting.
            VaultLeaderDto? leader = null;
            try
            {
                leader = await _http.GetFromJsonAsync(
                    $"{addr}/v1/sys/leader", NexusJsonContext.Default.VaultLeaderDto, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) { /* sealed/standby quirk -> leave null */ }

            return Result.Ok(new VaultNodeStatus(
                addr, seal.Sealed, seal.Initialized, seal.Type, seal.Version, seal.ClusterName,
                leader?.HaEnabled ?? false, leader?.IsSelf ?? false, leader?.LeaderAddress ?? ""));
        }
        catch (HttpRequestException ex) { return Result.Fail<VaultNodeStatus>($"{addr} unreachable: {ex.Message}"); }
        catch (TaskCanceledException) { return Result.Fail<VaultNodeStatus>($"{addr} timed out"); }
    }

    /// <summary>Raft peer set (node_id/address/leader/voter) -- read via the default address (forwarded to active).</summary>
    public async Task<Result<List<VaultRaftPeer>>> RaftPeersAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync(
                $"{_ctx.Address}/v1/sys/storage/raft/configuration",
                NexusJsonContext.Default.VaultRaftConfigResponse, ct).ConfigureAwait(false);
            var servers = resp?.Data?.Config?.Servers;
            if (servers is null) return Result.Fail<List<VaultRaftPeer>>("raft configuration returned no servers");
            return Result.Ok(servers.Select(s => new VaultRaftPeer(s.NodeId, s.Address, s.Leader, s.Voter)).ToList());
        }
        catch (HttpRequestException ex) { return Result.Fail<List<VaultRaftPeer>>($"raft config transport error: {ex.Message}"); }
        catch (TaskCanceledException) { return Result.Fail<List<VaultRaftPeer>>("raft config timed out"); }
    }

    /// <summary>Step down the ACTIVE node (must target the active address; step-down is not forwarded).</summary>
    public async Task<Result<bool>> StepDownAsync(string activeAddr, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{activeAddr}/v1/sys/step-down");
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.OK)
                return Result.Ok(true);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Result.Fail<bool>($"step-down on {activeAddr} returned {(int)resp.StatusCode}: {Trunc(body, 200)}");
        }
        catch (HttpRequestException ex) { return Result.Fail<bool>($"step-down transport error: {ex.Message}"); }
        catch (TaskCanceledException) { return Result.Fail<bool>("step-down timed out"); }
    }

    public Task<Result<List<string>>> ListPoliciesAsync(CancellationToken ct) =>
        ListKeysAsync($"{_ctx.Address}/v1/sys/policies/acl?list=true", ct);

    public Task<Result<List<string>>> ListApprolesAsync(CancellationToken ct) =>
        ListKeysAsync($"{_ctx.Address}/v1/auth/approle/role?list=true", ct);

    private async Task<Result<List<string>>> ListKeysAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync(url, NexusJsonContext.Default.VaultListKeysResponse, ct).ConfigureAwait(false);
            return Result.Ok(resp?.Data?.Keys ?? new List<string>());
        }
        catch (HttpRequestException ex) { return Result.Fail<List<string>>($"list transport error: {ex.Message}"); }
        catch (TaskCanceledException) { return Result.Fail<List<string>>("list timed out"); }
    }

    public async Task<Result<string>> ReadPolicyAsync(string name, CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetFromJsonAsync(
                $"{_ctx.Address}/v1/sys/policies/acl/{Uri.EscapeDataString(name)}",
                NexusJsonContext.Default.VaultPolicyReadResponse, ct).ConfigureAwait(false);
            return Result.Ok(resp?.Data?.Policy ?? "");
        }
        catch (HttpRequestException ex) { return Result.Fail<string>($"read policy '{name}' transport error: {ex.Message}"); }
        catch (TaskCanceledException) { return Result.Fail<string>($"read policy '{name}' timed out"); }
    }

    /// <summary>Write/overwrite an ACL policy (acl grant). HCL string body.</summary>
    public async Task<Result<bool>> WritePolicyAsync(string name, string hcl, CancellationToken ct)
    {
        try
        {
            var json = "{\"policy\":" + JsonSerializer.Serialize(hcl, NexusJsonContext.Default.String) + "}";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync(
                $"{_ctx.Address}/v1/sys/policies/acl/{Uri.EscapeDataString(name)}", content, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return Result.Ok(true);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Result.Fail<bool>($"write policy '{name}' returned {(int)resp.StatusCode}: {Trunc(body, 200)}");
        }
        catch (HttpRequestException ex) { return Result.Fail<bool>($"write policy transport error: {ex.Message}"); }
        catch (TaskCanceledException) { return Result.Fail<bool>("write policy timed out"); }
    }

    public async Task<Result<bool>> DeletePolicyAsync(string name, CancellationToken ct)
    {
        try
        {
            var resp = await _http.DeleteAsync(
                $"{_ctx.Address}/v1/sys/policies/acl/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return Result.Ok(true);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Result.Fail<bool>($"delete policy '{name}' returned {(int)resp.StatusCode}: {Trunc(body, 200)}");
        }
        catch (HttpRequestException ex) { return Result.Fail<bool>($"delete policy transport error: {ex.Message}"); }
        catch (TaskCanceledException) { return Result.Fail<bool>("delete policy timed out"); }
    }

    /// <summary>
    /// Stream a raft snapshot to a local build-host file (the non-destructive
    /// backup). Returns bytes written. The companion <see cref="InspectSnapshot"/>
    /// validates it without any restore.
    /// </summary>
    public async Task<Result<long>> SaveRaftSnapshotAsync(string localPath, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(
                $"{_ctx.Address}/v1/sys/storage/raft/snapshot", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return Result.Fail<long>($"raft snapshot returned {(int)resp.StatusCode}: {Trunc(body, 200)}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await using (var fs = File.Create(localPath))
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            return Result.Ok(new FileInfo(localPath).Length);
        }
        catch (HttpRequestException ex) { return Result.Fail<long>($"snapshot transport error: {ex.Message}"); }
        catch (TaskCanceledException) { return Result.Fail<long>("snapshot timed out"); }
        catch (IOException ex) { return Result.Fail<long>($"snapshot write error: {ex.Message}"); }
    }

    /// <summary>
    /// Non-destructive inspect: a Vault raft snapshot is a gzip(tar) whose
    /// <c>meta.json</c> entry carries {Index, Term, Version, Size}. Parse it without
    /// contacting Vault (the safe equivalent of `vault operator raft snapshot
    /// inspect` -- never a restore on the live trust root). Returns null + reason
    /// on a malformed archive.
    /// </summary>
    public static Result<VaultSnapshotMeta> InspectSnapshot(string localPath)
    {
        try
        {
            using var fs = File.OpenRead(localPath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                if (!string.Equals(Path.GetFileName(entry.Name), "meta.json", StringComparison.Ordinal)) continue;
                using var ms = new MemoryStream();
                entry.DataStream!.CopyTo(ms);
                var meta = JsonSerializer.Deserialize(ms.ToArray(), NexusJsonContext.Default.VaultSnapshotMetaDto);
                if (meta is null) return Result.Fail<VaultSnapshotMeta>("snapshot meta.json could not be parsed");
                return Result.Ok(new VaultSnapshotMeta(meta.Index, meta.Term, meta.Version, meta.Size));
            }
            return Result.Fail<VaultSnapshotMeta>("snapshot archive had no meta.json (not a valid raft snapshot)");
        }
        catch (InvalidDataException ex) { return Result.Fail<VaultSnapshotMeta>($"snapshot is not a valid gzip archive: {ex.Message}"); }
        catch (IOException ex) { return Result.Fail<VaultSnapshotMeta>($"snapshot read error: {ex.Message}"); }
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
    }
}
