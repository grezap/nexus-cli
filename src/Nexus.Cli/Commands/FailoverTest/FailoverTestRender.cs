using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core.Models;
using Spectre.Console;

namespace Nexus.Cli.Commands.FailoverTest;

internal static class FailoverTestRender
{
    public static void EmitHuman(FailoverTestReport r)
    {
        var (label, color) = (r.NewLeader, r.Recovery) switch
        {
            (null, _) => ("● RED  no new leader within deadline", "red"),
            (not null, FailoverRecoveryStatus.Recovered) => ("● GREEN  recovered", "green"),
            (not null, FailoverRecoveryStatus.RecoveryFailed) => ("● YELLOW  new leader OK; recovery FAILED", "yellow"),
            _ => ("● YELLOW  partial", "yellow")
        };

        AnsiConsole.Write(new Rule($"[{color}]{label}[/]  failover-test {ScenarioLabel(r.Scenario)}  ({r.StartedAtUtc:u})")
        {
            Justification = Justify.Left
        });

        AnsiConsole.MarkupLine($"  original leader : [bold]{Markup.Escape(r.OriginalLeader)}[/]");
        AnsiConsole.MarkupLine($"  new leader      : [bold]{Markup.Escape(r.NewLeader ?? "(none observed)")}[/]");
        AnsiConsole.MarkupLine($"  RTO             : [bold]{r.Rto.TotalSeconds:F2}s[/]");
        AnsiConsole.MarkupLine($"  recovery        : [bold]{RecoveryLabel(r.Recovery)}[/]");
        if (!string.IsNullOrEmpty(r.RecoveryHint))
            AnsiConsole.MarkupLine($"  recovery hint   : [yellow]{Markup.Escape(r.RecoveryHint)}[/]");

        var t = new Table().Title("[bold]Timeline (seconds from start)[/]")
            .AddColumns("Phase", "Offset");
        t.AddRow("pre-flight complete", r.Timeline.PreFlightCompleted.TotalSeconds.ToString("F2"));
        t.AddRow("failure injected", r.Timeline.FailureInjected.TotalSeconds.ToString("F2"));
        t.AddRow("new leader observed", r.Timeline.NewLeaderObserved.TotalSeconds.ToString("F2"));
        t.AddRow("recovery attempted", r.Timeline.RecoveryAttempted.TotalSeconds.ToString("F2"));
        t.AddRow("cluster healthy again", r.Timeline.ClusterHealthyAgain.TotalSeconds.ToString("F2"));
        AnsiConsole.Write(t);
    }

    public static void EmitJson(FailoverTestReport r)
    {
        var dto = new FailoverTestJsonOutput
        {
            Scenario = ScenarioLabel(r.Scenario),
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
            OriginalLeader = r.OriginalLeader,
            NewLeader = r.NewLeader,
            RtoSeconds = Math.Round(r.Rto.TotalSeconds, 3),
            Recovery = RecoveryLabel(r.Recovery),
            RecoveryHint = r.RecoveryHint,
            Timeline = new FailoverTimelineJson
            {
                PreFlightCompletedSec = Math.Round(r.Timeline.PreFlightCompleted.TotalSeconds, 3),
                FailureInjectedSec = Math.Round(r.Timeline.FailureInjected.TotalSeconds, 3),
                NewLeaderObservedSec = Math.Round(r.Timeline.NewLeaderObserved.TotalSeconds, 3),
                RecoveryAttemptedSec = Math.Round(r.Timeline.RecoveryAttempted.TotalSeconds, 3),
                ClusterHealthyAgainSec = Math.Round(r.Timeline.ClusterHealthyAgain.TotalSeconds, 3)
            }
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.FailoverTestJsonOutput));
    }

    private static string ScenarioLabel(FailoverScenario s) => s switch
    {
        FailoverScenario.ConsulLeader => "consul-leader",
        FailoverScenario.NomadLeader => "nomad-leader",
        FailoverScenario.SwarmManager => "swarm-manager",
        _ => s.ToString()
    };

    private static string RecoveryLabel(FailoverRecoveryStatus s) => s switch
    {
        FailoverRecoveryStatus.Recovered => "recovered",
        FailoverRecoveryStatus.RecoveryFailed => "failed",
        FailoverRecoveryStatus.NotAttempted => "not-attempted",
        _ => s.ToString().ToLowerInvariant()
    };
}
