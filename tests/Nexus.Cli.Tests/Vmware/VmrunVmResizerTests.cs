using FluentAssertions;
using Nexus.Cli.Adapters.Vmware;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;
using Xunit;

namespace Nexus.Cli.Tests.Vmware;

public class VmrunVmResizerTests
{
    // === ResolveOwningAdapterId (the vms.yaml-cluster -> adapter-id map) =====
    [Theory]
    [InlineData("sqlserver", "sql-fci-1", "sqlserver")]
    [InlineData("sqlserver", "sql-fci-2", "sqlserver")]
    [InlineData("sqlserver", "sql-ag-rep-1", "sqlserver-ag")]
    [InlineData("foundation", "vault-1", "vault")]
    [InlineData("foundation", "dc-nexus", "foundation-ad")]
    [InlineData("foundation", "dc-nexus-2", "foundation-ad")]
    [InlineData("foundation", "nexus-jumpbox", null)]
    [InlineData("platform-tools", "registry-1", "registry")]
    [InlineData("edge", "nexus-gateway", null)]
    [InlineData("windows-workstations", "win-1", null)]
    [InlineData("redis", "redis-1", "redis")]
    [InlineData("postgres", "pg-primary", "postgres")]
    [InlineData("kafka-east", "kafka-east-1", "kafka-east")]   // GAP #4: scale-up resolves a broker VM to its per-region adapter (gates on controller-leader)
    [InlineData("kafka-west", "kafka-west-3", "kafka-west")]
    public void ResolveOwningAdapterId_Maps_Splits_And_OneToOne(string cluster, string vm, string? expected)
        => VmrunVmResizer.ResolveOwningAdapterId(cluster, vm).Should().Be(expected);

    // === .vmx parse / edit ==================================================
    [Fact]
    public void ParseVmxInt_Reads_Quoted_Value()
    {
        var lines = new[] { "memsize = \"4096\"", "numvcpus = \"4\"", "displayName = \"x\"" };
        VmrunVmResizer.ParseVmxInt(lines, "memsize").Should().Be(4096);
        VmrunVmResizer.ParseVmxInt(lines, "numvcpus").Should().Be(4);
        VmrunVmResizer.ParseVmxInt(lines, "absent").Should().BeNull();
    }

    [Fact]
    public void SetVmxValue_Updates_Existing_Preserving_Other_Lines()
    {
        var lines = new[] { "memsize = \"4096\"", "numvcpus = \"2\"", "guestOS = \"debian\"" };
        var outLines = VmrunVmResizer.SetVmxValue(lines, "numvcpus", "8");
        outLines.Should().Contain("numvcpus = \"8\"");
        outLines.Should().NotContain("numvcpus = \"2\"");
        outLines.Should().Contain("memsize = \"4096\"");
        outLines.Should().Contain("guestOS = \"debian\"");
        outLines.Length.Should().Be(3);
    }

    [Fact]
    public void SetVmxValue_Appends_When_Missing()
    {
        var lines = new[] { "memsize = \"4096\"" };
        var outLines = VmrunVmResizer.SetVmxValue(lines, "numvcpus", "4");
        outLines.Should().Contain("numvcpus = \"4\"");
        outLines.Length.Should().Be(2);
    }

    [Fact]
    public void ParsePrimaryDiskFile_Prefers_Scsi_And_Skips_Absent_Devices()
    {
        var lines = new[]
        {
            "sata0:0.present = \"FALSE\"",
            "sata0:0.fileName = \"ghost.vmdk\"",
            "scsi0:0.present = \"TRUE\"",
            "scsi0:0.fileName = \"node.vmdk\"",
        };
        VmrunVmResizer.ParsePrimaryDiskFile(lines).Should().Be("node.vmdk");
    }

    [Fact]
    public void ParsePrimaryDiskFile_Falls_Back_To_Sata_When_No_Scsi()
    {
        var lines = new[] { "sata0:0.fileName = \"only.vmdk\"" };
        VmrunVmResizer.ParsePrimaryDiskFile(lines).Should().Be("only.vmdk");
    }

    [Fact]
    public void GrowScripts_Have_Expected_Shape()
    {
        VmrunVmResizer.LinuxGrowScript().Should().Contain("growpart").And.Contain("resize2fs").And.Contain("lvextend");
        VmrunVmResizer.WindowsGrowScript().Should().Contain("Resize-Partition").And.Contain("Get-PartitionSupportedSize");
    }

