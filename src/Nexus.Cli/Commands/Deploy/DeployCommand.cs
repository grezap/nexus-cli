using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Deploy;

/// <summary>
/// Implements <c>deploy &lt;project&gt;</c>: builds the end-to-end deploy plan for an application project
/// and, by default, prints it (a dry-run). With <c>--execute --yes</c> it runs the plan's steps — build
/// the images, apply the migrations, deploy the Api tier — stopping on the first failure.
/// </summary>
public sealed class DeployCommand : AsyncCommand<DeploySettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        DeploySettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        var repoPath = string.IsNullOrWhiteSpace(settings.RepoPath) ? Environment.CurrentDirectory : settings.RepoPath!;

        var planResult = DeployBootstrapper.BuildPlanner().BuildPlan(settings.Project, repoPath);
        if (planResult.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {planResult.Error}");
            return 2;
        }

        var plan = planResult.Value!;

        if (!settings.Execute)
        {
            if (settings.Json)
                DeployRender.EmitPlanJson(plan);
            else
                DeployRender.EmitPlanHuman(plan);
            return 0;
        }

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]error:[/] --execute runs the {plan.Steps.Count}-step deploy against your configured target; pass --yes to confirm.");
            return 2;
        }

        try
        {
            var reportResult = await DeployBootstrapper.BuildRunner().ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
            if (reportResult.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {reportResult.Error}");
                return 2;
            }

            var report = reportResult.Value!;
            if (settings.Json)
                DeployRender.EmitReportJson(report);
            else
                DeployRender.EmitReportHuman(report);
            return report.Status == DeployStatus.Ok ? 0 : 1;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
