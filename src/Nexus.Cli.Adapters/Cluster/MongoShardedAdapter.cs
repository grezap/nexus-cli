using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// MongoDB <b>sharded cluster</b> adapter for Phase 0.N (nexus-cli v0.7.1).
/// <para>
/// Distinct from <see cref="MongoAdapter"/> (ClusterId <c>mongo</c>, the 0.G.2
/// 3-node replica set). This adapter drives the genuinely-sharded topology
/// (vms.yaml cluster <c>mongo-sharded</c>, ADR-0040): a 3-member config-server
/// replica set (<c>config</c>, port 27019), two 3-member shard replica sets
/// (<c>shard-1</c> / <c>shard-2</c>, port 27018), and two stateless
/// <c>mongos</c> query routers (port 27017). 11 nodes total.
/// </para>
/// <para>
/// Implements <see cref="IClusterAdapter"/> via SSH-shell-out to on-node
/// <c>mongosh</c> / <c>mongodump</c> / <c>mongorestore</c> (ADR-0009). No managed
/// MongoDB driver is linked (NetArchTest-enforced), exactly like
/// <see cref="MongoAdapter"/>.
/// </para>
/// <para>
/// Connection contract: keyFile member auth + <b>wire mTLS as of 0.N.1</b>
/// (requireTLS with per-host Vault-PKI leaf certs, parity with the 0.G.2 mongo
/// RS); every mongosh/mongodump/mongorestore dials over TLS presenting the node's
/// own leaf as its client cert; <c>authorization=enabled</c>. The
/// adapter authenticates two ways, BOTH using the shared keyFile content as the
/// password (the cluster's single secret):
/// <list type="bullet">
///   <item><b>Direct mongod RS ops</b> (config + both shards) -- the
///   <c>__system</c> principal against the <c>local</c> DB (SCRAM-SHA-256). This
///   is the ONLY principal the shard mongods know (<c>nexus-sharded-admin</c> was
///   created only on the config-server RS). Used for rs.status / rs.stepDown /
///   rs.add / rs.remove against 127.0.0.1:&lt;rs-port&gt;.</item>
///   <item><b>Cluster-level ops</b> (sh.status, balancer, config metadata, ACL,
///   backup) -- the <c>nexus-sharded-admin</c> root user against <c>admin</c>,
///   THROUGH a <c>mongos</c> router (127.0.0.1:27017). <c>__system</c>/<c>local</c>
///   cannot be used through mongos ("Can't use 'local' database through mongos",
///   0.N transient N9), so cluster ops MUST use this user.</item>
/// </list>
/// The keyFile content lives in Vault KV at <c>nexus/oltp/mongo/keyfile</c>
/// (field <c>content</c>) -- the same secret seeded in 0.G.2 and SCP-distributed
/// to every node at <c>/etc/nexus-mongo/keyfile</c> (0400 mongodb:mongodb). The
/// adapter fetches it at runtime via <see cref="INexusVaultClient"/> (built from
/// VAULT_ADDR/VAULT_TOKEN/VAULT_CACERT); creds transit, never persist.
/// </para>
/// <para>
/// Verb surface (v0.7.1): status / health / topology (Shards populated -- the
/// sharded showcase) / failover (shard-primary stepDown + per-shard re-election)
/// / scale-out add+remove (shard RS member, apply-on-demand) / backup take+restore
/// (mongodump through mongos round-trip, over TLS) / acl (config-server admin
/// users via mongos) / chaos (process-kill a shard mongod) / <c>cert-rotate</c>
/// (0.N.1: per-node Vault-PKI leaf re-issue via the node's own agent + online
/// <c>rotateCertificates</c> reload -- no restart, no shard re-election).
/// </para>
/// </summary>
public sealed class MongoShardedAdapter : IClusterAdapter
{
    private const string ClusterName = "mongo-sharded";
    private const string OperatorUser = "nexus-sharded-admin";
    private const string ConfigRsName = "config";
    private const int ConfigPort = 27019;
    private const int ShardPort = 27018;
    private const int MongosPort = 27017;
    private const string KeyFilePath = "/etc/nexus-mongo/keyfile";

    // 0.N.1 wire mTLS (parity with the 0.G.2 mongo RS). Every mongod/mongos runs
    // requireTLS with a per-host Vault-PKI leaf rendered by nexus-infra-oltp
    // role-overlay-mongo-tls.tf: server.pem = leaf+PKCS#8 key, ca.crt =
    // intermediate+root. Owned root:mongodb 0640. TlsArgs is presented on every
    // mongosh dial (the node's own leaf doubles as the client cert).
    private const string TlsDir = "/etc/nexus-mongo/tls";
    private const string CaFile = TlsDir + "/ca.crt";
    private const string PemFile = TlsDir + "/server.pem";
    private const string TlsArgs = $"--tls --tlsCAFile {CaFile} --tlsCertificateKeyFile {PemFile}";
    private const string PkiRole = "mongo-sharded-server";
    private const string VaultAddr = "https://192.168.70.121:8200";
    private const string AgentToken = "/run/nexus-vault-agent/token";

    // Vault KV (mount nexus/, KV-v2). The shared keyFile (= the operator/__system
    // password) is sticky-seeded by the 0.G.2 security overlay
    // role-overlay-vault-mongo-keyfile-seed.tf at nexus/oltp/mongo/keyfile.
    private const string VaultMount = "nexus";
    private const string KeyFileKvPath = "oltp/mongo/keyfile";
    private const string KeyFileKvField = "content";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FailoverPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailoverDeadline = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan BackupTimeout = TimeSpan.FromSeconds(180);
    private static readonly string[] KnownChaosScenarios =
        ["network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill"];
    private static readonly string[] DefaultGrantRoles = ["read"];

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;

    private string? _keyfile;                 // cached shared keyFile content (the password)
    private ClusterStatus? _lastStatus;       // populated on GetStatusAsync; consulted by CanResizeVm (sync)