    // === ScaleUpAsync validation ============================================
    [Fact]
    public async Task ScaleUp_Requires_At_Least_One_Dimension()
    {
        var r = await NewResizer(out _, out _).ScaleUpAsync(new ScaleUpRequest("redis-1"), default);
        r.IsFail.Should().BeTrue();
        r.Error.Should().Contain("--cpu").And.Contain("--ram").And.Contain("--disk");
    }

    [Fact]
    public async Task ScaleUp_Rejects_Non_Multiple_Of_4_Ram()
    {
        var r = await NewResizer(out _, out _).ScaleUpAsync(new ScaleUpRequest("redis-1", RamMb: 4097), default);
        r.IsFail.Should().BeTrue();
        r.Error.Should().Contain("multiple of 4");
    }

    [Fact]
    public async Task ScaleUp_Unknown_Vm_Fails_With_Known_Names()
    {
        var resizer = NewResizer(out _, out _, vmName: "redis-1");
        var r = await resizer.ScaleUpAsync(new ScaleUpRequest("nope-9", CpuCount: 2), default);
        r.IsFail.Should().BeTrue();
        r.Error.Should().Contain("nope-9").And.Contain("redis-1");
    }

    // === ScaleUpAsync gate + edit ===========================================
    [Fact]
    public async Task ScaleUp_Refuses_Primary_Unless_Forced()
    {
        using var tmp = new TempVm("redis-1", memsize: 2048, numvcpus: 2);
        var vmrun = new MutableVmrun();
        var adapter = new FakeAdapter("redis") { StatusOk = true, CanResize = false };
        var resizer = new VmrunVmResizer(tmp.Catalog, vmrun, new FakeRegistry(adapter), new FakeSsh(), "nexusadmin", "key");

        var refused = await resizer.ScaleUpAsync(new ScaleUpRequest("redis-1", CpuCount: 4), default);
        refused.IsFail.Should().BeTrue();
        refused.Error.Should().Contain("primary").And.Contain("--force-primary");
        vmrun.StopCalls.Should().BeEmpty("must not touch power state when refused");
    }

    [Fact]
    public async Task ScaleUp_Force_Bypasses_Gate_And_Edits_Vmx()
    {
        using var tmp = new TempVm("redis-1", memsize: 2048, numvcpus: 2);
        var vmrun = new MutableVmrun();  // not running
        var adapter = new FakeAdapter("redis") { StatusOk = true, CanResize = false };
        var resizer = new VmrunVmResizer(tmp.Catalog, vmrun, new FakeRegistry(adapter), new FakeSsh(), "nexusadmin", "key");

        var r = await resizer.ScaleUpAsync(new ScaleUpRequest("redis-1", CpuCount: 8, RamMb: 4096, ForcePrimary: true), default);
        r.IsOk.Should().BeTrue();
        r.Value!.Outcome.Should().Be("ok");
        r.Value.OldCpu.Should().Be(2);
        r.Value.NewCpu.Should().Be(8);
        r.Value.NewRamMb.Should().Be(4096);
        VmrunVmResizer.ParseVmxInt(File.ReadAllLines(tmp.VmxPath), "numvcpus").Should().Be(8);
        VmrunVmResizer.ParseVmxInt(File.ReadAllLines(tmp.VmxPath), "memsize").Should().Be(4096);
    }

    [Fact]
    public async Task ScaleUp_Running_Vm_Stops_Edits_Starts()
    {
        using var tmp = new TempVm("redis-1", memsize: 2048, numvcpus: 2);
        var vmrun = new MutableVmrun(runningVmx: tmp.VmxPath);
        var adapter = new FakeAdapter("redis") { StatusOk = true, CanResize = true };
        var resizer = new VmrunVmResizer(tmp.Catalog, vmrun, new FakeRegistry(adapter), new FakeSsh(), "nexusadmin", "key");

        var r = await resizer.ScaleUpAsync(new ScaleUpRequest("redis-1", CpuCount: 4), default);
        r.IsOk.Should().BeTrue();
        r.Value!.Outcome.Should().Be("ok");
        vmrun.StopCalls.Should().ContainSingle();
        vmrun.StartCalls.Should().ContainSingle();
        VmrunVmResizer.ParseVmxInt(File.ReadAllLines(tmp.VmxPath), "numvcpus").Should().Be(4);
    }

