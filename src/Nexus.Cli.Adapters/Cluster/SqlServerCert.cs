using System.Security.Cryptography.X509Certificates;
using Nexus.Cli.Core;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Shared SQL Server cert-rotation primitive for both adapters (Phase 0.G.7).
/// ws2025 has no openssl and .NET Framework 4.8 on-node can't import a PKCS#1
/// PEM key, so the cert is issued + turned into a PFX on the BUILD HOST (where
/// the CLI runs under .NET 10) and shipped to the node for
/// <c>Import-PfxCertificate</c> — mirroring role-overlay-sqlserver-tls.tf. The
/// node then: imports the unified leaf to LocalMachine\My + the chain to CA/Root,
/// grants the SQL service account READ on the new private key, and (optionally)
/// repoints <c>HKLM:\…\SuperSocketNetLib\Certificate</c> at the new thumbprint.
/// <para>
/// TWO flows (per the live-caught FCI bug, 2026-06-12): a standalone instance
/// (AG replica) owns its OWN per-node <c>SuperSocketNetLib\Certificate</c> — so
/// each replica rotates independently (<see cref="RotateStandaloneAsync"/>). An
/// FCI checkpoints ONE cluster-replicated thumbprint applied to whichever node
/// hosts it — so the FCI must rotate to ONE shared cert imported on BOTH nodes
/// with a SINGLE checkpoint write (<see cref="IssueAsync"/> +
/// <see cref="ImportOnNodeAsync"/> + <see cref="SetCheckpointAsync"/>, driven by
/// <see cref="SqlFciAdapter.RotateCertAsync"/>). A per-node rotate would re-break
/// FCI failover.
/// </para>
/// </summary>
internal static class SqlServerCert
{
    private const string PfxTempPwd = "nexustempbake";

    /// <summary>A build-host-issued cert bundle ready to ship to a node: the base64 PFX
    /// (leaf + key), the base64 intermediate + root CA PEMs, and the leaf serial.</summary>
    internal sealed record CertArtifacts(string PfxB64, string InterB64, string RootB64, string Serial);

