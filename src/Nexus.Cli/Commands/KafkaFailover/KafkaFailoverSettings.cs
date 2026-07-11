using Nexus.Cli.Commands.FailoverTest;

namespace Nexus.Cli.Commands.KafkaFailover;

/// <summary>
/// Inherits the FailoverTest base (<c>--json</c>, <c>--no-color</c>,
/// <c>--yes</c>) so the kafka-failover verb stays uniform with the
/// failover-test family.
/// </summary>
public sealed class KafkaFailoverEastToWestSettings : FailoverTestSettingsBase;

/// <summary>
/// Settings for the <c>kafka failover west-to-east</c> verb. Inherits the
/// FailoverTest base (<c>--json</c>, <c>--no-color</c>, <c>--yes</c>) to stay
/// uniform with the failover-test family.
/// </summary>
public sealed class KafkaFailoverWestToEastSettings : FailoverTestSettingsBase;
