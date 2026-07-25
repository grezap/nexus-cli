using System.Diagnostics;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Deploy;

/// <summary>
/// Executes a <see cref="DeployPlan"/> step by step, each through the platform shell
/// (<c>cmd.exe /c</c> / <c>/bin/sh -c</c>) from the plan's repo path so relative <c>deploy/</c> commands
/// resolve. Stops on the first non-zero exit. Mirrors the demo runner's shell-out.
/// </summary>
public sealed class DeployRunner : IDeployRunner
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(20);
    private const int OutputTailLines = 12;

    /// <inheritdoc />
    public async Task<Result<DeployReport>> ExecuteAsync(DeployPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sw = Stopwatch.StartNew();
        var results = new List<DeployStepResult>(plan.Steps.Count);
        var status = DeployStatus.Ok;

        foreach (var step in plan.Steps)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var stepSw = Stopwatch.StartNew();
            var (exit, output) = await ExecShellAsync(step.Command, plan.RepoPath, cancellationToken).ConfigureAwait(false);
            stepSw.Stop();
            results.Add(new DeployStepResult(step.Name, exit, TailLines(output, OutputTailLines), stepSw.Elapsed));

            if (exit != 0)
            {
                status = DeployStatus.StepFailed;
                break;
            }
        }

        sw.Stop();
        return Result.Ok(new DeployReport(plan.Project, status, results, sw.Elapsed));
    }

    private static async Task<(int ExitCode, string Output)> ExecShellAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ((string)"cmd.exe", new[] { "/c", command })
            : ((string)"/bin/sh", new[] { "-c", command });

        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stepCts.CancelAfter(StepTimeout);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(stepCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(stepCts.Token);
            await proc.WaitForExitAsync(stepCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, stdout + "\n" + stderr);
        }
        catch (Exception ex)
        {
            return (-1, $"step invocation failed: {ex.Message}");
        }
    }

    private static string TailLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var lines = text.Split('\n');
        return lines.Length <= n ? text.TrimEnd() : string.Join('\n', lines[^n..]).TrimEnd();
    }
}
