using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Drives control-plane failover tests — deposes the current leader/manager and
/// verifies re-election — for the Consul, Nomad, and Swarm rafts.
/// </summary>
public interface IFailoverTestService
{
    /// <summary>Fails over the Consul raft leader (optionally the named <paramref name="targetNode"/>) and verifies re-election.</summary>
    Task<Result<FailoverTestReport>> RunConsulLeaderAsync(
        string? targetNode,
        CancellationToken cancellationToken);

    /// <summary>Fails over the Nomad raft leader (optionally the named <paramref name="targetNode"/>) and verifies re-election.</summary>
    Task<Result<FailoverTestReport>> RunNomadLeaderAsync(
        string? targetNode,
        CancellationToken cancellationToken);

    /// <summary>Fails over the Swarm manager (optionally the named <paramref name="targetNode"/>) and verifies re-election.</summary>
    Task<Result<FailoverTestReport>> RunSwarmManagerAsync(
        string? targetNode,
        CancellationToken cancellationToken);
}
