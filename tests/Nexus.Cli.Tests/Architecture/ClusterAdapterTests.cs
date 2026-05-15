using FluentAssertions;
using NetArchTest.Rules;
using Nexus.Cli.Core.Abstractions;
using Xunit;

namespace Nexus.Cli.Tests.Architecture;

/// <summary>
/// ADR-0009 constraints on the IClusterAdapter SPI:
///   1. Every concrete type ending with "Adapter" under
///      Nexus.Cli.Adapters.Cluster implements IClusterAdapter.
///   2. No adapter references a managed DB-driver type (StackExchange.Redis,
///      MongoDB.Driver, Npgsql, MySqlConnector, Microsoft.Data.SqlClient,
///      ClickHouse.Client). The SSH-shell-out invariant keeps AOT footprint
///      flat per ADR-0008's pattern; linking a managed driver would explode
///      the v0.6 binary past the 30 MB gate.
/// </summary>
public class ClusterAdapterTests
{
    private static readonly System.Reflection.Assembly AdaptersAssembly =
        typeof(Nexus.Cli.Adapters.Cluster.RedisAdapter).Assembly;

    private static readonly string[] BannedDriverNamespaces =
    [
        "StackExchange.Redis",
        "MongoDB.Driver",
        "MongoDB.Bson",
        "Npgsql",
        "MySqlConnector",
        "MySql.Data",
        "Microsoft.Data.SqlClient",
        "System.Data.SqlClient",
        "ClickHouse.Client",
        "ClickHouse.Ado",
    ];

    [Fact]
    public void Every_Concrete_Adapter_Implements_IClusterAdapter()
    {
        var result = Types.InAssembly(AdaptersAssembly)
            .That()
            .ResideInNamespace("Nexus.Cli.Adapters.Cluster")
            .And()
            .HaveNameEndingWith("Adapter")
            .And()
            .AreClasses()
            .And()
            .AreNotAbstract()
            .Should()
            .ImplementInterface(typeof(IClusterAdapter))
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "every concrete *Adapter under Nexus.Cli.Adapters.Cluster must implement IClusterAdapter (ADR-0009). Offenders: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Cluster_Adapters_Do_Not_Reference_Managed_DB_Drivers()
    {
        var result = Types.InAssembly(AdaptersAssembly)
            .That()
            .ResideInNamespace("Nexus.Cli.Adapters.Cluster")
            .ShouldNot()
            .HaveDependencyOnAny(BannedDriverNamespaces)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "cluster adapters must NOT link managed DB-driver types (ADR-0009 SSH-shell-out invariant; "
            + "linking one explodes the AOT binary past the 30 MB gate). Offenders: "
            + string.Join(", ", result.FailingTypeNames ?? []));
    }
}
