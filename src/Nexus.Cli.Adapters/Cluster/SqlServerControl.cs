using System.Text;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Shared Windows-over-SSH control plane for the two SQL Server adapters
/// (<see cref="SqlFciAdapter"/> + <see cref="SqlAgAdapter"/>), Phase 0.G.7 /
/// nexus-cli v0.6.6. This is the FIRST Windows cluster behind the
/// <see cref="IClusterAdapter"/> SPI — the access pattern differs fundamentally
/// from the six Linux adapters:
/// <list type="bullet">
///   <item><b>Every remote command is wrapped in</b> <c>powershell -NoProfile
///   -EncodedCommand &lt;base64-UTF16&gt;</c> (the smoke gate's Invoke-RemoteWin
///   shape). Plain multi-token commands get mangled by cmd.exe between sshd and
///   the shell (memory: windows-automation-over-ssh rule #2). EncodedCommand is
///   robust to either Windows default shell.</item>
///   <item><b>Two auth planes</b> (decided from the live probe 2026-06-12):
///   <list type="bullet">
///     <item>WSFC/FCI cluster-resource cmdlets (Get-Cluster*, Move-ClusterGroup,
///     Get-IscsiSession) run over plain SSH as the LOCAL nexusadmin — it carries
///     cluster-admin rights on the local node (the cluster service runs as
///     SYSTEM). Cross-machine cmdlets that open a remote SCM (Start-ClusterNode
///     &lt;other&gt;) FAIL (no network identity) — restart ClusSvc locally on the
///     target instead.</item>
///     <item>T-SQL against the FCI + the AG Listener runs as the dedicated SQL
///     login <c>nexus-cluster-admin</c> (the LOCKED Vault-KV operator-credential
///     model, ADR-0011 family; password ONLY in Vault KV
///     <c>nexus/oltp/sqlserver/operator-password</c> field <c>password</c>, via
///     <see cref="INexusVaultClient"/>). <c>$env:SQLCMDPASSWORD</c> avoids -P argv
///     exposure (mirrors MYSQL_PWD). The FCI is mixed-mode; the standalone AG
///     replicas are Windows-auth-only, so direct-replica T-SQL (the AG FAILOVER
///     issued on a secondary) uses Windows-auth <c>-E</c> (local nexusadmin IS
///     sysadmin on the replicas).</item>
///   </list></item>
/// </list>
/// No managed Microsoft.Data.SqlClient driver is linked (NetArchTest-enforced);
/// all T-SQL goes through the on-node <c>sqlcmd</c> (ODBC Driver 18 Tools).
/// </summary>
internal sealed class SqlServerControl
{
    // === vms.yaml + contract constants (live, probed 2026-06-12) ============
    /// <summary>vms.yaml cluster name holding both the FCI pair and the AG replicas.</summary>
    public const string VmsCluster = "sqlserver";
    /// <summary>The dedicated Vault-KV SQL operator login used for all T-SQL against the FCI/Listener.</summary>
    public const string OperatorUser = "nexus-cluster-admin";
    /// <summary>The FCI virtual SQL server name (the AG primary), reachable at .16.</summary>
    public const string FciVirtualServer = "sqlfci";          // FCI virtual SQL name @ .16
    /// <summary>The Always On AG Listener short name (the client front door).</summary>
    public const string ListenerName = "sql-ag-listener";     // AG Listener short name
    /// <summary>The AG Listener FQDN, reachable at .17:1433.</summary>
    public const string ListenerFqdn = "sql-ag-listener.nexus.lab"; // @ .17:1433
    /// <summary>The Always On Availability Group name.</summary>
    public const string AgName = "nexus-ag";
    /// <summary>The demo database replicated by the AG (and backed up/restored by the verbs).</summary>
    public const string AgDb = "nexus_demo";
    /// <summary>The Windows Server Failover Cluster (CNO) name.</summary>
    public const string WsfcCluster = "sql-fci-cluster";
    /// <summary>The WSFC cluster group (role) that owns the FCI instance.</summary>
    public const string SqlServerGroup = "SQL Server (MSSQLSERVER)"; // the FCI cluster role
    /// <summary>The Windows service name of the SQL Server engine.</summary>
    public const string SqlServiceName = "MSSQLSERVER";
    /// <summary>Vault PKI intermediate mount that issues the SQL Server leaf certs.</summary>
    public const string PkiMount = "pki_int";
    /// <summary>Vault PKI role used to issue SQL Server server certs.</summary>
    public const string PkiRole = "sqlserver-server";

