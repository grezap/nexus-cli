using System.Text.Json;
using Nexus.Cli.Adapters.Json;
using Nexus.Cli.Core.Models;
using Spectre.Console;

namespace Nexus.Cli.Commands.Cluster;

// ===========================================================================
// Render helpers for the cluster verbs. Human output via Spectre.Console;
// JSON output via NexusJsonContext source-gen DTOs (AOT-clean).
// Each EmitX{Human,Json} pair takes the SPI domain record + emits to stdout.
// ===========================================================================

/// <summary>
/// Rendering helpers for the cluster verbs: each <c>EmitXHuman</c> writes a
/// Spectre.Console table/rule view and each <c>EmitXJson</c> emits a
/// source-generated (AOT-clean) DTO to stdout for the matching SPI record.
/// </summary>
internal static class ClusterRender
{
    private static string HealthColor(string overall) => overall switch
    {
        "green" => "green",
        "yellow" => "yellow",
        "red" => "red",
        _ => "grey",
    };

    private static string HealthGlyph(string overall) => overall switch
    {
        "green" => "●",
        "yellow" => "●",
        "red" => "●",
        _ => "○",
    };

    // --- cluster-status ----------------------------------------------------

    /// <summary>Renders a cluster-status snapshot as a human members table.</summary>
    public static void EmitClusterStatusHuman(ClusterStatus s)
    {
        var color = HealthColor(s.OverallHealth);
        AnsiConsole.Write(new Rule(
            $"[{color}]{HealthGlyph(s.OverallHealth)}[/]  cluster-status [bold]{Markup.Escape(s.ClusterId)}[/]  ({Markup.Escape(s.DisplayName)})  {s.CapturedAtUtc:u}")
        { Justification = Justify.Left });

        var t = new Table().AddColumns("Hostname", "IP", "Role", "Status", "Shard");
        foreach (var m in s.Members)
        {
            t.AddRow(
                Markup.Escape(m.Hostname),
                Markup.Escape(m.IpAddress),
                Markup.Escape(m.Role),
                ColorStatus(m.Status),
                Markup.Escape(m.ShardId ?? "-"));
        }
        AnsiConsole.Write(t);

        if (!string.IsNullOrEmpty(s.Leader))
            AnsiConsole.MarkupLineInterpolated($"  leader: [bold]{s.Leader}[/]");
    }