    public MongoShardedAdapter(
        IVmsCatalog catalog,
        ISshClient ssh,
        string sshUsername,
        string sshKeyPath,
        INexusVaultClient? vault)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
        _vault = vault;
    }

    public string ClusterId => ClusterName;
    public string DisplayName => "MongoDB Sharded Cluster";

    // === Node classification (deterministic, from the vms.yaml name prefix) ==
    // vms.yaml carries no structured role/port, so we derive both from the name:
    //   mongo-cfg-N      -> ("configsvr", "config",  27019)
    //   mongo-shard-K-N  -> ("shardsvr",  "shard-K", 27018)
    //   mongo-mongos-N   -> ("mongos",    "",        27017)
    internal static (string Role, string RsName, int Port) Classify(string nodeName)
    {
        var n = nodeName.ToLowerInvariant();
        if (n.StartsWith("mongo-cfg", StringComparison.Ordinal))
            return ("configsvr", ConfigRsName, ConfigPort);
        if (n.StartsWith("mongo-mongos", StringComparison.Ordinal))
            return ("mongos", "", MongosPort);
        if (n.StartsWith("mongo-shard-", StringComparison.Ordinal))
        {
            // mongo-shard-<K>-<M> -> rs "shard-<K>"
            var rest = n.Substring("mongo-shard-".Length);
            var dash = rest.IndexOf('-');
            var k = dash > 0 ? rest.Substring(0, dash) : rest;
            return ("shardsvr", $"shard-{k}", ShardPort);
        }
        return ("unknown", "", MongosPort);
    }

    private static List<NodeRecord> NodesForRs(IReadOnlyList<NodeRecord> all, string rsName) =>
        all.Where(n => Classify(n.Name).RsName == rsName).ToList();

    private static List<NodeRecord> MongosNodes(IReadOnlyList<NodeRecord> all) =>
        all.Where(n => Classify(n.Name).Role == "mongos").ToList();

    private static List<string> DataShardRsNames(IReadOnlyList<NodeRecord> all) =>
        all.Select(n => Classify(n.Name).RsName)
           .Where(rs => rs.StartsWith("shard-", StringComparison.Ordinal))
           .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

    // === Credential (Vault KV keyFile content) ==============================
    private async Task<Result<string>> GetKeyfileAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_keyfile)) return Result.Ok(_keyfile);
        if (_vault is null)
            return Result.Fail<string>(
                "mongo-sharded verbs authenticate with the shared keyFile (Vault KV nexus/oltp/mongo/keyfile). "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT (e.g. `$env:VAULT_ADDR='https://192.168.70.121:8200'; "
                + "$env:VAULT_TOKEN=<token>; $env:VAULT_CACERT=$HOME\\.nexus\\vault-ca-bundle.crt`) and retry.");
        var read = await _vault.ReadKvFieldAsync(VaultMount, KeyFileKvPath, KeyFileKvField, ct).ConfigureAwait(false);
        if (read.IsFail)
            return Result.Fail<string>($"could not read the keyFile from Vault ({VaultMount}/{KeyFileKvPath}): {read.Error}");
        // The on-node keyFile is the trimmed KV content (the seed overlay trims it);
        // match that so the SCRAM password is byte-identical.
        _keyfile = (read.Value ?? string.Empty).Trim();
        if (_keyfile.Length < 100)
            return Result.Fail<string>($"keyFile from Vault is implausibly short ({_keyfile.Length} chars)");
        return Result.Ok(_keyfile);
    }

    // __system / local -- the only principal the shard mongods accept; also valid
    // on the config mongods. Used for ALL direct-mongod RS operations.
    private static string SysAuth(string pwd) =>
        $"{TlsArgs} --username __system --password '{pwd}' --authenticationDatabase local --authenticationMechanism SCRAM-SHA-256";

    // nexus-sharded-admin / admin -- the root user that lives on the config-server
    // RS and is reachable THROUGH mongos. Used for all cluster-level operations.
    private static string OperatorAuth(string pwd) =>
        $"{TlsArgs} --username {OperatorUser} --password '{pwd}' --authenticationDatabase admin";

    /// <summary>Run a mongosh eval against a mongod RS member (direct, via __system).</summary>
    private async Task<Result<string>> EvalMongodAsync(NodeRecord node, int port, string pwd, string js, CancellationToken ct)
    {
        var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var cmd = $"sudo mongosh --quiet {SysAuth(pwd)} --host 127.0.0.1:{port} --eval '{js}'";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {node.Name} ({node.Vmnet11}) failed: {exec.Error}");
        if (exec.Value!.ExitCode != 0)
            return Result.Fail<string>($"mongosh on {node.Name} returned exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");
        return Result.Ok(exec.Value.Stdout.Trim());
    }

    /// <summary>Run a mongosh eval through a mongos router (via nexus-sharded-admin).</summary>
    private async Task<Result<string>> EvalMongosAsync(NodeRecord mongos, string pwd, string js, CancellationToken ct)
    {
        var target = new SshTarget(mongos.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var cmd = $"sudo mongosh --quiet {OperatorAuth(pwd)} --host 127.0.0.1:{MongosPort} --eval '{js}'";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, ct).ConfigureAwait(false);
        if (exec.IsFail) return Result.Fail<string>($"ssh to {mongos.Name} ({mongos.Vmnet11}) failed: {exec.Error}");
        if (exec.Value!.ExitCode != 0)
            return Result.Fail<string>($"mongos eval on {mongos.Name} returned exit {exec.Value.ExitCode}: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");
        return Result.Ok(exec.Value.Stdout.Trim());
    }

    /// <summary>Run an rs.status() projection against a RS, trying members until one answers.</summary>
    private async Task<Result<(IReadOnlyList<ClusterMember> Members, string? Leader)>> GetRsStatusAsync(
        string rsName, IReadOnlyList<NodeRecord> rsNodes, int port, string pwd, CancellationToken ct)
    {
        const string js = "var s=rs.status();var p=s.members.map(function(m){return {n:m.name,s:m.stateStr,h:m.health,o:(m.optimeDate?m.optimeDate.getTime():0)}});print(JSON.stringify({set:s.set,members:p}));";
        var byEndpoint = rsNodes.ToDictionary(n => $"{n.Vmnet11}:{port}", n => n, StringComparer.OrdinalIgnoreCase);
        string? lastErr = null;
        foreach (var n in rsNodes)
        {
            var r = await EvalMongodAsync(n, port, pwd, js, ct).ConfigureAwait(false);
            if (r.IsOk && r.Value!.Contains('{'))
            {
                var parsed = ParseRsStatusJson(r.Value!, byEndpoint, rsName);
                if (parsed.Members.Count > 0) return Result.Ok(parsed);
            }
            lastErr = r.IsFail ? r.Error : $"unparseable rs.status from {n.Name}";
        }
        return Result.Fail<(IReadOnlyList<ClusterMember>, string?)>($"no {rsName} member answered rs.status(): {lastErr}");
    }

    /// <summary>Parse the rs.status() JSON projection into members (ShardId=rsName) + the PRIMARY hostname.</summary>
    internal static (IReadOnlyList<ClusterMember> Members, string? Leader) ParseRsStatusJson(
        string stdout, IReadOnlyDictionary<string, NodeRecord> byEndpoint, string rsName)
    {
        var members = new List<ClusterMember>();
        string? leader = null;
        var json = MongoAdapter.ExtractJson(stdout);
        if (json is null) return (members, null);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("members", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (members, null);

        long primaryOptime = 0;
        foreach (var m in arr.EnumerateArray())
            if (m.TryGetProperty("s", out var st) && st.GetString() == "PRIMARY" && m.TryGetProperty("o", out var op))
                primaryOptime = op.GetInt64();

        foreach (var m in arr.EnumerateArray())
        {
            var name = m.TryGetProperty("n", out var nn) ? nn.GetString() ?? "" : "";
            var state = m.TryGetProperty("s", out var ss) ? ss.GetString() ?? "UNKNOWN" : "UNKNOWN";
            var health = m.TryGetProperty("h", out var hh) ? hh.GetDouble() : 0;
            var optime = m.TryGetProperty("o", out var oo) ? oo.GetInt64() : 0;

            var role = state switch
            {
                "PRIMARY" => "primary",
                "SECONDARY" => "secondary",
                "ARBITER" => "arbiter",
                _ => state.ToLowerInvariant()
            };
            var status = health >= 1 && state is "PRIMARY" or "SECONDARY" or "ARBITER" ? "alive"
                : state is "STARTUP" or "STARTUP2" or "RECOVERING" or "ROLLBACK" ? "syncing"
                : "failed";

            double? lagSec = null;
            if (role == "secondary" && primaryOptime > 0 && optime > 0)
                lagSec = Math.Max(0, (primaryOptime - optime) / 1000.0);

            var hostname = byEndpoint.TryGetValue(name, out var node) ? node.Name : name;
            var ip = node?.Vmnet11 ?? name.Split(':')[0];
            if (role == "primary") leader = hostname;

            members.Add(new ClusterMember(hostname, ip, role, status, ShardId: rsName, ReplicationLagSeconds: lagSec));
        }
        return (members, leader);
    }

    // === GetStatusAsync =====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ClusterStatus>(cluster.Error!);
        var all = cluster.Value!.Nodes;
        if (all.Count == 0) return Result.Fail<ClusterStatus>($"cluster '{ClusterName}' has no nodes in vms.yaml");

        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ClusterStatus>(pwd.Error!);

        var members = new List<ClusterMember>();

        // The 3 replica sets: config + each data shard.
        var rsNames = new List<string> { ConfigRsName };
        rsNames.AddRange(DataShardRsNames(all));
        foreach (var rs in rsNames)
        {
            var rsNodes = NodesForRs(all, rs);
            if (rsNodes.Count == 0) continue;
            var port = rs == ConfigRsName ? ConfigPort : ShardPort;
            var st = await GetRsStatusAsync(rs, rsNodes, port, pwd.Value!, cancellationToken).ConfigureAwait(false);
            if (st.IsFail)
            {
                // Surface the RS as down rather than failing the whole status.
                foreach (var n in rsNodes)
                    members.Add(new ClusterMember(n.Name, n.Vmnet11, "unknown", "failed", ShardId: rs, ReplicationLagSeconds: null));
                continue;
            }
            members.AddRange(st.Value.Members);
        }

        // mongos routers: stateless; alive = nexus-mongos active + accepts a ping.
        foreach (var mongos in MongosNodes(all))
        {
            var alive = await MongosAliveAsync(mongos, pwd.Value!, cancellationToken).ConfigureAwait(false);
            members.Add(new ClusterMember(mongos.Name, mongos.Vmnet11, "router", alive ? "alive" : "failed", ShardId: null, ReplicationLagSeconds: null));
        }

        var overall = ComputeOverall(members, rsNames);
        var status = new ClusterStatus(ClusterName, DisplayName, overall, members, Leader: null, DateTimeOffset.UtcNow);
        _lastStatus = status;
        return Result.Ok(status);
    }

    private static string ComputeOverall(IReadOnlyList<ClusterMember> members, IReadOnlyList<string> rsNames)
    {
        if (members.Any(m => m.Status == "failed")) return "red";
        foreach (var rs in rsNames)
        {
            var rsm = members.Where(m => m.ShardId == rs).ToList();
            if (rsm.Count == 0) return "red";
            if (rsm.Count(m => m.Role == "primary") != 1) return "red";
        }
        var routers = members.Where(m => m.Role == "router").ToList();
        if (routers.Count == 0 || routers.All(r => r.Status != "alive")) return "red";
        if (members.Any(m => m.Status is "syncing") || routers.Any(r => r.Status != "alive")) return "yellow";
        return "green";
    }

    /// <summary>Is a mongos router accepting routed commands? (systemctl active + adminCommand ping).</summary>
    private async Task<bool> MongosAliveAsync(NodeRecord mongos, string pwd, CancellationToken ct)
    {
        var ping = await EvalMongosAsync(mongos, pwd, "print(db.adminCommand({ping:1}).ok)", ct).ConfigureAwait(false);
        return ping.IsOk && ping.Value!.Contains('1');
    }

    // === HealthAsync ========================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<HealthReport>(cluster.Error!);
        var all = cluster.Value!.Nodes;

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<HealthReport>(status.Error!);
        var members = status.Value!.Members;

        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<HealthReport>(pwd.Error!);

        var probes = new List<HealthProbe>();

        // Per-RS quorum + single-primary + per-secondary lag.
        var rsNames = new List<string> { ConfigRsName };
        rsNames.AddRange(DataShardRsNames(all));
        foreach (var rs in rsNames)
        {
            var rsm = members.Where(m => m.ShardId == rs).ToList();
            var alive = rsm.Count(m => m.Status == "alive");
            var need = rsm.Count / 2 + 1;
            probes.Add(new HealthProbe("quorum", rs, alive >= need ? "green" : "red", $"{alive}/{rsm.Count} up", $">= {need} (majority)"));
            var primaries = rsm.Count(m => m.Role == "primary");
            probes.Add(new HealthProbe("primary", rs, primaries == 1 ? "green" : "red", $"{primaries} PRIMARY", "exactly 1"));
            foreach (var m in rsm.Where(m => m.Role == "secondary" && m.Status == "alive"))
            {
                var lag = m.ReplicationLagSeconds ?? 0;
                var ls = lag < 10 ? "green" : lag < 60 ? "yellow" : "red";
                probes.Add(new HealthProbe("replication-lag", $"{rs}/{m.Hostname}", ls, $"{lag:F1}s", "<10s green; <60s yellow; >=60s red"));
            }
        }

        // mongos routers reachable.
        foreach (var r in members.Where(m => m.Role == "router"))
            probes.Add(new HealthProbe("router", r.Hostname, r.Status == "alive" ? "green" : "red", r.Status, "alive"));

        // Cluster-level: shard registration count + balancer state (via mongos).
        var mongos = MongosNodes(all).FirstOrDefault();
        if (mongos is not null)
        {
            // JS string literals MUST be double-quoted: EvalMongosAsync wraps the
            // script in `--eval '...'`, so single quotes inside would terminate it.
            var js = "var cfg=db.getSiblingDB(\"config\");print(\"SHARDS=\"+cfg.shards.countDocuments());print(\"BALANCER=\"+sh.getBalancerState());";
            var res = await EvalMongosAsync(mongos, pwd.Value!, js, cancellationToken).ConfigureAwait(false);
            if (res.IsOk)
            {
                var shardCount = System.Text.RegularExpressions.Regex.Match(res.Value!, @"SHARDS=(\d+)");
                var expected = DataShardRsNames(all).Count;
                if (shardCount.Success)
                {
                    var got = int.Parse(shardCount.Groups[1].Value, CultureInfo.InvariantCulture);
                    probes.Add(new HealthProbe("shards-registered", "config-servers", got >= expected ? "green" : "red", $"{got} shards", $">= {expected}"));
                }
                var bal = res.Value!.Contains("BALANCER=true", StringComparison.Ordinal);
                probes.Add(new HealthProbe("balancer", "mongos", bal ? "green" : "yellow", bal ? "enabled" : "disabled", "enabled"));
            }
            else
            {
                probes.Add(new HealthProbe("shards-registered", "config-servers", "red", "unreachable via mongos", null));
            }
        }

        var overall = probes.Any(p => p.Status == "red") ? "red"
            : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync (Shards POPULATED -- the sharded showcase) ===========
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<TopologySnapshot>(cluster.Error!);
        var all = cluster.Value!.Nodes;

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var members = status.Value!.Members;

        var nodes = members
            .Select(m => new TopologyNode(m.Hostname, m.ShardId is null ? m.Role : $"{m.ShardId}/{m.Role}", m.Status, m.ReplicationLagSeconds))
            .ToList();

        // One TopologyShard per DATA shard RS (config RS + mongos shown in Nodes).
        var shards = new List<TopologyShard>();
        foreach (var rs in DataShardRsNames(all))
        {
            var rsm = members.Where(m => m.ShardId == rs).ToList();
            var primary = rsm.FirstOrDefault(m => m.Role == "primary")?.Hostname ?? "(none)";
            var replicas = rsm.Where(m => m.Role != "primary").Select(m => m.Hostname).ToList();
            shards.Add(new TopologyShard(rs, primary, replicas, SlotRange: "hashed shard key"));
        }

        return Result.Ok(new TopologySnapshot(ClusterName, nodes, shards, DateTimeOffset.UtcNow));
    }

    // === FailoverAsync (shard-primary rs.stepDown + per-shard re-election) ===
    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<FailoverResult>(cluster.Error!);
        var all = cluster.Value!.Nodes;

        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<FailoverResult>(pwd.Error!);

        // Resolve the target replica set. --target may name a node (use its RS) or
        // an RS directly ("shard-1"/"shard-2"/"config"). Default: the first data shard.
        var dataShards = DataShardRsNames(all);
        string targetRs;
        if (!string.IsNullOrWhiteSpace(request.TargetNode))
        {
            var named = all.FirstOrDefault(n => string.Equals(n.Name, request.TargetNode, StringComparison.OrdinalIgnoreCase));
            targetRs = named is not null ? Classify(named.Name).RsName
                : (dataShards.Concat([ConfigRsName]).Contains(request.TargetNode) ? request.TargetNode! : "");
            if (string.IsNullOrEmpty(targetRs))
                return Result.Fail<FailoverResult>($"--target '{request.TargetNode}' is neither a mongo-sharded node nor a replica-set name ({string.Join("/", dataShards)}/config).");
        }
        else
        {
            targetRs = dataShards.Count > 0 ? dataShards[0] : ConfigRsName;
        }

        var rsNodes = NodesForRs(all, targetRs);
        var port = targetRs == ConfigRsName ? ConfigPort : ShardPort;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var before = await GetRsStatusAsync(targetRs, rsNodes, port, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (before.IsFail) return Result.Fail<FailoverResult>(before.Error!);
        var preFlightAt = sw.Elapsed;

        var originalPrimary = before.Value.Members.FirstOrDefault(m => m.Role == "primary");
        if (originalPrimary is null)
            return Result.Fail<FailoverResult>($"no PRIMARY found in {targetRs}; cannot step down");
        var primaryNode = rsNodes.FirstOrDefault(n => n.Vmnet11 == originalPrimary.IpAddress);
        if (primaryNode is null)
            return Result.Fail<FailoverResult>($"PRIMARY {originalPrimary.Hostname} of {targetRs} not found in vms.yaml");

        // rs.stepDown() runs ON the primary (local connection). It returns by
        // closing the connection; mongosh exits non-zero with a network error,
        // which is EXPECTED -- success is measured by a new primary via polling.
        var stepDownJs = "try{rs.stepDown(60)}catch(e){print(\"STEPDOWN_ISSUED\")}";
        var sshTarget = new SshTarget(primaryNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var cmd = $"sudo mongosh --quiet {SysAuth(pwd.Value!)} --host 127.0.0.1:{port} --eval '{stepDownJs}'";
        await _ssh.ExecuteAsync(sshTarget, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        var failureInjectedAt = sw.Elapsed;

        string? newPrimary = null;
        var newPrimaryAt = TimeSpan.Zero;
        var deadline = sw.Elapsed + FailoverDeadline;
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(FailoverPollInterval, cancellationToken).ConfigureAwait(false);
            var poll = await GetRsStatusAsync(targetRs, rsNodes, port, pwd.Value!, cancellationToken).ConfigureAwait(false);
            if (poll.IsFail) continue;
            var p = poll.Value.Members.FirstOrDefault(m => m.Role == "primary");
            if (p is not null && !string.Equals(p.Hostname, originalPrimary.Hostname, StringComparison.OrdinalIgnoreCase))
            {
                newPrimary = p.Hostname;
                newPrimaryAt = sw.Elapsed;
                break;
            }
        }
        sw.Stop();

        var rto = newPrimary is not null ? newPrimaryAt - failureInjectedAt : TimeSpan.Zero;
        return Result.Ok(new FailoverResult(
            Scenario: $"mongo-sharded-stepdown ({targetRs})",
            OriginalPrimary: $"{targetRs}/{originalPrimary.Hostname}",
            NewPrimary: newPrimary is not null ? $"{targetRs}/{newPrimary}" : null,
            Rto: rto,
            Recovery: newPrimary is not null ? "recovered" : "failed",
            RecoveryHint: newPrimary is null ? $"no new PRIMARY in {targetRs} within the deadline; check rs.status() on the shard members (rs.stepDown holds the old primary down 60s)" : null,
            Timeline: new FailoverTimeline(preFlightAt, failureInjectedAt, newPrimaryAt, sw.Elapsed, sw.Elapsed),
            StartedAtUtc: startedAt));
    }

    // === ScaleOutAddAsync (rs.add a member into a shard RS, apply-on-demand) =
    public async Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ScaleOutResult>(cluster.Error!);
        var all = cluster.Value!.Nodes;

        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ScaleOutResult>(pwd.Error!);

        // Target shard RS: request.ShardId, else the first data shard.
        var dataShards = DataShardRsNames(all);
        var targetRs = !string.IsNullOrWhiteSpace(request.ShardId) && dataShards.Contains(request.ShardId)
            ? request.ShardId!
            : (dataShards.Count > 0 ? dataShards[0] : "");
        if (string.IsNullOrEmpty(targetRs))
            return Result.Fail<ScaleOutResult>("no data shard found to add a member to");
        var rsNodes = NodesForRs(all, targetRs);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var st = await GetRsStatusAsync(targetRs, rsNodes, ShardPort, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (st.IsFail) return Result.Fail<ScaleOutResult>(st.Error!);
        var memberIps = st.Value.Members.Select(m => m.IpAddress).ToHashSet(StringComparer.Ordinal);

        // Discover a provisioned-but-unjoined, reachable shard node (mongod active).
        NodeRecord? candidate = null;
        foreach (var n in rsNodes)
        {
            if (memberIps.Contains(n.Vmnet11)) continue;
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var ping = await _ssh.ExecuteAsync(t, "sudo systemctl is-active nexus-mongo 2>/dev/null || echo down", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (ping.IsOk && ping.Value!.Stdout.Contains("active", StringComparison.Ordinal)) { candidate = n; break; }
        }
        if (candidate is null)
            return Result.Fail<ScaleOutResult>(
                $"no provisioned-but-unjoined mongod is reachable for {targetRs}. Provision one first (apply-on-demand, ADR-0010): "
                + "add a member VM + overlays in nexus-infra-oltp/terraform/envs/oltp-mongo-sharded, "
                + "`pwsh -File scripts/mongo-sharded.ps1 apply`, then re-run `scale-out add`.");

        var primaryNode = rsNodes.FirstOrDefault(n => st.Value.Members.Any(m => m.IpAddress == n.Vmnet11 && m.Role == "primary"));
        if (primaryNode is null) return Result.Fail<ScaleOutResult>($"no reachable PRIMARY in {targetRs} to run rs.add");
        var addJs = $"try{{var r=rs.add(\"{candidate.Vmnet11}:{ShardPort}\");print(\"ADD_OK=\"+r.ok)}}catch(e){{print(\"ADD_ERR:\"+e.message)}}";
        var add = await EvalMongodAsync(primaryNode, ShardPort, pwd.Value!, addJs, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (add.IsFail || !add.Value!.Contains("ADD_OK=1"))
            return Result.Fail<ScaleOutResult>($"rs.add({candidate.Name}) into {targetRs} failed: {(add.IsFail ? add.Error : Tail(add.Value ?? "", 300))}");

        return Result.Ok(new ScaleOutResult("add", [candidate.Name], "ok",
            $"added {candidate.Name} ({candidate.Vmnet11}:{ShardPort}) to {targetRs} as a SECONDARY (initial-sync follows)",
            sw.Elapsed, startedAt));
    }

    // === ScaleOutRemoveAsync (rs.remove a member from a shard RS) ============
    public async Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeName))
            return Result.Fail<ScaleOutResult>("scale-out remove requires a node name");

        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ScaleOutResult>(cluster.Error!);
        var all = cluster.Value!.Nodes;
        var node = all.FirstOrDefault(n => string.Equals(n.Name, request.NodeName, StringComparison.OrdinalIgnoreCase));
        if (node is null) return Result.Fail<ScaleOutResult>($"node '{request.NodeName}' is not in the mongo-sharded cluster");

        var (role, rsName, _) = Classify(node.Name);
        if (role == "mongos")
            return Result.Fail<ScaleOutResult>($"{node.Name} is a stateless mongos router, not an RS member; remove it by deprovisioning the VM (terraform), not via rs.remove.");

        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<ScaleOutResult>(pwd.Error!);

        var rsNodes = NodesForRs(all, rsName);
        var port = rsName == ConfigRsName ? ConfigPort : ShardPort;
        var st = await GetRsStatusAsync(rsName, rsNodes, port, pwd.Value!, cancellationToken).ConfigureAwait(false);
        if (st.IsFail) return Result.Fail<ScaleOutResult>(st.Error!);

        var member = st.Value.Members.FirstOrDefault(m => m.IpAddress == node.Vmnet11);
        if (member is null) return Result.Fail<ScaleOutResult>($"{node.Name} ({node.Vmnet11}) is not currently a member of {rsName}");
        if (member.Role == "primary" && request.Drain)
            return Result.Fail<ScaleOutResult>(
                $"{node.Name} is the PRIMARY of {rsName}; step it down first (`nexus failover-test cluster mongo-sharded --target {node.Name}`) before removing -- "
                + "removing the PRIMARY directly would force an unplanned election.");

        var primaryNode = rsNodes.FirstOrDefault(n => st.Value.Members.Any(m => m.IpAddress == n.Vmnet11 && m.Role == "primary"));
        if (primaryNode is null) return Result.Fail<ScaleOutResult>($"no reachable PRIMARY in {rsName} to run rs.remove");

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var removeJs = $"try{{var r=rs.remove(\"{node.Vmnet11}:{port}\");print(\"REMOVE_OK=\"+r.ok)}}catch(e){{print(\"REMOVE_ERR:\"+e.message)}}";
        var rm = await EvalMongodAsync(primaryNode, port, pwd.Value!, removeJs, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (rm.IsFail || !rm.Value!.Contains("REMOVE_OK=1"))
            return Result.Fail<ScaleOutResult>($"rs.remove({node.Name}) from {rsName} failed: {(rm.IsFail ? rm.Error : Tail(rm.Value ?? "", 300))}");

        return Result.Ok(new ScaleOutResult("remove", [node.Name], "ok",
            $"removed {node.Name} ({node.Vmnet11}:{port}) from {rsName} (node still running; ready for re-add or deprovision)",
            sw.Elapsed, startedAt));
    }

    // === BackupTakeAsync (mongodump through mongos) =========================
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<BackupResult>(cluster.Error!);
        var all = cluster.Value!.Nodes;
        var mongos = MongosNodes(all).FirstOrDefault();
        if (mongos is null) return Result.Fail<BackupResult>("no mongos router found to run mongodump through");

        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<BackupResult>(pwd.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"mongo-sharded-backup-{startedAt:yyyyMMdd-HHmmss}"
            : $"mongo-sharded-{request.Tag}-{startedAt:yyyyMMdd-HHmmss}";
        var dir = "/var/backups/nexus-mongo-sharded";
        var archive = $"{dir}/{backupId}.archive.gz";

        // mongodump connects to the LOCAL mongos (127.0.0.1:27017) as the operator;
        // dumping through mongos collects the routed data of the sharded DB.
        // --archive + --gzip = one compressed file written node-local on the mongos.
        var dumpUri = $"mongodb://127.0.0.1:{MongosPort}/nexus_n_smoke?authSource=admin";
        var script =
            $"sudo mkdir -p {dir}; "
            + $"sudo mongodump --uri '{dumpUri}' --ssl --sslCAFile {CaFile} --sslPEMKeyFile {PemFile} --username {OperatorUser} --password '{pwd.Value}' --authenticationDatabase admin "
            + $"--archive={archive} --gzip 2>&1 | tail -3; "
            + $"sudo stat -c %s {archive}";
        var target = new SshTarget(mongos.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<BackupResult>($"backup on {mongos.Name} failed: {exec.Error}");
        var outLines = exec.Value!.Stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        long size = 0;
        if (outLines.Length == 0 || !long.TryParse(outLines[^1].Trim(), out size) || size <= 0)
            return Result.Fail<BackupResult>($"mongodump did not produce a non-empty archive: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");

        return Result.Ok(new BackupResult(backupId,
            $"{archive} (node-local on {mongos.Name}; mongodump --archive --gzip through mongos)",
            size, sw.Elapsed, startedAt));
    }

    // === BackupRestoreAsync (mongorestore round-trip into a verify namespace) =
    public async Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BackupId))
            return Result.Fail<RestoreResult>("restore requires a backup id");

        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<RestoreResult>(cluster.Error!);
        var all = cluster.Value!.Nodes;

        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<RestoreResult>(pwd.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var dir = "/var/backups/nexus-mongo-sharded";
        var archive = $"{dir}/{request.BackupId}.archive.gz";

        // Backups are node-local on a mongos. Find which mongos holds the archive,
        // run mongorestore from there (through the same local mongos).
        NodeRecord? runNode = null;
        foreach (var n in MongosNodes(all))
        {
            var t = new SshTarget(n.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var probe = await _ssh.ExecuteAsync(t, $"test -s {archive} && echo FOUND || echo NO", SshTimeout, cancellationToken).ConfigureAwait(false);
            if (probe.IsOk && probe.Value!.Stdout.Contains("FOUND", StringComparison.Ordinal)) { runNode = n; break; }
        }
        if (runNode is null)
            return Result.Fail<RestoreResult>($"backup archive '{request.BackupId}' not found on any mongos (looked for {archive}). Run `nexus backup take mongo-sharded` first, or check the backup id.");

        var restoreUri = $"mongodb://127.0.0.1:{MongosPort}/?authSource=admin";
        var script =
            $"test -s {archive} || {{ echo MISSING-ARCHIVE; exit 9; }}; "
            + $"sudo mongorestore --uri '{restoreUri}' --ssl --sslCAFile {CaFile} --sslPEMKeyFile {PemFile} --username {OperatorUser} --password '{pwd.Value}' --authenticationDatabase admin "
            + $"--gzip --archive={archive} --nsInclude 'nexus_n_smoke.*' --nsFrom 'nexus_n_smoke.*' --nsTo 'nexus_n_restore_verify.*' --drop 2>&1 | tail -3; "
            + $"sudo mongosh --quiet {OperatorAuth(pwd.Value!)} --host 127.0.0.1:{MongosPort} --eval "
            + "'var c=db.getSiblingDB(\"nexus_n_restore_verify\").getCollectionNames().reduce(function(a,n){return a+db.getSiblingDB(\"nexus_n_restore_verify\").getCollection(n).countDocuments({})},0);print(\"RESTORED=\"+c)'";
        var target = new SshTarget(runNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var exec = await _ssh.ExecuteAsync(target, script, BackupTimeout, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (exec.IsFail) return Result.Fail<RestoreResult>($"restore on {runNode.Name} failed: {exec.Error}");
        var m = System.Text.RegularExpressions.Regex.Match(exec.Value!.Stdout, @"RESTORED=(\d+)");
        if (!m.Success)
            return Result.Fail<RestoreResult>($"mongorestore round-trip did not confirm restored docs: {Tail(exec.Value.Stdout + exec.Value.Stderr, 300)}");

        return Result.Ok(new RestoreResult(request.BackupId,
            long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), sw.Elapsed, startedAt));
    }

    // === RotateCertAsync (0.N.1: per-node Vault-PKI leaf re-issue + online reload) =
    // Implemented by 0.N.1 (was N/A in 0.N v1 which had no wire TLS). For each of
    // the 11 nodes: force the node's OWN vault-agent to re-issue a fresh leaf, then
    // reload it ONLINE via MongoDB's rotateCertificates (no restart, no shard
    // re-election). Sequential + evidence-based per node.
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<CertRotationResult>(cluster.Error!);
        var all = cluster.Value!.Nodes;
        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<CertRotationResult>(pwd.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        // Order: config RS, then shards, then mongos (routers reload last). The
        // online reload never demotes a primary, so ordering is for tidiness only.
        var ordered = all.OrderBy(n => Classify(n.Name).Role switch { "configsvr" => 0, "shardsvr" => 1, _ => 2 })
                         .ThenBy(n => n.Name, StringComparer.Ordinal).ToList();

        foreach (var node in ordered)
        {
            var (role, _, port) = Classify(node.Name);
            var target = new SshTarget(node.Vmnet11, 22, _sshUsername, _sshKeyPath);

            // Force the node's OWN vault-agent to RE-ISSUE a fresh leaf (durable:
            // a direct issue+write is reverted by the agent's next render -- the
            // Swarm/vitess lesson). rm bundle.pem + restart the agent -> template 70
            // (pkiCert) re-issues bundle.pem + its command mongo-tls-split.sh
            // regenerates server.pem (leaf+key) + ca.crt. Wait for the server.pem
            // serial to CHANGE (proof of a durable re-issue); restore the .bak if not.
            var rerender =
                $"D={TlsDir}; OLD=$(sudo openssl x509 -in $D/server.pem -noout -serial 2>/dev/null|sed 's/serial=//'); "
                + "if sudo test -f $D/bundle.pem; then sudo cp -a $D/bundle.pem $D/bundle.pem.bak; sudo rm -f $D/bundle.pem; fi; "
                + "sudo systemctl restart nexus-vault-agent; "
                + "for i in $(seq 1 30); do NEW=$(sudo openssl x509 -in $D/server.pem -noout -serial 2>/dev/null|sed 's/serial=//'); if [ -n \"$NEW\" ] && [ \"$NEW\" != \"$OLD\" ]; then break; fi; sleep 2; done; "
                + "if sudo test -f $D/bundle.pem.bak; then if sudo test -f $D/bundle.pem; then sudo rm -f $D/bundle.pem.bak; else sudo mv $D/bundle.pem.bak $D/bundle.pem; fi; fi; "
                + "echo \"OLD=$OLD NEW=$(sudo openssl x509 -in $D/server.pem -noout -serial 2>/dev/null|sed 's/serial=//')\"";
            var rr = await _ssh.ExecuteAsync(target, rerender, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
            var (oldSerial, newSerial) = ParseRerender(rr.IsOk ? rr.Value!.Stdout : "");
            if (rr.IsFail || oldSerial.Length == 0 || newSerial.Length == 0 || string.Equals(oldSerial, newSerial, StringComparison.OrdinalIgnoreCase))
            {
                rotated.Add(new CertRotatedNode(node.Name, oldSerial.Length > 0 ? oldSerial : "(unknown)", "(unchanged)",
                    Error: rr.IsFail ? rr.Error : "vault-agent did not re-issue a fresh leaf (server.pem serial unchanged -- the node may be on the OLD Vault root, or its pkiCert did not re-render)."));
                continue;
            }

            // Online reload -- MongoDB 8.0 rotateCertificates reloads the leaf from
            // certificateKeyFile/CAFile with NO restart + NO re-election. Run as the
            // right principal per role (mongod=__system/local, mongos=operator/admin).
            var auth = role == "mongos" ? OperatorAuth(pwd.Value!) : SysAuth(pwd.Value!);
            var reloadCmd = $"sudo mongosh --quiet {auth} --host 127.0.0.1:{port} --eval 'print(db.adminCommand({{rotateCertificates:1}}).ok)'";
            var reload = await _ssh.ExecuteAsync(target, reloadCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            string? note = null;
            if (reload.IsFail || !reload.Value!.Stdout.Trim().EndsWith('1'))
                note = $"new leaf rendered on disk, but rotateCertificates did not confirm ok:1 (the cert loads on the next engine restart regardless): {(reload.IsFail ? reload.Error : Tail(reload.Value!.Stdout + reload.Value.Stderr, 180))}";
            rotated.Add(new CertRotatedNode(node.Name, oldSerial, newSerial, Error: note));
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    /// <summary>Parse the force-rerender probe's `OLD=&lt;serial&gt; NEW=&lt;serial&gt;` line.</summary>
    internal static (string Old, string New) ParseRerender(string stdout)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stdout, @"OLD=([0-9A-Fa-f]*)\s+NEW=([0-9A-Fa-f]*)");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : ("", "");
    }

    // === AclAsync (config-server admin users, via mongos) ===================
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<AclSnapshot>(cluster.Error!);
        var all = cluster.Value!.Nodes;
        var mongos = MongosNodes(all).FirstOrDefault();
        if (mongos is null) return Result.Fail<AclSnapshot>("no mongos router found");

        var pwd = await GetKeyfileAsync(cancellationToken).ConfigureAwait(false);
        if (pwd.IsFail) return Result.Fail<AclSnapshot>(pwd.Error!);

        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            var js = "var u=db.getSiblingDB(\"admin\").getUsers().users.map(function(x){return {u:x.user,r:x.roles.map(function(z){return z.role+\"@\"+z.db})}});print(JSON.stringify(u));";
            var res = await EvalMongosAsync(mongos, pwd.Value!, js, cancellationToken).ConfigureAwait(false);
            if (res.IsFail) return Result.Fail<AclSnapshot>(res.Error!);
            var users = ParseUsers(res.Value!);
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
                users = users.Where(u => string.Equals(u.Name, operation.User, StringComparison.OrdinalIgnoreCase)).ToList();
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user.");
            var roleNames = operation.Permissions is { Count: > 0 } ? operation.Permissions : DefaultGrantRoles;
            var rolesArr = "[" + string.Join(",", roleNames.Select(r => $"{{role:\"{r}\",db:\"admin\"}}")) + "]";
            string js = verb == "grant"
                ? $"var a=db.getSiblingDB(\"admin\");try{{a.createUser({{user:\"{operation.User}\",pwd:\"{operation.User}-ChangeMe!{DateTime.UtcNow.Ticks}\",roles:{rolesArr}}});print(\"GRANT_CREATED\")}}catch(e){{if(e.codeName===\"Location51003\"||(e.message&&e.message.indexOf(\"already exists\")>=0)){{a.grantRolesToUser(\"{operation.User}\",{rolesArr});print(\"GRANT_UPDATED\")}}else{{print(\"GRANT_ERR:\"+e.message)}}}}"
                : $"db.getSiblingDB(\"admin\").revokeRolesFromUser(\"{operation.User}\",{rolesArr});print(\"REVOKE_OK\")";
            var res = await EvalMongosAsync(mongos, pwd.Value!, js, cancellationToken).ConfigureAwait(false);
            if (res.IsFail || res.Value!.Contains("_ERR:"))
                return Result.Fail<AclSnapshot>($"acl {verb} failed: {(res.IsFail ? res.Error : Tail(res.Value ?? "", 200))}");
            return await AclAsync(new AclOperation("describe", operation.User), cancellationToken).ConfigureAwait(false);
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    private static List<AclUser> ParseUsers(string stdout)
    {
        var users = new List<AclUser>();
        var json = MongoAdapter.ExtractJson(stdout);
        if (json is null) return users;
        using var doc = JsonDocument.Parse(json);
        foreach (var u in doc.RootElement.EnumerateArray())
        {
            var name = u.TryGetProperty("u", out var nn) ? nn.GetString() ?? "" : "";
            var roles = new List<string>();
            if (u.TryGetProperty("r", out var rr) && rr.ValueKind == JsonValueKind.Array)
                foreach (var r in rr.EnumerateArray()) roles.Add(r.GetString() ?? "");
            users.Add(new AclUser(name, roles, Enabled: true));
        }
        return users;
    }

    // === ApplyChaosAsync (process-kill a shard mongod + RS rejoin) ==========
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        if (!KnownChaosScenarios.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", KnownChaosScenarios)}");

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<ChaosOutcome>(status.Error!);
        var members = status.Value!.Members;

        // Default target: a SECONDARY of a data shard (safer than a primary or config).
        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? members.FirstOrDefault(m => string.Equals(m.Hostname, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : (members.FirstOrDefault(m => m.Role == "secondary" && m.ShardId is not null && m.ShardId.StartsWith("shard-", StringComparison.Ordinal))
               ?? members.FirstOrDefault(m => m.Role == "secondary"));
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target node found");

        var target = new SshTarget(victim.IpAddress, 22, _sshUsername, _sshKeyPath);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperTarget = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? "nexus-mongo" : "";
        var intensity = scenario.IntensityPercent?.ToString(CultureInfo.InvariantCulture) ?? "";

        var injectCmd = $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperTarget}'";
        var injectExec = await _ssh.ExecuteAsync(target, injectCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (injectExec.IsFail || injectExec.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Hostname} failed: {(injectExec.IsFail ? injectExec.Error : Tail(injectExec.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(60);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var post = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (post.IsOk && post.Value!.OverallHealth == "green") { recovered = true; break; }
        }
        sw.Stop();

        return Result.Ok(new ChaosOutcome(scenario.ScenarioType, victim.Hostname, observed, sw.Elapsed, startedAt, recovered));
    }

    private async Task<Result<bool>> PushChaosHelperAsync(SshTarget target, CancellationToken cancellationToken)
    {
        var asm = typeof(RedisAdapter).Assembly;
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("nexus-chaos.sh", StringComparison.Ordinal));
        if (resName is null) return Result.Fail<bool>("embedded nexus-chaos.sh resource not found in the assembly");
        string script;
        using (var s = asm.GetManifestResourceStream(resName)!)
        using (var r = new StreamReader(s))
            script = (await r.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Replace("\r\n", "\n");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var cmd = $"echo {b64} | base64 -d | sudo tee /usr/local/bin/nexus-chaos.sh >/dev/null && sudo chmod +x /usr/local/bin/nexus-chaos.sh && echo PUSHED";
        var exec = await _ssh.ExecuteAsync(target, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (exec.IsFail || exec.Value!.ExitCode != 0 || !exec.Value.Stdout.Contains("PUSHED", StringComparison.Ordinal))
            return Result.Fail<bool>($"failed to install nexus-chaos.sh: {(exec.IsFail ? exec.Error : Tail(exec.Value!.Stderr, 200))}");
        return Result.Ok(true);
    }

    // === CanResizeVm ========================================================
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false; // conservative: caller should GetStatusAsync first
        var member = _lastStatus.Members.FirstOrDefault(m =>
            string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        // Routers are stateless (safe). Any current RS primary (config or shard) is refused.
        return member.Role != "primary";
    }

    // === Helpers ============================================================
    private static string Tail(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= n ? s : s.Substring(s.Length - n);
    }
}