    /// <summary>Read the serial of the cert currently in My matching a CN (the "old" serial).</summary>
    public static async Task<string> OldSerialAsync(SqlServerControl c, NodeRecord node, string cn, CancellationToken ct)
    {
        var r = await c.WinPsAsync(node.Vmnet11,
            "$ErrorActionPreference='SilentlyContinue';" +
            $"$x=Get-ChildItem Cert:\\LocalMachine\\My | Where-Object {{ $_.Subject -eq 'CN={cn}' -and $_.HasPrivateKey }} | Sort-Object NotBefore -Descending | Select-Object -First 1;" +
            "Write-Output $x.SerialNumber", ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Trim().Length > 0 ? r.Value!.Trim() : "(unknown)";
    }

    /// <summary>SQL service account on a node (gmsa on FCI, NETWORK SERVICE on replicas).</summary>
    public static async Task<string> ServiceAccountAsync(SqlServerControl c, NodeRecord node, CancellationToken ct)
    {
        var r = await c.WinPsAsync(node.Vmnet11,
            "Write-Output (Get-CimInstance Win32_Service -Filter \"Name='MSSQLSERVER'\").StartName", ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Trim().Length > 0 ? r.Value!.Trim() : "NT AUTHORITY\\NETWORK SERVICE";
    }

    /// <summary>Issue ONE cert from Vault PKI + build the PFX on the build host.</summary>
    public static async Task<Result<CertArtifacts>> IssueAsync(SqlServerControl c, string cn, string alt, string ipsan, CancellationToken ct)
    {
        if (c.Vault is null) return Result.Fail<CertArtifacts>("cert-rotate issues certs via Vault PKI; set VAULT_ADDR + VAULT_TOKEN + VAULT_CACERT and retry.");
        var issue = await c.Vault.IssuePkiCertAsync(SqlServerControl.PkiMount, SqlServerControl.PkiRole, cn, alt, ipsan, "2160h", ct).ConfigureAwait(false);
        if (issue.IsFail) return Result.Fail<CertArtifacts>($"vault issue failed: {issue.Error}");
        byte[] pfxBytes;
        try
        {
            using var leaf = X509Certificate2.CreateFromPem(issue.Value!.Certificate, issue.Value.PrivateKey);
            pfxBytes = leaf.Export(X509ContentType.Pfx, PfxTempPwd);
        }
        catch (Exception ex)
        {
            return Result.Fail<CertArtifacts>($"PFX build failed: {ex.Message}");
        }
        return Result.Ok(new CertArtifacts(
            Convert.ToBase64String(pfxBytes),
            issue.Value!.CaChain.Count > 0 ? SqlServerControl.B64(issue.Value.CaChain[0]) : "",
            issue.Value.CaChain.Count > 1 ? SqlServerControl.B64(issue.Value.CaChain[1]) : "",
            string.IsNullOrEmpty(issue.Value.SerialNumber) ? "(unknown)" : issue.Value.SerialNumber));
    }

    /// <summary>
    /// Ship a prebuilt PFX to a node, import the leaf (+chain), grant the SQL
    /// service account read on the key, and optionally rebind
    /// SuperSocketNetLib\Certificate. Returns the imported thumbprint.
    /// </summary>
    public static async Task<Result<string>> ImportOnNodeAsync(SqlServerControl c, NodeRecord node, CertArtifacts art, string svcAccount, bool setCheckpoint, CancellationToken ct)
    {
        // Ship the PFX (+ chain) over SFTP — an inline base64 EncodedCommand would
        // blow past the Windows command-line limit (live-caught, 2026-06-12).
        // Windows OpenSSH SFTP needs the /C:/… absolute form (a bare C:/… resolves
        // relative to the SSH home). PowerShell reads the same file as C:/….
        var pfxUp = await c.UploadAsync(node.Vmnet11, Convert.FromBase64String(art.PfxB64), "/C:/Windows/Temp/nx-rot.pfx", ct).ConfigureAwait(false);
        if (pfxUp.IsFail) return Result.Fail<string>($"PFX upload failed: {pfxUp.Error}");
        var haveInter = art.InterB64.Length > 0;
        var haveRoot = art.RootB64.Length > 0;
        if (haveInter)
        {
            var u = await c.UploadAsync(node.Vmnet11, Convert.FromBase64String(art.InterB64), "/C:/Windows/Temp/nx-rot-int.crt", ct).ConfigureAwait(false);
            if (u.IsFail) return Result.Fail<string>($"intermediate upload failed: {u.Error}");
        }
        if (haveRoot)
        {
            var u = await c.UploadAsync(node.Vmnet11, Convert.FromBase64String(art.RootB64), "/C:/Windows/Temp/nx-rot-root.crt", ct).ConfigureAwait(false);
            if (u.IsFail) return Result.Fail<string>($"root upload failed: {u.Error}");
        }
        var script = ImportScript
            .Replace("__SVC__", svcAccount.Replace("'", "''"))
            .Replace("__SETREG__", setCheckpoint ? "1" : "0");
        var imp = await c.WinPsAsync(node.Vmnet11, script, ct, TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        if (imp.IsFail || !imp.Value!.Contains("ROTOK", StringComparison.Ordinal))
            return Result.Fail<string>(imp.IsFail ? imp.Error! : $"import/rebind failed: {SqlServerControl.Tail(imp.Value!, 200)}");
        var m = System.Text.RegularExpressions.Regex.Match(imp.Value!, @"thumb=([0-9A-Fa-f]+)");
        return Result.Ok(m.Success ? m.Groups[1].Value : "(unknown)");
    }

    /// <summary>Set the FCI's SuperSocketNetLib\Certificate checkpoint (one node; the cluster replicates it).</summary>
    public static async Task<Result<string>> SetCheckpointAsync(SqlServerControl c, string ip, string thumbprint, CancellationToken ct)
    {
        var ps =
            "$ErrorActionPreference='Stop';" +
            "$inst=(Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Microsoft SQL Server\\Instance Names\\SQL').MSSQLSERVER;" +
            $"Set-ItemProperty -Path (\"HKLM:\\SOFTWARE\\Microsoft\\Microsoft SQL Server\\$inst\\MSSQLServer\\SuperSocketNetLib\") -Name Certificate -Value '{thumbprint.ToLowerInvariant()}';" +
            "Write-Output 'CHECKPOINT_SET'";
        return await c.WinPsAsync(ip, ps, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rotate a STANDALONE instance's cert (AG replica): issue + import + rebind its
    /// own per-node checkpoint. Does NOT restart SQL (the caller does).
    /// </summary>
    public static async Task<CertRotatedNode> RotateStandaloneAsync(SqlServerControl c, NodeRecord node, bool fciSans, CancellationToken ct)
    {
        var cn = $"{node.Name}.sqlserver.nexus.lab";
        var oldSerial = await OldSerialAsync(c, node, cn, ct).ConfigureAwait(false);
        var svc = await ServiceAccountAsync(c, node, ct).ConfigureAwait(false);
        var (alt, ipsan) = Sans(node, fciSans);
        var art = await IssueAsync(c, cn, alt, ipsan, ct).ConfigureAwait(false);
        if (art.IsFail) return new CertRotatedNode(node.Name, oldSerial, "(unchanged)", Error: art.Error);
        var imp = await ImportOnNodeAsync(c, node, art.Value!, svc, setCheckpoint: true, ct).ConfigureAwait(false);
        if (imp.IsFail) return new CertRotatedNode(node.Name, oldSerial, "(unchanged)", Error: imp.Error);
        return new CertRotatedNode(node.Name, oldSerial, art.Value!.Serial, Error: null);
    }

    /// <summary>SAN lists mirroring role-overlay-sqlserver-tls.tf.</summary>
    public static (string Alt, string IpSan) Sans(NodeRecord node, bool fciSans) => fciSans
        ? ($"{node.Name},{node.Name}.nexus.lab,{node.Name}.sqlserver.nexus.lab,sql-fci-cluster,sql-fci-cluster.nexus.lab,sqlfci,sqlfci.nexus.lab,sql-ag-listener,sql-ag-listener.nexus.lab,localhost",
           $"{node.Vmnet11},{node.Vmnet10},127.0.0.1,192.168.70.16,192.168.70.17")
        : ($"{node.Name},{node.Name}.nexus.lab,{node.Name}.sqlserver.nexus.lab,sql-ag-listener,sql-ag-listener.nexus.lab,localhost",
           $"{node.Vmnet11},{node.Vmnet10},127.0.0.1,192.168.70.17");

    private const string ImportScript = """
$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'
$c=Import-PfxCertificate -FilePath 'C:/Windows/Temp/nx-rot.pfx' -CertStoreLocation Cert:\LocalMachine\My -Password (ConvertTo-SecureString 'nexustempbake' -AsPlainText -Force) -Exportable
if(Test-Path 'C:/Windows/Temp/nx-rot-int.crt'){Import-Certificate -FilePath 'C:/Windows/Temp/nx-rot-int.crt' -CertStoreLocation Cert:\LocalMachine\CA -EA SilentlyContinue | Out-Null}
if(Test-Path 'C:/Windows/Temp/nx-rot-root.crt'){Import-Certificate -FilePath 'C:/Windows/Temp/nx-rot-root.crt' -CertStoreLocation Cert:\LocalMachine\Root -EA SilentlyContinue | Out-Null}
$thumb=$c.Thumbprint
try {
  $rsa=[System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($c)
  $kp=$null
  if($rsa -is [System.Security.Cryptography.RSACng]){ $kp=Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\'+$rsa.Key.UniqueName) }
  elseif($rsa.CspKeyContainerInfo){ $kp=Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\'+$rsa.CspKeyContainerInfo.UniqueKeyContainerName) }
  $verb=[char]47+'grant'
  if($kp -and (Test-Path $kp)){ icacls $kp $verb ('__SVC__:(R)') | Out-Null }
} catch {}
if('__SETREG__' -eq '1'){
  $inst=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL').MSSQLSERVER
  Set-ItemProperty -Path ("HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$inst\MSSQLServer\SuperSocketNetLib") -Name Certificate -Value $thumb.ToLower()
}
Remove-Item 'C:/Windows/Temp/nx-rot.pfx','C:/Windows/Temp/nx-rot-int.crt','C:/Windows/Temp/nx-rot-root.crt' -EA SilentlyContinue
Write-Output ('ROTOK thumb=' + $thumb)
""";
}
