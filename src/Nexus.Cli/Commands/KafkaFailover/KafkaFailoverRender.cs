using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core.Models;
using Spectre.Console;

namespace Nexus.Cli.Commands.KafkaFailover;

/// <summary>Rendering helpers for the kafka-failover verbs (human timeline table, JSON via source-gen DTO).</summary>
internal static class KafkaFailoverRender
{
    /// <summary>Renders a kafka-failover report (source/target clusters, RTO, recovery, phase timeline) for humans.</summary>
    public static void EmitHuman(KafkaFailoverReport r)
    {
        var (label, color) = (r.TargetServedAfterFailure, r.Recovery) switch
        {
            (false, _) => ("● RED  target did NOT serve under source-loss", "red"),
            (true, KafkaFailoverRecoveryStatus.Recovered) => ("● GREEN  failover OK; source recovered", "green"),
            (true, KafkaFailoverRecoveryStatus.RecoveryFailed) => ("● YELLOW  target served; source RECOVERY FAILED", "yellow"),
            _ => ("● YELLOW  partial", "yellow"),
        };

        AnsiConsole.Write(new Rule($"[{color}]{label}[/]  kafka failover {DirectionLabel(r.Direction)}  ({r.StartedAtUtc:u})")
        {
            Justification = Justify.Left,
        });

        AnsiConsole.MarkupLine($"  source cluster    : [bold]{Markup.Escape(r.SourceCluster)}[/] (suspended {r.SuspendedBrokers.Count} brokers)");
        AnsiConsole.MarkupLine($"  target cluster    : [bold]{Markup.Escape(r.TargetCluster)}[/]");
        AnsiConsole.MarkupLine($"  suspended brokers : [bold]{Markup.Escape(string.Join(", ", r.SuspendedBrokers))}[/]");
        AnsiConsole.MarkupLine($"  target served?    : [bold]{(r.TargetServedAfterFailure ? "yes" : "NO")}[/]");
        if (r.TargetProbeToken is not null)
            AnsiConsole.MarkupLine($"  probe token       : [grey]{Markup.Escape(r.TargetProbeToken)}[/]  (RF=3 produce/consume round-trip on target)");
        AnsiConsole.MarkupLine($"  RTO               : [bold]{r.Rto.TotalSeconds:F2}s[/]");
        AnsiConsole.MarkupLine($"  recovery          : [bold]{RecoveryLabel(r.Recovery)}[/]");
        if (!string.IsNullOrEmpty(r.RecoveryHint))
            AnsiConsole.MarkupLine($"  recovery hint     : [yellow]{Markup.Escape(r.RecoveryHint)}[/]");

        var t = new Table().Title("[bold]Timeline (seconds from start)[/]")
            .AddColumns("Phase", "Offset");
        t.AddRow("pre-flight complete (target healthy)", r.Timeline.PreFlightCompleted.TotalSeconds.ToString("F2"));
        t.AddRow("failure injected (all source brokers suspended)", r.Timeline.FailureInjected.TotalSeconds.ToString("F2"));
        t.AddRow("target healthy (probe consumed)", r.Timeline.TargetHealthy.TotalSeconds.ToString("F2"));
        t.AddRow("recovery attempted (vmrun start)", r.Timeline.RecoveryAttempted.TotalSeconds.ToString("F2"));
        t.AddRow("source healthy again", r.Timeline.SourceHealthyAgain.TotalSeconds.ToString("F2"));
        AnsiConsole.Write(t);
    }

    /// <summary>Emits a kafka-failover report as JSON.</summary>
    public static void EmitJson(KafkaFailoverReport r)
    {
        var dto = new KafkaFailoverJsonOutput
        {
            Direction = DirectionLabel(r.Direction),
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
            SourceCluster = r.SourceCluster,
            TargetCluster = r.TargetCluster,
            SuspendedBrokers = r.SuspendedBrokers.ToList(),
            TargetServedAfterFailure = r.TargetServedAfterFailure,
            TargetProbeToken = r.TargetProbeToken,
            RtoSeconds = Math.Round(r.Rto.TotalSeconds, 3),
            Recovery = RecoveryLabel(r.Recovery),
            RecoveryHint = r.RecoveryHint,
            Timeline = new KafkaFailoverTimelineJson
            {
                PreFlightCompletedSec = Math.Round(r.Timeline.PreFlightCompleted.TotalSeconds, 3),
                FailureInjectedSec = Math.Round(r.Timeline.FailureInjected.TotalSeconds, 3),
                TargetHealthySec = Math.Round(r.Timeline.TargetHealthy.TotalSeconds, 3),
                RecoveryAttemptedSec = Math.Round(r.Timeline.RecoveryAttempted.TotalSeconds, 3),
                SourceHealthyAgainSec = Math.Round(r.Timeline.SourceHealthyAgain.TotalSeconds, 3),
            },
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.KafkaFailoverJsonOutput));
    }

    private static string DirectionLabel(KafkaFailoverDirection d) => d switch
    {
        KafkaFailoverDirection.EastToWest => "east-to-west",
        KafkaFailoverDirection.WestToEast => "west-to-east",
        _ => d.ToString(),
    };

    private static string RecoveryLabel(KafkaFailoverRecoveryStatus s) => s switch
    {
        KafkaFailoverRecoveryStatus.Recovered => "recovered",
        KafkaFailoverRecoveryStatus.RecoveryFailed => "failed",
        KafkaFailoverRecoveryStatus.NotAttempted => "not-attempted",
        _ => s.ToString().ToLowerInvariant(),
    };
}
