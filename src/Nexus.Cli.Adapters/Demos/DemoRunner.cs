using System.Diagnostics;
using System.Text;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Demos;

/// <summary>
/// Sequences a demo's steps. Each step is a shell command line; runs through
/// <c>cmd.exe /c</c> on Windows or <c>/bin/sh -c</c> on Linux so the operator
/// can use redirects, pipes, and env-var expansion naturally.
///
/// RecordAsync writes a VHS .tape file with each step as a Type+Enter+Sleep
/// triplet, then invokes <see cref="IVhsClient.RenderAsync"/> to produce the
/// output GIF. The output file path is declared inside the .tape itself
/// (<c>Output ./out.gif</c>); we surface that path back in the report.
/// </summary>
public sealed class DemoRunner : IDemoRunner
{
    private readonly IVhsClient _vhs;

    // Tunables. Per-step timeout is intentionally generous -- demos may include
    // failover-test invocations which take 30s+.
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);
    private const int StdoutTailLines = 12;

    /// <summary>Creates a runner that renders recordings through <paramref name="vhs"/>.</summary>
    /// <param name="vhs">VHS client used by <see cref="RecordAsync"/> to render the .tape to a GIF.</param>
    public DemoRunner(IVhsClient vhs) => _vhs = vhs;

    /// <inheritdoc />
    public async Task<Result<DemoRunReport>> RunAsync(
        DemoSpec spec,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var stepResults = new List<DemoStepResult>(spec.Steps.Count);
        var status = DemoStatus.Ok;

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                status = DemoStatus.Aborted;
                break;
            }
            var step = spec.Steps[i];
            var stepSw = Stopwatch.StartNew();
            var (exit, outFull, errFull) = await ExecShellAsync(step.Command, cancellationToken).ConfigureAwait(false);
            stepSw.Stop();

            // ADR-0009: if expectations are set, they drive step success/failure
            // (not just the raw exit code). When no expectations are set, behaviour
            // matches v0.4.0 -- exit==0 means step OK.
            bool? expectationMet = null;
            string? expectationFailureReason = null;
            var hasExpectations = step.ExpectedExitCode.HasValue
                || (step.ExpectedOutputContains is { Count: > 0 });
            if (hasExpectations)
            {
                var failures = new List<string>();
                if (step.ExpectedExitCode.HasValue && exit != step.ExpectedExitCode.Value)
                    failures.Add($"expected exit code {step.ExpectedExitCode.Value}, got {exit}");
                if (step.ExpectedOutputContains is { Count: > 0 })
                {
                    // Assert against the FULL output -- tokens may sit above the displayed tail.
                    var combined = outFull + "\n" + errFull;
                    foreach (var token in step.ExpectedOutputContains)
                    {
                        if (!combined.Contains(token, StringComparison.Ordinal))
                            failures.Add($"expected output to contain '{token}'");
                    }
                }
                expectationMet = failures.Count == 0;
                if (!expectationMet.Value)
                    expectationFailureReason = string.Join("; ", failures);
            }

            stepResults.Add(new DemoStepResult(
                i, step.Command, exit,
                TailLines(outFull, StdoutTailLines), TailLines(errFull, StdoutTailLines), stepSw.Elapsed,
                ExpectationMet: expectationMet,
                ExpectationFailureReason: expectationFailureReason));

            var stepOk = hasExpectations ? expectationMet == true : exit == 0;
            if (!stepOk)
            {
                status = DemoStatus.StepFailed;
                break;
            }
            if (step.WaitAfterSeconds > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(step.WaitAfterSeconds), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    status = DemoStatus.Aborted;
                    break;
                }
            }
        }

        sw.Stop();
        return Result.Ok(new DemoRunReport(
            spec.Id,
            spec.Title,
            startedAt,
            status,
            stepResults,
            sw.Elapsed));
    }

    /// <inheritdoc />
    public async Task<Result<DemoRecordReport>> RecordAsync(
        DemoSpec spec,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        Directory.CreateDirectory(outputDirectory);
        var tapeFile = Path.Combine(outputDirectory, $"{spec.Id}.tape");
        var gifFile = Path.Combine(outputDirectory, $"{spec.Id}.gif");
        var tape = BuildTape(spec, gifFile);
        await File.WriteAllTextAsync(tapeFile, tape, cancellationToken).ConfigureAwait(false);

        if (!_vhs.IsAvailable)
        {
            sw.Stop();
            return Result.Ok(new DemoRecordReport(
                spec.Id,
                spec.Title,
                startedAt,
                tapeFile,
                OutputFilePath: null,
                VhsAvailable: false,
                VhsUnavailableMessage: _vhs.UnavailableMessage(),
                sw.Elapsed));
        }

        var render = await _vhs.RenderAsync(tapeFile, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        if (render.IsFail)
            return Result.Fail<DemoRecordReport>(render.Error!);

        return Result.Ok(new DemoRecordReport(
            spec.Id,
            spec.Title,
            startedAt,
            tapeFile,
            OutputFilePath: File.Exists(gifFile) ? gifFile : null,
            VhsAvailable: true,
            VhsUnavailableMessage: null,
            sw.Elapsed));
    }

    /// <summary>Renders a demo spec into a VHS .tape script: header + per-step Type/Enter/Sleep triplets, with the GIF path baked into the <c>Output</c> directive.</summary>
    internal static string BuildTape(DemoSpec spec, string outputGifPath)
    {
        var sb = new StringBuilder();
        sb.Append("# Generated by nexus-cli demo record for ").Append(spec.Id).Append('\n');
        sb.Append("# ").Append(spec.Title).Append('\n');
        sb.Append("Output ").Append(outputGifPath).Append('\n');
        sb.Append("Set FontSize 14\n");
        sb.Append("Set Width 1200\n");
        sb.Append("Set Height 800\n");
        sb.Append("Set Padding 20\n");
        sb.Append("Sleep 1s\n");
        foreach (var step in spec.Steps)
        {
            // Escape backslashes + quotes for the Type directive.
            var escaped = step.Command.Replace("\\", "\\\\").Replace("\"", "\\\"");
            sb.Append("Type \"").Append(escaped).Append("\"\n");
            sb.Append("Enter\n");
            var sleep = step.WaitAfterSeconds > 0 ? step.WaitAfterSeconds : 2;
            sb.Append("Sleep ").Append(sleep.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append("s\n");
        }
        return sb.ToString();
    }

    // Run one demo step through the platform shell (cmd.exe /c or /bin/sh -c) so the
    // step line can use pipes/redirects/env-expansion; a per-step timeout caps runaway
    // commands. Returns FULL stdout/stderr (the caller truncates for display only).
    private static async Task<(int ExitCode, string Stdout, string Stderr)> ExecShellAsync(
        string command,
        CancellationToken cancellationToken)
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ((string)"cmd.exe", new[] { "/c", command })
            : ((string)"/bin/sh", new[] { "-c", command });

        var psi = new ProcessStartInfo
        {
            FileName = file,
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
                ?? throw new InvalidOperationException("Process.Start returned null");
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stepCts.CancelAfter(StepTimeout);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(stepCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(stepCts.Token);
            await proc.WaitForExitAsync(stepCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            // Return FULL output; the caller truncates to a tail for display but asserts on full.
            return (proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, $"step invocation failed: {ex.Message}");
        }
    }

    // Keep only the last n lines of text (trimmed) for a compact step report.
    private static string TailLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var lines = text.Split('\n');
        if (lines.Length <= n) return text.TrimEnd();
        return string.Join('\n', lines[^n..]).TrimEnd();
    }
}
