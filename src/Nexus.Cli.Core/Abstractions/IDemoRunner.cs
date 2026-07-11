using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

/// <summary>
/// Executes a <see cref="DemoSpec"/> — either live to the console or recorded to an
/// asciinema/VHS asset for the portfolio.
/// </summary>
public interface IDemoRunner
{
    /// <summary>Runs the demo <paramref name="spec"/> interactively and returns a run report.</summary>
    Task<Result<DemoRunReport>> RunAsync(DemoSpec spec, CancellationToken cancellationToken);

    /// <summary>Records the demo <paramref name="spec"/> to <paramref name="outputDirectory"/> as replayable assets.</summary>
    Task<Result<DemoRecordReport>> RecordAsync(
        DemoSpec spec,
        string outputDirectory,
        CancellationToken cancellationToken);
}
