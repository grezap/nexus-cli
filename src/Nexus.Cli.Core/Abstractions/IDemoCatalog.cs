using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface IDemoCatalog
{
    Result<IReadOnlyDictionary<string, DemoSpec>> Load();

    Result<DemoSpec> GetDemo(string id);
}
