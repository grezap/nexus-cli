using System.Diagnostics.CodeAnalysis;
using Nexus.Cli.Commands;
using Nexus.Cli.Commands.Demo;
using Nexus.Cli.Commands.FailoverTest;
using Nexus.Cli.Commands.Infrastructure;

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

    [DynamicDependency(SettingsRoots, typeof(KafkaFailoverSettings))]
    public static void KeepAlive() { }
}
