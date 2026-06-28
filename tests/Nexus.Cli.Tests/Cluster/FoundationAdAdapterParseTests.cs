using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Xunit;

namespace Nexus.Cli.Tests.Cluster;

/// <summary>
/// Parser-contract tests for <see cref="FoundationAdAdapter"/> (Phase 0.C/0.M,
/// nexus-cli v0.8.1). Fixtures mirror the pipe-delimited PowerShell output the
/// adapter emits over Windows-SSH (Get-ADDomainController /
/// Get-ADReplicationPartnerMetadata / Get-ADUser), captured during the v0.8.1
/// contract probe 2026-06-18 (nexus.lab, Windows Server 2025, 2 DCs).
/// </summary>
public class FoundationAdAdapterParseTests
{
    // === ParseDcLines (Get-ADDomainController) =============================
    private const string DcFixture =
        "DC-NEXUS|192.168.70.240|True|PDCEmulator,RIDMaster,InfrastructureMaster,SchemaMaster,DomainNamingMaster\n"
        + "DC-NEXUS-2|192.168.70.242|True|\n"
        + "PDC|dc-nexus.nexus.lab";   // trailing PDC line is ignored by ParseDcLines

    [Fact]
    public void ParseDcLines_maps_dc_name_ip_gc_and_roles()
    {
        var dcs = FoundationAdAdapter.ParseDcLines(DcFixture);
        dcs.Should().HaveCount(2);     // the trailing "PDC|..." line has <3 fields and is correctly skipped

        var dc1 = dcs.Single(d => d.Name == "DC-NEXUS");
        dc1.Ip.Should().Be("192.168.70.240");
        dc1.IsGlobalCatalog.Should().BeTrue();
        dc1.FsmoRoles.Should().HaveCount(5);

        var dc2 = dcs.Single(d => d.Name == "DC-NEXUS-2");
        dc2.IsGlobalCatalog.Should().BeTrue();
        dc2.FsmoRoles.Should().BeEmpty();
    }

    [Fact]
    public void ParseDcLines_empty_on_noise()
    {
        FoundationAdAdapter.ParseDcLines("").Should().BeEmpty();
        FoundationAdAdapter.ParseDcLines("just one field\n").Should().BeEmpty();
    }

    // === ParseReplMetadata (Get-ADReplicationPartnerMetadata) ==============

    [Theory]
    [InlineData("0|0", 0, 0)]
    [InlineData("8453|3", 8453, 3)]
    public void ParseReplMetadata_reads_result_and_failures(string line, int result, int failures)
    {
        var m = FoundationAdAdapter.ParseReplMetadata(line);
        m.Should().NotBeNull();
        m!.Value.Result.Should().Be(result);
        m.Value.Failures.Should().Be(failures);
    }

    [Fact]
    public void ParseReplMetadata_null_on_no_partner_or_noise()
    {
        FoundationAdAdapter.ParseReplMetadata("NO_PARTNER").Should().BeNull();
        FoundationAdAdapter.ParseReplMetadata("").Should().BeNull();
    }

    // === ParseAclUser (Get-ADUser describe) ================================

    [Fact]
    public void ParseAclUser_reads_sam_enabled_and_groups()
    {
        var u = FoundationAdAdapter.ParseAclUser("svc-vault-ldap|True|nexus-vault-readers,Domain Users");
        u.Should().NotBeNull();
        u!.Name.Should().Be("svc-vault-ldap");
        u.Enabled.Should().BeTrue();
        u.Permissions.Should().Contain("nexus-vault-readers");
        u.Permissions.Should().Contain("Domain Users");
    }

    [Fact]
    public void ParseAclUser_handles_no_groups()
    {
        var u = FoundationAdAdapter.ParseAclUser("lonely-user|False|");
        u.Should().NotBeNull();
        u!.Enabled.Should().BeFalse();
        u.Permissions.Should().ContainSingle().Which.Should().Be("(no groups)");
    }

    [Fact]
    public void ParseAclUser_null_on_sentinel_or_empty()
    {
        FoundationAdAdapter.ParseAclUser("NO_USER").Should().BeNull();
        FoundationAdAdapter.ParseAclUser("").Should().BeNull();
    }

    // === ParseIfmResult (ntdsutil ifm create full) =========================

    [Fact]
    public void ParseIfmResult_reads_size_and_path()
    {
        var r = FoundationAdAdapter.ParseIfmResult(
            "IFM media created successfully\nIFM_OK|100663296|C:\\nexus-backups\\ad\\ad-ifm-20260628-200000\\Active Directory\\ntds.dit");
        r.Should().NotBeNull();
        r!.Value.Size.Should().Be(100663296);
        r.Value.Path.Should().EndWith("ntds.dit");
    }

    [Fact]
    public void ParseIfmResult_null_on_error_or_noise()
    {
        FoundationAdAdapter.ParseIfmResult("IFM_ERR").Should().BeNull();
        FoundationAdAdapter.ParseIfmResult("").Should().BeNull();
        FoundationAdAdapter.ParseIfmResult("IFM_OK|notanumber|x").Should().BeNull();
    }

    // === Sanitize (operator tag -> backup-id slug) =========================

    [Theory]
    [InlineData("nightly", "nightly")]
    [InlineData("pre upgrade 2026", "preupgrade2026")]
    [InlineData("weekly_full-1", "weekly_full-1")]
    [InlineData("../../etc", "etc")]
    public void Sanitize_keeps_only_id_safe_chars(string input, string expected)
        => FoundationAdAdapter.Sanitize(input).Should().Be(expected);
}
