using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Lighter OBSERVE adapter (Phase 0.H.7 / nexus-cli v0.6.7) for the Kafka
/// ecosystem tier (ClusterId <c>kafka-ecosystem</c>) -- the 9 client services
/// that ride on top of the two KRaft clusters: Schema Registry HA pair, REST
/// Proxy, Kafka Connect (+ Debezium), ksqlDB, and the MirrorMaker 2 DR pair.
/// <para>
/// These are not a clustered data store with a leader/quorum, so the adapter
/// implements the OBSERVE + maintenance subset: <c>status</c> / <c>health</c>
/// / <c>topology</c> (per-service systemctl + each service's HTTPS health
/// endpoint + MM2 liveness), <c>cert-rotate</c> (re-issue each node's Vault-PKI
/// leaf + rebuild its PEM/PKCS#12 keystores + restart its service), and
/// <c>chaos</c> (process-kill a service + recover). Failover / scale-out /
/// backup / ACL are not meaningful at the ecosystem layer and return a clear
/// pointer to the right surface (the per-cluster kafka-east/kafka-west
/// adapters or nexus-infra-kafka's overlays).
/// </para>
/// <para>
/// mTLS-only, no managed driver, SSH-shell-out -- same invariants as
/// <see cref="KafkaClusterAdapter"/>. Health endpoints (live-probed v0.6.7):
/// Schema Registry :8081 /subjects, REST Proxy :8082 /v3/clusters, Kafka
/// Connect :8083 /, ksqlDB :8088 /healthcheck. MM2 has no REST surface -- its
/// liveness is systemctl + a live MirrorSourceConnector in the journal.
/// </para>
/// </summary>
public sealed class KafkaEcosystemAdapter : IClusterAdapter
{
    private const string ClusterName = "kafka-ecosystem";
    private const string PkiRole = "kafka-broker"; // historical name; issues for the whole tier
    private const string VaultAddr = "https://192.168.70.121:8200";
    private const string VaultCaCert = "/etc/ssl/certs/kafka-ca.pem";
    private const string AgentTokenPath = "/run/nexus-vault-agent/token";
    private const string CaPem = "/etc/ssl/certs/kafka-ca.pem";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(30);

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;

    private ClusterStatus? _lastStatus;

