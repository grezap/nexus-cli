using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Opt-in capability for clusters that expose a declarative, atomic
/// high-availability recovery step beyond the generic <see cref="IClusterAdapter"/>
/// surface (nexus-cli v0.8.1, ADR-0022). The only implementor is
/// <c>VaultAdapter</c> (in Nexus.Cli.Adapters — not referenced from Core): the foundation Vault
/// trust root sits at the bottom of the auto-unseal chain (vault-transit is
/// Shamir-only), so a build-host reboot leaves the HA nodes failed until the
/// transit node is unsealed. <c>recover-ha</c> wraps the
/// <c>scripts/recover-vault-ha.ps1</c> boot-race recovery as a first-class verb:
/// unseal vault-transit from the operator's Shamir key file, restart vault-1/2/3,
/// poll until unsealed. It is the ONLY exposed unseal path -- raw
/// <c>vault operator unseal</c> is never surfaced.
/// <para>
/// The verb dispatcher (RecoverHaCommand) checks <c>adapter is
/// IRecoverableCluster</c> and returns a graceful actionable N/A for any cluster
/// that does not implement it, so adding the capability never forces the other
/// adapters to grow a method.
/// </para>
/// </summary>
public interface IRecoverableCluster
{
    /// <summary>Runs the cluster's atomic HA recovery step (the <c>recover-ha</c> verb).</summary>
    Task<Result<RecoverHaResult>> RecoverHaAsync(CancellationToken cancellationToken);
}
