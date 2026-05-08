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
    public bool IsAvailable => VmrunPaths.IsAvailable();

    public async Task<Result<IReadOnlySet<string>>> ListRunningVmxPathsAsync(CancellationToken cancellationToken)
    {
        var (ok, stdout, stderr) = await RunAsync(BuildListArgs(), cancellationToken).ConfigureAwait(false);
        if (!ok)
            return Result.Fail<IReadOnlySet<string>>(stderr);
        return Result.Ok<IReadOnlySet<string>>(ParseRunningList(stdout));
    }

    public async Task<Result<bool>> SuspendAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var (ok, _, stderr) = await RunAsync(BuildSuspendArgs(vmxPath), cancellationToken).ConfigureAwait(false);
        return ok ? Result.Ok(true) : Result.Fail<bool>(stderr);
    }

    public async Task<Result<bool>> ResumeAsync(string vmxPath, CancellationToken cancellationToken)
    {
        var (ok, _, stderr) = await RunAsync(BuildResumeArgs(vmxPath), cancellationToken).ConfigureAwait(false);
        return ok ? Result.Ok(true) : Result.Fail<bool>(stderr);
    }

    internal static string[] BuildListArgs() => new[] { "list" };

    internal static string[] BuildSuspendArgs(string vmxPath) => new[] { "suspend", vmxPath };

    internal static string[] BuildResumeArgs(string vmxPath) => new[] { "start", vmxPath, "nogui" };

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

        var psi = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-T");
        psi.ArgumentList.Add("ws");
        foreach (var a in tail)
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for vmrun.exe");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, "", $"vmrun invocation failed: {ex.Message}");
        }
    }
}
