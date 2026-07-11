// All v0.x master-plan verbs are now implemented; this file is intentionally
// empty. Kept in source so the project still has an anchor for any future
// stub-command pattern; the StubCommandBase helper above is preserved for
// reuse if a new master-plan verb is sketched ahead of implementation.

using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands;

/// <summary>
/// Reusable base for placeholder commands representing master-plan verbs that are
/// sketched ahead of implementation. Prints a "not yet implemented" notice and
/// exits 0. Currently unused (all v0.x verbs are implemented) but preserved as an
/// anchor for the stub-command pattern.
/// </summary>
/// <typeparam name="TSettings">The Spectre settings type for the stubbed verb.</typeparam>
internal abstract class StubCommandBase<TSettings> : Command<TSettings>
    where TSettings : CommandSettings
{
    /// <summary>Display name of the stubbed verb (used in the notice text).</summary>
    protected abstract string Name { get; }

    /// <summary>The roadmap version the verb is planned for (used in the notice text).</summary>
    protected abstract string PlannedVersion { get; }

    /// <inheritdoc />
    protected override int Execute(CommandContext context, TSettings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLineInterpolated(
            $"[yellow]{Name}[/] is not yet implemented — planned for [bold]{PlannedVersion}[/]. See the roadmap in README.md.");
        return 0;
    }
}
