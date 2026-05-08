using FluentAssertions;
using Nexus.Cli.Adapters.Vmware;
using Xunit;

namespace Nexus.Cli.Tests.Vmware;

public class VmrunArgsTests
{
    private const string SampleVmx = @"H:\VMS\NexusPlatform\01-foundation\vault-3\vault-3.vmx";

    [Fact]
    public void BuildListArgs_Has_Single_Verb()
        => VmrunProcessClient.BuildListArgs().Should().Equal("list");

    [Fact]
    public void BuildSuspendArgs_Passes_Vmx_Verbatim()
        => VmrunProcessClient.BuildSuspendArgs(SampleVmx).Should().Equal("suspend", SampleVmx);

    [Fact]
    public void BuildResumeArgs_Appends_Nogui()
        => VmrunProcessClient.BuildResumeArgs(SampleVmx).Should().Equal("start", SampleVmx, "nogui");

    [Fact]
    public void ParseRunningList_Empty_Stdout_Returns_Empty_Set()
        => VmrunProcessClient.ParseRunningList("").Should().BeEmpty();

    [Fact]
    public void ParseRunningList_Header_Only_Returns_Empty_Set()
        => VmrunProcessClient.ParseRunningList("Total running VMs: 0\n").Should().BeEmpty();

    [Fact]
    public void ParseRunningList_Skips_Header_And_Returns_All_Paths()
    {
        const string stdout = "Total running VMs: 2\r\n" +
                              @"H:\VMS\NexusPlatform\01-foundation\vault-1\vault-1.vmx" + "\r\n" +
                              @"H:\VMS\NexusPlatform\06-orchestration\swarm-manager-2\swarm-manager-2.vmx" + "\r\n";
        var set = VmrunProcessClient.ParseRunningList(stdout);
        set.Should().HaveCount(2);
        set.Should().Contain(@"H:\VMS\NexusPlatform\01-foundation\vault-1\vault-1.vmx");
        set.Should().Contain(@"H:\VMS\NexusPlatform\06-orchestration\swarm-manager-2\swarm-manager-2.vmx");
    }

    [Fact]
    public void ParseRunningList_Is_Case_Insensitive()
    {
        var set = VmrunProcessClient.ParseRunningList(@"Total running VMs: 1
H:\VMS\foo.vmx");
        set.Should().Contain(@"h:\vms\foo.vmx");
    }

    [Fact]
    public void GetVmxPath_Composes_Dir_And_Name()
        => VmrunPaths.GetVmxPath(@"H:\VMS\NexusPlatform\01-foundation\vault-3", "vault-3")
            .Should().Be(@"H:\VMS\NexusPlatform\01-foundation\vault-3\vault-3.vmx");

    [Fact]
    public void GetVmssSidecar_Replaces_Vmx_Extension()
        => VmrunPaths.GetVmssSidecar(@"H:\VMS\foo\bar.vmx")
            .Should().Be(@"H:\VMS\foo\bar.vmss");

    [Fact]
    public void UnavailableMessage_Mentions_PathEnvVar_On_Windows()
    {
        if (OperatingSystem.IsWindows())
            VmrunPaths.UnavailableMessage().Should().Contain(VmrunPaths.PathEnvVar);
        else
            VmrunPaths.UnavailableMessage().Should().Contain("Windows-only");
    }

    [Fact]
    public void Resolve_Honours_Env_Var_When_Path_Exists()
    {
        var prev = Environment.GetEnvironmentVariable(VmrunPaths.PathEnvVar);
        var tmp = Path.Combine(Path.GetTempPath(), $"fake-vmrun-{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "");
        try
        {
            Environment.SetEnvironmentVariable(VmrunPaths.PathEnvVar, tmp);
            VmrunPaths.Resolve().Should().Be(tmp);
            VmrunPaths.IsAvailable().Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(VmrunPaths.PathEnvVar, prev);
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Resolve_Ignores_Env_Var_When_Path_Missing()
    {
        var prev = Environment.GetEnvironmentVariable(VmrunPaths.PathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(
                VmrunPaths.PathEnvVar,
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe"));
            // Falls through to OS defaults; on a Windows build host with vmrun installed
            // this returns the canonical path, on bare-Linux it returns null.
            // Either way: the env-var-points-at-missing branch must NOT throw.
            var resolved = VmrunPaths.Resolve();
            (resolved is null || File.Exists(resolved)).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(VmrunPaths.PathEnvVar, prev);
        }
    }
}
