using System.Diagnostics.CodeAnalysis;
using Nexus.Cli.Commands;

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
    [DynamicDependency(SettingsRoots, typeof(InfrastructureSettings))]
    [DynamicDependency(SettingsRoots, typeof(FailoverTestSettings))]
    [DynamicDependency(SettingsRoots, typeof(KafkaFailoverSettings))]
    [DynamicDependency(SettingsRoots, typeof(DemoRunSettings))]
    [DynamicDependency(SettingsRoots, typeof(DemoRecordSettings))]
    public static void KeepAlive() { }
}