    private const string VaultMount = "nexus";
    private const string OperatorPwdPath = "oltp/sqlserver/operator-password";
    private const string PwdField = "password";

    /// <summary>Default timeout for a Windows-over-SSH PowerShell command.</summary>
    public static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Default timeout for a sqlcmd T-SQL round-trip.</summary>
    public static readonly TimeSpan SqlTimeout = TimeSpan.FromSeconds(90);
    /// <summary>Poll interval used while waiting for cluster/AG state transitions.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly IVmsCatalog _catalog;
    private readonly ISshClient _ssh;
    private readonly string _sshUsername;
    private readonly string _sshKeyPath;
    private readonly INexusVaultClient? _vault;
    private string? _operatorPassword;

    /// <summary>
    /// Creates the control plane over the vms.yaml catalog, an SSH client + credentials
    /// (the Windows-SSH transport), and an optional operator <see cref="INexusVaultClient"/>
    /// (the Vault-KV source of the SQL operator password + the PKI issuer for cert-rotate).
    /// </summary>
    public SqlServerControl(IVmsCatalog catalog, ISshClient ssh, string sshUsername, string sshKeyPath, INexusVaultClient? vault)
    {
        _catalog = catalog;
        _ssh = ssh;
        _sshUsername = sshUsername;
        _sshKeyPath = sshKeyPath;
        _vault = vault;
    }

    /// <summary>The optional operator Vault client (null when the operator token is not set).</summary>
    public INexusVaultClient? Vault => _vault;

    // === node discovery ====================================================
    /// <summary>True if the node is an FCI node (sql-fci*).</summary>
    public static bool IsFci(NodeRecord n) => n.Name.StartsWith("sql-fci", StringComparison.OrdinalIgnoreCase);
    /// <summary>True if the node is a standalone AG replica (sql-ag-rep*).</summary>
    public static bool IsRep(NodeRecord n) => n.Name.StartsWith("sql-ag-rep", StringComparison.OrdinalIgnoreCase);

    /// <summary>Split vms.yaml cluster `sqlserver` into FCI pair + AG replica pair.</summary>
    public Result<(IReadOnlyList<NodeRecord> Fci, IReadOnlyList<NodeRecord> Rep)> Split()
    {
        var cluster = _catalog.GetCluster(VmsCluster);
        if (cluster.IsFail) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>(cluster.Error!);
        var fci = cluster.Value!.Nodes.Where(IsFci).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        var rep = cluster.Value.Nodes.Where(IsRep).OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        if (fci.Count == 0) return Result.Fail<(IReadOnlyList<NodeRecord>, IReadOnlyList<NodeRecord>)>("no sql-fci* nodes in vms.yaml cluster 'sqlserver'");
        return Result.Ok(((IReadOnlyList<NodeRecord>)fci, (IReadOnlyList<NodeRecord>)rep));
    }

    /// <summary>All sqlserver-cluster nodes (FCI pair + AG replicas), or empty if discovery fails.</summary>
    public IReadOnlyList<NodeRecord> AllNodes()
    {
        var s = Split();
        return s.IsOk ? s.Value.Fci.Concat(s.Value.Rep).ToList() : Array.Empty<NodeRecord>();
    }