    /// <summary>Constructs the adapter over an <see cref="ISshClient"/> transport and the <see cref="IVmsCatalog"/> node inventory.</summary>
    public KafkaEcosystemAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
    }

    /// <inheritdoc />
    public string ClusterId => ClusterName;
    /// <inheritdoc />
    public string DisplayName => "Kafka ecosystem (Schema Registry, REST, Connect, ksqlDB, MirrorMaker 2)";

    /// <summary>Service profile per node, keyed off the hostname prefix.</summary>
    internal sealed record Svc(string Kind, string Unit, int HttpPort, string? HealthPath);

    /// <summary>Maps a node hostname prefix to its <see cref="Svc"/> profile (unit, health port/path); MM2 has no HTTP surface.</summary>
    internal static Svc ServiceFor(string name)
    {
        if (name.StartsWith("schema-registry", StringComparison.OrdinalIgnoreCase))
            return new Svc("schema-registry", "schema-registry.service", 8081, "/subjects");
        if (name.StartsWith("kafka-connect", StringComparison.OrdinalIgnoreCase))
            return new Svc("kafka-connect", "connect-distributed.service", 8083, "/");
        if (name.StartsWith("ksqldb", StringComparison.OrdinalIgnoreCase))
            return new Svc("ksqldb", "ksqldb-server.service", 8088, "/healthcheck");
        if (name.StartsWith("kafka-rest", StringComparison.OrdinalIgnoreCase))
            return new Svc("kafka-rest", "kafka-rest.service", 8082, "/v3/clusters");
        if (name.StartsWith("mm2", StringComparison.OrdinalIgnoreCase))
            return new Svc("mirrormaker2", "mm2.service", 0, null); // no REST surface
        return new Svc("unknown", "", 0, null);
    }

    private SshTarget Ssh(NodeRecord n) => new(n.Vmnet11, 22, _sshUsername, _sshKeyPath);

    // === GetStatusAsync =====================================================
    /// <inheritdoc />
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ClusterStatus>(cluster.Error!);

        var members = new List<ClusterMember>();
        foreach (var n in cluster.Value!.Nodes)
        {
            var svc = ServiceFor(n.Name);
            var exec = await _ssh.ExecuteAsync(Ssh(n),
                $"systemctl is-active {svc.Unit}", SshTimeout, cancellationToken).ConfigureAwait(false);
            var active = exec.IsOk && exec.Value!.Stdout.Trim() == "active";
            members.Add(new ClusterMember(n.Name, n.Vmnet11, svc.Kind, active ? "alive" : "down", null, null));
        }

        var overall = members.All(m => m.Status == "alive") ? "green"
            : members.Count(m => m.Status == "alive") >= members.Count - 1 ? "yellow"
            : "red";
        var status = new ClusterStatus(ClusterName, DisplayName, overall, members, null, DateTimeOffset.UtcNow);
        _lastStatus = status;
        return Result.Ok(status);
    }

    // === HealthAsync (systemctl + HTTPS health endpoint + MM2 journal) ======
    /// <inheritdoc />
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<HealthReport>(cluster.Error!);

        var probes = new List<HealthProbe>();
        foreach (var n in cluster.Value!.Nodes)
        {
            var svc = ServiceFor(n.Name);
            var active = await IsActiveAsync(n, svc.Unit, cancellationToken).ConfigureAwait(false);
            probes.Add(new HealthProbe($"{svc.Kind}-service", n.Name, active ? "green" : "red",
                active ? "active" : "inactive", $"{svc.Unit} active"));
            if (!active) continue;

            if (svc.HttpPort > 0 && svc.HealthPath is not null)
            {
                var url = $"https://localhost:{svc.HttpPort}{svc.HealthPath}";
                var code = await HttpCodeAsync(n, url, cancellationToken).ConfigureAwait(false);
                probes.Add(new HealthProbe($"{svc.Kind}-endpoint", n.Name, code == 200 ? "green" : "red",
                    $"HTTP {code} {url}", "HTTPS health endpoint returns 200"));
            }
            else if (svc.Kind == "mirrormaker2")
            {
                // No REST surface -- liveness = a Mirror*Connector actively logging.
                var exec = await _ssh.ExecuteAsync(Ssh(n),
                    "sudo journalctl -u mm2.service -n 40 --no-pager 2>/dev/null | grep -Ec 'Mirror(Source|Heartbeat|Checkpoint)Connector'", SshTimeout, cancellationToken).ConfigureAwait(false);
                var live = exec.IsOk && int.TryParse(exec.Value!.Stdout.Trim(), out var c) && c > 0;
                probes.Add(new HealthProbe("mirrormaker2-flow", n.Name, live ? "green" : "yellow",
                    live ? "Mirror*Connector active in journal" : "no recent connector log lines",
                    "MM2 connector actively mirroring"));
            }
        }

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync (service groups) =====================================
    /// <inheritdoc />
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);

        var nodes = status.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.Role, m.Status, null))
            .ToList();

        // Group the services by kind -> a "shard" per service type (HA pairs etc.).
        var shards = status.Value.Members
            .GroupBy(m => m.Role)
            .Select(g => new TopologyShard(
                ShardId: g.Key,
                Primary: g.First().Hostname,
                Replicas: g.Skip(1).Select(m => m.Hostname).ToList(),
                SlotRange: $"{g.Count()} node(s)"))
            .ToList();

        return Result.Ok(new TopologySnapshot(ClusterName, nodes, shards, DateTimeOffset.UtcNow));
    }

    // === RotateCertAsync (re-issue + rebuild keystores + restart service) ===
    /// <inheritdoc />
    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<CertRotationResult>(cluster.Error!);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var rotated = new List<CertRotatedNode>();

        foreach (var n in cluster.Value!.Nodes)
        {
            var svc = ServiceFor(n.Name);
            var target = Ssh(n);

            var oldExec = await _ssh.ExecuteAsync(target,
                "sudo openssl x509 -in /etc/nexus-kafka/tls/keystore.pem -noout -serial 2>/dev/null | sed 's/serial=//'", SshTimeout, cancellationToken).ConfigureAwait(false);
            var oldSerial = oldExec.IsOk && oldExec.Value!.ExitCode == 0 && oldExec.Value.Stdout.Trim().Length > 0 ? oldExec.Value.Stdout.Trim() : "(unknown)";

            var cn = $"{n.Name}.kafka.nexus.lab";
            var alts = $"{n.Name},{n.Name}.nexus.lab,{n.Name}.kafka.nexus.lab,localhost";
            var ips = $"{n.Vmnet10},{n.Vmnet11},127.0.0.1";
            var issueCmd =
                $"T=$(sudo cat {AgentTokenPath} 2>/dev/null); " +
                $"sudo env VAULT_ADDR={VaultAddr} VAULT_TOKEN=\"$T\" VAULT_CACERT={VaultCaCert} " +
                $"/usr/local/bin/vault write -format=json pki_int/issue/{PkiRole} common_name={cn} alt_names={alts} ip_sans={ips} ttl=2160h";
            var issue = await _ssh.ExecuteAsync(target, issueCmd, SshTimeout, cancellationToken).ConfigureAwait(false);
            if (issue.IsFail || issue.Value!.ExitCode != 0)
            {
                rotated.Add(new CertRotatedNode(n.Name, oldSerial, "(unchanged)", issue.IsFail ? issue.Error : $"vault issue failed: {Tail(issue.Value!.Stderr, 200)}"));
                continue;
            }

            string cert, key, ca, newSerial;
            try
            {
                using var doc = JsonDocument.Parse(issue.Value.Stdout);
                var d = doc.RootElement.GetProperty("data");
                cert = d.GetProperty("certificate").GetString() ?? "";
                key = d.GetProperty("private_key").GetString() ?? "";
                ca = d.GetProperty("issuing_ca").GetString() ?? "";
                newSerial = d.GetProperty("serial_number").GetString() ?? "(unknown)";
            }
            catch (Exception ex)
            {
                rotated.Add(new CertRotatedNode(n.Name, oldSerial, "(unchanged)", $"could not parse vault issue response: {ex.Message}"));
                continue;
            }

            // bundle.pem -> kafka-tls-split.sh rebuilds keystore.pem + truststore.pem
            // AND (on ecosystem nodes) keystore.p12 + truststore.p12. Then restart
            // the node's own service so it reloads the rotated identity.
            var bundle = cert.TrimEnd() + "\n" + key.TrimEnd() + "\n" + ca.TrimEnd() + "\n";
            var writeCmd =
                $"echo {B64(bundle)} | base64 -d | sudo tee /etc/nexus-kafka/tls/bundle.pem >/dev/null; " +
                "sudo chown root:kafka /etc/nexus-kafka/tls/bundle.pem; sudo chmod 0640 /etc/nexus-kafka/tls/bundle.pem; " +
                "sudo /usr/local/sbin/kafka-tls-split.sh >/dev/null 2>&1; " +
                $"sudo systemctl reset-failed {svc.Unit} 2>/dev/null; sudo systemctl restart {svc.Unit}; echo WROTE";
            var write = await _ssh.ExecuteAsync(target, writeCmd, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            if (write.IsFail || write.Value!.ExitCode != 0 || !write.Value.Stdout.Contains("WROTE", StringComparison.Ordinal))
            {
                rotated.Add(new CertRotatedNode(n.Name, oldSerial, "(unchanged)", write.IsFail ? write.Error : $"writing new cert / restart failed: {Tail(write.Value!.Stderr, 200)}"));
                continue;
            }
            rotated.Add(new CertRotatedNode(n.Name, oldSerial, newSerial, null));
        }
        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    // === ApplyChaosAsync (process-kill a service + recover) =================
    /// <inheritdoc />
    public async Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken)
    {
        var known = new[] { "network-partition", "packet-loss", "slow-disk", "cpu-starve", "memory-pressure", "process-kill" };
        if (!known.Contains(scenario.ScenarioType, StringComparer.OrdinalIgnoreCase))
            return Result.Fail<ChaosOutcome>($"unknown chaos scenario '{scenario.ScenarioType}'. Known: {string.Join(", ", known)}");

        var cluster = _catalog.GetCluster(ClusterName);
        if (cluster.IsFail) return Result.Fail<ChaosOutcome>(cluster.Error!);

        var victim = !string.IsNullOrWhiteSpace(scenario.Target)
            ? cluster.Value!.Nodes.FirstOrDefault(n => string.Equals(n.Name, scenario.Target, StringComparison.OrdinalIgnoreCase))
            : cluster.Value!.Nodes.FirstOrDefault(n => n.Name.StartsWith("ksqldb", StringComparison.OrdinalIgnoreCase)) ?? (cluster.Value!.Nodes.Count > 0 ? cluster.Value!.Nodes[0] : null);
        if (victim is null) return Result.Fail<ChaosOutcome>("no chaos target node found");
        var svc = ServiceFor(victim.Name);

        var target = Ssh(victim);
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var push = await PushChaosHelperAsync(target, cancellationToken).ConfigureAwait(false);
        if (push.IsFail) return Result.Fail<ChaosOutcome>(push.Error!);

        var dur = scenario.DurationSeconds <= 0 ? 30 : scenario.DurationSeconds;
        var helperUnit = string.Equals(scenario.ScenarioType, "process-kill", StringComparison.OrdinalIgnoreCase) ? svc.Unit : "";
        var intensity = scenario.IntensityPercent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";

        var inject = await _ssh.ExecuteAsync(target,
            $"sudo /usr/local/bin/nexus-chaos.sh inject {scenario.ScenarioType} {dur} '{intensity}' '{helperUnit}'", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (inject.IsFail || inject.Value!.ExitCode != 0)
            return Result.Fail<ChaosOutcome>($"chaos inject on {victim.Name} failed: {(inject.IsFail ? inject.Error : Tail(inject.Value!.Stderr, 200))}");

        await Task.Delay(TimeSpan.FromSeconds(Math.Min(dur, 20)), cancellationToken).ConfigureAwait(false);
        var impact = await HealthAsync(cancellationToken).ConfigureAwait(false);
        var observed = impact.IsOk ? impact.Value!.Probes : (IReadOnlyList<HealthProbe>)Array.Empty<HealthProbe>();

        await _ssh.ExecuteAsync(target, $"sudo /usr/local/bin/nexus-chaos.sh lift {scenario.ScenarioType}", SshTimeout, cancellationToken).ConfigureAwait(false);

        var recovered = false;
        var deadline = sw.Elapsed + TimeSpan.FromSeconds(60);
        while (sw.Elapsed < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
            var active = await IsActiveAsync(victim, svc.Unit, cancellationToken).ConfigureAwait(false);
            if (active) { recovered = true; break; }
        }
        sw.Stop();

        return Result.Ok(new ChaosOutcome(scenario.ScenarioType, victim.Name, observed, sw.Elapsed, startedAt, recovered));
    }

    // === Deferred (not meaningful at the ecosystem layer) ===================
    /// <inheritdoc />
    public Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<FailoverResult>(
            "kafka-ecosystem has no leader to fail over. For cross-region Kafka DR use `nexus failover-test cluster kafka` (MirrorMaker 2 east<->west); for a controller-leader move use `failover-test cluster kafka-east|kafka-west`."));

    /// <inheritdoc />
    public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(
            "kafka-ecosystem scale-out is an apply-on-demand IaC operation (add a node to vms.yaml + the kafka env and `kafka.ps1 apply`); the SR/Connect/ksqlDB services then HA-join via their group.id/cluster id."));

    /// <inheritdoc />
    public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(
            "kafka-ecosystem scale-out remove is managed via the nexus-infra-kafka terraform overlays (per-VM enable toggles)."));

    /// <inheritdoc />
    public Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<BackupResult>(
            "kafka-ecosystem services are stateless clients of the brokers (Schema Registry's _schemas, Connect's config/offset/status, ksqlDB's command topic all live ON the brokers). Back up via `nexus backup take kafka-east <topic>`."));

    /// <inheritdoc />
    public Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<RestoreResult>("see BackupTakeAsync -- ecosystem state lives on the brokers; restore via `nexus backup restore kafka-east <id>`."));

    /// <inheritdoc />
    public Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<AclSnapshot>(
            "Kafka ACLs are enforced on the brokers. Use `nexus acl kafka-east|kafka-west list|grant|revoke ...` (the ecosystem service principals are already in super.users)."));

    /// <inheritdoc />
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false;
        // Ecosystem services are stateless/HA -- any single node can be resized;
        // refuse only if it's the sole alive node of its service kind.
        var member = _lastStatus.Members.FirstOrDefault(m => string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        var aliveSameKind = _lastStatus.Members.Count(m => m.Role == member.Role && m.Status == "alive");
        return aliveSameKind > 1 || member.Role is "kafka-rest"; // rest is a singleton; allow
    }

    // === Helpers ============================================================
    private async Task<bool> IsActiveAsync(NodeRecord n, string unit, CancellationToken cancellationToken)
    {
        var exec = await _ssh.ExecuteAsync(Ssh(n), $"systemctl is-active {unit}", SshTimeout, cancellationToken).ConfigureAwait(false);
        return exec.IsOk && exec.Value!.Stdout.Trim() == "active";
    }

    private async Task<int> HttpCodeAsync(NodeRecord n, string url, CancellationToken cancellationToken)
    {
        var exec = await _ssh.ExecuteAsync(Ssh(n),
            $"curl -s --cacert {CaPem} {url} -o /dev/null -w '%{{http_code}}'", SshTimeout, cancellationToken).ConfigureAwait(false);
        if (exec.IsFail || exec.Value!.ExitCode != 0) return 0;
        return int.TryParse(exec.Value.Stdout.Trim(), out var code) ? code : 0;
    }

    private async Task<Result<bool>> PushChaosHelperAsync(SshTarget target, CancellationToken cancellationToken)
    {
        var asm = typeof(KafkaEcosystemAdapter).Assembly;
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

    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s[^n..]);
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
