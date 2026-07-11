using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Adapters.Inventory;
using Nexus.Cli.Adapters.Vmware;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// Wires the v0.2 'infrastructure' verb's dependencies. Mirrors
/// <see cref="NexusBootstrapper"/>'s on-demand pattern: nothing
/// is allocated until <see cref="BuildService"/> is called, and no
/// Vault token is required (vmrun.exe is local and the YAML catalog
/// is on disk).
/// </summary>
public sealed class InfrastructureBootstrapper : IDisposable
{
    private readonly IVmsCatalog _catalog;
    private readonly IVmrunClient _vmrun;

    /// <summary>Constructs the bootstrapper, allocating the on-disk YAML catalog + local vmrun client.</summary>
    public InfrastructureBootstrapper()
    {
        _catalog = new VmsYamlCatalog();
        _vmrun = new VmrunProcessClient();
    }

    /// <summary>Builds the <see cref="IInfrastructureService"/> backing the <c>infrastructure</c> verb.</summary>
    public IInfrastructureService BuildService()
        => new InfrastructureService(_catalog, _vmrun);

    /// <inheritdoc />
    public void Dispose() { }
}
