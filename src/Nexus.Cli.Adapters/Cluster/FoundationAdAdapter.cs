using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Foundation Active Directory + DNS + gateway adapter (nexus-cli v0.8.1, Phase
/// 0.C/0.M, ADR-0022). The companion to <see cref="VaultAdapter"/> that completes
/// the foundation tier: the 2-DC AD DS forest (<c>nexus.lab</c>, Windows Server
/// 2025) reached over <b>Windows-SSH</b> (the <see cref="SqlServerControl"/>
/// EncodedCommand idiom) as the local <c>nexusadmin</c>, plus the Debian
/// <c>nexus-gateway</c> egress (dnsmasq DNS/DHCP + nftables NAT) folded into the
/// health view over Linux-SSH.
/// <para>
/// AD is multi-master, so its mutating surface is intentionally narrow:
/// status/health/topology + acl (AD users/groups) + backup take (a non-
/// destructive <c>ntdsutil ifm</c> database snapshot — the AD analogue of the
/// Vault raft-snapshot verb) + failover (a GRACEFUL <c>Move-ADDirectoryServer
/// OperationMasterRole</c> FSMO transfer-and-back, the planned-maintenance
/// drill). The genuinely risky/terraform verbs — DC add/remove, authoritative
/// restore (console-only DSRM), an unguarded NTDS cert rotation / chaos kill,
/// and FSMO <i>seize</i> (permanent-loss last resort) — return a graceful,
/// ACTIONABLE "not applicable" pointing at the right out-of-band tool, never a
/// silent stub.
/// </para>
/// <para>
/// DC IPs are infra canon, NOT the catalog value: vms.yaml records dc-nexus at
/// the canonical <c>.10</c> but it has ALWAYS run at <c>.240</c> (DHCP pool; the
/// documented canon-vs-reality drift, ADR-0039). dc-nexus-2 runs at <c>.242</c>.
/// We hardcode the reality (like <see cref="CitusAdapter"/> hardcodes its VIPs).
/// </para>
/// </summary>
public sealed class FoundationAdAdapter : IClusterAdapter
{
    private const string ClusterName = "foundation-ad";
    private const string DisplayNameConst = "Foundation Active Directory + DNS + gateway";
    private const string Domain = "nexus.lab";
    private const string GatewayIp = "192.168.70.1";

    internal sealed record DcNode(string Name, string Ip);

    // Reality IPs (ADR-0039 drift); names match vms.yaml foundation cluster.
    private static readonly DcNode[] Dcs =
    [
        new("dc-nexus", "192.168.70.240"),
        new("dc-nexus-2", "192.168.70.242"),
    ];

    // AD principals that acl grant/revoke must never touch.
    private static readonly HashSet<string> ProtectedPrincipals = new(StringComparer.OrdinalIgnoreCase)
    { "Administrator", "krbtgt", "nexusadmin", "Guest", "Domain Admins", "Enterprise Admins", "Schema Admins" };

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WinTimeout = TimeSpan.FromSeconds(60);

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private ClusterStatus? _lastStatus;

    public FoundationAdAdapter(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
    }

    public string ClusterId => ClusterName;
    public string DisplayName => DisplayNameConst;

    private SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    /// <summary>Run a PowerShell script on a Windows DC via EncodedCommand. Trimmed stdout on exit 0.</summary>
    private async Task<Result<string>> WinPsAsync(string ip, string ps, CancellationToken ct, TimeSpan? timeout = null)
    {
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        var r = await _ssh.ExecuteAsync(T(ip), $"powershell -NoProfile -EncodedCommand {b64}", timeout ?? WinTimeout, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"ssh(win) to {ip} failed: {r.Error}");
        if (r.Value!.ExitCode != 0)
            return Result.Fail<string>($"remote PowerShell on {ip} exit {r.Value.ExitCode}: {Tail((r.Value.Stdout + "\n" + r.Value.Stderr).Trim(), 300)}");
        return Result.Ok(r.Value.Stdout.Trim());
    }

    /// <summary>First DC that answers a `hostname` probe (dc-nexus preferred).</summary>
    private async Task<(DcNode Dc, HashSet<string> Alive)> ReachableDcAsync(CancellationToken ct)
    {
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DcNode? first = null;
        foreach (var dc in Dcs)
        {
            var r = await WinPsAsync(dc.Ip, "Write-Output (hostname)", ct).ConfigureAwait(false);
            if (r.IsOk && r.Value!.Length > 0) { alive.Add(dc.Name); first ??= dc; }
        }
        return (first ?? Dcs[0], alive);
    }

    // === parse helpers (testable) ==========================================
    internal sealed record DcInfo(string Name, string Ip, bool IsGlobalCatalog, IReadOnlyList<string> FsmoRoles);

    /// <summary>Parse `name|ip|isGC|role1,role2` lines from Get-ADDomainController.</summary>
    internal static List<DcInfo> ParseDcLines(string stdout)
    {
        var list = new List<DcInfo>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim().Split('|');
            if (p.Length < 3 || p[0].Length == 0) continue;
            var roles = p.Length > 3 && p[3].Length > 0
                ? p[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray()
                : Array.Empty<string>();
            list.Add(new DcInfo(p[0].Trim(), p[1].Trim(),
                bool.TryParse(p[2].Trim(), out var gc) && gc, roles));
        }
        return list;
    }

    /// <summary>Parse `LastReplicationResult|ConsecutiveFailures` from a repl-metadata probe.</summary>
    internal static (int Result, int Failures)? ParseReplMetadata(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim().Split('|');
            if (p.Length >= 2 && int.TryParse(p[0].Trim(), out var res) && int.TryParse(p[1].Trim(), out var fail))
                return (res, fail);
        }
        return null;
    }

    // === GetStatusAsync ====================================================
    public async Task<Result<ClusterStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (dc, alive) = await ReachableDcAsync(cancellationToken).ConfigureAwait(false);
        var members = new List<ClusterMember>();
        string? pdc = null;

        if (alive.Count > 0)
        {
            // Get the AD view of every DC + the PDC role holder from a reachable DC.
            var ps =
                "$ErrorActionPreference='SilentlyContinue';"
                + "Get-ADDomainController -Filter * | ForEach-Object { Write-Output ($_.Name + '|' + $_.IPv4Address + '|' + $_.IsGlobalCatalog + '|' + ($_.OperationMasterRoles -join ',')) };"
                + "Write-Output ('PDC|' + (Get-ADDomain).PDCEmulator)";
            var r = await WinPsAsync(dc.Ip, ps, cancellationToken).ConfigureAwait(false);
            if (r.IsOk)
            {
                foreach (var line in r.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    if (line.StartsWith("PDC|", StringComparison.Ordinal))
                        pdc = line[4..].Split('.')[0].Trim();
                var dcInfos = ParseDcLines(r.Value!);
                foreach (var info in dcInfos)
                {
                    var isUp = alive.Contains(info.Name);
                    var role = string.Equals(info.Name, pdc, StringComparison.OrdinalIgnoreCase) ? "pdc" : "dc";
                    members.Add(new ClusterMember(info.Name, info.Ip, role, isUp ? "alive" : "failed"));
                }
            }
        }
        // Any DC we know about but didn't see in the AD view (e.g. AD view unreachable).
        foreach (var d in Dcs)
            if (!members.Any(m => string.Equals(m.Hostname, d.Name, StringComparison.OrdinalIgnoreCase)))
                members.Add(new ClusterMember(d.Name, d.Ip, "dc", alive.Contains(d.Name) ? "alive" : "failed"));

        // Gateway (Linux egress).
        var gwUp = await GatewayUpAsync(cancellationToken).ConfigureAwait(false);
        members.Add(new ClusterMember("nexus-gateway", GatewayIp, "gateway", gwUp ? "alive" : "failed"));

        var dcAlive = members.Count(m => m.Role is "dc" or "pdc" && m.Status == "alive");
        var overall =
            (dcAlive == Dcs.Length && gwUp) ? "green"
            : (dcAlive >= 1) ? "yellow" : "red";

        var status = new ClusterStatus(ClusterName, DisplayNameConst, overall, members, pdc, DateTimeOffset.UtcNow);
        _lastStatus = status;
        return Result.Ok(status);
    }

    private async Task<bool> GatewayUpAsync(CancellationToken ct)
    {
        var r = await _ssh.ExecuteAsync(T(GatewayIp),
            "systemctl is-active dnsmasq nftables 2>/dev/null | tr '\\n' ',' ; true", SshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Split(',', StringSplitOptions.RemoveEmptyEntries).Count(s => s.Trim() == "active") >= 2;
    }

    // === HealthAsync =======================================================
    public async Task<Result<HealthReport>> HealthAsync(CancellationToken cancellationToken)
    {
        var (dc, alive) = await ReachableDcAsync(cancellationToken).ConfigureAwait(false);
        var probes = new List<HealthProbe>();

        foreach (var d in Dcs)
            probes.Add(new HealthProbe("dc-reachable", d.Name, alive.Contains(d.Name) ? "green" : "red",
                alive.Contains(d.Name) ? "ADWS responding" : "unreachable", "DC online (ADWS up)"));

        if (alive.Count == 0)
            return Result.Ok(new HealthReport(ClusterName, "red", probes, DateTimeOffset.UtcNow));

        // Replication: per-DC inbound metadata (Scope Server avoids the cross-DC ADWS dep).
        foreach (var d in Dcs.Where(x => alive.Contains(x.Name)))
        {
            // NB: run ON the target DC (WinPs SSHes to d.Ip) with the DEFAULT -Server.
            // Passing an explicit `-Server <ip>` silently returns an object whose
            // metadata fields are all empty (live-caught 2026-06-18) — the local
            // default-server query is the one that populates LastReplicationResult.
            var ps =
                "$ErrorActionPreference='SilentlyContinue';"
                + $"$m = Get-ADReplicationPartnerMetadata -Target {d.Name} -Scope Server;"
                + "if ($m) { $m | ForEach-Object { Write-Output ($_.LastReplicationResult.ToString() + '|' + $_.ConsecutiveReplicationFailures.ToString()) } } else { Write-Output 'NO_PARTNER' }";
            var r = await WinPsAsync(d.Ip, ps, cancellationToken).ConfigureAwait(false);
            if (r.IsFail) { probes.Add(new HealthProbe("ad-replication", d.Name, "yellow", "ADWS not ready", "LastReplicationResult=0")); continue; }
            var meta = ParseReplMetadata(r.Value!);
            if (meta is null)
                probes.Add(new HealthProbe("ad-replication", d.Name, r.Value!.Contains("NO_PARTNER") ? "yellow" : "yellow", "no inbound partner metadata yet", "LastReplicationResult=0"));
            else
                probes.Add(new HealthProbe("ad-replication", d.Name, meta.Value.Result == 0 && meta.Value.Failures == 0 ? "green" : "red",
                    $"result={meta.Value.Result}, failures={meta.Value.Failures}", "result=0, failures=0"));
        }

        // DNS zones (AD-integrated).
        var dns = await WinPsAsync(dc.Ip,
            "$ErrorActionPreference='SilentlyContinue';"
            + "$z = Get-DnsServerZone -Name 'nexus.lab'; Write-Output (('nexus.lab|' + $z.ZoneType + '|' + $z.IsDsIntegrated))", cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("dns-zone", "nexus.lab",
            dns.IsOk && dns.Value!.Contains("Primary") && dns.Value.Contains("True") ? "green" : "yellow",
            dns.IsOk ? dns.Value!.Trim() : "unreachable", "Primary + AD-integrated"));

        // KDS root key (the GMSA chain) via AD object -- Get-KdsRootKey is unreliable over SSH.
        var kds = await WinPsAsync(dc.Ip,
            "$ErrorActionPreference='SilentlyContinue';"
            + "$c=(Get-ADRootDSE).configurationNamingContext;"
            + "$n=(Get-ADObject -SearchBase ('CN=Master Root Keys,CN=Group Key Distribution Service,CN=Services,' + $c) -Filter * | Where-Object {$_.ObjectClass -eq 'msKds-ProvRootKey'}).Count;"
            + "Write-Output ('KDS=' + $n)", cancellationToken).ConfigureAwait(false);
        var kdsCount = 0;
        if (kds.IsOk)
        {
            var m = System.Text.RegularExpressions.Regex.Match(kds.Value!, @"KDS=(\d+)");
            if (m.Success) kdsCount = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        probes.Add(new HealthProbe("kds-root-key", "Master Root Keys", kdsCount >= 1 ? "green" : "red",
            $"{kdsCount} root key(s)", ">= 1 (GMSA chain)"));

        // FSMO: all 5 roles held in the domain/forest.
        var fsmo = await WinPsAsync(dc.Ip,
            "$ErrorActionPreference='SilentlyContinue';"
            + "$d=Get-ADDomain; $f=Get-ADForest;"
            + "$roles=@($d.PDCEmulator,$d.RIDMaster,$d.InfrastructureMaster,$f.SchemaMaster,$f.DomainNamingMaster) | Where-Object {$_};"
            + "Write-Output ('FSMO=' + $roles.Count)", cancellationToken).ConfigureAwait(false);
        var fsmoOk = fsmo.IsOk && fsmo.Value!.Contains("FSMO=5");
        probes.Add(new HealthProbe("fsmo-roles", "forest", fsmoOk ? "green" : "yellow",
            fsmo.IsOk ? fsmo.Value!.Trim() : "unreachable", "all 5 roles held"));

        // Gateway services.
        var gwUp = await GatewayUpAsync(cancellationToken).ConfigureAwait(false);
        probes.Add(new HealthProbe("gateway-services", "nexus-gateway", gwUp ? "green" : "red",
            gwUp ? "dnsmasq + nftables active" : "down", "dnsmasq + nftables active"));
        var nat = await _ssh.ExecuteAsync(T(GatewayIp),
            "sudo nft list table ip nat 2>/dev/null | grep -c masquerade; true", SshTimeout, cancellationToken).ConfigureAwait(false);
        var natOk = nat.IsOk && nat.Value!.Stdout.Trim().StartsWith('1');
        probes.Add(new HealthProbe("gateway-nat", "nexus-gateway", natOk ? "green" : "yellow",
            natOk ? "NAT masquerade present" : "no masquerade rule", "ip nat masquerade rule"));

        var overall = probes.Any(p => p.Status == "red") ? "red" : probes.Any(p => p.Status == "yellow") ? "yellow" : "green";
        return Result.Ok(new HealthReport(ClusterName, overall, probes, DateTimeOffset.UtcNow));
    }

    // === TopologyAsync =====================================================
    public async Task<Result<TopologySnapshot>> TopologyAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<TopologySnapshot>(status.Error!);
        var nodes = status.Value!.Members
            .Select(m => new TopologyNode(m.Hostname, m.Role, m.Status))
            .ToList();
        return Result.Ok(new TopologySnapshot(ClusterName, nodes, Shards: null, DateTimeOffset.UtcNow));
    }

    // === AclAsync (AD users + groups) ======================================
    public async Task<Result<AclSnapshot>> AclAsync(AclOperation operation, CancellationToken cancellationToken)
    {
        var (dc, alive) = await ReachableDcAsync(cancellationToken).ConfigureAwait(false);
        if (alive.Count == 0) return Result.Fail<AclSnapshot>("no reachable domain controller for AD acl ops");
        var verb = operation.Verb.ToLowerInvariant();

        if (verb is "list" or "describe")
        {
            if (verb == "describe" && !string.IsNullOrWhiteSpace(operation.User))
            {
                var esc = operation.User!.Replace("'", "''");
                var ps =
                    "$ErrorActionPreference='SilentlyContinue';"
                    + $"$u=Get-ADUser -Identity '{esc}' -Properties MemberOf,Enabled;"
                    + "if ($u) { Write-Output ($u.SamAccountName + '|' + $u.Enabled + '|' + (($u.MemberOf | ForEach-Object {($_ -split ',')[0] -replace 'CN='}) -join ',')) } else { Write-Output 'NO_USER' }";
                var r = await WinPsAsync(dc.Ip, ps, cancellationToken).ConfigureAwait(false);
                if (r.IsFail) return Result.Fail<AclSnapshot>(r.Error!);
                if (r.Value!.Contains("NO_USER")) return Result.Fail<AclSnapshot>($"no AD user '{operation.User}'.");
                var u = ParseAclUser(r.Value!);
                return Result.Ok(new AclSnapshot(ClusterName, verb, u is null ? [] : [u], DateTimeOffset.UtcNow));
            }

            // list: enabled users (Sam|Enabled) + the nexus-* security groups.
            var psList =
                "$ErrorActionPreference='SilentlyContinue';"
                + "Get-ADUser -Filter * | ForEach-Object { Write-Output ('U|' + $_.SamAccountName + '|' + $_.Enabled) };"
                + "Get-ADGroup -Filter \"name -like 'nexus-*'\" | ForEach-Object { Write-Output ('G|' + $_.Name) }";
            var rl = await WinPsAsync(dc.Ip, psList, cancellationToken).ConfigureAwait(false);
            if (rl.IsFail) return Result.Fail<AclSnapshot>(rl.Error!);
            var users = new List<AclUser>();
            foreach (var line in rl.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Trim().Split('|');
                if (p.Length >= 3 && p[0] == "U")
                    users.Add(new AclUser(p[1].Trim(), ["user"], Enabled: bool.TryParse(p[2].Trim(), out var en) && en));
                else if (p.Length >= 2 && p[0] == "G")
                    users.Add(new AclUser(p[1].Trim(), ["group"], Enabled: true));
            }
            return Result.Ok(new AclSnapshot(ClusterName, verb, users, DateTimeOffset.UtcNow));
        }

        if (verb is "grant" or "revoke")
        {
            if (string.IsNullOrWhiteSpace(operation.User))
                return Result.Fail<AclSnapshot>($"acl {verb} requires --user (the AD user).");
            if (operation.Permissions is not { Count: > 0 })
                return Result.Fail<AclSnapshot>($"acl {verb} requires --permissions <group[,group]> (the AD group(s) to {(verb == "grant" ? "add the user to" : "remove the user from")}).");
            if (ProtectedPrincipals.Contains(operation.User!))
                return Result.Fail<AclSnapshot>($"refusing to modify the protected principal '{operation.User}'.");
            var cmdlet = verb == "grant" ? "Add-ADGroupMember" : "Remove-ADGroupMember";
            var userEsc = operation.User!.Replace("'", "''");
            var sb = new StringBuilder("$ErrorActionPreference='Stop'; try {");
            foreach (var g in operation.Permissions)
            {
                if (ProtectedPrincipals.Contains(g))
                    return Result.Fail<AclSnapshot>($"refusing to modify the protected group '{g}'.");
                var gEsc = g.Replace("'", "''");
                var piece = $" {cmdlet} -Identity '{gEsc}' -Members '{userEsc}' -Confirm:$false;";
                sb.Append(piece);
            }
            sb.Append(" Write-Output 'ACL_OK' } catch { Write-Output ('ACL_ERR: ' + $_.Exception.Message) }");
            var r = await WinPsAsync(dc.Ip, sb.ToString(), cancellationToken).ConfigureAwait(false);
            if (r.IsFail) return Result.Fail<AclSnapshot>(r.Error!);
            if (!r.Value!.Contains("ACL_OK")) return Result.Fail<AclSnapshot>($"acl {verb} failed: {Tail(r.Value, 220)}");
            return await AclAsync(new AclOperation("describe", operation.User), cancellationToken).ConfigureAwait(false);
        }

        return Result.Fail<AclSnapshot>($"unknown ACL verb '{operation.Verb}'; expected list|describe|grant|revoke");
    }

    /// <summary>Parse a single `Sam|Enabled|group1,group2` user row.</summary>
    internal static AclUser? ParseAclUser(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim().Split('|');
            if (p.Length < 2 || p[0].Length == 0 || p[0].StartsWith("NO_", StringComparison.Ordinal)) continue;
            var groups = p.Length > 2 && p[2].Length > 0
                ? p[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray()
                : new[] { "(no groups)" };
            return new AclUser(p[0].Trim(), groups, Enabled: bool.TryParse(p[1].Trim(), out var en) && en);
        }
        return null;
    }

    // === FailoverAsync (graceful FSMO role transfer + transfer-back) =========
    // AD is multi-master, so there is no "DC failover" in the data-tier sense
    // (a single DC loss is transparent — the surviving DC keeps serving auth +
    // DNS, proven by smoke-0.M). What IS the meaningful operator drill is the
    // graceful relocation of the FSMO single-master roles to the other DC (what
    // you do to a surviving DC when planning maintenance on the role holder).
    // This wires that as `Move-ADDirectoryServerOperationMasterRole` (a GRACEFUL
    // online transfer — both DCs up + replicating; NOT `ntdsutil` seize, which
    // stays a manual permanent-loss last resort). Mirrors the failover-test
    // recover pattern: move the roles holder→target, verify, then move them
    // BACK (unless --no-recover). Requires ≥2 reachable DCs (the recipient).
    //
    // Scope = the 4 roles a Domain Admin + Enterprise Admin can relocate — the
    // realistic "evacuate this DC for maintenance" set. SchemaMaster is
    // DELIBERATELY excluded: transferring it requires Schema Admins membership
    // (kept restricted by AD design — schema changes are rare + dangerous), and
    // the schema master is never part of a routine maintenance failover. (Live-
    // caught 2026-06-29: an all-5 batch run as Domain/Enterprise Admin moves the
    // first 4 then aborts "Access is denied" on SchemaMaster, leaving a SPLIT
    // placement — so we scope to exactly what the operator identity can move,
    // keeping the transfer atomic.)
    private static readonly string[] FsmoRoleNames =
        ["PDCEmulator", "RIDMaster", "InfrastructureMaster", "DomainNamingMaster"];

    public async Task<Result<FailoverResult>> FailoverAsync(FailoverRequest request, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<FailoverResult>(status.Error!);
        var aliveDcs = status.Value!.Members.Where(m => m.Role is "dc" or "pdc" && m.Status == "alive").ToList();
        if (aliveDcs.Count < 2)
            return Result.Fail<FailoverResult>(
                "graceful FSMO transfer needs ≥2 reachable domain controllers (the role recipient); only "
                + $"{(aliveDcs.Count == 1 ? aliveDcs[0].Hostname : "no DC")} is up — power on dc-nexus-2 first. "
                + "(On PERMANENT DC loss, seize with `ntdsutil` — a manual last resort, never a runtime verb.)");

        // Current FSMO holder = the PDC member (in this forest one DC holds all 5).
        var holderName = status.Value!.Leader;
        var original = aliveDcs.FirstOrDefault(m => string.Equals(m.Hostname, holderName, StringComparison.OrdinalIgnoreCase))
                       ?? aliveDcs[0];

        // Target = explicit --node, else the other alive DC.
        ClusterMember? target;
        if (!string.IsNullOrWhiteSpace(request.TargetNode))
        {
            target = aliveDcs.FirstOrDefault(m => string.Equals(m.Hostname, request.TargetNode, StringComparison.OrdinalIgnoreCase));
            if (target is null) return Result.Fail<FailoverResult>($"--node '{request.TargetNode}' is not a reachable DC.");
        }
        else
        {
            target = aliveDcs.FirstOrDefault(m => !string.Equals(m.Hostname, original.Hostname, StringComparison.OrdinalIgnoreCase));
        }
        if (target is null || string.Equals(target.Hostname, original.Hostname, StringComparison.OrdinalIgnoreCase))
            return Result.Fail<FailoverResult>("no distinct FSMO transfer target (current holder is the only candidate).");

        var origIp = DcIp(original.Hostname);
        var targetIp = DcIp(target.Hostname);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var preFlightAt = sw.Elapsed;

        // Inject: graceful transfer of the operator-movable roles to the target.
        var moveTo = await MoveFsmoAsync(targetIp, target.Hostname, cancellationToken).ConfigureAwait(false);
        var injectedAt = sw.Elapsed;
        if (moveTo.IsFail) return Result.Fail<FailoverResult>($"FSMO transfer to {target.Hostname} failed: {moveTo.Error}");
        var heldByTarget = await FsmoRolesHeldByAsync(targetIp, target.Hostname, cancellationToken).ConfigureAwait(false);
        var newLeaderAt = sw.Elapsed;
        if (heldByTarget.IsFail) return Result.Fail<FailoverResult>(heldByTarget.Error!);
        if (!heldByTarget.Value)
            return Result.Fail<FailoverResult>($"FSMO move reported OK but {target.Hostname} does not hold the transferred roles.");

        string recovery, hint;
        var recoveryAt = newLeaderAt;
        var healthyAt = newLeaderAt;
        if (request.NoRecover)
        {
            recovery = "skipped";
            hint = $"FSMO roles left on {target.Hostname} (--no-recover). Transfer back with "
                   + $"`failover-test cluster foundation-ad --node {original.Hostname}`.";
        }
        else
        {
            var moveBack = await MoveFsmoAsync(origIp, original.Hostname, cancellationToken).ConfigureAwait(false);
            recoveryAt = sw.Elapsed;
            if (moveBack.IsFail)
            {
                recovery = "failed";
                hint = $"FSMO moved to {target.Hostname} but transfer BACK to {original.Hostname} failed: {moveBack.Error}. "
                       + "Transfer manually with `Move-ADDirectoryServerOperationMasterRole`.";
            }
            else
            {
                var heldByOrig = await FsmoRolesHeldByAsync(origIp, original.Hostname, cancellationToken).ConfigureAwait(false);
                healthyAt = sw.Elapsed;
                recovery = (heldByOrig.IsOk && heldByOrig.Value) ? "recovered" : "failed";
                hint = recovery == "recovered"
                    ? $"graceful FSMO transfer drill complete — {FsmoRoleNames.Length} roles (SchemaMaster excluded — needs Schema Admins) moved {original.Hostname}→{target.Hostname} and back; "
                      + "AD served auth + DNS throughout (multi-master; transfers are online)."
                    : $"transfer back issued but {original.Hostname} does not hold the transferred roles — verify with `netdom query fsmo`.";
            }
        }
        sw.Stop();

        return Result.Ok(new FailoverResult(
            Scenario: "ad-fsmo-transfer",
            OriginalPrimary: original.Hostname,
            NewPrimary: target.Hostname,
            Rto: newLeaderAt - injectedAt,
            Recovery: recovery,
            RecoveryHint: hint,
            Timeline: new FailoverTimeline(preFlightAt, injectedAt, newLeaderAt, recoveryAt, healthyAt),
            StartedAtUtc: startedAt));
    }

    private static string DcIp(string name) =>
        Dcs.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.Ip ?? name;

    /// <summary>Graceful online transfer of all 5 FSMO roles to <paramref name="targetName"/> (run on the target DC).</summary>
    private async Task<Result<bool>> MoveFsmoAsync(string dcIp, string targetName, CancellationToken ct)
    {
        var roles = string.Join(",", FsmoRoleNames);
        var nameEsc = targetName.Replace("'", "''");
        var ps =
            "$ErrorActionPreference='Stop';"
            + $"try {{ Move-ADDirectoryServerOperationMasterRole -Identity '{nameEsc}' -OperationMasterRole {roles} -Confirm:$false; Write-Output 'MOVE_OK' }}"
            + " catch { Write-Output ('MOVE_ERR:'+$_.Exception.Message) }";
        var r = await WinPsAsync(dcIp, ps, ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<bool>(r.Error!);
        return r.Value!.Contains("MOVE_OK", StringComparison.Ordinal)
            ? Result.Ok(true)
            : Result.Fail<bool>(Tail(r.Value, 220));
    }

    /// <summary>True iff every role in <see cref="FsmoRoleNames"/> is held by <paramref name="holderName"/> (queried from <paramref name="dcIp"/>).</summary>
    private async Task<Result<bool>> FsmoRolesHeldByAsync(string dcIp, string holderName, CancellationToken ct)
    {
        var ps =
            "$ErrorActionPreference='SilentlyContinue';"
            + "$d=Get-ADDomain; $f=Get-ADForest;"
            + "Write-Output ('FSMO|'+$d.PDCEmulator+'|'+$d.RIDMaster+'|'+$d.InfrastructureMaster+'|'+$f.SchemaMaster+'|'+$f.DomainNamingMaster)";
        var r = await WinPsAsync(dcIp, ps, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<bool>(r.Error!);
        var holders = ParseFsmoHolders(r.Value!);
        if (holders is null) return Result.Fail<bool>($"could not read FSMO holders: {Tail(r.Value!, 180)}");
        return Result.Ok(FsmoRoleNames.All(role =>
            holders.TryGetValue(role, out var h)
            && (string.Equals(h, holderName, StringComparison.OrdinalIgnoreCase)
                || h.StartsWith(holderName + ".", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Parse a role→holder-FQDN map from a `FSMO|pdc|rid|infra|schema|naming` line.</summary>
    internal static Dictionary<string, string>? ParseFsmoHolders(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (!t.StartsWith("FSMO|", StringComparison.Ordinal)) continue;
            var p = t.Split('|');
            if (p.Length < 6 || p.Skip(1).Take(5).Any(x => x.Trim().Length == 0)) continue;
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PDCEmulator"] = p[1].Trim(),
                ["RIDMaster"] = p[2].Trim(),
                ["InfrastructureMaster"] = p[3].Trim(),
                ["SchemaMaster"] = p[4].Trim(),
                ["DomainNamingMaster"] = p[5].Trim(),
            };
        }
        return null;
    }

    // === Graceful, ACTIONABLE N/A for the remaining terraform mutators =======

    public Task<Result<ScaleOutResult>> ScaleOutAddAsync(ScaleOutAddRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(
            "adding a domain controller is a terraform/Packer operation, not a runtime scale-out: add the VM + the "
            + "role-overlay-dc-nexus-N-promotion.tf overlay in nexus-infra-vmware/terraform/envs/foundation and re-apply "
            + "(Install-ADDSDomainController, ADR-0039). The forest is already HA at 2 DCs."));

    public Task<Result<ScaleOutResult>> ScaleOutRemoveAsync(ScaleOutRemoveRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ScaleOutResult>(
            "removing a domain controller requires graceful demotion (Uninstall-ADDSDomainController) + AD metadata cleanup, "
            + "an out-of-band terraform/ntdsutil operation (handbook §1m) — never a runtime drop, which would orphan the AD topology."));

    // === BackupTakeAsync (ntdsutil IFM -- verifiable AD database artifact) ===
    // A point-in-time copy of the AD database (ntds.dit + registry hives)
    // created via `ntdsutil ifm create full` ON a reachable DC. This is the AD
    // analogue of the Vault raft-snapshot verb. It is NON-DESTRUCTIVE: IFM
    // mounts a VSS snapshot and copies it out; the live directory is untouched
    // (multi-master AD already gives RPO≈0 via replication, but a point-in-time
    // database artifact is what a backup verb must produce). Prefers a NON-PDC
    // DC to keep the snapshot load off the PDC emulator (the "back up from a
    // secondary" hygiene the data adapters follow). The artifact stays on the
    // DC; restore is the console-only DSRM authoritative-restore path
    // ([[feedback_ntdsutil_dsrm_console_mode_ssh]]) -- BackupRestoreAsync below
    // stays a graceful N/A.
    public async Task<Result<BackupResult>> BackupTakeAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.IsFail) return Result.Fail<BackupResult>(status.Error!);

        // Choose an alive DC, preferring a non-PDC; map the name back to the
        // hardcoded reality IP (ADR-0039) rather than the AD-reported address.
        var aliveDcs = status.Value!.Members
            .Where(m => m.Role is "dc" or "pdc" && m.Status == "alive")
            .ToList();
        var chosen = aliveDcs.FirstOrDefault(m => m.Role == "dc") ?? aliveDcs.FirstOrDefault();
        if (chosen is null)
            return Result.Fail<BackupResult>("no reachable domain controller to take an IFM backup from.");
        var dcIp = Dcs.FirstOrDefault(d => string.Equals(d.Name, chosen.Hostname, StringComparison.OrdinalIgnoreCase))?.Ip
                   ?? chosen.IpAddress;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var backupId = string.IsNullOrWhiteSpace(request.Tag)
            ? $"ad-ifm-{startedAt:yyyyMMdd-HHmmss}"
            : $"ad-{Sanitize(request.Tag!)}-{startedAt:yyyyMMdd-HHmmss}";
        var dest = $@"C:\nexus-backups\ad\{backupId}";

        // Non-interactive batch (no DSRM password prompt). Verify by the
        // ntds.dit artifact, not by parsing ntdsutil's chatty stdout.
        var ps =
            "$ErrorActionPreference='Stop';"
            + $"$dest='{dest}';"
            + "if (Test-Path $dest) { Remove-Item -Recurse -Force $dest };"
            + "New-Item -ItemType Directory -Force -Path $dest | Out-Null;"
            + "$null = & ntdsutil \"activate instance ntds\" \"ifm\" \"create full $dest\" \"quit\" \"quit\" 2>&1;"
            + "$dit = Get-ChildItem -Recurse -Path $dest -Filter ntds.dit -ErrorAction SilentlyContinue | Select-Object -First 1;"
            + "if (-not $dit) { Write-Output 'IFM_ERR'; exit 1 };"
            + "Write-Output ('IFM_OK|' + $dit.Length + '|' + $dit.FullName)";
        // IFM is heavier than a status query (VSS snapshot + DB copy) -- allow up to 5 min.
        var r = await WinPsAsync(dcIp, ps, cancellationToken, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        sw.Stop();
        if (r.IsFail) return Result.Fail<BackupResult>($"ntdsutil ifm on {chosen.Hostname} failed: {r.Error}");
        var parsed = ParseIfmResult(r.Value!);
        if (parsed is null)
            return Result.Fail<BackupResult>($"ntdsutil ifm on {chosen.Hostname} did not produce an ntds.dit: {Tail(r.Value!, 220)}");

        return Result.Ok(new BackupResult(
            BackupId: backupId,
            Destination: $"{chosen.Hostname}:{parsed.Value.Path} (ntdsutil IFM full copy of the AD database; "
                + "restore is the console-only DSRM authoritative-restore path, not a runtime verb).",
            SizeBytes: parsed.Value.Size,
            Duration: sw.Elapsed,
            StartedAtUtc: startedAt));
    }

    /// <summary>Parse `IFM_OK|&lt;size&gt;|&lt;path&gt;` from the ntdsutil IFM result line.</summary>
    internal static (long Size, string Path)? ParseIfmResult(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim();
            if (!t.StartsWith("IFM_OK|", StringComparison.Ordinal)) continue;
            var p = t.Split('|');
            if (p.Length >= 3 && long.TryParse(p[1].Trim(), out var size) && p[2].Trim().Length > 0)
                return (size, p[2].Trim());
        }
        return null;
    }

    /// <summary>Backup-id-safe slug for an operator tag (alnum + dash/underscore).</summary>
    internal static string Sanitize(string s) =>
        new string(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());

    public Task<Result<RestoreResult>> BackupRestoreAsync(RestoreRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<RestoreResult>(
            "AD authoritative restore (`ntdsutil` DSRM) is a console-only DR procedure (Server 2025 blocks it over SSH, "
            + "[[feedback_ntdsutil_dsrm_console_mode_ssh]]); never exposed as a runtime verb."));

    public Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<CertRotationResult>(
            "the DC LDAPS certificate is rotated by the nexus-infra-vmware security overlay (role-overlay-ldaps + an NTDS "
            + "restart, ADR-0015) — not wired here to avoid an unguarded NTDS restart on the live auth plane. Vault's own "
            + "listener certs rotate via `nexus cert-rotate vault`."));

    public Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ChaosOutcome>(
            "chaos on a live domain controller is out of scope — an unguarded ADDS/Netlogon kill risks the auth plane. The "
            + "2-DC HA is validated by smoke-0.M (host-level kill of dc-nexus → auth + DNS continue on dc-nexus-2)."));

    // === CanResizeVm =======================================================
    public bool CanResizeVm(string vmName, string role)
    {
        if (_lastStatus is null) return false;
        var member = _lastStatus.Members.FirstOrDefault(m => string.Equals(m.Hostname, vmName, StringComparison.OrdinalIgnoreCase));
        if (member is null) return false;
        // Refuse the PDC + the single-egress gateway; the secondary DC is resizable.
        return member.Role == "dc";
    }

    private static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
}
