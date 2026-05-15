using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Dictionary-backed implementation of <see cref="IClusterRegistry"/>.
/// Populated at DI bootstrap (<see cref="Nexus.Cli.Infrastructure.ClusterBootstrapper"/>);
/// adapter lookups are O(1) by id (case-insensitive per the established
/// lookup-by-name convention used by JsonDemoCatalog + VmsCatalog).
/// </summary>
public sealed class ClusterRegistry : IClusterRegistry
{
    private readonly Dictionary<string, IClusterAdapter> _adapters;

    public ClusterRegistry(IEnumerable<IClusterAdapter> adapters)
    {
        _adapters = new Dictionary<string, IClusterAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in adapters)
        {
            if (string.IsNullOrWhiteSpace(a.ClusterId))
                throw new InvalidOperationException($"adapter {a.GetType().Name} has empty ClusterId");
            if (!_adapters.TryAdd(a.ClusterId, a))
            {
                var existing = _adapters[a.ClusterId];
                throw new InvalidOperationException(
                    $"duplicate ClusterId '{a.ClusterId}' (adapters: {existing.GetType().Name} vs {a.GetType().Name})");
            }
        }
        Ids = _adapters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Ids { get; }

    public Result<IClusterAdapter> GetAdapter(string clusterId)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
            return Result.Fail<IClusterAdapter>("cluster id is required");
        if (_adapters.TryGetValue(clusterId, out var a))
            return Result.Ok(a);
        var known = string.Join(", ", Ids);
        return Result.Fail<IClusterAdapter>(
            string.IsNullOrEmpty(known)
                ? "no cluster adapters registered"
                : $"unknown cluster '{clusterId}'. Known: {known}");
    }
}
