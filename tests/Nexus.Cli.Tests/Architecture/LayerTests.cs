using FluentAssertions;
using NetArchTest.Rules;
using Xunit;

namespace Nexus.Cli.Tests.Architecture;

public class LayerTests
{
    private static readonly System.Reflection.Assembly CoreAssembly =
        typeof(Nexus.Cli.Core.Result).Assembly;
    private static readonly System.Reflection.Assembly AdaptersAssembly =
        typeof(Nexus.Cli.Adapters.Vault.VaultTokenResolver).Assembly;
    private static readonly System.Reflection.Assembly CliAssembly =
        typeof(Nexus.Cli.Commands.ClusterStatusCommand).Assembly;

    private static readonly string[] CliHostOnlyNamespaces =
    [
        "Nexus.Cli.Commands",
        "Nexus.Cli.Infrastructure",
        "Spectre.Console.Cli"
    ];

    private static readonly string[] AdaptersNamespaces =
    [
        "Nexus.Cli.Adapters"
    ];

    [Fact]
    public void Core_Should_Not_Depend_On_Adapters()
    {
        var result = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(AdaptersNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Nexus.Cli.Core must remain a pure abstractions/domain layer; offending types: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Core_Should_Not_Depend_On_Cli_Host()
    {
        var result = Types.InAssembly(CoreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(CliHostOnlyNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Nexus.Cli.Core must not reach into the Cli host; offending types: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Adapters_Should_Not_Depend_On_Cli_Host()
    {
        var result = Types.InAssembly(AdaptersAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(CliHostOnlyNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Adapters must not reach into the Cli host; offending types: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Cli_Host_Is_The_Only_Aot_Publish_Root()
    {
        // Sanity: CliAssembly compiles; nobody else has Main.
        CliAssembly.EntryPoint.Should().NotBeNull();
        CoreAssembly.EntryPoint.Should().BeNull();
        AdaptersAssembly.EntryPoint.Should().BeNull();
    }
}
