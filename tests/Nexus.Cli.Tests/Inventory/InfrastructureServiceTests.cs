using FluentAssertions;
using Nexus.Cli.Adapters.Cluster;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;
using Xunit;

namespace Nexus.Cli.Tests.Inventory;

public class InfrastructureServiceTests
{
    [Theory]
    [InlineData(false, false, false, false, VmRuntimeState.Unknown)]
    [InlineData(true, false, false, false, VmRuntimeState.Missing)]
    [InlineData(true, true, false, true, VmRuntimeState.Running)]
    [InlineData(true, true, true, false, VmRuntimeState.Suspended)]
    [InlineData(true, true, false, false, VmRuntimeState.Stopped)]
    public void ClassifyState_Truth_Table(bool vmrun, bool vmx, bool hasSuspendedSidecar, bool inRunning, VmRuntimeState expected)
        => InfrastructureService.ClassifyState(vmrun, vmx, hasSuspendedSidecar, inRunning).Should().Be(expected);

    [Fact]
    public async Task SuspendAsync_Recognises_Session_Suffixed_Vmem_As_Already_Suspended()
    {
        // Real-world file shape on Workstation Pro 17.5+: vault-3-3c85c1f6.vmem
        // (session UUID suffix); the un-suffixed vault-3.vmem rarely exists.
        var dir = Path.Combine(Path.GetTempPath(), $"nexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var nodeDir = Path.Combine(dir, "vault-3"); Directory.CreateDirectory(nodeDir);
            var vmx = Path.Combine(nodeDir, "vault-3.vmx"); File.WriteAllText(vmx, "");
            File.WriteAllText(Path.Combine(nodeDir, "vault-3-3c85c1f6.vmem"), "");

            var catalog = new FakeCatalog(new ClusterRecord("test", "p", "0.X", new[]
            {
                new NodeRecord("vault-3", "deb13", "1", "2", nodeDir, "n/a")
            }));
            var vmrun = new RecordingVmrun();
            var svc = new InfrastructureService(catalog, vmrun);

            var r = await svc.SuspendAsync("test", null, default);
            r.IsOk.Should().BeTrue();
            var ops = r.Value!;
            ops.Should().ContainSingle();
            ops[0].Success.Should().BeTrue();
            ops[0].Message.Should().Be("already suspended");
            vmrun.SuspendCalls.Should().BeEmpty("VM has session-suffixed .vmem; suspend is a no-op");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StatusAsync_Filters_To_Single_Node()
    {
        var catalog = new FakeCatalog(new ClusterRecord("foundation", "p", "0.A", new[]
        {
            new NodeRecord("vault-1", "deb13", "192.168.10.121", "192.168.70.121", @"H:\dummy\vault-1", "vault"),
            new NodeRecord("vault-2", "deb13", "192.168.10.122", "192.168.70.122", @"H:\dummy\vault-2", "vault")
        }));
        var svc = new InfrastructureService(catalog, new UnavailableVmrun());

        var r = await svc.StatusAsync("foundation", "vault-2", default);
        r.IsOk.Should().BeTrue();
        var rows = r.Value!;
        rows.Should().HaveCount(1);
        rows[0].Node.Name.Should().Be("vault-2");
    }

    [Fact]
    public async Task StatusAsync_Unknown_Node_Returns_Fail_With_Known_Names()
    {
        var catalog = new FakeCatalog(new ClusterRecord("foundation", "p", "0.A", new[]
        {
            new NodeRecord("vault-1", "deb13", "1", "2", @"H:\dummy\vault-1", "vault")
        }));
        var svc = new InfrastructureService(catalog, new UnavailableVmrun());

        var r = await svc.StatusAsync("foundation", "vault-99", default);
        r.IsFail.Should().BeTrue();
        r.Error.Should().Contain("vault-99").And.Contain("vault-1");
    }

    [Fact]
    public async Task StatusAsync_Returns_Unknown_State_When_Vmrun_Unavailable()
    {
        var catalog = new FakeCatalog(new ClusterRecord("foundation", "p", "0.A", new[]
        {
            new NodeRecord("vault-1", "deb13", "1", "2", @"H:\dummy\vault-1", "vault")
        }));
        var svc = new InfrastructureService(catalog, new UnavailableVmrun());

        var r = await svc.StatusAsync("foundation", null, default);
        r.IsOk.Should().BeTrue();
        r.Value!.Single().State.Should().Be(VmRuntimeState.Unknown);
    }

    [Fact]
    public async Task SuspendAsync_Returns_Fail_When_Vmrun_Unavailable()
    {
        var catalog = new FakeCatalog(new ClusterRecord("foundation", "p", "0.A", Array.Empty<NodeRecord>()));
        var svc = new InfrastructureService(catalog, new UnavailableVmrun());

        var r = await svc.SuspendAsync("foundation", null, default);
        r.IsFail.Should().BeTrue();
        (r.Error!.Contains("Windows-only") || r.Error.Contains("vmrun.exe not found")).Should().BeTrue();
    }

    [Fact]
    public async Task SuspendAsync_Skips_Already_Stopped_And_Suspended_Nodes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Two real .vmx files; one with .vmss (suspended), one without (stopped).
            var stoppedDir = Path.Combine(dir, "stopped"); Directory.CreateDirectory(stoppedDir);
            var stoppedVmx = Path.Combine(stoppedDir, "stopped.vmx"); File.WriteAllText(stoppedVmx, "");

            var suspDir = Path.Combine(dir, "susp"); Directory.CreateDirectory(suspDir);
            var suspVmx = Path.Combine(suspDir, "susp.vmx"); File.WriteAllText(suspVmx, "");
            File.WriteAllText(Path.ChangeExtension(suspVmx, ".vmss"), "");

            var catalog = new FakeCatalog(new ClusterRecord("test", "p", "0.X", new[]
            {
                new NodeRecord("stopped", "deb13", "1", "2", stoppedDir, "n/a"),
                new NodeRecord("susp",    "deb13", "1", "2", suspDir,    "n/a")
            }));
            var vmrun = new RecordingVmrun();
            var svc = new InfrastructureService(catalog, vmrun);

            var r = await svc.SuspendAsync("test", null, default);
            r.IsOk.Should().BeTrue();
            var ops = r.Value!;
            ops.Should().HaveCount(2);
            ops.Should().AllSatisfy(o => o.Success.Should().BeTrue());
            ops[0].Message.Should().Be("already stopped");
            ops[1].Message.Should().Be("already suspended");
            vmrun.SuspendCalls.Should().BeEmpty("no live VMs to suspend in this fixture");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeAsync_Skips_Already_Running()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var nodeDir = Path.Combine(dir, "running"); Directory.CreateDirectory(nodeDir);
            var vmx = Path.Combine(nodeDir, "running.vmx"); File.WriteAllText(vmx, "");

            var catalog = new FakeCatalog(new ClusterRecord("test", "p", "0.X", new[]
            {
                new NodeRecord("running", "deb13", "1", "2", nodeDir, "n/a")
            }));
            var vmrun = new RecordingVmrun(running: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { vmx });
            var svc = new InfrastructureService(catalog, vmrun);

            var r = await svc.ResumeAsync("test", null, default);
            r.IsOk.Should().BeTrue();
            var ops = r.Value!;
            ops.Single().Success.Should().BeTrue();
            ops[0].Message.Should().Be("already running");
            vmrun.ResumeCalls.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SuspendAsync_Reports_Missing_For_Planned_Not_Deployed()
    {
        var catalog = new FakeCatalog(new ClusterRecord("planned", "p", "0.Y", new[]
        {
            new NodeRecord("ghost", "deb13", "1", "2", @"H:\does\not\exist", "n/a")
        }));
        var vmrun = new RecordingVmrun();
        var svc = new InfrastructureService(catalog, vmrun);

        var r = await svc.SuspendAsync("planned", null, default);
        r.IsOk.Should().BeTrue();
        var ops = r.Value!;
        ops.Single().Success.Should().BeFalse();
        ops[0].Message.Should().Contain("not on disk");
        vmrun.SuspendCalls.Should().BeEmpty();
    }

    private sealed class FakeCatalog : IVmsCatalog
    {
        private readonly Dictionary<string, ClusterRecord> _data;

        public FakeCatalog(ClusterRecord cluster)
        {
            _data = new Dictionary<string, ClusterRecord>(StringComparer.Ordinal) { [cluster.Name] = cluster };
        }

        public Result<IReadOnlyDictionary<string, ClusterRecord>> Load()
            => Result.Ok<IReadOnlyDictionary<string, ClusterRecord>>(_data);

        public Result<ClusterRecord> GetCluster(string name)
            => _data.TryGetValue(name, out var c)
                ? Result.Ok(c)
                : Result.Fail<ClusterRecord>($"unknown cluster '{name}'");
    }

    private sealed class UnavailableVmrun : IVmrunClient
    {
        public bool IsAvailable => false;
        public Task<Result<IReadOnlySet<string>>> ListRunningVmxPathsAsync(CancellationToken ct)
            => Task.FromResult(Result.Fail<IReadOnlySet<string>>("unavailable"));
        public Task<Result<bool>> SuspendAsync(string vmx, CancellationToken ct)
            => Task.FromResult(Result.Fail<bool>("unavailable"));
        public Task<Result<bool>> ResumeAsync(string vmx, CancellationToken ct)
            => Task.FromResult(Result.Fail<bool>("unavailable"));
        public Task<Result<bool>> StopAsync(string vmx, bool hard, CancellationToken ct)
            => Task.FromResult(Result.Fail<bool>("unavailable"));
        public Task<Result<bool>> StartAsync(string vmx, CancellationToken ct)
            => Task.FromResult(Result.Fail<bool>("unavailable"));
        public Task<Result<bool>> GrowVirtualDiskAsync(string vmdk, int newSizeGb, CancellationToken ct)
            => Task.FromResult(Result.Fail<bool>("unavailable"));
    }

    private sealed class RecordingVmrun : IVmrunClient
    {
        private readonly IReadOnlySet<string> _running;
        public List<string> SuspendCalls { get; } = new();
        public List<string> ResumeCalls { get; } = new();
        public List<string> StopCalls { get; } = new();
        public List<string> StartCalls { get; } = new();
        public List<(string Vmdk, int Gb)> GrowCalls { get; } = new();

        public RecordingVmrun(IReadOnlySet<string>? running = null)
            => _running = running ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsAvailable => true;

        public Task<Result<IReadOnlySet<string>>> ListRunningVmxPathsAsync(CancellationToken ct)
            => Task.FromResult(Result.Ok(_running));

        public Task<Result<bool>> SuspendAsync(string vmx, CancellationToken ct)
        {
            SuspendCalls.Add(vmx);
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<bool>> ResumeAsync(string vmx, CancellationToken ct)
        {
            ResumeCalls.Add(vmx);
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<bool>> StopAsync(string vmx, bool hard, CancellationToken ct)
        {
            StopCalls.Add(vmx);
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<bool>> StartAsync(string vmx, CancellationToken ct)
        {
            StartCalls.Add(vmx);
            return Task.FromResult(Result.Ok(true));
        }

        public Task<Result<bool>> GrowVirtualDiskAsync(string vmdk, int newSizeGb, CancellationToken ct)
        {
            GrowCalls.Add((vmdk, newSizeGb));
            return Task.FromResult(Result.Ok(true));
        }
    }
}
