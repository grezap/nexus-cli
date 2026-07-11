using System.Diagnostics;
using Nexus.Cli.Adapters.Consul;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Nomad;
using Nexus.Cli.Adapters.Vmware;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Orchestrates the consul-leader failover scenario:
///   1. Identify the current Consul leader via /v1/status/leader.
///   2. Map the leader's RPC address (192.168.10.X:8300) to a node in
///      vms.yaml's `swarm` cluster.
///   3. Pick a different manager as the polling endpoint (otherwise our
///      poll queries hit the very agent we're about to stop).
///   4. SSH to the leader and run `sudo systemctl stop consul`.
///   5. Poll the non-leader endpoint until /v1/status/leader returns a
///      DIFFERENT address (raft elected a new leader). Time it.
///   6. SSH `sudo systemctl start consul` to recover. If the start
///      fails, fall through with a Result.Ok report whose
///      <see cref="FailoverTestReport.RecoveryHint"/> contains the exact
///      manual recovery command.
///   7. Wait for the previously-stopped agent to rejoin the gossip
///      (consul members reports 6 alive).
///
/// All wall-clock measurements are taken from a single Stopwatch so the
/// timeline offsets in the returned report are monotonic.
/// </summary>
public sealed class FailoverTestService : IFailoverTestService
{
    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly IVmrunClient _vmrun;
    private readonly NexusHttpClientFactory _httpFactory;
    private readonly string _consulMgmtToken;
    private readonly string _nomadMgmtToken;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;

    // Tunables. Kept conservative for v0.3.x; --options to override are a future task.
    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ElectionDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecoveryWaitDeadline = TimeSpan.FromSeconds(45);
    // VM boot after vmrun-suspend / vmrun-resume needs longer than a service restart.
    private static readonly TimeSpan VmRecoveryWaitDeadline = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Constructs the service over the fleet catalog, SSH + vmrun transports, and the
    /// HTTP client factory, plus the Consul/Nomad management tokens the pollers use.
    /// </summary>
    public FailoverTestService(
        IVmsCatalog catalog,
        ISshClient ssh,
        IVmrunClient vmrun,
        NexusHttpClientFactory httpFactory,
        string consulMgmtToken,
        string nomadMgmtToken,
        string sshUsername,
        string sshKeyPath)
    {
        _catalog = catalog;
        _ssh = ssh;
        _vmrun = vmrun;
        _httpFactory = httpFactory;
        _consulMgmtToken = consulMgmtToken;
        _nomadMgmtToken = nomadMgmtToken;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
    }

