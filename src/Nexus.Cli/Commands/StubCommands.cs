// All v0.x master-plan verbs are now implemented; this file is intentionally
// empty. Kept in source so the project still has an anchor for any future
// stub-command pattern; the StubCommandBase helper above is preserved for
// reuse if a new master-plan verb is sketched ahead of implementation.

using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands;

internal abstract class StubCommandBase<TSettings> : Command<TSettings>
    where TSettings : CommandSettings
{
    protected abstract string Name { get; }
    protected abstract string PlannedVersion { get; }

    protected override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]{Name}[/] is not yet implemented — planned for [bold]{PlannedVersion}[/]. See the roadmap in README.md.");
        return 0;
    }
}
