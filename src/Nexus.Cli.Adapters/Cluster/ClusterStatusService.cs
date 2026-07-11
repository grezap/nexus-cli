using System.Diagnostics;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Aggregates the Swarm/Nomad orchestration tier's health by fanning out to the
/// Consul, Nomad, and Portainer HTTP clients in parallel and folding their results
/// into a single <see cref="ClusterStatusReport"/> (with per-component latencies).
/// </summary>
public sealed class ClusterStatusService : IClusterStatusService
{
    private readonly INexusConsulClient _consul;
    private readonly INexusNomadClient _nomad;
    private readonly INexusPortainerClient _portainer;

    /// <summary>Constructs the service over the three orchestration-tier HTTP clients.</summary>
    public ClusterStatusService(
        INexusConsulClient consul,
        INexusNomadClient nomad,
        INexusPortainerClient portainer)
    {
        _consul = consul;
        _nomad = nomad;
        _portainer = portainer;
    }

    /// <inheritdoc />
    public async Task<ClusterStatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        var consulTask = TimedAsync(_consul.GetHealthAsync(cancellationToken));
        var nomadTask = TimedAsync(_nomad.GetHealthAsync(cancellationToken));
        var portainerTask = TimedAsync(_portainer.GetStatusAsync(cancellationToken));

        await Task.WhenAll(consulTask, nomadTask, portainerTask).ConfigureAwait(false);

        var (consul, consulMs) = consulTask.Result;
        var (nomad, nomadMs) = nomadTask.Result;
        var (portainer, portainerMs) = portainerTask.Result;

        var overall = ComputeOverall(consul, nomad, portainer);

        return new ClusterStatusReport(
            Overall: overall,
            Consul: consul,
            Nomad: nomad,
            Portainer: portainer,
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Timings: new ComponentTimings(consulMs, nomadMs, portainerMs));
    }

    /// <summary>Await a component fetch and record its wall-clock latency (ms).</summary>
    private static async Task<(T Result, double Ms)> TimedAsync<T>(Task<T> task)
    {
        var start = Stopwatch.GetTimestamp();
        var r = await task.ConfigureAwait(false);
        return (r, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>
    /// Rolls the three component results into one level: any failed fetch is Red;
    /// a reachable-but-degraded component (lost Consul leader / &lt;6 alive, no single
    /// Nomad leader or a non-ready client, unreachable Portainer) is Yellow; else Green.
    /// </summary>
    private static HealthLevel ComputeOverall(
        Result<ConsulHealth> consul,
        Result<NomadHealth> nomad,
        Result<PortainerStatus> portainer)
    {
        if (consul.IsFail || nomad.IsFail || portainer.IsFail) return HealthLevel.Red;

        bool consulYellow = consul.Value!.Failed > 0
            || consul.Value!.Alive < 6
            || string.IsNullOrEmpty(consul.Value!.Leader);

        bool nomadYellow = nomad.Value!.Servers.Count(s => s.IsLeader) != 1
            || nomad.Value!.Clients.Any(c => !string.Equals(c.Status, "ready", StringComparison.OrdinalIgnoreCase));

        bool portainerYellow = !portainer.Value!.Reachable;

        return (consulYellow || nomadYellow || portainerYellow) ? HealthLevel.Yellow : HealthLevel.Green;
    }
}
