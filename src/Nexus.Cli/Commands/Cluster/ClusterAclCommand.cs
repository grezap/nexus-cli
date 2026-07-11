using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands.Cluster;

/// <summary>Implements <c>acl &lt;cluster&gt; &lt;verb&gt;</c>: reads (list/describe) or mutates (grant/revoke) a cluster's access-control state. Mutations guarded by <c>--yes</c>.</summary>
public sealed class ClusterAclCommand : AsyncCommand<ClusterAclSettings>
{
    /// <inheritdoc />
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterAclSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        var verb = settings.Verb.ToLowerInvariant();
        if (verb is not ("list" or "describe" or "grant" or "revoke"))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] unknown ACL verb '{settings.Verb}'; expected list|describe|grant|revoke.");
            return 2;
        }
        if (verb is "grant" or "revoke" && !settings.Yes)
        {
            AnsiConsole.MarkupLine($"[yellow]Mutating op:[/] {verb} permissions on cluster [bold]{Markup.Escape(settings.Cluster)}[/] for user [bold]{Markup.Escape(settings.User ?? "")}[/].");
            if (Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine($"[red]aborted:[/] acl {verb} requires interactive confirmation; pass --yes for non-interactive use.");
                return 3;
            }
            if (!AnsiConsole.Confirm("Proceed?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[grey]aborted by user.[/]");
                return 3;
            }
        }

        var registry = ClusterBootstrapper.BuildRegistry();
        var adapterResult = registry.GetAdapter(settings.Cluster);
        if (adapterResult.IsFail)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {adapterResult.Error}");
            return 2;
        }

        var perms = string.IsNullOrWhiteSpace(settings.Permissions)
            ? null
            : settings.Permissions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            var op = new AclOperation(verb, settings.User, perms);
            var r = await adapterResult.Value!.AclAsync(op, cts.Token).ConfigureAwait(false);
            if (r.IsFail)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {r.Error}");
                return 2;
            }
            if (settings.Json) ClusterRender.EmitAclJson(r.Value!);
            else ClusterRender.EmitAclHuman(r.Value!);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }
    }
}