    /// <summary>Emits a cluster-status snapshot as JSON.</summary>
    public static void EmitClusterStatusJson(ClusterStatus s)
    {
        var dto = new ClusterStatusOutputJson
        {
            ClusterId = s.ClusterId,
            DisplayName = s.DisplayName,
            OverallHealth = s.OverallHealth,
            Leader = s.Leader,
            CapturedAtUtc = s.CapturedAtUtc.ToString("u"),
            Members = s.Members.Select(m => new ClusterMemberJson
            {
                Hostname = m.Hostname,
                Ip = m.IpAddress,
                Role = m.Role,
                Status = m.Status,
                ShardId = m.ShardId,
                ReplicationLagSeconds = m.ReplicationLagSeconds,
            }).ToList(),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterStatusOutputJson));
    }

    // --- failover-test -----------------------------------------------------

    /// <summary>Renders a failover-test result (RTO, primaries, phase timeline) for humans.</summary>
    public static void EmitFailoverHuman(FailoverResult r)
    {
        var color = r.NewPrimary is null ? "red" : "green";
        var label = r.NewPrimary is null ? "● RED  no new primary observed" : "● GREEN  failover ok";
        AnsiConsole.Write(new Rule(
            $"[{color}]{label}[/]  failover-test [bold]{Markup.Escape(r.Scenario)}[/]  ({r.StartedAtUtc:u})  rto={r.Rto.TotalSeconds:F2}s")
        { Justification = Justify.Left });
        AnsiConsole.MarkupLineInterpolated($"  original primary : [bold]{Markup.Escape(r.OriginalPrimary)}[/]");
        AnsiConsole.MarkupLineInterpolated($"  new primary      : [bold]{Markup.Escape(r.NewPrimary ?? "(none)")}[/]");
        AnsiConsole.MarkupLineInterpolated($"  recovery         : [bold]{Markup.Escape(r.Recovery)}[/]");
        if (!string.IsNullOrEmpty(r.RecoveryHint))
            AnsiConsole.MarkupLineInterpolated($"  recovery hint    : [yellow]{Markup.Escape(r.RecoveryHint)}[/]");

        var tl = new Table().AddColumns("phase", "offset (s)");
        tl.AddRow("pre-flight done", $"{r.Timeline.PreFlightCompleted.TotalSeconds:F2}");
        tl.AddRow("failure injected", $"{r.Timeline.FailureInjected.TotalSeconds:F2}");
        tl.AddRow("new primary observed", $"{r.Timeline.NewLeaderObserved.TotalSeconds:F2}");
        tl.AddRow("recovery attempted", $"{r.Timeline.RecoveryAttempted.TotalSeconds:F2}");
        tl.AddRow("cluster healthy again", $"{r.Timeline.ClusterHealthyAgain.TotalSeconds:F2}");
        AnsiConsole.Write(tl);
    }

    /// <summary>Emits a failover-test result as JSON.</summary>
    public static void EmitFailoverJson(FailoverResult r)
    {
        var dto = new ClusterFailoverOutputJson
        {
            Scenario = r.Scenario,
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
            OriginalPrimary = r.OriginalPrimary,
            NewPrimary = r.NewPrimary,
            RtoSeconds = Math.Round(r.Rto.TotalSeconds, 3),
            Recovery = r.Recovery,
            RecoveryHint = r.RecoveryHint,
            Timeline = new FailoverTimelineSecondsJson
            {
                PreFlightCompletedSec = Math.Round(r.Timeline.PreFlightCompleted.TotalSeconds, 3),
                FailureInjectedSec = Math.Round(r.Timeline.FailureInjected.TotalSeconds, 3),
                NewLeaderObservedSec = Math.Round(r.Timeline.NewLeaderObserved.TotalSeconds, 3),
                RecoveryAttemptedSec = Math.Round(r.Timeline.RecoveryAttempted.TotalSeconds, 3),
                ClusterHealthyAgainSec = Math.Round(r.Timeline.ClusterHealthyAgain.TotalSeconds, 3),
            },
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterFailoverOutputJson));
    }

    // --- scale-out ---------------------------------------------------------

    /// <summary>Renders a scale-out (add/remove) result and affected nodes for humans.</summary>
    public static void EmitScaleOutHuman(ScaleOutResult r)
    {
        var color = r.Outcome == "ok" ? "green" : r.Outcome == "partial" ? "yellow" : "red";
        AnsiConsole.Write(new Rule(
            $"[{color}]● {r.Outcome.ToUpperInvariant()}[/]  scale-out [bold]{Markup.Escape(r.OperationType)}[/]  ({r.StartedAtUtc:u})  {r.Duration.TotalSeconds:F2}s")
        { Justification = Justify.Left });
        if (!string.IsNullOrEmpty(r.OutcomeReason))
            AnsiConsole.MarkupLineInterpolated($"  reason : [yellow]{Markup.Escape(r.OutcomeReason)}[/]");
        if (r.AffectedNodes.Count > 0)
        {
            var t = new Table().AddColumns("affected node");
            foreach (var n in r.AffectedNodes)
                t.AddRow(Markup.Escape(n));
            AnsiConsole.Write(t);
        }
    }

    /// <summary>Emits a scale-out result as JSON.</summary>
    public static void EmitScaleOutJson(ScaleOutResult r)
    {
        var dto = new ClusterScaleOutOutputJson
        {
            OperationType = r.OperationType,
            Outcome = r.Outcome,
            OutcomeReason = r.OutcomeReason,
            AffectedNodes = r.AffectedNodes.ToList(),
            DurationSec = Math.Round(r.Duration.TotalSeconds, 3),
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterScaleOutOutputJson));
    }

    // --- scale-up ----------------------------------------------------------

    /// <summary>Renders a scale-up (VM resize) result with old/new resource values for humans.</summary>
    public static void EmitScaleUpHuman(ScaleUpResult r)
    {
        var color = r.Outcome == "ok" ? "green" : r.Outcome == "skipped" ? "yellow" : "red";
        AnsiConsole.Write(new Rule(
            $"[{color}]● {r.Outcome.ToUpperInvariant()}[/]  scale-up [bold]{Markup.Escape(r.VmName)}[/]  {r.Duration.TotalSeconds:F2}s")
        { Justification = Justify.Left });
        if (!string.IsNullOrEmpty(r.OutcomeReason))
            AnsiConsole.MarkupLineInterpolated($"  reason : [yellow]{Markup.Escape(r.OutcomeReason)}[/]");
        var t = new Table().AddColumns("resource", "old", "new");
        t.AddRow("cpu", r.OldCpu?.ToString() ?? "-", r.NewCpu?.ToString() ?? "-");
        t.AddRow("ram (MB)", r.OldRamMb?.ToString() ?? "-", r.NewRamMb?.ToString() ?? "-");
        t.AddRow("disk (GB)", r.OldDiskGb?.ToString() ?? "-", r.NewDiskGb?.ToString() ?? "-");
        AnsiConsole.Write(t);
    }

    /// <summary>Emits a scale-up result as JSON.</summary>
    public static void EmitScaleUpJson(ScaleUpResult r)
    {
        var dto = new ClusterScaleUpOutputJson
        {
            VmName = r.VmName,
            Outcome = r.Outcome,
            OutcomeReason = r.OutcomeReason,
            OldCpu = r.OldCpu,
            NewCpu = r.NewCpu,
            OldRamMb = r.OldRamMb,
            NewRamMb = r.NewRamMb,
            OldDiskGb = r.OldDiskGb,
            NewDiskGb = r.NewDiskGb,
            DurationSec = Math.Round(r.Duration.TotalSeconds, 3),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterScaleUpOutputJson));
    }

    // --- health ------------------------------------------------------------

    /// <summary>Renders a health report (per-probe status table) for humans.</summary>
    public static void EmitHealthHuman(HealthReport r)
    {
        var color = HealthColor(r.OverallHealth);
        AnsiConsole.Write(new Rule(
            $"[{color}]{HealthGlyph(r.OverallHealth)}[/]  health [bold]{Markup.Escape(r.ClusterId)}[/]  overall={r.OverallHealth}  {r.CapturedAtUtc:u}")
        { Justification = Justify.Left });
        var t = new Table().AddColumns("probe", "target", "status", "value", "threshold");
        foreach (var p in r.Probes)
        {
            t.AddRow(
                Markup.Escape(p.Name),
                Markup.Escape(p.Target),
                ColorStatus(p.Status),
                Markup.Escape(p.Value ?? "-"),
                Markup.Escape(p.Threshold ?? "-"));
        }
        AnsiConsole.Write(t);
    }

    /// <summary>Emits a health report as JSON.</summary>
    public static void EmitHealthJson(HealthReport r)
    {
        var dto = new ClusterHealthOutputJson
        {
            ClusterId = r.ClusterId,
            OverallHealth = r.OverallHealth,
            CapturedAtUtc = r.CapturedAtUtc.ToString("u"),
            Probes = r.Probes.Select(p => new HealthProbeJson
            {
                Name = p.Name,
                Target = p.Target,
                Status = p.Status,
                Value = p.Value,
                Threshold = p.Threshold,
            }).ToList(),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterHealthOutputJson));
    }

    // --- topology ----------------------------------------------------------

    /// <summary>Renders a topology snapshot (nodes and, if present, shards) for humans.</summary>
    public static void EmitTopologyHuman(TopologySnapshot r)
    {
        AnsiConsole.Write(new Rule(
            $"topology [bold]{Markup.Escape(r.ClusterId)}[/]  ({r.CapturedAtUtc:u})")
        { Justification = Justify.Left });
        var nt = new Table().AddColumns("hostname", "role", "status", "lag (s)");
        foreach (var n in r.Nodes)
            nt.AddRow(Markup.Escape(n.Hostname), Markup.Escape(n.Role), ColorStatus(n.Status), n.ReplicationLagSeconds?.ToString("F1") ?? "-");
        AnsiConsole.Write(nt);
        if (r.Shards is { Count: > 0 })
        {
            var st = new Table().AddColumns("shard", "primary", "replicas", "slot range");
            foreach (var sh in r.Shards)
                st.AddRow(Markup.Escape(sh.ShardId), Markup.Escape(sh.Primary), Markup.Escape(string.Join(", ", sh.Replicas)), Markup.Escape(sh.SlotRange ?? "-"));
            AnsiConsole.Write(st);
        }
    }

    /// <summary>Emits a topology snapshot as JSON.</summary>
    public static void EmitTopologyJson(TopologySnapshot r)
    {
        var dto = new ClusterTopologyOutputJson
        {
            ClusterId = r.ClusterId,
            CapturedAtUtc = r.CapturedAtUtc.ToString("u"),
            Nodes = r.Nodes.Select(n => new TopologyNodeJson
            {
                Hostname = n.Hostname,
                Role = n.Role,
                Status = n.Status,
                ReplicationLagSeconds = n.ReplicationLagSeconds,
            }).ToList(),
            Shards = r.Shards?.Select(s => new TopologyShardJson
            {
                ShardId = s.ShardId,
                Primary = s.Primary,
                Replicas = s.Replicas.ToList(),
                SlotRange = s.SlotRange,
            }).ToList(),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterTopologyOutputJson));
    }

    // --- backup take -------------------------------------------------------

    /// <summary>Renders a backup-take result (id, size, destination) for humans.</summary>
    public static void EmitBackupHuman(BackupResult r)
    {
        AnsiConsole.Write(new Rule(
            $"[green]● GREEN[/]  backup take [bold]{Markup.Escape(r.BackupId)}[/]  {FormatBytes(r.SizeBytes)}  {r.Duration.TotalSeconds:F2}s")
        { Justification = Justify.Left });
        AnsiConsole.MarkupLineInterpolated($"  destination : [bold]{Markup.Escape(r.Destination)}[/]");
        AnsiConsole.MarkupLineInterpolated($"  started     : {r.StartedAtUtc:u}");
    }

    /// <summary>Emits a backup-take result as JSON.</summary>
    public static void EmitBackupJson(BackupResult r)
    {
        var dto = new ClusterBackupOutputJson
        {
            BackupId = r.BackupId,
            Destination = r.Destination,
            SizeBytes = r.SizeBytes,
            DurationSec = Math.Round(r.Duration.TotalSeconds, 3),
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterBackupOutputJson));
    }

    // --- backup restore ----------------------------------------------------

    /// <summary>Renders a backup-restore result for humans.</summary>
    public static void EmitRestoreHuman(RestoreResult r)
    {
        AnsiConsole.Write(new Rule(
            $"[green]● GREEN[/]  backup restore [bold]{Markup.Escape(r.BackupId)}[/]  {r.Duration.TotalSeconds:F2}s")
        { Justification = Justify.Left });
        AnsiConsole.MarkupLineInterpolated($"  items restored : [bold]{r.ItemsRestored}[/]");
        AnsiConsole.MarkupLineInterpolated($"  started        : {r.StartedAtUtc:u}");
    }

    /// <summary>Emits a backup-restore result as JSON.</summary>
    public static void EmitRestoreJson(RestoreResult r)
    {
        var dto = new ClusterRestoreOutputJson
        {
            BackupId = r.BackupId,
            ItemsRestored = r.ItemsRestored,
            DurationSec = Math.Round(r.Duration.TotalSeconds, 3),
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterRestoreOutputJson));
    }

    // --- cert-rotate -------------------------------------------------------

    /// <summary>Renders a cert-rotation result (per-node old/new serials) for humans.</summary>
    public static void EmitCertRotationHuman(CertRotationResult r)
    {
        var any = r.RotatedNodes.Any(n => !string.IsNullOrEmpty(n.Error));
        var color = any ? "yellow" : "green";
        var label = any ? "● YELLOW  cert-rotate partial" : "● GREEN  cert-rotate ok";
        AnsiConsole.Write(new Rule(
            $"[{color}]{label}[/]  {r.Duration.TotalSeconds:F2}s")
        { Justification = Justify.Left });
        var t = new Table().AddColumns("hostname", "old serial", "new serial", "error");
        foreach (var n in r.RotatedNodes)
            t.AddRow(Markup.Escape(n.Hostname), Markup.Escape(n.OldSerial), Markup.Escape(n.NewSerial), Markup.Escape(n.Error ?? "-"));
        AnsiConsole.Write(t);
    }

    /// <summary>Emits a cert-rotation result as JSON.</summary>
    public static void EmitCertRotationJson(CertRotationResult r)
    {
        var dto = new ClusterCertRotationOutputJson
        {
            DurationSec = Math.Round(r.Duration.TotalSeconds, 3),
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
            RotatedNodes = r.RotatedNodes.Select(n => new CertRotatedNodeJson
            {
                Hostname = n.Hostname,
                OldSerial = n.OldSerial,
                NewSerial = n.NewSerial,
                Error = n.Error,
            }).ToList(),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterCertRotationOutputJson));
    }

    // --- chaos -------------------------------------------------------------

    /// <summary>Renders a chaos outcome (recovery verdict + observed impact) for humans.</summary>
    public static void EmitChaosHuman(ChaosOutcome r)
    {
        var color = r.Recovered ? "green" : "yellow";
        var label = r.Recovered ? "● GREEN  chaos: cluster recovered" : "● YELLOW  chaos: cluster did NOT recover within window";
        AnsiConsole.Write(new Rule(
            $"[{color}]{label}[/]  scenario={Markup.Escape(r.ScenarioApplied)}  target={Markup.Escape(r.Target)}  {r.Duration.TotalSeconds:F2}s")
        { Justification = Justify.Left });
        var t = new Table().AddColumns("probe", "target", "status", "value");
        foreach (var p in r.ObservedImpact)
            t.AddRow(Markup.Escape(p.Name), Markup.Escape(p.Target), ColorStatus(p.Status), Markup.Escape(p.Value ?? "-"));
        AnsiConsole.Write(t);
    }

    /// <summary>Emits a chaos outcome as JSON.</summary>
    public static void EmitChaosJson(ChaosOutcome r)
    {
        var dto = new ClusterChaosOutputJson
        {
            ScenarioApplied = r.ScenarioApplied,
            Target = r.Target,
            Recovered = r.Recovered,
            DurationSec = Math.Round(r.Duration.TotalSeconds, 3),
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
            ObservedImpact = r.ObservedImpact.Select(p => new HealthProbeJson
            {
                Name = p.Name,
                Target = p.Target,
                Status = p.Status,
                Value = p.Value,
                Threshold = p.Threshold,
            }).ToList(),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterChaosOutputJson));
    }

    // --- acl ---------------------------------------------------------------

    /// <summary>Renders an ACL snapshot (users, enabled flag, permissions) for humans.</summary>
    public static void EmitAclHuman(AclSnapshot r)
    {
        AnsiConsole.Write(new Rule(
            $"acl [bold]{Markup.Escape(r.ClusterId)}[/]  verb={Markup.Escape(r.Verb)}  ({r.CapturedAtUtc:u})")
        { Justification = Justify.Left });
        var t = new Table().AddColumns("user", "enabled", "permissions");
        foreach (var u in r.Users)
        {
            t.AddRow(
                Markup.Escape(u.Name),
                u.Enabled ? "[green]yes[/]" : "[red]no[/]",
                Markup.Escape(string.Join(" ", u.Permissions)));
        }
        AnsiConsole.Write(t);
    }

    /// <summary>Emits an ACL snapshot as JSON.</summary>
    public static void EmitAclJson(AclSnapshot r)
    {
        var dto = new ClusterAclOutputJson
        {
            ClusterId = r.ClusterId,
            Verb = r.Verb,
            CapturedAtUtc = r.CapturedAtUtc.ToString("u"),
            Users = r.Users.Select(u => new AclUserJson
            {
                Name = u.Name,
                Enabled = u.Enabled,
                Permissions = u.Permissions.ToList(),
            }).ToList(),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.ClusterAclOutputJson));
    }

    // --- recover-ha (v0.8.1) ----------------------------------------------

    /// <summary>Renders a recover-ha result (transit/leader state + per-node seal status) for humans.</summary>
    public static void EmitRecoverHaHuman(RecoverHaResult r)
    {
        var color = r.AllUnsealed ? "green" : "yellow";
        var label = r.AllUnsealed ? "● GREEN  HA recovered (all nodes unsealed)" : "● YELLOW  HA partially recovered";
        AnsiConsole.Write(new Rule(
            $"[{color}]{label}[/]  transit={(r.TransitUnsealed ? "unsealed" : "SEALED")}  leader={Markup.Escape(r.Leader ?? "(none)")}  {r.Duration.TotalSeconds:F2}s")
        { Justification = Justify.Left });
        var t = new Table().AddColumns("node", "sealed", "outcome");
        foreach (var n in r.Nodes)
            t.AddRow(Markup.Escape(n.Hostname), n.Sealed ? "[red]yes[/]" : "[green]no[/]", Markup.Escape(n.Outcome));
        AnsiConsole.Write(t);
    }

    /// <summary>Emits a recover-ha result as JSON.</summary>
    public static void EmitRecoverHaJson(RecoverHaResult r)
    {
        var dto = new RecoverHaOutputJson
        {
            ClusterId = r.ClusterId,
            TransitUnsealed = r.TransitUnsealed,
            AllUnsealed = r.AllUnsealed,
            Leader = r.Leader,
            DurationSec = Math.Round(r.Duration.TotalSeconds, 3),
            StartedAtUtc = r.StartedAtUtc.ToString("u"),
            Nodes = r.Nodes.Select(n => new RecoverHaNodeJson
            {
                Hostname = n.Hostname,
                Sealed = n.Sealed,
                Outcome = n.Outcome,
            }).ToList(),
        };
        Console.WriteLine(JsonSerializer.Serialize(dto, NexusJsonContext.Default.RecoverHaOutputJson));
    }

    // === Helpers ============================================================

    private static string ColorStatus(string status) => status switch
    {
        "alive" or "ready" or "green" or "ok" => $"[green]{status}[/]",
        "yellow" or "syncing" or "handshake" or "draining" or "partial" => $"[yellow]{status}[/]",
        "fail" or "fail?" or "red" or "failed" or "missing" => $"[red]{status}[/]",
        _ => Markup.Escape(status),
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KiB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MiB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
    }
}
