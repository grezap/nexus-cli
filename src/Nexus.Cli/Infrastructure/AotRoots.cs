using System.Diagnostics.CodeAnalysis;
using Nexus.Cli.Commands;
using Nexus.Cli.Commands.Cluster;
using Nexus.Cli.Commands.Demo;
using Nexus.Cli.Commands.FailoverTest;
using Nexus.Cli.Commands.Infrastructure;
using Nexus.Cli.Commands.KafkaFailover;

namespace Nexus.Cli.Infrastructure;

/// <summary>
/// AOT-rooting attribute carrier. Spectre.Console.Cli reflects on each
/// Command's TSettings generic argument at boot to discover options/arguments;
/// without explicit roots the trimmer drops Settings ctors + properties and
/// the binary fails at runtime with "Could not get settings type for command".
/// Centralised here so every Command class stays free of clutter.
/// </summary>
internal static class AotRoots
{
    private const DynamicallyAccessedMemberTypes CommandRoots =
        DynamicallyAccessedMemberTypes.PublicConstructors;

    private const DynamicallyAccessedMemberTypes SettingsRoots =
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties;

    /// <summary>
    /// No-op anchor whose sole purpose is to carry the <see cref="DynamicDependencyAttribute"/>
    /// roots attached to it. Called once from <c>Program.Main</c> so the trimmer treats every
    /// referenced Command + Settings type (and their ctors/properties) as reachable and does
    /// not strip the metadata Spectre.Console.Cli reflects on at boot.
    /// </summary>
    [DynamicDependency(CommandRoots, typeof(ClusterStatusCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterStatusSettings))]

    [DynamicDependency(CommandRoots, typeof(InfrastructureListCommand))]
    [DynamicDependency(SettingsRoots, typeof(InfrastructureListSettings))]
    [DynamicDependency(CommandRoots, typeof(InfrastructureStatusCommand))]
    [DynamicDependency(SettingsRoots, typeof(InfrastructureStatusSettings))]
    [DynamicDependency(CommandRoots, typeof(InfrastructureSuspendCommand))]
    [DynamicDependency(SettingsRoots, typeof(InfrastructureSuspendSettings))]
    [DynamicDependency(CommandRoots, typeof(InfrastructureResumeCommand))]
    [DynamicDependency(SettingsRoots, typeof(InfrastructureResumeSettings))]

    [DynamicDependency(CommandRoots, typeof(FailoverTestConsulLeaderCommand))]
    [DynamicDependency(SettingsRoots, typeof(FailoverTestConsulLeaderSettings))]
    [DynamicDependency(CommandRoots, typeof(FailoverTestNomadLeaderCommand))]
    [DynamicDependency(SettingsRoots, typeof(FailoverTestNomadLeaderSettings))]
    [DynamicDependency(CommandRoots, typeof(FailoverTestSwarmManagerCommand))]
    [DynamicDependency(SettingsRoots, typeof(FailoverTestSwarmManagerSettings))]

    [DynamicDependency(CommandRoots, typeof(DemoListCommand))]
    [DynamicDependency(SettingsRoots, typeof(DemoListSettings))]
    [DynamicDependency(CommandRoots, typeof(DemoRunCommand))]
    [DynamicDependency(SettingsRoots, typeof(DemoRunSettings))]
    [DynamicDependency(CommandRoots, typeof(DemoRecordCommand))]
    [DynamicDependency(SettingsRoots, typeof(DemoRecordSettings))]

    [DynamicDependency(CommandRoots, typeof(KafkaFailoverEastToWestCommand))]
    [DynamicDependency(SettingsRoots, typeof(KafkaFailoverEastToWestSettings))]
    [DynamicDependency(CommandRoots, typeof(KafkaFailoverWestToEastCommand))]
    [DynamicDependency(SettingsRoots, typeof(KafkaFailoverWestToEastSettings))]

    // v0.6 cluster verbs (ADR-0009 IClusterAdapter SPI)
    [DynamicDependency(CommandRoots, typeof(ClusterStatusForClusterCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterStatusForClusterSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterFailoverTestCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterFailoverTestSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterScaleOutAddCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterScaleOutAddSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterScaleOutRemoveCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterScaleOutRemoveSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterScaleUpCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterScaleUpSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterBackupTakeCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterBackupTakeSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterBackupRestoreCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterBackupRestoreSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterHealthCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterHealthSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterTopologyCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterTopologySettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterCertRotateCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterCertRotateSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterChaosCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterChaosSettings))]
    [DynamicDependency(CommandRoots, typeof(ClusterAclCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterAclSettings))]

    // v0.8.1 recover-ha (IRecoverableCluster; foundation vault)
    [DynamicDependency(CommandRoots, typeof(RecoverHaCommand))]
    [DynamicDependency(SettingsRoots, typeof(ClusterRecoverHaSettings))]
    public static void KeepAlive() { }
}
