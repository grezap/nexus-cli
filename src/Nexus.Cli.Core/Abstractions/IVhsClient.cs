namespace Nexus.Cli.Core.Abstractions;

public interface IVhsClient
{
    bool IsAvailable { get; }

    string UnavailableMessage();

    Task<Result<int>> RenderAsync(string tapeFilePath, CancellationToken cancellationToken);
}
