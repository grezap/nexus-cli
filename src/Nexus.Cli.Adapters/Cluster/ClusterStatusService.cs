using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

public sealed class ClusterStatusService : IClusterStatusService
{
    private readonly INexusConsulClient _consul;
    private readonly INexusNomadClient _nomad;
    private readonly INexusPortainerClient _portainer;

    public ClusterStatusService(
        INexusConsulClient consul,
        INexusNomadClient nomad,
        INexusPortainerClient portainer)
    {
        _consul = consul;
        _nomad = nomad;
        _portainer = portainer;
    }

    public async Task<ClusterStatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        var consulTask = _consul.GetHealthAsync(cancellationToken);
        var nomadTask = _nomad.GetHealthAsync(cancellationToken);
        var portainerTask = _portainer.GetStatusAsync(cancellationToken);

        await Task.WhenAll(consulTask, nomadTask, portainerTask).ConfigureAwait(false);

        var consul = consulTask.Result;
        var nomad = nomadTask.Result;
        var portainer = portainerTask.Result;

        var overall = ComputeOverall(consul, nomad, portainer);

        return new ClusterStatusReport(
            Overall: overall,
            Consul: consul,
            Nomad: nomad,
            Portainer: portainer,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

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
