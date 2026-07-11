using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core.Models;
using Spectre.Console;

namespace Nexus.Cli.Commands.Infrastructure;

/// <summary>Rendering helpers for the infrastructure verbs (human tables via Spectre.Console, JSON via source-gen DTOs).</summary>
internal static class InfrastructureRender
{
    /// <summary>Renders the full VM inventory as a human table.</summary>
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

    /// <summary>Emits the full VM inventory as JSON.</summary>
    public static void EmitListJson(IReadOnlyList<VmStatus> rows)
    {
        var output = new InfrastructureListJsonOutput
        {
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("u"),
            Vms = rows.Select(ToJson).ToList()
        };
        Console.WriteLine(JsonSerializer.Serialize(output, NexusJsonContext.Default.InfrastructureListJsonOutput));
    }

    /// <summary>Renders a per-cluster/per-node VM status as a human table.</summary>
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

    /// <summary>Emits a per-cluster/per-node VM status as JSON.</summary>
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

    /// <summary>Renders the per-node outcomes of a mutating verb (suspend/resume) for humans.</summary>
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

    /// <summary>Emits the per-node outcomes of a mutating verb as JSON.</summary>
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

    /// <summary>Maps a VM runtime state to its Spectre.Console color-markup label.</summary>
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
