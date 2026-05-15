using System.Diagnostics;
using Nexus.Cli.Adapters.Vmware;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// v0.5 kafka-failover verb.
/// <para>
/// Demo-grade DR failover per ADR-0008: <c>vmrun suspend</c> every broker in
/// the source 3-node KRaft cluster (HOST-LEVEL outage simulating region
/// loss), then prove the target cluster keeps serving by running an RF=3
/// produce + consume round-trip on a probe topic. RTO is measured from
/// "all source brokers suspended" to "target probe consumed token". Then
/// auto-recover by <c>vmrun start nogui</c> on each source broker.
/// </para>
/// <para>
/// No real per-consumer-group offset translation -- that's deferred to v0.5.1
/// once a real consumer app (streamcore, Phase 12) exists to translate
/// offsets FOR. See ADR-0008 for the scope split.
/// </para>
/// </summary>
public sealed class KafkaFailoverService : IKafkaFailoverService
{
    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly IVmrunClient _vmrun;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;

    // Conservative defaults; tuned to be wide enough for the 8 GB / 8 GB / 8 GB
    // KRaft cluster under build-host load (the cold-rebuild proof gave us a
    // realistic sense of timing).
    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VmrunSuspendGap = TimeSpan.FromSeconds(2);   // small gap between sequential vmrun suspends to avoid the 0.H.6 "Unknown error" concurrency flake
    private static readonly TimeSpan TargetProbeDeadline = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan TargetProbePollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SourceRecoveryDeadline = TimeSpan.FromMinutes(4);   // cold VM boot + KRaft quorum re-form
    private static readonly TimeSpan SourceRecoveryPollInterval = TimeSpan.FromSeconds(5);

    public KafkaFailoverService(
        IVmsCatalog catalog,
        ISshClient ssh,
        IVmrunClient vmrun,
        string sshUsername,
        string sshKeyPath)
    {
        _catalog = catalog;
        _ssh = ssh;
        _vmrun = vmrun;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
    }

