using System.Diagnostics;
using Nexus.Cli.Adapters.Consul;
using Nexus.Cli.Adapters.Http;
using Nexus.Cli.Adapters.Nomad;
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
    private readonly NexusHttpClientFactory _httpFactory;
    private readonly string _consulMgmtToken;
    private readonly string _nomadMgmtToken;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;

    // Tunables. Kept conservative for v0.3.0; --options to override are a v0.3.x task.
    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ElectionDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecoveryWaitDeadline = TimeSpan.FromSeconds(45);

    public FailoverTestService(
        IVmsCatalog catalog,
        ISshClient ssh,
        NexusHttpClientFactory httpFactory,
        string consulMgmtToken,
        string nomadMgmtToken,
        string sshUsername,
        string sshKeyPath)
    {
        _catalog = catalog;
        _ssh = ssh;
        _httpFactory = httpFactory;
        _consulMgmtToken = consulMgmtToken;
        _nomadMgmtToken = nomadMgmtToken;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
    }

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

    private ConsulClient MakeConsul(string ip) =>
        new(new ConsulClient.Settings($"https://{ip}:8501", _consulMgmtToken), _httpFactory);

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

    private static string? MapToNodeName(string rpcAddr, IEnumerable<NodeRecord> managers)
    {
        var ip = rpcAddr.Split(':')[0];
        return managers.FirstOrDefault(n => n.Vmnet10 == ip)?.Name;
    }

    // ===== Nomad failover (v0.3.1) =========================================

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

    private NomadClient MakeNomad(string ip) =>
        new(new NomadClient.Settings($"https://{ip}:4646", _nomadMgmtToken), _httpFactory);

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
}
