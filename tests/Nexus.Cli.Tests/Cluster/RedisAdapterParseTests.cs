using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser/guard-contract tests for <see cref="RedisAdapter"/>. Covers the
/// <c>ACL LIST</c> parser and the ACL-token safety guard that protects the
/// <c>sudo bash -c '…'</c> wrapper from injection on acl grant/revoke (v0.8.6).
/// </summary>
public class RedisAdapterParseTests
{
    // === ParseAclList =======================================================
    private const string AclListFixture = """
        user default on nopass ~* &* +@all
        user nexus-app on #a1b2 ~cache:* resetchannels +@read +@write
        user readonly off ~* resetchannels +@read
        """;

    [Fact]
    public void ParseAclList_reads_name_enabled_and_permissions()
    {
        var users = RedisAdapter.ParseAclList(AclListFixture);
        users.Should().HaveCount(3);
        users.Should().ContainSingle(u => u.Name == "default" && u.Enabled);
        var app = users.Single(u => u.Name == "nexus-app");
        app.Enabled.Should().BeTrue();
        app.Permissions.Should().Contain("+@read").And.Contain("+@write");
        users.Single(u => u.Name == "readonly").Enabled.Should().BeFalse();
    }

    // === IsSafeAclToken (acl grant/revoke injection guard) ==================
    [Theory]
    [InlineData("nexus-app")]
    [InlineData("on")]
    [InlineData(">s0me-P4ss")]          // password rule
    [InlineData("~cache:*")]            // key pattern
    [InlineData("+@read")]              // command category
    [InlineData("&channel.*")]          // pubsub pattern
    [InlineData("#a1b2c3d4")]           // hashed password
    [InlineData("allkeys")]
    public void IsSafeAclToken_accepts_redis_acl_rule_chars(string token) =>
        RedisAdapter.IsSafeAclToken(token).Should().BeTrue();

    [Theory]
    [InlineData("")]                    // empty
    [InlineData("foo'bar")]             // single quote — would break bash -c '…'
    [InlineData("foo bar")]             // whitespace — would split tokens
    [InlineData("foo;rm -rf")]          // semicolon
    [InlineData("$(whoami)")]           // command substitution
    [InlineData("foo`id`")]             // backtick
    [InlineData("foo\\nbar")]           // backslash
    [InlineData("foo\"bar")]            // double quote
    public void IsSafeAclToken_rejects_shell_breaking_tokens(string token) =>
        RedisAdapter.IsSafeAclToken(token).Should().BeFalse();
}
