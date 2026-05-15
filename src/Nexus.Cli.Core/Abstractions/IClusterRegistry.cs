namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Lookup for cluster adapters by ClusterId. Populated at DI bootstrap; the
/// CLI commands dispatch by user-supplied id (e.g. <c>nexus failover-test
/// redis</c> resolves the "redis" adapter and calls its FailoverAsync).
/// </summary>
public interface IClusterRegistry
{
    /// <summary>
    /// Look up an adapter by id. Returns Fail with a friendly "unknown
    /// cluster" + the list of known ids when there's no match.
    /// </summary>
    Result<IClusterAdapter> GetAdapter(string clusterId);

    /// <summary>All registered cluster ids, ordered alphabetically.</summary>
    IReadOnlyList<string> Ids { get; }
}