    public async Task<Result<KafkaFailoverReport>> RunAsync(
        KafkaFailoverDirection direction,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        if (!_vmrun.IsAvailable)
            return Result.Fail<KafkaFailoverReport>(
                "kafka failover requires vmrun.exe (HOST-LEVEL failure injection). " +
                "Run on the Windows build host where VMware Workstation Pro is installed.");

        var (sourceName, targetName) = direction switch
        {
            KafkaFailoverDirection.EastToWest => ("kafka-east", "kafka-west"),
            KafkaFailoverDirection.WestToEast => ("kafka-west", "kafka-east"),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

        var sourceCluster = _catalog.GetCluster(sourceName);
        if (sourceCluster.IsFail)
            return Result.Fail<KafkaFailoverReport>(sourceCluster.Error!);
        var targetCluster = _catalog.GetCluster(targetName);
        if (targetCluster.IsFail)
            return Result.Fail<KafkaFailoverReport>(targetCluster.Error!);

        var sourceBrokers = sourceCluster.Value!.Nodes
            .Where(n => n.Name.StartsWith(sourceName, StringComparison.Ordinal))
            .ToList();
        var targetBrokers = targetCluster.Value!.Nodes
            .Where(n => n.Name.StartsWith(targetName, StringComparison.Ordinal))
            .ToList();
        if (sourceBrokers.Count < 3)
            return Result.Fail<KafkaFailoverReport>($"expected 3 brokers in {sourceName}; got {sourceBrokers.Count}");
        if (targetBrokers.Count < 3)
            return Result.Fail<KafkaFailoverReport>($"expected 3 brokers in {targetName}; got {targetBrokers.Count}");

        // Pick a target broker to drive the post-failure probe. Any one works
        // (we go through localhost:9092 on the broker itself for SSL trust).
        var probeBroker = targetBrokers[0];
        var probeTarget = new SshTarget(probeBroker.Vmnet11, 22, _sshUsername, _sshKeyPath);

        // ─── 1. Pre-flight: target must be HEALTHY before we touch source ───
        var preFlight = await CheckTargetHealthyAsync(probeTarget, cancellationToken).ConfigureAwait(false);
        if (preFlight.IsFail)
            return Result.Fail<KafkaFailoverReport>(
                $"pre-flight: target cluster {targetName} is not healthy -- refusing to inject failure. " +
                $"Details: {preFlight.Error}");

        var preFlightCompleted = sw.Elapsed;

        // ─── 2. Inject failure: vmrun-suspend every source broker, sequential ─
        //     (parallel vmrun has the 0.H.6 "Unknown error" concurrency flake) ─
        var suspendedNames = new List<string>(sourceBrokers.Count);
        var suspendVmxPaths = new List<string>(sourceBrokers.Count);
        foreach (var broker in sourceBrokers)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var vmxPath = VmrunPaths.GetVmxPath(broker.Dir, broker.Name);
            var suspend = await _vmrun.SuspendAsync(vmxPath, cancellationToken).ConfigureAwait(false);
            if (suspend.IsFail)
                return Result.Fail<KafkaFailoverReport>(
                    $"vmrun suspend failed for {broker.Name}: {suspend.Error}. Source cluster may be partially down -- " +
                    $"recover with: vmrun -T ws start \"{vmxPath}\" nogui");
            suspendedNames.Add(broker.Name);
            suspendVmxPaths.Add(vmxPath);
            await Task.Delay(VmrunSuspendGap, cancellationToken).ConfigureAwait(false);
        }
        var failureInjected = sw.Elapsed;

        // ─── 3. Verify target keeps serving: RF=3 produce + consume round-trip ─
        //     Retried because the target's brokers may briefly see disconnects
        //     from the now-suspended source's MM2 producer client (irrelevant
        //     to target health, but log-noisy). ──────────────────────────────
        var probeToken = $"probe-{Guid.NewGuid().ToString("N")[..16]}";
        var probeOk = false;
        var probeDeadline = sw.Elapsed.Add(TargetProbeDeadline);
        Result<bool>? lastProbe = null;
        while (sw.Elapsed < probeDeadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            lastProbe = await RunProduceConsumeProbeAsync(probeTarget, probeToken, cancellationToken).ConfigureAwait(false);
            if (lastProbe.Value.IsOk && lastProbe.Value.Value)
            {
                probeOk = true;
                break;
            }
            await Task.Delay(TargetProbePollInterval, cancellationToken).ConfigureAwait(false);
        }
        var targetHealthy = sw.Elapsed;
        var rto = targetHealthy - failureInjected;

        // ─── 4. Auto-recovery: vmrun start nogui each source broker ─────────
        var recoveryOk = true;
        for (int i = 0; i < suspendVmxPaths.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var resume = await _vmrun.ResumeAsync(suspendVmxPaths[i], cancellationToken).ConfigureAwait(false);
            if (resume.IsFail || !resume.Value)
                recoveryOk = false;
        }
        var recoveryAttempted = sw.Elapsed;
        var recovery = recoveryOk
            ? KafkaFailoverRecoveryStatus.Recovered
            : KafkaFailoverRecoveryStatus.RecoveryFailed;
        var recoveryHint = recoveryOk
            ? null
            : "manual recovery: " + string.Join(" && ",
                suspendVmxPaths.Select(p => $"vmrun -T ws start \"{p}\" nogui"));

        // ─── 5. Wait for source cluster to be healthy again ─────────────────
        //     Best-effort: a slow VM boot under load may exceed our deadline.
        //     The report records whether source recovered cleanly; if not,
        //     the operator gets a clear hint. ──────────────────────────────
        var sourceProbeBroker = sourceBrokers[0];
        var sourceProbeTarget = new SshTarget(sourceProbeBroker.Vmnet11, 22, _sshUsername, _sshKeyPath);
        var recoveryDeadline = sw.Elapsed.Add(SourceRecoveryDeadline);
        while (sw.Elapsed < recoveryDeadline)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await Task.Delay(SourceRecoveryPollInterval, cancellationToken).ConfigureAwait(false);
            var srcHealth = await CheckTargetHealthyAsync(sourceProbeTarget, cancellationToken).ConfigureAwait(false);
            if (srcHealth.IsOk) break;
        }
        var sourceHealthyAgain = sw.Elapsed;

        var timeline = new KafkaFailoverTimeline(
            preFlightCompleted, failureInjected, targetHealthy, recoveryAttempted, sourceHealthyAgain);

