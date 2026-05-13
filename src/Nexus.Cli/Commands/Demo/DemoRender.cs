using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core.Models;
using Spectre.Console;

namespace Nexus.Cli.Commands.Demo;

internal static class DemoRender
{
    public static void EmitListHuman(IEnumerable<DemoSpec> demos)
    {
        var arr = demos.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        AnsiConsole.Write(new Rule($"[bold]Demos[/] · {arr.Count} available")
        {
            Justification = Justify.Left
        });
        if (arr.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no demos found. Set NEXUS_DEMOS_PATH or place <id>.json files under ./docs/demos/.)[/]");
            return;
        }
        var t = new Table().AddColumns("Id", "Title", "Steps");
        foreach (var d in arr)
            t.AddRow(Markup.Escape(d.Id), Markup.Escape(d.Title), d.Steps.Count.ToString());
        AnsiConsole.Write(t);
    }

    public static void EmitListJson(IEnumerable<DemoSpec> demos)
    {
        var dtos = demos.Select(d => new DemoSpecJson
        {
            Id = d.Id,
            Title = d.Title,
            Description = d.Description,
            Steps = d.Steps.Select(s => new DemoStepJson { Command = s.Command, WaitAfterSeconds = s.WaitAfterSeconds }).ToList()
        }).ToList();
        // No top-level list shape registered; emit as JSON array via a wrapping
        // string serialization (loops, source-gen still drives each item).
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        for (var i = 0; i < dtos.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(dtos[i], NexusJsonContext.Default.DemoSpecJson));
        }
        sb.Append(']');
        Console.WriteLine(sb.ToString());
    }

    public static void EmitRunHuman(DemoRunReport report)
    {
        var (label, color) = report.Status switch
        {
            DemoStatus.Ok => ("● GREEN  ok", "green"),
            DemoStatus.StepFailed => ("● RED  step failed", "red"),
            DemoStatus.Aborted => ("● YELLOW  aborted", "yellow"),
            _ => ("● YELLOW  partial", "yellow")
        };
        AnsiConsole.Write(new Rule(
            $"[{color}]{label}[/]  demo run {Markup.Escape(report.DemoId)}  ({report.StartedAtUtc:u})  total {report.TotalDuration.TotalSeconds:F2}s")
        {
            Justification = Justify.Left
        });

        var t = new Table().AddColumns("#", "Exit", "Duration", "Command");
        foreach (var s in report.Steps)
        {
            var exit = s.ExitCode == 0 ? $"[green]{s.ExitCode}[/]" : $"[red]{s.ExitCode}[/]";
            t.AddRow(
                s.StepIndex.ToString(),
                exit,
                $"{s.Duration.TotalSeconds:F2}s",
                Markup.Escape(s.Command));
        }
        AnsiConsole.Write(t);

        var failed = report.Steps.FirstOrDefault(s => s.ExitCode != 0);
        if (failed is not null)
        {
            AnsiConsole.MarkupLine($"[red]first failed step #{failed.StepIndex} stderr tail:[/]");
            AnsiConsole.WriteLine(failed.StderrTail.Length > 0 ? failed.StderrTail : "(empty)");
        }
    }

    public static void EmitRunJson(DemoRunReport r)
    {
        var dto = new DemoRunReportJson
        {
            DemoId = r.DemoId,
            Title = r.Title,
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
            Status = r.Status.ToString().ToLowerInvariant(),
            TotalDurationSec = Math.Round(r.TotalDuration.TotalSeconds, 3),
            Steps = r.Steps.Select(s => new DemoStepResultJson
            {
                StepIndex = s.StepIndex,
                Command = s.Command,
                ExitCode = s.ExitCode,
                StdoutTail = s.StdoutTail,
                StderrTail = s.StderrTail,
                DurationSec = Math.Round(s.Duration.TotalSeconds, 3)
            }).ToList()
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.DemoRunReportJson));
    }

    public static void EmitRecordHuman(DemoRecordReport r)
    {
        var (label, color) = r.VhsAvailable
            ? ("● GREEN  recorded", "green")
            : ("● YELLOW  tape written; vhs unavailable", "yellow");
        AnsiConsole.Write(new Rule(
            $"[{color}]{label}[/]  demo record {Markup.Escape(r.DemoId)}  ({r.StartedAtUtc:u})  {r.Duration.TotalSeconds:F2}s")
        {
            Justification = Justify.Left
        });
        AnsiConsole.MarkupLine($"  tape           : [bold]{Markup.Escape(r.TapeFilePath)}[/]");
        if (!string.IsNullOrEmpty(r.OutputFilePath))
            AnsiConsole.MarkupLine($"  output         : [bold]{Markup.Escape(r.OutputFilePath)}[/]");
        if (!r.VhsAvailable && !string.IsNullOrEmpty(r.VhsUnavailableMessage))
            AnsiConsole.MarkupLine($"  vhs hint       : [yellow]{Markup.Escape(r.VhsUnavailableMessage)}[/]");
    }

    public static void EmitRecordJson(DemoRecordReport r)
    {
        var dto = new DemoRecordReportJson
        {
            DemoId = r.DemoId,
            Title = r.Title,
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
            TapeFilePath = r.TapeFilePath,
            OutputFilePath = r.OutputFilePath,
            VhsAvailable = r.VhsAvailable,
            VhsUnavailableMessage = r.VhsUnavailableMessage,
            DurationSec = Math.Round(r.Duration.TotalSeconds, 3)
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.DemoRecordReportJson));
    }
}
