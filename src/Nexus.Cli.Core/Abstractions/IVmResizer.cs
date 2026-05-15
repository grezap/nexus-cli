using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Generic vertical-resize verb implementation (the <c>scale-up</c> verb
/// per <c>feedback_cli_verb_terminology.md</c> -- vertical = CPU/RAM/disk
/// on an existing VM; horizontal = <c>scale-out</c> add/remove cluster
/// node, which lives on the per-cluster IClusterAdapter).
/// <para>
/// Cluster-aware: before any resize, the resizer queries the
/// IClusterRegistry to find the owning adapter and calls
/// <see cref="IClusterAdapter.CanResizeVm(string, string)"/>. If that
/// returns false (typically because the VM is a current primary), the
/// resize is refused unless <see cref="ScaleUpRequest.ForcePrimary"/> is
/// true.
/// </para>
/// </summary>
public interface IVmResizer
{
    Task<Result<ScaleUpResult>> ScaleUpAsync(
        ScaleUpRequest request,
        CancellationToken cancellationToken);
}
