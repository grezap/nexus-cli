using System.ComponentModel;
using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core.Models;
using Nexus.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nexus.Cli.Commands;

public sealed class ClusterStatusSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Emit JSON to stdout instead of the human table view.")]
    public bool Json { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Include per-component HTTP timing in the human view.")]
    public bool Verbose { get; set; }

    [CommandOption("--no-color")]
    [Description("Disable ANSI color in the human view.")]
    public bool NoColor { get; set; }
}

public sealed class ClusterStatusCommand : AsyncCommand<ClusterStatusSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ClusterStatusSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.NoColor)
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        using var bootstrapper = new NexusBootstrapper(
            new Adapters.Vault.VaultTokenResolver(new Adapters.Vault.ProcessEnvironmentReader()));

        ClusterStatusReport report;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var service = await bootstrapper.BuildClusterStatusServiceAsync(cts.Token);
            report = await service.GetStatusAsync(cts.Token);
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
            return 2;
        }

        if (settings.Json)
        {
            EmitJson(report);
        }
        else
        {
            EmitHuman(report, settings.Verbose);
        }

        return report.Overall == HealthLevel.Red ? 1 : 0;
    }

    private static void EmitHuman(ClusterStatusReport report, bool verbose)
    {
        var (label, color) = report.Overall switch
        {
            HealthLevel.Green => ("● GREEN", "green"),
            HealthLevel.Yellow => ("● YELLOW", "yellow"),
            _ => ("● RED", "red")
        };

        AnsiConsole.Write(new Rule($"[{color}]{label}[/]  Cluster status  ({report.CapturedAtUtc:u})")
        {
            Justification = Justify.Left
        });

        // ---- Consul ----
        if (report.Consul.IsOk)
        {
            var ch = report.Consul.Value!;
            var t = new Table().Title("[bold]Consul[/]")
                .AddColumns("Name", "Address", "Status", "Role");
            foreach (var m in ch.Members)
                t.AddRow(m.Name, $"{m.Addr}:{m.Port}", Color(m.Status), m.Role);
            t.Caption($"[grey]{ch.Alive} alive · {ch.Failed} failed · leader: {ch.Leader ?? "(none)"}[/]");
            AnsiConsole.Write(t);
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Consul:[/] {report.Consul.Error}");
        }

        // ---- Nomad ----
        if (report.Nomad.IsOk)
        {
            var nh = report.Nomad.Value!;
            var st = new Table().Title("[bold]Nomad servers[/]")
                .AddColumns("Name", "Address", "Leader");
            foreach (var s in nh.Servers)
                st.AddRow(s.Name, s.Address, s.IsLeader ? "[green]●[/]" : "");
            AnsiConsole.Write(st);

            var ct = new Table().Title("[bold]Nomad clients[/]")
                .AddColumns("Name", "Address", "Status", "Class");
            foreach (var c in nh.Clients)
                ct.AddRow(c.Name, c.Address, Color(c.Status), c.NodeClass);
            AnsiConsole.Write(ct);
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Nomad:[/] {report.Nomad.Error}");
        }

        // ---- Portainer ----
        if (report.Portainer.IsOk)
        {
            var p = report.Portainer.Value!;
            AnsiConsole.MarkupLineInterpolated(
                $"[bold]Portainer:[/] {(p.Reachable ? "[green]reachable[/]" : "[red]unreachable[/]")} · v{p.Version}");
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Portainer:[/] {report.Portainer.Error}");
        }

        if (verbose)
        {
            AnsiConsole.MarkupLine("[grey]verbose timings: not yet wired (planned v0.2)[/]");
        }
    }

    private static string Color(string status) => status.ToLowerInvariant() switch
    {
        "alive" or "ready" => $"[green]{status}[/]",
        "leaving" or "init" or "down" => $"[yellow]{status}[/]",
        "failed" or "left" or "error" => $"[red]{status}[/]",
        _ => status
    };

    private static void EmitJson(ClusterStatusReport report)
    {
        var output = new ClusterStatusJsonOutput
        {
            Overall = report.Overall.ToString().ToLowerInvariant(),
            CapturedAtUtc = report.CapturedAtUtc.ToString("u")
        };

        if (report.Consul.IsOk)
        {
            var ch = report.Consul.Value!;
            output.Consul = new ConsulSection
            {
                Alive = ch.Alive,
                Failed = ch.Failed,
                Leader = ch.Leader,
                Members = ch.Members.Select(m => new ConsulMemberJson
                {
                    Name = m.Name,
                    Addr = $"{m.Addr}:{m.Port}",
                    Status = m.Status,
                    Role = m.Role
                }).ToList()
            };
        }
        else
        {
            output.Consul = new ConsulSection { Error = report.Consul.Error };
        }

        if (report.Nomad.IsOk)
        {
            var nh = report.Nomad.Value!;
            output.Nomad = new NomadSection
            {
                LeaderAddress = nh.LeaderAddress,
                Servers = nh.Servers.Select(s => new NomadServerJson
                {
                    Name = s.Name,
                    Address = s.Address,
                    IsLeader = s.IsLeader
                }).ToList(),
                Clients = nh.Clients.Select(c => new NomadClientJson
                {
                    Name = c.Name,
                    Address = c.Address,
                    Status = c.Status,
                    NodeClass = c.NodeClass
                }).ToList()
            };
        }
        else
        {
            output.Nomad = new NomadSection { Error = report.Nomad.Error };
        }

        if (report.Portainer.IsOk)
        {
            var p = report.Portainer.Value!;
            output.Portainer = new PortainerSection
            {
                Version = p.Version,
                InstanceId = p.InstanceId,
                Reachable = p.Reachable
            };
        }
        else
        {
            output.Portainer = new PortainerSection { Error = report.Portainer.Error };
        }

        var json = JsonSerializer.Serialize(output, NexusJsonContext.Default.ClusterStatusJsonOutput);
        Console.WriteLine(json);
    }
}
