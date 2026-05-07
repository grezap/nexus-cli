namespace Nexus.Cli.Core.Models;

public enum HealthLevel
{
    Green = 0,
    Yellow = 1,
    Red = 2
}

public sealed record ClusterStatusReport(
    HealthLevel Overall,
    Result<ConsulHealth> Consul,
    Result<NomadHealth> Nomad,
    Result<PortainerStatus> Portainer,
    DateTimeOffset CapturedAtUtc);
