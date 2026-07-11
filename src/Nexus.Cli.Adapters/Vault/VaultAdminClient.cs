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

/// <summary>Point-in-time status of a single Vault node: seal state, version, and its HA/leader role.</summary>
/// <param name="Address">Full node address (<c>https://ip:8200</c>).</param>
/// <param name="Sealed">True when the node is sealed.</param>
/// <param name="Initialized">True when the node has been initialized.</param>
/// <param name="Type">Seal type (e.g. <c>transit</c>, <c>shamir</c>).</param>
/// <param name="Version">Vault server version string.</param>
/// <param name="ClusterName">Raft cluster name reported by the node.</param>
/// <param name="HaEnabled">True when HA is enabled on the node.</param>
/// <param name="IsActive">True when this node is the active (leader) node.</param>
/// <param name="LeaderAddress">Address of the current active node as seen by this node.</param>
public sealed record VaultNodeStatus(
    string Address, bool Sealed, bool Initialized, string Type, string Version,
    string ClusterName, bool HaEnabled, bool IsActive, string LeaderAddress);

/// <summary>One row of the raft peer set (<c>/sys/storage/raft/configuration</c>).</summary>
/// <param name="NodeId">Raft node id.</param>
/// <param name="Address">Raft advertise address.</param>
/// <param name="Leader">True when this peer is the raft leader.</param>
/// <param name="Voter">True when this peer is a voting member.</param>
public sealed record VaultRaftPeer(string NodeId, string Address, bool Leader, bool Voter);

/// <summary>Parsed <c>meta.json</c> from a raft snapshot archive (the non-destructive inspect).</summary>
/// <param name="Index">Raft log index captured by the snapshot.</param>
/// <param name="Term">Raft term captured by the snapshot.</param>
/// <param name="Version">Snapshot format version.</param>
/// <param name="Size">Snapshot payload size in bytes.</param>
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

    /// <summary>Creates a control-plane client for the Vault HA cluster described by <paramref name="ctx"/>, minting its own CA-pinned client (90s timeout to accommodate snapshot streams).</summary>
    /// <param name="ctx">Resolved Vault address + token + CA-bundle path.</param>
    public VaultAdminClient(VaultContext ctx)
    {
        _ctx = ctx;
        // 90s: a raft snapshot stream can be a few MB; the read verbs are sub-second.
        _factory = new NexusHttpClientFactory(ctx.CaBundlePath, TimeSpan.FromSeconds(90));
        _http = _factory.Create();
        _http.DefaultRequestHeaders.Add("X-Vault-Token", ctx.Token);
    }

    /// <summary>The default (active-forwarded) Vault address this client targets for cluster-wide reads.</summary>
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

    /// <summary>Lists ACL policy names (<c>sys/policies/acl</c>).</summary>
    public Task<Result<List<string>>> ListPoliciesAsync(CancellationToken ct) =>
        ListKeysAsync($"{_ctx.Address}/v1/sys/policies/acl?list=true", ct);

    /// <summary>Lists AppRole role names (<c>auth/approle/role</c>).</summary>
    public Task<Result<List<string>>> ListApprolesAsync(CancellationToken ct) =>
        ListKeysAsync($"{_ctx.Address}/v1/auth/approle/role?list=true", ct);

    // Shared LIST helper: GETs a ?list=true endpoint and returns the data.keys array.
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

    /// <summary>Reads the HCL body of ACL policy <paramref name="name"/> (empty string if it has none).</summary>
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

    /// <summary>Deletes ACL policy <paramref name="name"/> (acl revoke).</summary>
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

    // Cap an untrusted Vault error body before surfacing it in a failure message.
    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
    }
}
