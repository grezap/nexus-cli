using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Core.Abstractions;

public interface IKafkaFailoverService
{
    Task<Result<KafkaFailoverReport>> RunAsync(
        KafkaFailoverDirection direction,
        CancellationToken cancellationToken);
}
