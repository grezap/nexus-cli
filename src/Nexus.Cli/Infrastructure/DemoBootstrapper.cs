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
    /// <summary>Builds the JSON-backed demo catalog (discovered via <c>NEXUS_DEMOS_PATH</c> or <c>./docs/demos/</c>).</summary>
    public static IDemoCatalog BuildCatalog() => new JsonDemoCatalog();

    /// <summary>Builds the demo runner, backed by a <c>vhs</c> subprocess client for recording.</summary>
    public static IDemoRunner BuildRunner() => new DemoRunner(new VhsProcessClient());

    /// <inheritdoc />
    public void Dispose() { }
}
