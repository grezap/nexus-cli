using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Loads the catalog of packaged demo specifications (the System A/B playbooks)
/// keyed by demo id.
/// </summary>
public interface IDemoCatalog
{
    /// <summary>Loads all demo specs, keyed by id.</summary>
    Result<IReadOnlyDictionary<string, DemoSpec>> Load();

    /// <summary>Resolves a single demo spec by <paramref name="id"/>, failing when unknown.</summary>
    Result<DemoSpec> GetDemo(string id);
}