    /// <summary>Look up a cluster node by name (case-insensitive), or null if not found.</summary>
    public NodeRecord? NodeByName(string name)
    {
        var s = Split();
        if (s.IsFail) return null;
        return s.Value.Fci.Concat(s.Value.Rep).FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Build an SSH target (port 22, adapter credentials) for a node IP.</summary>
    public SshTarget T(string ip) => new(ip, 22, _sshUsername, _sshKeyPath);

    // === operator password =================================================
    /// <summary>Lazily fetch (and cache) the nexus-cluster-admin password from Vault KV.</summary>
    public async Task<Result<string>> OperatorPwdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_operatorPassword)) return Result.Ok(_operatorPassword);
        if (_vault is null)
            return Result.Fail<string>(
                "SQL Server verbs authenticate as nexus-cluster-admin against the FCI, whose password lives in Vault KV. "
                + "Set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var r = await _vault.ReadKvFieldAsync(VaultMount, OperatorPwdPath, PwdField, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"could not read operator password from Vault ({VaultMount}/{OperatorPwdPath}): {r.Error}");
        _operatorPassword = r.Value;
        return Result.Ok(_operatorPassword!);
    }

    // === Windows-SSH primitives ============================================
    /// <summary>Run a PowerShell script on a Windows node via EncodedCommand. Returns the raw SshExecResult.</summary>
    public Task<Result<SshExecResult>> WinExecAsync(string ip, string psScript, CancellationToken ct, TimeSpan? timeout = null)
    {
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
        var cmd = $"powershell -NoProfile -EncodedCommand {b64}";
        return _ssh.ExecuteAsync(T(ip), cmd, timeout ?? SshTimeout, ct);
    }

    /// <summary>Upload bytes to a remote path over SFTP (cert-rotate ships the PFX this way).</summary>
    public Task<Result<bool>> UploadAsync(string ip, byte[] content, string remotePath, CancellationToken ct, TimeSpan? timeout = null) =>
        _ssh.UploadBytesAsync(T(ip), content, remotePath, timeout ?? SshTimeout, ct);

    /// <summary>Download a remote file over SFTP (AG scale-out ferries the .bak/.trn manual-seed base this way).</summary>
    public Task<Result<byte[]>> DownloadAsync(string ip, string remotePath, CancellationToken ct, TimeSpan? timeout = null) =>
        _ssh.DownloadBytesAsync(T(ip), remotePath, timeout ?? SshTimeout, ct);

    /// <summary>Run a PowerShell script; succeed on exit 0, returning trimmed stdout.</summary>
    public async Task<Result<string>> WinPsAsync(string ip, string psScript, CancellationToken ct, TimeSpan? timeout = null)
    {
        var r = await WinExecAsync(ip, psScript, ct, timeout).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"ssh(win) to {ip} failed: {r.Error}");
        if (r.Value!.ExitCode != 0)
            return Result.Fail<string>($"remote PowerShell on {ip} exit {r.Value.ExitCode}: {Tail((r.Value.Stdout + "\n" + r.Value.Stderr).Trim(), 300)}");
        return Result.Ok(r.Value.Stdout.Trim());
    }

    /// <summary>
    /// Run T-SQL on a node via sqlcmd. <paramref name="operatorAuth"/>=true uses the
    /// SQL login nexus-cluster-admin (FCI/Listener path); false uses Windows-auth -E
    /// (standalone replicas). <paramref name="enc"/>: -C trust, -N strict.
    /// </summary>
    public async Task<Result<string>> SqlAsync(string ip, string server, string tsql, CancellationToken ct,
        bool operatorAuth = true, string enc = "-C", TimeSpan? timeout = null)
    {
        string authPrep, authArgs;
        if (operatorAuth)
        {
            var pwd = await OperatorPwdAsync(ct).ConfigureAwait(false);
            if (pwd.IsFail) return Result.Fail<string>(pwd.Error!);
            var pwB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(pwd.Value!));
            authPrep = $"$env:SQLCMDPASSWORD=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{pwB64}'));";
            authArgs = $"-U {OperatorUser}";
        }
        else
        {
            authPrep = string.Empty;
            authArgs = "-E";
        }
        var tsqlB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(tsql));
        var ps =
            "$ErrorActionPreference='Continue';" + authPrep +
            $"$sql=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{tsqlB64}'));" +
            $"$o=& sqlcmd -S '{server}' {authArgs} {enc} -b -h -1 -W -Q $sql 2>&1 | Out-String;" +
            "$rc=$LASTEXITCODE; Write-Output $o.Trim(); exit $rc";
        var r = await WinExecAsync(ip, ps, ct, timeout ?? SqlTimeout).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<string>($"ssh(win-sql) to {ip} failed: {r.Error}");
        if (r.Value!.ExitCode != 0)
            return Result.Fail<string>($"sqlcmd on {ip} (-S {server}) exit {r.Value.ExitCode}: {Tail(r.Value.Stdout.Trim(), 300)}");
        return Result.Ok(r.Value.Stdout.Trim());
    }

    // === cluster-cmdlet helpers (plain SSH, local nexusadmin) ==============
    private static readonly char[] Nl = ['\n'];

    /// <summary>Get-ClusterNode → name→state map (Up/Down/Paused/Joining). Run from any node.</summary>
    public async Task<Result<Dictionary<string, string>>> ClusterNodesAsync(string ip, CancellationToken ct)
    {
        var ps = "$ErrorActionPreference='SilentlyContinue';" +
            "Get-ClusterNode | ForEach-Object { Write-Output ($_.Name + '|' + $_.State) }";
        var r = await WinPsAsync(ip, ps, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<Dictionary<string, string>>(r.Error!);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in r.Value!.Split(Nl, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('|');
            if (p.Length >= 2) map[p[0].Trim()] = p[1].Trim();
        }
        return Result.Ok(map);
    }

    /// <summary>Get-ClusterGroup &lt;name&gt; → (State, OwnerNode). Run from any node.</summary>
    public async Task<Result<(string State, string Owner)>> ClusterGroupAsync(string ip, string group, CancellationToken ct)
    {
        var esc = group.Replace("'", "''");
        var ps = "$ErrorActionPreference='SilentlyContinue';" +
            $"$g=Get-ClusterGroup -Name '{esc}'; Write-Output ($g.State.ToString() + '|' + $g.OwnerNode.Name)";
        var r = await WinPsAsync(ip, ps, ct).ConfigureAwait(false);
        if (r.IsFail) return Result.Fail<(string, string)>(r.Error!);
        var p = r.Value!.Trim().Split('|');
        if (p.Length < 2) return Result.Fail<(string, string)>($"unexpected Get-ClusterGroup output: {r.Value}");
        return Result.Ok((p[0].Trim(), p[1].Trim()));
    }

    /// <summary>True if <paramref name="nodeName"/> reports WSFC state Up (probed from <paramref name="ip"/>).</summary>
    public async Task<bool> NodeStateUpAsync(string ip, string nodeName, CancellationToken ct)
    {
        var nodes = await ClusterNodesAsync(ip, ct).ConfigureAwait(false);
        return nodes.IsOk && nodes.Value!.TryGetValue(nodeName, out var st) && st.Equals("Up", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if the named Windows service is Running on the node.</summary>
    public async Task<bool> ServiceRunningAsync(string ip, string svc, CancellationToken ct)
    {
        var r = await WinPsAsync(ip, $"Write-Output (Get-Service {svc} -EA SilentlyContinue).Status", ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Trim().Equals("Running", StringComparison.OrdinalIgnoreCase);
    }

    // === string helpers ====================================================
    /// <summary>Last <paramref name="n"/> chars of a string (for trimming long stderr into error messages).</summary>
    public static string Tail(string s, int n) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s.Substring(s.Length - n));
    /// <summary>Truncate a string to <paramref name="n"/> chars with an ellipsis.</summary>
    public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    /// <summary>UTF-8 base64-encode a string (for shipping payloads through the SSH transport).</summary>
    public static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    /// <summary>Parse pipe-delimited tuple rows (sqlcmd -h -1 -W with a|b|c SELECT).</summary>
    public static List<string[]> PipeRows(string stdout) =>
        stdout.Split(Nl, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l.Contains('|'))
            .Select(l => l.Split('|'))
            .ToList();
}
