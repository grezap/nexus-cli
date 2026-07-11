using System.Diagnostics;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;

namespace Nexus.Cli.Adapters.Vhs;

/// <summary>
/// IVhsClient implementation that shells out to the vhs binary
/// (charmbracelet/vhs). vhs reads a .tape script, spawns its own embedded
/// terminal, types commands, and captures the output as GIF/MP4/WebM.
/// We never see the rendered frames -- vhs writes the output file path
/// declared inside the .tape (the <c>Output ...</c> directive).
/// </summary>
public sealed class VhsProcessClient : IVhsClient
{
    /// <inheritdoc />
    public bool IsAvailable => VhsPaths.IsAvailable();

    /// <inheritdoc />
    public string UnavailableMessage() => VhsPaths.UnavailableMessage();

    /// <inheritdoc />
    public async Task<Result<int>> RenderAsync(
        string tapeFilePath,
        CancellationToken cancellationToken)
    {
        var path = VhsPaths.Resolve();
        if (path is null)
            return Result.Fail<int>(VhsPaths.UnavailableMessage());

        if (!File.Exists(tapeFilePath))
            return Result.Fail<int>($"vhs tape file not found at '{tapeFilePath}'.");

        var psi = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(tapeFilePath);

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for vhs");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
            return proc.ExitCode == 0
                ? Result.Ok(proc.ExitCode)
                : Result.Fail<int>($"vhs returned exit {proc.ExitCode}: {stderr}");
        }
        catch (Exception ex)
        {
            return Result.Fail<int>($"vhs invocation failed: {ex.Message}");
        }
    }
}
