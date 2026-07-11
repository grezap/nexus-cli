namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Wrapper over the external VHS tool used to render demo <c>.tape</c> scripts into
/// GIF/asciinema assets; gracefully reports absence when VHS is not installed.
/// </summary>
public interface IVhsClient
{
    /// <summary>Whether the VHS binary is present and usable on this host.</summary>
    bool IsAvailable { get; }

    /// <summary>Returns the actionable message explaining why VHS is unavailable.</summary>
    string UnavailableMessage();

    /// <summary>Renders the VHS <paramref name="tapeFilePath"/> to its output asset and returns the process exit code.</summary>
    Task<Result<int>> RenderAsync(string tapeFilePath, CancellationToken cancellationToken);
}
