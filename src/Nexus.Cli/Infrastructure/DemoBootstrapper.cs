using Nexus.Cli.Adapters.Demos;
using Nexus.Cli.Adapters.Vhs;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// DI bootstrap for the v0.4 demo verb. No Vault dependency: demos run local
/// shell commands and recording is purely a vhs subprocess. The
/// JsonDemoCatalog discovers demos via NEXUS_DEMOS_PATH or sibling
/// ./docs/demos/.
/// </summary>
public sealed class DemoBootstrapper : IDisposable
{
    public static IDemoCatalog BuildCatalog() => new JsonDemoCatalog();

    public static IDemoRunner BuildRunner() => new DemoRunner(new VhsProcessClient());

    public void Dispose() { }
}