    [Fact]
    public async Task ScaleUp_NoOp_Is_Skipped()
    {
        using var tmp = new TempVm("redis-1", memsize: 2048, numvcpus: 2);
        var vmrun = new MutableVmrun();
        var adapter = new FakeAdapter("redis") { StatusOk = true, CanResize = true };
        var resizer = new VmrunVmResizer(tmp.Catalog, vmrun, new FakeRegistry(adapter), new FakeSsh(), "nexusadmin", "key");

        var r = await resizer.ScaleUpAsync(new ScaleUpRequest("redis-1", CpuCount: 2, RamMb: 2048), default);
        r.IsOk.Should().BeTrue();
        r.Value!.Outcome.Should().Be("skipped");
        vmrun.StopCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ScaleUp_Disk_Grows_Vmdk_And_Extends_Guest()
    {
        using var tmp = new TempVm("redis-1", memsize: 2048, numvcpus: 2);
        var vmrun = new MutableVmrun(runningVmx: tmp.VmxPath);
        var adapter = new FakeAdapter("redis") { StatusOk = true, CanResize = true };
        var resizer = new VmrunVmResizer(tmp.Catalog, vmrun, new FakeRegistry(adapter), new FakeSsh(exit: 0), "nexusadmin", "key");

        var r = await resizer.ScaleUpAsync(new ScaleUpRequest("redis-1", DiskGb: 50, ForcePrimary: true), default);
        r.IsOk.Should().BeTrue();
        r.Value!.Outcome.Should().Be("ok");
        r.Value.NewDiskGb.Should().Be(50);
        vmrun.GrowCalls.Should().ContainSingle();
        vmrun.GrowCalls[0].Item2.Should().Be(50);
        vmrun.GrowCalls[0].Item1.Should().EndWith("redis-1.vmdk");
    }

    [Fact]
    public async Task ScaleUp_Disk_Reports_Honestly_When_Guest_Cannot_Extend()
    {
        using var tmp = new TempVm("redis-1", memsize: 2048, numvcpus: 2);
        var vmrun = new MutableVmrun(runningVmx: tmp.VmxPath);
        var adapter = new FakeAdapter("redis") { StatusOk = true, CanResize = true };
        // exit 3 = the layout can't grow in place (swap follows root) -> safe, honest.
        var resizer = new VmrunVmResizer(tmp.Catalog, vmrun, new FakeRegistry(adapter),
            new FakeSsh(exit: 3, stdout: "NEXUS_OK\nroot partition /dev/sda1 has no free space to grow into"), "nexusadmin", "key");

        var r = await resizer.ScaleUpAsync(new ScaleUpRequest("redis-1", DiskGb: 50, ForcePrimary: true), default);
        r.IsOk.Should().BeTrue();
        r.Value!.Outcome.Should().Be("ok", "the vmdk grew even though the guest FS was safely left alone");
        r.Value.OutcomeReason.Should().Contain("NOT auto-extended");
        vmrun.GrowCalls.Should().ContainSingle();
    }

    // === helpers / fakes ====================================================
    private static VmrunVmResizer NewResizer(out MutableVmrun vmrun, out FakeRegistry registry, string vmName = "redis-1")
    {
        var catalog = new SingleVmCatalog("redis", vmName, "deb13", @"H:\nope\" + vmName);
        vmrun = new MutableVmrun();
        registry = new FakeRegistry(new FakeAdapter("redis") { StatusOk = true, CanResize = true });
        return new VmrunVmResizer(catalog, vmrun, registry, new FakeSsh(), "nexusadmin", "key");
    }

    private sealed class TempVm : IDisposable
    {
        public string Dir { get; }
        public string VmxPath { get; }
        public IVmsCatalog Catalog { get; }

        public TempVm(string name, int memsize, int numvcpus, string cluster = "redis", string os = "deb13")
        {
            Dir = Path.Combine(Path.GetTempPath(), $"nexus-resizer-{Guid.NewGuid():N}", name);
            Directory.CreateDirectory(Dir);
            VmxPath = Path.Combine(Dir, name + ".vmx");
            File.WriteAllLines(VmxPath, new[]
            {
                ".encoding = \"UTF-8\"",
                $"displayName = \"{name}\"",
                $"memsize = \"{memsize}\"",
                $"numvcpus = \"{numvcpus}\"",
                "scsi0:0.present = \"TRUE\"",
                $"scsi0:0.fileName = \"{name}.vmdk\"",
            });
            Catalog = new SingleVmCatalog(cluster, name, os, Dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(Dir)!, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class SingleVmCatalog : IVmsCatalog
    {
        private readonly Dictionary<string, ClusterRecord> _data;
        public SingleVmCatalog(string cluster, string vm, string os, string dir)
        {
            _data = new Dictionary<string, ClusterRecord>(StringComparer.Ordinal)
            {
                [cluster] = new ClusterRecord(cluster, "p", "0.X", new[]
                {
                    new NodeRecord(vm, os, "192.168.10.99", "192.168.70.99", dir, "member")
                })
            };
        }
        public Result<IReadOnlyDictionary<string, ClusterRecord>> Load()
            => Result.Ok<IReadOnlyDictionary<string, ClusterRecord>>(_data);
        public Result<ClusterRecord> GetCluster(string name)
            => _data.TryGetValue(name, out var c) ? Result.Ok(c) : Result.Fail<ClusterRecord>($"unknown '{name}'");
    }

    private sealed class MutableVmrun : IVmrunClient
    {
        private readonly HashSet<string> _running;
        public List<string> StopCalls { get; } = new();
        public List<string> StartCalls { get; } = new();
        public List<(string, int)> GrowCalls { get; } = new();

        public MutableVmrun(string? runningVmx = null)
        {
            _running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (runningVmx is not null)
                _running.Add(runningVmx);
        }

        public bool IsAvailable => true;
        public Task<Result<IReadOnlySet<string>>> ListRunningVmxPathsAsync(CancellationToken ct)
            => Task.FromResult(Result.Ok<IReadOnlySet<string>>(_running));
        public Task<Result<bool>> SuspendAsync(string vmx, CancellationToken ct) => Task.FromResult(Result.Ok(true));
        public Task<Result<bool>> ResumeAsync(string vmx, CancellationToken ct) => Task.FromResult(Result.Ok(true));
        public Task<Result<bool>> StopAsync(string vmx, bool hard, CancellationToken ct)
        {
            StopCalls.Add(vmx);
            _running.Remove(vmx);
            return Task.FromResult(Result.Ok(true));
        }
        public Task<Result<bool>> StartAsync(string vmx, CancellationToken ct)
        {
            StartCalls.Add(vmx);
            _running.Add(vmx);
            return Task.FromResult(Result.Ok(true));
        }
        public Task<Result<bool>> GrowVirtualDiskAsync(string vmdk, int gb, CancellationToken ct)
        {
            GrowCalls.Add((vmdk, gb));
            return Task.FromResult(Result.Ok(true));
        }
    }

    private sealed class FakeRegistry : IClusterRegistry
    {
        private readonly IClusterAdapter _adapter;
        public FakeRegistry(IClusterAdapter adapter) => _adapter = adapter;
        public Result<IClusterAdapter> GetAdapter(string id)
            => string.Equals(id, _adapter.ClusterId, StringComparison.Ordinal)
                ? Result.Ok(_adapter)
                : Result.Fail<IClusterAdapter>($"unknown '{id}'");
        public IReadOnlyList<string> Ids => new[] { _adapter.ClusterId };
    }

    private sealed class FakeSsh : ISshClient
    {
        private readonly int _exit;
        private readonly string _stdout;
        // Default stdout carries NEXUS_OK so the resizer's post-restart SSH-ready probe passes.
        public FakeSsh(int exit = 0, string stdout = "NEXUS_OK\nGREW=1") { _exit = exit; _stdout = stdout; }
        public Task<Result<SshExecResult>> ExecuteAsync(SshTarget t, string cmd, TimeSpan to, CancellationToken ct)
            => Task.FromResult(Result.Ok(new SshExecResult(_exit, _stdout, "", TimeSpan.Zero)));
        public Task<Result<bool>> UploadBytesAsync(SshTarget t, byte[] c, string p, TimeSpan to, CancellationToken ct)
            => Task.FromResult(Result.Ok(true));
        public Task<Result<byte[]>> DownloadBytesAsync(SshTarget t, string p, TimeSpan to, CancellationToken ct)
            => Task.FromResult(Result.Ok(Array.Empty<byte>()));
    }

    private sealed class FakeAdapter : IClusterAdapter
    {
        public FakeAdapter(string id) => ClusterId = id;
        public bool StatusOk { get; set; } = true;
        public bool CanResize { get; set; } = true;

        public string ClusterId { get; }
        public string DisplayName => ClusterId;

        public Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(StatusOk
                ? Result.Ok(new ClusterStatus(ClusterId, ClusterId, "green", Array.Empty<ClusterMember>(), null, DateTimeOffset.UnixEpoch))
                : Result.Fail<ClusterStatus>("cluster down"));

        public bool CanResizeVm(string vmName, string role) => CanResize;

        public Task<Result<FailoverResult>> FailoverAsync(FailoverRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<HealthReport>> HealthAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<BackupResult>> BackupTakeAsync(BackupRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario s, CancellationToken ct) => throw new NotSupportedException();
        public Task<Result<AclSnapshot>> AclAsync(AclOperation o, CancellationToken ct) => throw new NotSupportedException();
    }
}
