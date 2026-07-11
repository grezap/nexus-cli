using System.Diagnostics;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Adapters.Vmware;

/// <summary>
/// IVmrunClient implementation that shells out to vmrun.exe (VMware
/// Workstation Pro). Linux build hosts have no vmrun; every method
/// returns a typed Result.Fail with a clear message instead of trying
/// to spawn. Argv construction + stdout parsing are pulled into static
/// internals so tests don't need to spawn processes.
/// </summary>
public sealed class VmrunProcessClient : IVmrunClient
{
    /// <inheritdoc />
    public bool IsAvailable => VmrunPaths.IsAvailable();

    /// <inheritdoc />
    public async Task<Result<IReadOnlySet<string>>> ListRunningVmxPathsAsync(CancellationToken cancellationToken)
    {
        var (ok, stdout, stderr) = await RunAsync(BuildListArgs(), cancellationToken).ConfigureAwait(false);
        if (!ok)
            return Result.Fail<IReadOnlySet<string>>(stderr);
        return Result.Ok<IReadOnlySet<string>>(ParseRunningList(stdout));
    }

    /// <inheritdoc />
    public async Task<Result<bool>> SuspendAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var (ok, _, stderr) = await RunAsync(BuildSuspendArgs(vmxPath), cancellationToken).ConfigureAwait(false);
        return ok ? Result.Ok(true) : Result.Fail<bool>(stderr);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ResumeAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var (ok, _, stderr) = await RunAsync(BuildResumeArgs(vmxPath), cancellationToken).ConfigureAwait(false);
        return ok ? Result.Ok(true) : Result.Fail<bool>(stderr);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> StopAsync(string vmxPath, bool hard, CancellationToken cancellationToken)
    {
        var (ok, _, stderr) = await RunAsync(BuildStopArgs(vmxPath, hard), cancellationToken).ConfigureAwait(false);
        return ok ? Result.Ok(true) : Result.Fail<bool>(stderr);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> StartAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var (ok, _, stderr) = await RunAsync(BuildResumeArgs(vmxPath), cancellationToken).ConfigureAwait(false);
        return ok ? Result.Ok(true) : Result.Fail<bool>(stderr);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> GrowVirtualDiskAsync(string vmdkPath, int newSizeGb, CancellationToken cancellationToken)
    {
        var vdm = VmrunPaths.ResolveVdiskManager();
        if (vdm is null)
            return Result.Fail<bool>(VmrunPaths.VdiskManagerUnavailableMessage());
        var (ok, stdout, stderr) = await RunExeAsync(vdm, BuildGrowDiskArgs(vmdkPath, newSizeGb), cancellationToken).ConfigureAwait(false);
        if (ok)
            return Result.Ok(true);
        // vmware-vdiskmanager writes the actionable failure (snapshots present,
        // shrink attempt, disk in use) to stdout, not stderr.
        var msg = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout.Trim();
        return Result.Fail<bool>(string.IsNullOrWhiteSpace(msg) ? "vmware-vdiskmanager failed" : msg);
    }

    internal static string[] BuildListArgs() => new[] { "list" };

    internal static string[] BuildSuspendArgs(string vmxPath) => new[] { "suspend", vmxPath };

    internal static string[] BuildResumeArgs(string vmxPath) => new[] { "start", vmxPath, "nogui" };

    internal static string[] BuildStopArgs(string vmxPath, bool hard) => new[] { "stop", vmxPath, hard ? "hard" : "soft" };

    // vmware-vdiskmanager -x <n>GB <vmdk>  (grow the virtual disk to n GB).
    internal static string[] BuildGrowDiskArgs(string vmdkPath, int newSizeGb)
        => new[] { "-x", $"{newSizeGb.ToString(System.Globalization.CultureInfo.InvariantCulture)}GB", vmdkPath };

    // Parse `vmrun list` stdout into a case-insensitive set of running .vmx paths,
    // dropping the trailing "Total running VMs:" summary line.
    internal static IReadOnlySet<string> ParseRunningList(string stdout)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(stdout))
            return set;

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith("Total running VMs:", StringComparison.Ordinal))
                continue;
            set.Add(line);
        }
        return set;
    }

    private static async Task<(bool Ok, string Stdout, string Stderr)> RunAsync(
        string[] tail,
        CancellationToken cancellationToken)
    {
        var path = VmrunPaths.Resolve();
        if (path is null)
            return (false, "", VmrunPaths.UnavailableMessage());
        // vmrun requires the "-T ws" host-type prefix before the verb.
        var argv = new string[tail.Length + 2];
        argv[0] = "-T";
        argv[1] = "ws";
        Array.Copy(tail, 0, argv, 2, tail.Length);
        return await RunExeAsync(path, argv, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(bool Ok, string Stdout, string Stderr)> RunExeAsync(
        string exePath,
        string[] args,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for {Path.GetFileName(exePath)}");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, "", $"{Path.GetFileName(exePath)} invocation failed: {ex.Message}");
        }
    }
}