    /// <inheritdoc />
    public async Task<Result<FailoverTestReport>> RunConsulLeaderAsync(
        string? targetNode,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var swarm = _catalog.GetCluster("swarm");
        if (swarm.IsFail)
            return Result.Fail<FailoverTestReport>(swarm.Error!);
        var managers = swarm.Value!.Nodes.Where(n => n.Name.StartsWith("swarm-manager-", StringComparison.Ordinal)).ToList();
        if (managers.Count < 2)
            return Result.Fail<FailoverTestReport>("expected at least 2 swarm managers in vms.yaml; got " + managers.Count);

        // 1. Discover leader via the first manager we can reach.
        var leaderRpc = await TryGetLeaderAsync(managers, cancellationToken).ConfigureAwait(false);
        if (leaderRpc.IsFail)
            return Result.Fail<FailoverTestReport>(leaderRpc.Error!);
        var leaderIp = leaderRpc.Value!.Split(':')[0];

        // 2. Map to node.
        var leaderNode = managers.FirstOrDefault(n => n.Vmnet10 == leaderIp);
        if (leaderNode is null)
            return Result.Fail<FailoverTestReport>(
                $"current leader {leaderIp} not found in vms.yaml swarm.managers; refusing to act blind.");

        if (!string.IsNullOrEmpty(targetNode) && !string.Equals(targetNode, leaderNode.Name, StringComparison.Ordinal))
            return Result.Fail<FailoverTestReport>(
                $"--node was '{targetNode}' but current leader is '{leaderNode.Name}'. Rerun without --node " +
                "or wait for raft to elect that node leader.");

        // 3. Pick a different manager as the polling endpoint.
        var pollNode = managers.First(n => !string.Equals(n.Name, leaderNode.Name, StringComparison.Ordinal));
        using var pollConsul = MakeConsul(pollNode.Vmnet11);

        var preFlightCompleted = sw.Elapsed;

        // 4. Inject failure: SSH stop consul on the leader.
        var sshTarget = new SshTarget(leaderNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var stop = await _ssh.ExecuteAsync(sshTarget, "sudo systemctl stop consul", SshTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (stop.IsFail)
            return Result.Fail<FailoverTestReport>($"SSH stop failed: {stop.Error}");
        if (stop.Value!.ExitCode != 0)
            return Result.Fail<FailoverTestReport>(
                $"`systemctl stop consul` returned exit {stop.Value.ExitCode} on {leaderNode.Name}: {stop.Value.Stderr}");
        var failureInjected = sw.Elapsed;

        // 5. Poll non-leader until raft elects a new leader.
        string? newLeaderRpc = null;
        var pollDeadline = failureInjected.Add(ElectionDeadline);
        while (sw.Elapsed < pollDeadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            var poll = await pollConsul.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (poll.IsOk && !string.IsNullOrEmpty(poll.Value!.Leader)
                && !string.Equals(poll.Value.Leader, leaderRpc.Value, StringComparison.Ordinal))
            {
                newLeaderRpc = poll.Value.Leader;
                break;
            }
        }
        var newLeaderObserved = sw.Elapsed;

        // 6. Auto-recovery: SSH start consul on the original leader.
        var start = await _ssh.ExecuteAsync(sshTarget, "sudo systemctl start consul", SshTimeout, cancellationToken)
            .ConfigureAwait(false);
        var recoveryAttempted = sw.Elapsed;
        var recovery = start.IsOk && start.Value!.ExitCode == 0
            ? FailoverRecoveryStatus.Recovered
            : FailoverRecoveryStatus.RecoveryFailed;
        var recoveryHint = recovery == FailoverRecoveryStatus.Recovered
            ? null
            : $"ssh {_sshUsername}@{sshTarget.Host} sudo systemctl start consul";

        // 7. Wait for the previously-stopped agent to rejoin (alive count back to full).
        var expectedAlive = swarm.Value.Nodes.Count;
        var healthyDeadline = sw.Elapsed.Add(RecoveryWaitDeadline);
        while (sw.Elapsed < healthyDeadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var h = await pollConsul.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (h.IsOk && h.Value!.Alive == expectedAlive) break;
        }
        var clusterHealthyAgain = sw.Elapsed;

        var rto = newLeaderObserved - failureInjected;
        var newLeaderName = newLeaderRpc is null ? null : MapToNodeName(newLeaderRpc, managers);

        var timeline = new FailoverTimeline(
            preFlightCompleted, failureInjected, newLeaderObserved, recoveryAttempted, clusterHealthyAgain);

        var report = new FailoverTestReport(
            FailoverScenario.ConsulLeader,
            startedAt,
            leaderNode.Name,
            newLeaderName,
            rto,
            recovery,
            recoveryHint,
            timeline);

        return Result.Ok(report);
    }

    /// <summary>Builds a Consul client against a node's TLS API endpoint (:8501) with the mgmt token.</summary>
    private ConsulClient MakeConsul(string ip) =>
        new(new ConsulClient.Settings($"https://{ip}:8501", _consulMgmtToken), _httpFactory);

    /// <summary>Returns the current Consul leader RPC address from the first manager that answers.</summary>
    private async Task<Result<string>> TryGetLeaderAsync(
        IReadOnlyList<NodeRecord> managers,
        CancellationToken cancellationToken)
    {
        foreach (var m in managers)
        {
            using var c = MakeConsul(m.Vmnet11);
            var h = await c.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (h.IsOk && !string.IsNullOrEmpty(h.Value!.Leader))
                return Result.Ok(h.Value.Leader);
        }
        return Result.Fail<string>("no manager returned a current Consul leader; cluster may already be failing.");
    }

    /// <summary>Maps a raft RPC/leader address (host:port) back to its vms.yaml node name via the VMnet10 IP.</summary>
    private static string? MapToNodeName(string rpcAddr, IEnumerable<NodeRecord> managers)
    {
        var ip = rpcAddr.Split(':')[0];
        return managers.FirstOrDefault(n => n.Vmnet10 == ip)?.Name;
    }

    // ===== Nomad failover (v0.3.1) =========================================

    /// <inheritdoc />
    public async Task<Result<FailoverTestReport>> RunNomadLeaderAsync(
        string? targetNode,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var swarm = _catalog.GetCluster("swarm");
        if (swarm.IsFail)
            return Result.Fail<FailoverTestReport>(swarm.Error!);
        var managers = swarm.Value!.Nodes.Where(n => n.Name.StartsWith("swarm-manager-", StringComparison.Ordinal)).ToList();
        if (managers.Count < 2)
            return Result.Fail<FailoverTestReport>("expected at least 2 swarm managers in vms.yaml; got " + managers.Count);

        // 1. Discover Nomad leader.
        var leaderAddr = await TryGetNomadLeaderAsync(managers, cancellationToken).ConfigureAwait(false);
        if (leaderAddr.IsFail)
            return Result.Fail<FailoverTestReport>(leaderAddr.Error!);
        var leaderIp = leaderAddr.Value!.Split(':')[0];

        // 2. Map leader IP to vms.yaml node.
        var leaderNode = managers.FirstOrDefault(n => n.Vmnet10 == leaderIp);
        if (leaderNode is null)
            return Result.Fail<FailoverTestReport>(
                $"current Nomad leader {leaderIp} not found in vms.yaml swarm.managers; refusing to act blind.");

        if (!string.IsNullOrEmpty(targetNode) && !string.Equals(targetNode, leaderNode.Name, StringComparison.Ordinal))
            return Result.Fail<FailoverTestReport>(
                $"--node was '{targetNode}' but current Nomad leader is '{leaderNode.Name}'. Rerun without --node " +
                "or wait for raft to elect that node leader.");

        // 3. Polling endpoint = different manager.
        var pollNode = managers.First(n => !string.Equals(n.Name, leaderNode.Name, StringComparison.Ordinal));
        using var pollNomad = MakeNomad(pollNode.Vmnet11);

        var preFlightCompleted = sw.Elapsed;

        // 4. SSH stop nomad on the leader.
        var sshTarget = new SshTarget(leaderNode.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var stop = await _ssh.ExecuteAsync(sshTarget, "sudo systemctl stop nomad", SshTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (stop.IsFail)
            return Result.Fail<FailoverTestReport>($"SSH stop failed: {stop.Error}");
        if (stop.Value!.ExitCode != 0)
            return Result.Fail<FailoverTestReport>(
                $"`systemctl stop nomad` returned exit {stop.Value.ExitCode} on {leaderNode.Name}: {stop.Value.Stderr}");
        var failureInjected = sw.Elapsed;

        // 5. Poll non-leader until raft elects a new Nomad leader.
        string? newLeaderAddr = null;
        var pollDeadline = failureInjected.Add(ElectionDeadline);
        while (sw.Elapsed < pollDeadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            var poll = await pollNomad.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (poll.IsOk && !string.IsNullOrEmpty(poll.Value!.LeaderAddress)
                && !string.Equals(poll.Value.LeaderAddress, leaderAddr.Value, StringComparison.Ordinal))
            {
                newLeaderAddr = poll.Value.LeaderAddress;
                break;
            }
        }
        var newLeaderObserved = sw.Elapsed;

        // 6. Auto-recovery.
        var start = await _ssh.ExecuteAsync(sshTarget, "sudo systemctl start nomad", SshTimeout, cancellationToken)
            .ConfigureAwait(false);
        var recoveryAttempted = sw.Elapsed;
        var recovery = start.IsOk && start.Value!.ExitCode == 0
            ? FailoverRecoveryStatus.Recovered
            : FailoverRecoveryStatus.RecoveryFailed;
        var recoveryHint = recovery == FailoverRecoveryStatus.Recovered
            ? null
            : $"ssh {_sshUsername}@{sshTarget.Host} sudo systemctl start nomad";

        // 7. Wait for Nomad servers to reconverge (3 alive servers + a leader).
        var expectedServers = managers.Count;
        var healthyDeadline = sw.Elapsed.Add(RecoveryWaitDeadline);
        while (sw.Elapsed < healthyDeadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            var h = await pollNomad.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (h.IsOk && h.Value!.Servers.Count == expectedServers && !string.IsNullOrEmpty(h.Value.LeaderAddress)) break;
        }
        var clusterHealthyAgain = sw.Elapsed;

        var rto = newLeaderObserved - failureInjected;
        var newLeaderName = newLeaderAddr is null ? null : MapToNodeName(newLeaderAddr, managers);

        var timeline = new FailoverTimeline(
            preFlightCompleted, failureInjected, newLeaderObserved, recoveryAttempted, clusterHealthyAgain);

        return Result.Ok(new FailoverTestReport(
            FailoverScenario.NomadLeader,
            startedAt,
            leaderNode.Name,
            newLeaderName,
            rto,
            recovery,
            recoveryHint,
            timeline));
    }

    /// <summary>Builds a Nomad client against a node's TLS API endpoint (:4646) with the mgmt token.</summary>
    private NomadClient MakeNomad(string ip) =>
        new(new NomadClient.Settings($"https://{ip}:4646", _nomadMgmtToken), _httpFactory);

    /// <summary>Returns the current Nomad leader address from the first manager that answers.</summary>
    private async Task<Result<string>> TryGetNomadLeaderAsync(
        IReadOnlyList<NodeRecord> managers,
        CancellationToken cancellationToken)
    {
        foreach (var m in managers)
        {
            using var c = MakeNomad(m.Vmnet11);
            var h = await c.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (h.IsOk && !string.IsNullOrEmpty(h.Value!.LeaderAddress))
                return Result.Ok(h.Value.LeaderAddress);
        }
        return Result.Fail<string>("no manager returned a current Nomad leader; cluster may already be failing.");
    }

    // ===== Swarm manager failover (v0.3.2) =================================
    //
    // Structurally different from consul/nomad: failure injection is HOST-LEVEL
    // (vmrun suspend) instead of service-level (systemctl stop), and leader
    // discovery uses SSH+docker (Docker Swarm raft has no public HTTP API like
    // Consul/Nomad). Recovery uses vmrun start nogui. Healthy-wait window is
    // longer because VM cold-boot takes ~30-60s (vs ~1-5s for a service restart).

    /// <inheritdoc />
    public async Task<Result<FailoverTestReport>> RunSwarmManagerAsync(
        string? targetNode,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        if (!_vmrun.IsAvailable)
            return Result.Fail<FailoverTestReport>(
                "swarm-manager scenario requires vmrun.exe (host-level failure injection). " +
                "Run on the Windows build host where VMware Workstation Pro is installed.");

        var swarm = _catalog.GetCluster("swarm");
        if (swarm.IsFail)
            return Result.Fail<FailoverTestReport>(swarm.Error!);
        var managers = swarm.Value!.Nodes
            .Where(n => n.Name.StartsWith("swarm-manager-", StringComparison.Ordinal))
            .ToList();
        if (managers.Count < 2)
            return Result.Fail<FailoverTestReport>("expected at least 2 swarm managers in vms.yaml; got " + managers.Count);

        // 1. Discover Swarm raft leader via SSH + docker node ls.
        var leaderProbe = await TryGetSwarmLeaderAsync(managers, cancellationToken).ConfigureAwait(false);
        if (leaderProbe.IsFail)
            return Result.Fail<FailoverTestReport>(leaderProbe.Error!);
        var leaderName = leaderProbe.Value!;
        var leaderNode = managers.FirstOrDefault(n => n.Name == leaderName);
        if (leaderNode is null)
            return Result.Fail<FailoverTestReport>(
                $"current Swarm leader '{leaderName}' not found in vms.yaml swarm.managers; refusing to act blind.");

        if (!string.IsNullOrEmpty(targetNode) && !string.Equals(targetNode, leaderName, StringComparison.Ordinal))
            return Result.Fail<FailoverTestReport>(
                $"--node was '{targetNode}' but current Swarm leader is '{leaderName}'. Rerun without --node " +
                "or wait for raft to elect that node leader.");

        // 2. Polling endpoint = a different (still-running) manager.
        var pollNode = managers.First(n => !string.Equals(n.Name, leaderName, StringComparison.Ordinal));
        var pollTarget = new SshTarget(pollNode.Vmnet11, 22, _sshUsername, _sshKeyPath);

        var preFlightCompleted = sw.Elapsed;

        // 3. Inject failure: vmrun suspend the leader's VM (HOST-LEVEL outage).
        var vmxPath = VmrunPaths.GetVmxPath(leaderNode.Dir, leaderNode.Name);
        var suspendResult = await _vmrun.SuspendAsync(vmxPath, cancellationToken).ConfigureAwait(false);
        if (suspendResult.IsFail)
            return Result.Fail<FailoverTestReport>($"vmrun suspend failed: {suspendResult.Error}");
        var failureInjected = sw.Elapsed;

        // 4. Poll non-leader (SSH+docker) until a new leader emerges.
        string? newLeaderName = null;
        var pollDeadline = failureInjected.Add(ElectionDeadline);
        while (sw.Elapsed < pollDeadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            var poll = await GetSwarmLeaderFromAsync(pollTarget, cancellationToken).ConfigureAwait(false);
            if (poll.IsOk && !string.IsNullOrEmpty(poll.Value)
                && !string.Equals(poll.Value, leaderName, StringComparison.Ordinal))
            {
                newLeaderName = poll.Value;
                break;
            }
        }
        var newLeaderObserved = sw.Elapsed;

        // 5. Auto-recovery: vmrun start nogui to resume the suspended VM.
        var resumeResult = await _vmrun.ResumeAsync(vmxPath, cancellationToken).ConfigureAwait(false);
        var recoveryAttempted = sw.Elapsed;
        var recovery = resumeResult.IsOk && resumeResult.Value
            ? FailoverRecoveryStatus.Recovered
            : FailoverRecoveryStatus.RecoveryFailed;
        var recoveryHint = recovery == FailoverRecoveryStatus.Recovered
            ? null
            : $"vmrun -T ws start \"{vmxPath}\" nogui";

        // 6. Wait for all 3 managers Ready (VM boot + docker re-join).
        //    Uses the longer VmRecoveryWaitDeadline because cold VM boot
        //    plus Docker engine startup + swarm rejoin is materially slower
        //    than a systemctl restart.
        var healthyDeadline = sw.Elapsed.Add(VmRecoveryWaitDeadline);
        while (sw.Elapsed < healthyDeadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var statuses = await GetSwarmManagerStatusesAsync(pollTarget, managers, cancellationToken).ConfigureAwait(false);
            if (statuses.IsOk && statuses.Value!.Count == managers.Count
                && statuses.Value.All(s => string.Equals(s, "Ready", StringComparison.Ordinal)))
                break;
        }
        var clusterHealthyAgain = sw.Elapsed;

        var rto = newLeaderObserved - failureInjected;
        var timeline = new FailoverTimeline(
            preFlightCompleted, failureInjected, newLeaderObserved, recoveryAttempted, clusterHealthyAgain);

        return Result.Ok(new FailoverTestReport(
            FailoverScenario.SwarmManager,
            startedAt,
            leaderName,
            newLeaderName,
            rto,
            recovery,
            recoveryHint,
            timeline));
    }

    /// <summary>Probes each manager over SSH+docker until one reports the current Swarm raft leader hostname.</summary>
    private async Task<Result<string>> TryGetSwarmLeaderAsync(
        IReadOnlyList<NodeRecord> managers,
        CancellationToken cancellationToken)
    {
        foreach (var m in managers)
        {
            var target = new SshTarget(m.Vmnet11, 22, _sshUsername, _sshKeyPath);
            var probe = await GetSwarmLeaderFromAsync(target, cancellationToken).ConfigureAwait(false);
            if (probe.IsOk && !string.IsNullOrEmpty(probe.Value))
                return probe;
        }
        return Result.Fail<string>("no manager returned a current Swarm raft leader; cluster may already be failing.");
    }

    /// <summary>
    /// Reads the Swarm leader hostname from one node via <c>docker node ls</c> (Swarm raft
    /// has no HTTP API). Returns an empty string (not a failure) when no node reports Leader
    /// yet, so callers can keep polling through an in-progress election.
    /// </summary>
    private async Task<Result<string>> GetSwarmLeaderFromAsync(
        SshTarget target,
        CancellationToken cancellationToken)
    {
        const string cmd = "docker node ls --format '{{.Hostname}}|{{.Status}}|{{.ManagerStatus}}'";
        var r = await _ssh.ExecuteAsync(target, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (r.IsFail)
            return Result.Fail<string>(r.Error!);
        if (r.Value!.ExitCode != 0)
            return Result.Fail<string>($"docker node ls returned exit {r.Value.ExitCode}: {r.Value.Stderr}");
        foreach (var rawLine in r.Value.Stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length < 3) continue;
            if (string.Equals(parts[2].Trim(), "Leader", StringComparison.Ordinal))
                return Result.Ok(parts[0].Trim());
        }
        return Result.Ok(string.Empty); // no leader yet (election in progress)
    }

    /// <summary>Reads the <c>docker node ls</c> Status column for each manager node (used to confirm all managers are Ready again post-recovery).</summary>
    private async Task<Result<IReadOnlyList<string>>> GetSwarmManagerStatusesAsync(
        SshTarget pollTarget,
        List<NodeRecord> managers,
        CancellationToken cancellationToken)
    {
        const string cmd = "docker node ls --format '{{.Hostname}}|{{.Status}}|{{.ManagerStatus}}'";
        var r = await _ssh.ExecuteAsync(pollTarget, cmd, SshTimeout, cancellationToken).ConfigureAwait(false);
        if (r.IsFail)
            return Result.Fail<IReadOnlyList<string>>(r.Error!);
        if (r.Value!.ExitCode != 0)
            return Result.Fail<IReadOnlyList<string>>($"docker node ls returned exit {r.Value.ExitCode}: {r.Value.Stderr}");
        var managerNames = managers.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var statuses = new List<string>(managers.Count);
        foreach (var rawLine in r.Value.Stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            var parts = line.Split('|');
            if (parts.Length < 3) continue;
            if (managerNames.Contains(parts[0].Trim()))
                statuses.Add(parts[1].Trim());
        }
        return Result.Ok<IReadOnlyList<string>>(statuses);
    }
}
