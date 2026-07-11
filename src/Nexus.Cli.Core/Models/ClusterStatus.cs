namespace Nexus.Cli.Core.Models;

/// <summary>Rolled-up traffic-light health for a cluster or component.</summary>
public enum HealthLevel
{
    /// <summary>Healthy; all probes within thresholds.</summary>
    Green = 0,

    /// <summary>Degraded; at least one probe is warning but service continues.</summary>
    Yellow = 1,

    /// <summary>Unhealthy; a critical probe failed.</summary>
    Red = 2
}

/// <summary>Aggregate cluster-status snapshot combining Consul, Nomad and Portainer health.</summary>
/// <param name="Overall">Worst-of rollup across the three component results.</param>
/// <param name="Consul">Consul membership/leader health, or the fetch error.</param>
/// <param name="Nomad">Nomad server/client health, or the fetch error.</param>
/// <param name="Portainer">Portainer reachability/version, or the fetch error.</param>
/// <param name="CapturedAtUtc">Instant the snapshot was assembled.</param>
/// <param name="Timings">Optional per-component fetch latencies; populated under verbose mode.</param>
public sealed record ClusterStatusReport(
    HealthLevel Overall,
    Result<ConsulHealth> Consul,
    Result<NomadHealth> Nomad,
    Result<PortainerStatus> Portainer,
    DateTimeOffset CapturedAtUtc,
    ComponentTimings? Timings = null);

/// <summary>Per-component fetch latency (ms) for the cluster-status rollup; surfaced by `-v|--verbose`.</summary>
/// <param name="ConsulMs">Milliseconds spent fetching Consul health.</param>
/// <param name="NomadMs">Milliseconds spent fetching Nomad health.</param>
/// <param name="PortainerMs">Milliseconds spent fetching Portainer status.</param>
public sealed record ComponentTimings(double ConsulMs, double NomadMs, double PortainerMs);
