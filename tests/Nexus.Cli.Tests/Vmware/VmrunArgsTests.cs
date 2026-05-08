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
    {
        // Windows-style dir literal but cross-platform expectation: Path.Combine
        // uses '/' on Linux (the CI runner) and '\\' on Windows. The production
        // code is only ever called with this output passed back to vmrun.exe on
        // Windows, but the assertion has to match Path.Combine's joiner on the
        // test runner's OS.
        const string dir = @"H:\VMS\NexusPlatform\01-foundation\vault-3";
        VmrunPaths.GetVmxPath(dir, "vault-3").Should().Be(Path.Combine(dir, "vault-3.vmx"));
    }

    [Fact]
    public void GetVmssSidecar_Replaces_Vmx_Extension()
        => VmrunPaths.GetVmssSidecar(@"H:\VMS\foo\bar.vmx")
            .Should().Be(@"H:\VMS\foo\bar.vmss");

    [Fact]
    public void GetVmemSidecar_Replaces_Vmx_Extension()
        => VmrunPaths.GetVmemSidecar(@"H:\VMS\foo\bar.vmx")
            .Should().Be(@"H:\VMS\foo\bar.vmem");

    [Fact]
    public void HasSuspendedStateSidecar_Detects_Suffixed_And_Unsuffixed_Vmss_Or_Vmem()
    {
        // Each VM lives in its own subdir per the vmware_per_vm_folders canon,
        // so the directory-prefix search is bounded.
        var root = Path.Combine(Path.GetTempPath(), $"nexus-suspend-{Guid.NewGuid():N}");
        try
        {
            // 1) bare .vmx, no sidecars: stopped
            var d1 = Path.Combine(root, "stopped"); Directory.CreateDirectory(d1);
            var vmx1 = Path.Combine(d1, "stopped.vmx"); File.WriteAllText(vmx1, "");
            VmrunPaths.HasSuspendedStateSidecar(vmx1).Should().BeFalse();

            // 2) un-suffixed .vmss next to .vmx (older Workstation)
            var d2 = Path.Combine(root, "vmss-bare"); Directory.CreateDirectory(d2);
            var vmx2 = Path.Combine(d2, "vmss-bare.vmx"); File.WriteAllText(vmx2, "");
            File.WriteAllText(Path.Combine(d2, "vmss-bare.vmss"), "");
            VmrunPaths.HasSuspendedStateSidecar(vmx2).Should().BeTrue();

            // 3) un-suffixed .vmem (older Workstation)
            var d3 = Path.Combine(root, "vmem-bare"); Directory.CreateDirectory(d3);
            var vmx3 = Path.Combine(d3, "vmem-bare.vmx"); File.WriteAllText(vmx3, "");
            File.WriteAllText(Path.Combine(d3, "vmem-bare.vmem"), "");
            VmrunPaths.HasSuspendedStateSidecar(vmx3).Should().BeTrue();

            // 4) session-suffixed .vmem (Workstation Pro 17.5+, the real-world case)
            var d4 = Path.Combine(root, "session"); Directory.CreateDirectory(d4);
            var vmx4 = Path.Combine(d4, "vault-3.vmx"); File.WriteAllText(vmx4, "");
            File.WriteAllText(Path.Combine(d4, "vault-3-3c85c1f6.vmem"), "");
            VmrunPaths.HasSuspendedStateSidecar(vmx4).Should().BeTrue();

            // 5) session-suffixed .vmss
            var d5 = Path.Combine(root, "session-vmss"); Directory.CreateDirectory(d5);
            var vmx5 = Path.Combine(d5, "node.vmx"); File.WriteAllText(vmx5, "");
            File.WriteAllText(Path.Combine(d5, "node-deadbeef.vmss"), "");
            VmrunPaths.HasSuspendedStateSidecar(vmx5).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
