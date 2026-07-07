using System.Diagnostics;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
/// restore (console-only DSRM), an unguarded NTDS cert rotation, FSMO
/// <i>seize</i> (permanent-loss last resort), and chaos (stopping ADDS severs
/// the Netlogon channel SSH auth rides on → the adapter can't recover the DC
/// it stranded; see <see cref="ApplyChaosAsync"/>) — return a graceful,
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

    // === RotateCertAsync — guarded DC LDAPS rotation (GAP #9, v0.8.9) ========
    // Rotate each DC's LDAPS leaf (pki_int/issue/vault-server, openssl PFX on
    // vault-1 — the proven Schannel path; a .NET-exported PFX lands ephemeral in
    // MachineKeys and NTDS resets every handshake). STANDBY-FIRST (the non-PDC),
    // PDC LAST — a botched standby rotation aborts before touching the PDC's auth
    // plane. The install + NTDS restart run in ONE SSH session (sshd is
    // independent of NTDS, so the established session survives the ~20-30s
    // restart), and the :636 handshake is verified from the build host over a
    // fresh TCP+TLS socket (no SSH re-auth during the AD-settle window) — so the
    // #10 self-fence (which needed a NEW SSH connection while NTDS was down)
    // cannot occur.
    private const string PkiMount = "pki_int";
    private const string PkiRole = "vault-server";
    private const string RootCn = "NexusPlatform Root CA";
    private const string IntCn = "NexusPlatform Intermediate CA";
    private const string LdapsTtl = "2160h";
    private const string Vault1IpFallback = "192.168.70.121";

    public async Task<Result<CertRotationResult>> RotateCertAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var token = Environment.GetEnvironmentVariable("VAULT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            return Result.Fail<CertRotationResult>("cert-rotate foundation-ad issues the DC LDAPS leaf from Vault pki_int — export VAULT_TOKEN (the operator token) and retry.");
        var rootPemR = ReadRootPem();
        if (rootPemR.IsFail) return Result.Fail<CertRotationResult>(rootPemR.Error!);
        var rootPem = rootPemR.Value!;
        var vault1 = ResolveVault1Ip();

        // Reachability precheck: only touch DCs whose NTDS + Netlogon are Running.
        var reachable = new List<DcNode>();
        foreach (var dc in Dcs)
        {
            var h = await WinPsAsync(dc.Ip,
                "$s=Get-Service NTDS,Netlogon -EA SilentlyContinue; if(-not $s -or ($s | Where-Object { $_.Status -ne 'Running' })){Write-Output 'NOTREADY'}else{Write-Output 'READY'}",
                cancellationToken).ConfigureAwait(false);
            if (h.IsOk && h.Value!.Contains("READY", StringComparison.Ordinal)) reachable.Add(dc);
        }
        if (reachable.Count == 0)
            return Result.Fail<CertRotationResult>("no DC is reachable with NTDS + Netlogon Running — power on dc-nexus (+ dc-nexus-2) first.");

        // Resolve the PDC so the NON-PDC standby rotates first.
        string? pdcFqdn = null;
        var pdcR = await WinPsAsync(reachable[0].Ip, "try{ Write-Output (Get-ADDomain -ErrorAction Stop).PDCEmulator }catch{}", cancellationToken).ConfigureAwait(false);
        if (pdcR.IsOk) pdcFqdn = pdcR.Value!.Trim();
        bool IsPdc(DcNode d) => pdcFqdn is not null && pdcFqdn.StartsWith(d.Name + ".", StringComparison.OrdinalIgnoreCase);
        var ordered = reachable.OrderBy(d => IsPdc(d) ? 1 : 0).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();

        var rotated = new List<CertRotatedNode>();
        var standbyFailed = false;
        foreach (var dc in ordered)
        {
            var isPdc = IsPdc(dc);
            if (isPdc && standbyFailed)
            {
                rotated.Add(new CertRotatedNode(dc.Name, "(skipped)", "(skipped)",
                    Error: "PDC LDAPS rotation skipped — the standby DC's rotation failed; resolve it first (an NTDS restart on the PDC is the lab's auth plane, and standby-first exists to prove the flow on the non-PDC before touching it)."));
                continue;
            }
            var res = await RotateOneDcLdapsAsync(dc, isPdc, vault1, token, rootPem, cancellationToken).ConfigureAwait(false);
            rotated.Add(res);
            if (res.Error is not null && !isPdc) standbyFailed = true;
        }

        sw.Stop();
        return Result.Ok(new CertRotationResult(rotated, sw.Elapsed, startedAt));
    }

    private async Task<CertRotatedNode> RotateOneDcLdapsAsync(DcNode dc, bool isPdc, string vault1, string token, string rootPem, CancellationToken ct)
    {
        var fqdn = $"{dc.Name}.{Domain}";
        var label = isPdc ? $"{dc.Name} (PDC)" : $"{dc.Name} (standby)";
        var oldSerial = await DcLdapsSerialAsync(dc.Ip, fqdn, ct).ConfigureAwait(false);

        // 1. Issue leaf + build the PFX on vault-1 (openssl).
        var pfxPwd = RandomToken(32);
        var upper = dc.Name.ToUpperInvariant();
        var issueScript = LdapsIssueBash
            .Replace("__TOKEN__", token)
            .Replace("__ROLE__", $"{PkiMount}/issue/{PkiRole}")
            .Replace("__CN__", fqdn)
            .Replace("__ALT__", $"{dc.Name},{upper},{upper}.{Domain}")
            .Replace("__IPSAN__", dc.Ip)
            .Replace("__TTL__", LdapsTtl)
            .Replace("__PFXNAME__", $"{dc.Name}-ldaps")
            .Replace("__PFXPWD__", pfxPwd);
        var issB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(issueScript.Replace("\r\n", "\n")));
        var issR = await _ssh.ExecuteAsync(T(vault1), $"echo {issB64} | base64 -d | bash", TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        if (issR.IsFail || issR.Value!.ExitCode != 0)
            return new CertRotatedNode(dc.Name, oldSerial, "(unchanged)", Error: $"vault-1 issue+PFX failed for {label}: {(issR.IsFail ? issR.Error : Tail(issR.Value!.Stdout + issR.Value.Stderr, 220))}");
        var (pfxB64, intPem) = ParseIssueJson(issR.Value!.Stdout);
        if (pfxB64.Length == 0 || intPem.Length == 0)
            return new CertRotatedNode(dc.Name, oldSerial, "(unchanged)", Error: $"vault-1 issue for {label} returned no PFX/intermediate: {Tail(issR.Value!.Stdout, 200)}");

        // 2. Upload PFX + intermediate + root to the DC (SFTP; the /C:/… form Windows OpenSSH needs).
        byte[] pfxBytes;
        try { pfxBytes = Convert.FromBase64String(pfxB64); }
        catch { return new CertRotatedNode(dc.Name, oldSerial, "(unchanged)", Error: $"{label}: PFX base64 from vault-1 was malformed."); }
        foreach (var (bytes, path, what) in new (byte[], string, string)[]
                 {
                     (pfxBytes, "/C:/Windows/Temp/nx-ldaps.pfx", "PFX"),
                     (Encoding.ASCII.GetBytes(intPem), "/C:/Windows/Temp/nx-ldaps-int.pem", "intermediate"),
                     (Encoding.ASCII.GetBytes(rootPem), "/C:/Windows/Temp/nx-ldaps-root.pem", "root"),
                 })
        {
            var up = await _ssh.UploadBytesAsync(T(dc.Ip), bytes, path, SshTimeout, ct).ConfigureAwait(false);
            if (up.IsFail) return new CertRotatedNode(dc.Name, oldSerial, "(unchanged)", Error: $"{label}: {what} upload failed: {up.Error}");
        }

        // 3. Import (root→Root, int→CA, leaf→My) + verify chain + RESTART NTDS — one session.
        var importScript = LdapsImportPs
            .Replace("__FQDN__", fqdn)
            .Replace("__ROOTCN__", RootCn)
            .Replace("__INTCN__", IntCn)
            .Replace("__PFXPWD__", pfxPwd);
        var scriptUp = await _ssh.UploadBytesAsync(T(dc.Ip), Encoding.UTF8.GetBytes(importScript.Replace("\r\n", "\n")), "/C:/Windows/Temp/nx-ldaps-import.ps1", SshTimeout, ct).ConfigureAwait(false);
        if (scriptUp.IsFail) return new CertRotatedNode(dc.Name, oldSerial, "(unchanged)", Error: $"{label}: import-script upload failed: {scriptUp.Error}");
        var imp = await _ssh.ExecuteAsync(T(dc.Ip), "powershell -NoProfile -ExecutionPolicy Bypass -File C:/Windows/Temp/nx-ldaps-import.ps1", TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (imp.IsFail || imp.Value!.ExitCode != 0 || !imp.Value.Stdout.Contains("LDAPSROT", StringComparison.Ordinal))
            return new CertRotatedNode(dc.Name, oldSerial, "(unchanged)", Error: $"{label} import/NTDS-restart failed: {(imp.IsFail ? imp.Error : Tail(imp.Value!.Stdout + imp.Value.Stderr, 260))}");

        // 4. Verify LDAPS serves the NEW cert on :636 (build-host SslStream — no SSH).
        var verify = await VerifyLdapsAsync(dc.Ip, fqdn, ct).ConfigureAwait(false);
        if (verify.IsFail)
            return new CertRotatedNode(dc.Name, oldSerial, "(unverified)", Error: $"{label}: cert installed + NTDS restarted, but the LDAPS :636 handshake did not complete: {verify.Error}");
        return new CertRotatedNode(dc.Name, oldSerial, verify.Value!, Error: null);
    }

    /// <summary>The serial of the DC's current LDAPS leaf in LocalMachine\My (exact CN match).</summary>
    private async Task<string> DcLdapsSerialAsync(string ip, string fqdn, CancellationToken ct)
    {
        var r = await WinPsAsync(ip,
            $"$x=Get-ChildItem Cert:\\LocalMachine\\My -EA SilentlyContinue | Where-Object {{ $_.Subject -eq 'CN={fqdn}' -and $_.HasPrivateKey }} | Sort-Object NotBefore -Descending | Select-Object -First 1; Write-Output $x.SerialNumber",
            ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Trim().Length > 0 ? r.Value!.Trim() : "(none)";
    }

    /// <summary>Confirm dc:636 completes a TLS handshake; returns the served cert's serial.</summary>
    private static async Task<Result<string>> VerifyLdapsAsync(string ip, string fqdn, CancellationToken ct)
    {
        for (var i = 0; i < 8; i++)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ip, 636, ct).ConfigureAwait(false);
                // Handshake-ONLY verify: we accept any server cert on purpose — the goal is to
                // confirm :636 now serves a working leaf + read its serial. Trust of the chain is
                // proven separately, on the DC, by X509Chain.Build in the import script BEFORE the
                // NTDS restart (a PartialChain there aborts the rotation). So this is not a TLS-trust
                // bypass on a data path — it's a post-rotation liveness probe.
#pragma warning disable CA5359
                using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
#pragma warning restore CA5359
                await ssl.AuthenticateAsClientAsync(fqdn).ConfigureAwait(false);
                var remote = ssl.RemoteCertificate;
                if (remote is null) return Result.Fail<string>("handshake completed but no server certificate was presented.");
                using var cert = remote as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(remote.Export(X509ContentType.Cert));
                return Result.Ok(cert.GetSerialNumberString());
            }
            catch (Exception ex)
            {
                if (i == 7) return Result.Fail<string>($"{ex.GetType().Name}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        return Result.Fail<string>("no handshake after 8 tries");
    }

    private static Result<string> ReadRootPem()
    {
        var path = Environment.GetEnvironmentVariable("VAULT_CACERT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Result.Fail<string>("VAULT_CACERT is not set or missing — it supplies the current root CA to install into each DC's Root store (the Schannel 36886 fix). Point it at ~/.nexus/vault-ca-bundle.crt.");
        try
        {
            var text = File.ReadAllText(path);
            var m = System.Text.RegularExpressions.Regex.Match(text, "-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----", System.Text.RegularExpressions.RegexOptions.Singleline);
            return m.Success ? Result.Ok(m.Value + "\n") : Result.Fail<string>($"no PEM certificate block in VAULT_CACERT ({path}).");
        }
        catch (Exception ex) { return Result.Fail<string>($"failed to read VAULT_CACERT: {ex.Message}"); }
    }

    private string ResolveVault1Ip()
    {
        var loaded = _catalog.Load();
        if (loaded.IsOk)
            foreach (var (_, cluster) in loaded.Value!)
            {
                var v = cluster.Nodes.FirstOrDefault(n => string.Equals(n.Name, "vault-1", StringComparison.OrdinalIgnoreCase));
                if (v is not null) return v.Vmnet11;
            }
        return Vault1IpFallback;
    }

    private static string RandomToken(int n)
    {
        const string cs = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var b = new char[n];
        for (var i = 0; i < n; i++) b[i] = cs[System.Security.Cryptography.RandomNumberGenerator.GetInt32(cs.Length)];
        return new string(b);
    }

    /// <summary>Extract pfx_b64 + intermediate_pem from the vault-1 issue script's JSON line.</summary>
    internal static (string Pfx, string Int) ParseIssueJson(string stdout)
    {
        var line = stdout.Split('\n').Reverse().FirstOrDefault(l => l.TrimStart().StartsWith('{'))?.Trim();
        if (string.IsNullOrEmpty(line)) return ("", "");
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(line);
            var root = d.RootElement;
            var pfx = root.TryGetProperty("pfx_b64", out var p) ? p.GetString() ?? "" : "";
            var it = root.TryGetProperty("intermediate_pem", out var i) ? i.GetString() ?? "" : "";
            return (pfx, it);
        }
        catch { return ("", ""); }
    }

    private const string LdapsIssueBash = """
set -euo pipefail
TMPDIR=$(mktemp -d); trap 'rm -rf "$TMPDIR"' EXIT
ISSUED=$(VAULT_TOKEN='__TOKEN__' VAULT_SKIP_VERIFY=true VAULT_ADDR=https://127.0.0.1:8200 vault write -format=json __ROLE__ common_name=__CN__ alt_names=__ALT__ ip_sans=__IPSAN__ ttl=__TTL__)
echo "$ISSUED" | jq -r '.data.certificate' > "$TMPDIR/cert.pem"
echo "$ISSUED" | jq -r '.data.private_key' > "$TMPDIR/key.pem"
echo "$ISSUED" | jq -r '.data.issuing_ca'  > "$TMPDIR/int.pem"
if [ ! -s "$TMPDIR/cert.pem" ] || [ ! -s "$TMPDIR/key.pem" ] || [ ! -s "$TMPDIR/int.pem" ]; then echo "ERR: empty cert/key/int from vault" >&2; exit 1; fi
openssl pkcs12 -export -inkey "$TMPDIR/key.pem" -in "$TMPDIR/cert.pem" -name '__PFXNAME__' -passout 'pass:__PFXPWD__' -out "$TMPDIR/cert.pfx" 2>/dev/null
PFX_B64=$(base64 -w 0 "$TMPDIR/cert.pfx")
INT_PEM=$(cat "$TMPDIR/int.pem")
jq -nc --arg pfx "$PFX_B64" --arg int "$INT_PEM" '{pfx_b64:$pfx, intermediate_pem:$int}'
""";

    private const string LdapsImportPs = """
$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'
try {
  Get-ChildItem Cert:\LocalMachine\Root -EA SilentlyContinue | Where-Object { $_.Subject -match 'CN=__ROOTCN__' } | ForEach-Object { Remove-Item $_.PSPath -Force }
  Import-Certificate -FilePath 'C:/Windows/Temp/nx-ldaps-root.pem' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
  Get-ChildItem Cert:\LocalMachine\CA -EA SilentlyContinue | Where-Object { $_.Subject -match 'CN=__INTCN__' } | ForEach-Object { Remove-Item $_.PSPath -Force }
  Import-Certificate -FilePath 'C:/Windows/Temp/nx-ldaps-int.pem' -CertStoreLocation Cert:\LocalMachine\CA | Out-Null
  Get-ChildItem Cert:\LocalMachine\My -EA SilentlyContinue | Where-Object { $_.Subject -eq 'CN=__FQDN__' } | ForEach-Object { Remove-Item $_.PSPath -Force }
  $pwd = ConvertTo-SecureString '__PFXPWD__' -AsPlainText -Force
  $imp = Import-PfxCertificate -FilePath 'C:/Windows/Temp/nx-ldaps.pfx' -CertStoreLocation Cert:\LocalMachine\My -Password $pwd -Exportable
  $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
  $chain.ChainPolicy.RevocationMode = 'NoCheck'
  if (-not $chain.Build($imp)) { $s=($chain.ChainStatus | ForEach-Object { $_.Status }) -join ','; Write-Output ('CHAIN_FAILED: '+$s); exit 1 }
  Remove-Item 'C:/Windows/Temp/nx-ldaps.pfx','C:/Windows/Temp/nx-ldaps-int.pem','C:/Windows/Temp/nx-ldaps-root.pem','C:/Windows/Temp/nx-ldaps-import.ps1' -EA SilentlyContinue
  Restart-Service NTDS -Force
  Start-Sleep -Seconds 20
  # Re-cycle ADWS after the NTDS restart: it comes back Running but in a degraded
  # state that fails Get-AD* with "bad parameter" (the adapter's own status/health
  # verbs use Get-AD*), and a clean restart re-establishes it. Best-effort.
  try { Restart-Service ADWS -Force -EA SilentlyContinue; Start-Sleep -Seconds 5 } catch {}
  Write-Output ('LDAPSROT thumb=' + $imp.Thumbprint + ' serial=' + $imp.SerialNumber + ' ntds=' + (Get-Service NTDS).Status + ' adws=' + (Get-Service ADWS).Status)
} catch { Write-Output ('IMPORT_FAILED: ' + $_); exit 1 }
""";

    // === ApplyChaosAsync — graceful, evidence-based N/A ======================
    // GENUINE N/A for an SSH-managed adapter, not a stub. A meaningful DC chaos
    // means taking the directory service down — but `Stop-Service NTDS` also
    // stops Netlogon (a dependent), which severs the DC's domain secure channel,
    // and OpenSSH authenticates the domain `nexusadmin` THROUGH that channel.
    // So the moment the chaos lands, the adapter can no longer SSH back in to
    // recover it (live-proven 2026-06-29: an in-adapter NTDS stop on the non-PDC
    // dc-nexus-2 left it `Permission denied (publickey)` — recovery required an
    // out-of-band `vmrun reset`, outside the SSH-shell-out architecture
    // [ADR-0009]). The verb would therefore strand the very DC it "tests". The
    // 2-DC HA property it would demonstrate is already validated out-of-band by
    // smoke-0.M (host-level kill of a DC → auth + DNS continue on the survivor).
    public Task<Result<ChaosOutcome>> ApplyChaosAsync(ChaosScenario scenario, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Fail<ChaosOutcome>(
            "chaos on a domain controller is a genuine N/A for this SSH-managed adapter: a meaningful DC chaos stops "
            + "ADDS/NTDS, which also stops Netlogon and severs the domain secure channel OpenSSH uses to authenticate "
            + "`nexusadmin` — so the chaos self-fences the adapter's own recovery path (it cannot SSH back in to restart "
            + "the service; recovery needs an out-of-band `vmrun reset`). The 2-DC HA is validated out-of-band by "
            + "smoke-0.M (host-level kill of a DC → auth + DNS continue on the survivor)."));

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
