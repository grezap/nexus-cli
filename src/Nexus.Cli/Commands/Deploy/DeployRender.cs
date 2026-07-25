using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core.Models;
using Spectre.Console;

namespace Nexus.Cli.Commands.Deploy;

/// <summary>Spectre + JSON rendering for the <c>deploy</c> verb (plan dry-run + execution report).</summary>
public static class DeployRender
{
    /// <summary>Renders a deploy plan as a human-readable step table (the dry-run view).</summary>
    public static void EmitPlanHuman(DeployPlan plan)
    {
        AnsiConsole.MarkupLineInterpolated($"[bold]deploy plan[/] · [green]{plan.Project}[/] · {plan.Steps.Count} steps · path [grey]{plan.RepoPath}[/]");
        AnsiConsole.MarkupLine("[grey](dry-run — re-run with --execute --yes to apply)[/]");
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("step");
        table.AddColumn("command");
        table.AddColumn("what");
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.Steps[i];
            table.AddRow(
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Markup.Escape(s.Name),
                Markup.Escape(s.Command),
                Markup.Escape(s.Description));
        }

        AnsiConsole.Write(table);
    }

    /// <summary>Emits the deploy plan as source-generated JSON.</summary>
    public static void EmitPlanJson(DeployPlan plan)
    {
        var dto = new DeployPlanJson
        {
            Project = plan.Project,
            RepoPath = plan.RepoPath,
            Steps = [.. plan.Steps.Select(s => new DeployStepJson { Name = s.Name, Command = s.Command, Description = s.Description })],
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.DeployPlanJson));
    }

    /// <summary>Renders an execution report as a human-readable summary.</summary>
    public static void EmitReportHuman(DeployReport report)
    {
        var colour = report.Status == DeployStatus.Ok ? "green" : "red";
        AnsiConsole.MarkupLineInterpolated($"[bold]deploy[/] · [green]{report.Project}[/] · [{colour}]{report.Status}[/] · {report.TotalDuration.TotalSeconds:F1}s");
        foreach (var s in report.Steps)
        {
            var mark = s.ExitCode == 0 ? "[green]✓[/]" : "[red]✗[/]";
            AnsiConsole.MarkupLineInterpolated($"  {mark} {s.Name} (exit {s.ExitCode}, {s.Duration.TotalSeconds:F1}s)");
        }
    }

    /// <summary>Emits the execution report as source-generated JSON.</summary>
    public static void EmitReportJson(DeployReport report)
    {
        var dto = new DeployReportJson
        {
            Project = report.Project,
            Status = report.Status.ToString(),
            TotalDurationSec = report.TotalDuration.TotalSeconds,
            Steps = [.. report.Steps.Select(s => new DeployStepResultJson
            {
                Name = s.Name,
                ExitCode = s.ExitCode,
                DurationSec = s.Duration.TotalSeconds,
                OutputTail = s.OutputTail,
            })],
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.DeployReportJson));
    }
}