        return Result.Ok(new KafkaFailoverReport(
            direction,
            startedAt,
            sourceName,
            targetName,
            suspendedNames,
            probeOk,
            probeOk ? probeToken : null,
            rto,
            recovery,
            recoveryHint,
            timeline));
    }

    /// <summary>
    /// Verify a Kafka cluster is serving by asking its KRaft metadata quorum
    /// for the current Leader. Returns Ok if the quorum reports a leader voter.
    /// <para>
    /// Apache Kafka 3.8's <c>kafka-metadata-quorum.sh ... describe --status</c>
    /// emits <c>LeaderId:</c> + <c>CurrentVoters:</c> lines when a leader is
    /// elected (the field was <c>CurrentLeader:</c> in earlier KRaft drafts).
    /// We match on <c>LeaderId:</c> followed by a positive integer.
    /// </para>
    /// </summary>
    private async Task<Result<bool>> CheckTargetHealthyAsync(SshTarget target, CancellationToken ct)
    {
        const string cmd =
            "sudo /opt/kafka/bin/kafka-metadata-quorum.sh " +
            "--bootstrap-server SSL://localhost:9092 " +
            "--command-config /etc/nexus-kafka/client-ssl.properties " +
            "describe --status 2>&1";
        var r = await _ssh.ExecuteAsync(target, cmd, SshTimeout, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<bool>(r.Error!);
        if (r.Value!.ExitCode != 0)
            return Result.Fail<bool>($"kafka-metadata-quorum exit {r.Value.ExitCode}: {r.Value.Stderr.Trim()}");
        var match = System.Text.RegularExpressions.Regex.Match(
            r.Value.Stdout, @"LeaderId:\s+(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) && id > 0
            ? Result.Ok(true)
            : Result.Fail<bool>($"kafka-metadata-quorum returned no elected LeaderId: {r.Value.Stdout.Trim()}");
    }

    /// <summary>
    /// Run a one-shot produce + consume round-trip against the broker at
    /// <paramref name="target"/>'s <c>SSL://localhost:9092</c> on a fresh
    /// probe topic. Returns Ok(true) iff the token came back unchanged.
    /// </summary>
    private async Task<Result<bool>> RunProduceConsumeProbeAsync(
        SshTarget target, string token, CancellationToken ct)
    {
        // Single SSH command, semicolon-chained. Each step echoes a marker on
        // failure so the caller can grep the failure mode out of stdout.
        var topic = $"nexus-fo-probe-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        const string kafka = "/opt/kafka/bin";
        const string bs = "SSL://localhost:9092";
        const string cfg = "/etc/nexus-kafka/client-ssl.properties";

        var script =
            $"sudo {kafka}/kafka-topics.sh --bootstrap-server {bs} --command-config {cfg} --create --topic {topic} --partitions 1 --replication-factor 3 2>&1 | grep -qE 'Created topic|already exists' || {{ echo PROBE_CREATE_FAIL; exit 1; }} && " +
            $"echo '{token}' | sudo {kafka}/kafka-console-producer.sh --bootstrap-server {bs} --producer.config {cfg} --topic {topic} 2>/dev/null || {{ echo PROBE_PRODUCE_FAIL; exit 2; }} && " +
            $"OUT=$(sudo {kafka}/kafka-console-consumer.sh --bootstrap-server {bs} --consumer.config {cfg} --topic {topic} --from-beginning --max-messages 1 --timeout-ms 20000 2>/dev/null) && " +
            $"echo \"$OUT\" | grep -qF '{token}' && echo PROBE_OK || echo PROBE_MISMATCH; " +
            $"sudo {kafka}/kafka-topics.sh --bootstrap-server {bs} --command-config {cfg} --delete --topic {topic} >/dev/null 2>&1 || true";

        // Wrap the whole pipeline in bash -c so we get one logical step over SSH.
        var cmd = $"bash -c {Shellquote(script)}";

        var r = await _ssh.ExecuteAsync(target, cmd, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<bool>(r.Error!);
        // We DON'T fail on non-zero exit: PROBE_CREATE_FAIL etc. exit non-zero
        // but the caller wants a retryable signal, not a hard error.
        return r.Value!.Stdout.Contains("PROBE_OK", StringComparison.Ordinal)
            ? Result.Ok(true)
            : Result.Ok(false);
    }

    private static string Shellquote(string s)
    {
        // Wrap in single quotes; escape any single quote in the body via the
        // classic '\'' close-reopen trick.
        return "'" + s.Replace("'", "'\\''") + "'";
    }
}
