using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface IDemoRunner
{
    Task<Result<DemoRunReport>> RunAsync(DemoSpec spec, CancellationToken cancellationToken);

    Task<Result<DemoRecordReport>> RecordAsync(
        DemoSpec spec,
        string outputDirectory,
        CancellationToken cancellationToken);
}
