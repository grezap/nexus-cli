using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core.Models;
using Spectre.Console;

namespace Nexus.Cli.Commands.Infrastructure;

internal static class InfrastructureRender
{
    public static void EmitListHuman(IReadOnlyList<VmStatus> rows)
    {
        AnsiConsole.Write(new Rule($"[bold]Infrastructure[/] · {rows.Count} VMs declared in vms.yaml")
        {
            Justification = Justify.Left
        });
        var table = new Table()
            .AddColumns("Cluster", "Name", "State", "VMnet10", "VMnet11", "Role");
        foreach (var r in rows)
            table.AddRow(
                r.ClusterName,
                r.Node.Name,
                ColorState(r.State),
                r.Node.Vmnet10,
                r.Node.Vmnet11,
                Markup.Escape(r.Node.Role));
        AnsiConsole.Write(table);
    }

    public static void EmitListJson(IReadOnlyList<VmStatus> rows)
    {
        var output = new InfrastructureListJsonOutput
        {
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("u"),
            Vms = rows.Select(ToJson).ToList()
        };
        Console.WriteLine(JsonSerializer.Serialize(output, NexusJsonContext.Default.InfrastructureListJsonOutput));
    }

    public static void EmitStatusHuman(string cluster, string? node, IReadOnlyList<VmStatus> rows)
    {
        var subtitle = node is null ? cluster : $"{cluster} / {node}";
        AnsiConsole.Write(new Rule($"[bold]{subtitle}[/] · {rows.Count} VMs")
        {
            Justification = Justify.Left
        });
        var table = new Table()
            .AddColumns("Name", "State", "OS", "VMnet10", "VMnet11", "VMX");
        foreach (var r in rows)
            table.AddRow(
                r.Node.Name,
                ColorState(r.State),
                r.Node.Os,
                r.Node.Vmnet10,
                r.Node.Vmnet11,
                Markup.Escape(r.VmxPath));
        AnsiConsole.Write(table);
    }

    public static void EmitStatusJson(string cluster, string? node, IReadOnlyList<VmStatus> rows)
    {
        var output = new InfrastructureStatusJsonOutput
        {
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("u"),
            Cluster = cluster,
            Node = node,
            Vms = rows.Select(ToJson).ToList()
        };
        Console.WriteLine(JsonSerializer.Serialize(output, NexusJsonContext.Default.InfrastructureStatusJsonOutput));
    }

    public static void EmitOpsHuman(string verb, string cluster, IReadOnlyList<OpResult> ops)
    {
        AnsiConsole.Write(new Rule($"[bold]{verb}[/] · {cluster} · {ops.Count} VMs")
        {
            Justification = Justify.Left
        });
        foreach (var o in ops)
        {
            var glyph = o.Success ? "[green]✓[/]" : "[red]✗[/]";
            // MarkupLine (not MarkupLineInterpolated) so the pre-rendered glyph
            // markup parses; user-controlled fields are still explicitly escaped.
            AnsiConsole.MarkupLine($"  {glyph} {Markup.Escape(o.NodeName),-22} {Markup.Escape(o.Message)}");
        }
    }

    public static void EmitOpsJson(string verb, string cluster, IReadOnlyList<OpResult> ops)
    {
        var output = new InfrastructureOpsJsonOutput
        {
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("u"),
            Cluster = cluster,
            Verb = verb,
            Ops = ops.Select(o => new OpResultJson
            {
                Node = o.NodeName,
                Success = o.Success,
                Message = o.Message
            }).ToList()
        };
        Console.WriteLine(JsonSerializer.Serialize(output, NexusJsonContext.Default.InfrastructureOpsJsonOutput));
    }

    public static string ColorState(VmRuntimeState state) => state switch
    {
        VmRuntimeState.Running => "[green]running[/]",
        VmRuntimeState.Suspended => "[yellow]suspended[/]",
        VmRuntimeState.Stopped => "[grey]stopped[/]",
        VmRuntimeState.Missing => "[red]missing[/]",
        _ => "[grey]unknown[/]"
    };

    private static VmStatusJson ToJson(VmStatus s) => new()
    {
        Cluster = s.ClusterName,
        Name = s.Node.Name,
        State = s.State.ToString().ToLowerInvariant(),
        Os = s.Node.Os,
        Vmnet10 = s.Node.Vmnet10,
        Vmnet11 = s.Node.Vmnet11,
        Vmx = s.VmxPath,
        Role = s.Node.Role
    };
}
