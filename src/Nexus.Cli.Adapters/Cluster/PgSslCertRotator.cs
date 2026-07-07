using Nexus.Cli.Core;
using Nexus.Cli.Core.Abstractions;
using Nexus.Cli.Core.Models;

namespace Nexus.Cli.Adapters.Cluster;

/// <summary>
/// Shared TLS-leaf rotation for a PostgreSQL streaming-replication pair — <c>grafana-pg</c>
/// on the observability tier (v0.8.7 GAP #5) and <c>iceberg-pg</c> on the lakehouse tier
/// (GAP #6). Both are a 2-node PG 17 pair behind a keepalived VRRP VIP whose leaf is
/// rendered by each node's own <c>nexus-vault-agent</c> (<c>pkiCert</c> → <c>bundle.pem</c>,
/// post-render split into <c>server.crt</c>/<c>server.key</c>).
///
/// <para>
/// The rotation is <b>STANDBY-FIRST, then PRIMARY</b> to minimise write-window risk, and PG
/// picks up the new leaf via a SIGHUP <c>systemctl reload</c> — <b>never a restart</b>. A
/// reload re-reads <c>ssl_cert_file</c>/<c>ssl_key_file</c> for NEW connections while existing
/// sessions (including the streaming-replication connection) keep running on the old context,
/// so replication is never dropped and the primary's write window is not interrupted. If the
/// standby's rotation fails, the primary is deliberately left untouched (rotating the
/// sole-serving primary blind is exactly what standby-first exists to avoid).
/// </para>
/// <para>
/// The vault-agent re-render is forced the same way every tier does it (the Swarm v0.8.2
/// lesson): back up + remove the rendered <c>bundle.pem</c> (<c>pkiCert</c> otherwise PERSISTS
/// + reuses the leaf across a bare restart), restart the agent, wait for the re-render, and
/// restore the bundle if it did not reappear.
/// </para>
/// </summary>
internal static class PgSslCertRotator
{
    /// <summary>
    /// Rotate the leaf on a PG streaming pair. The primary is the node where
    /// <c>pg_is_in_recovery()</c> returns <c>f</c>; the other node(s) rotate first.
    /// </summary>
    public static async Task<List<CertRotatedNode>> RotatePairAsync(
        ISshClient ssh,
        Func<string, SshTarget> target,
        IReadOnlyList<NodeRecord> pair,
        string tlsDir,
        string pgUnit,
        TimeSpan sshTimeout,
        CancellationToken ct)
    {
        var rotated = new List<CertRotatedNode>();
        if (pair.Count == 0) return rotated;

        var certPath = $"{tlsDir}/server.crt";
        var bundle = $"{tlsDir}/bundle.pem";

        // 1. Resolve the primary (in_recovery='f') so the standby rotates FIRST.
        string? primaryIp = null;
        foreach (var n in pair)
        {
            var r = await ssh.ExecuteAsync(target(n.Vmnet11),
                "sudo -u postgres psql -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null",
                sshTimeout, ct).ConfigureAwait(false);
            if (r.IsOk && string.Equals(r.Value!.Stdout.Trim(), "f", StringComparison.Ordinal))
            {
                primaryIp = n.Vmnet11;
                break;
            }
        }

        // 2. Order: standby(s) first, the primary last; stable by name otherwise.
        var ordered = OrderStandbyFirst(pair, primaryIp);

        var standbyFailed = false;
        foreach (var n in ordered)
        {
            var isPrimary = primaryIp is not null && n.Vmnet11 == primaryIp;

            // Safety: never rotate the primary if a standby's rotation failed.
            if (isPrimary && standbyFailed)
            {
                rotated.Add(new CertRotatedNode(n.Name, "(skipped)", "(skipped)",
                    Error: "primary rotation skipped — a standby's rotation failed; resolve it first (rotating the sole-serving primary blind is what standby-first avoids)."));
                continue;
            }

            var oldSerial = await SerialAsync(ssh, target, n.Vmnet11, certPath, sshTimeout, ct).ConfigureAwait(false);

            var script = string.Join(" ; ", new[]
            {
                $"if sudo test -f \"{bundle}\"; then sudo cp -a \"{bundle}\" \"{bundle}.bak\"; sudo rm -f \"{bundle}\"; fi",
                "sudo systemctl restart nexus-vault-agent 2>/dev/null",
                $"for i in $(seq 1 25); do sudo test -f \"{bundle}\" && break; sleep 1; done",
                $"if sudo test -f \"{bundle}.bak\"; then if sudo test -f \"{bundle}\"; then sudo rm -f \"{bundle}.bak\"; else sudo mv \"{bundle}.bak\" \"{bundle}\"; fi; fi",
                // let the post-render hook split bundle.pem → server.crt/server.key
                $"for i in $(seq 1 10); do sudo test -f \"{certPath}\" && break; sleep 1; done",
                // SIGHUP reload: re-reads the leaf for new connections; replication keeps running.
                $"sudo systemctl reload {pgUnit}",
                "echo ROTATED",
            });
            var exec = await ssh.ExecuteAsync(target(n.Vmnet11), script, sshTimeout, ct).ConfigureAwait(false);
            if (exec.IsFail || !exec.Value!.Stdout.Contains("ROTATED", StringComparison.Ordinal))
            {
                if (!isPrimary) standbyFailed = true;
                rotated.Add(new CertRotatedNode(n.Name, oldSerial, "(unchanged)",
                    Error: exec.IsFail
                        ? exec.Error
                        : $"force-rerender/reload failed (node may be on the OLD Vault root — needs the trust re-cert): {Tail(exec.Value!.Stdout + exec.Value.Stderr, 200)}"));
                continue;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            var newSerial = await SerialAsync(ssh, target, n.Vmnet11, certPath, sshTimeout, ct).ConfigureAwait(false);
            // A same-serial result means the agent re-used the cached leaf (still on the old root, or the
            // template TTL hadn't elapsed) — surface it rather than claim a rotation that didn't happen.
            var err = !string.Equals(oldSerial, "(unknown)", StringComparison.Ordinal) &&
                      string.Equals(oldSerial, newSerial, StringComparison.Ordinal)
                ? "serial unchanged after re-render (vault-agent reused the cached leaf — check the node is on the current Vault root)"
                : null;
            if (err is not null && !isPrimary) standbyFailed = true;
            rotated.Add(new CertRotatedNode(n.Name, oldSerial, newSerial, err));
        }
        return rotated;
    }

    /// <summary>
    /// Order a PG pair so the standby (or any non-primary) rotates FIRST and the primary LAST;
    /// stable by node name when the primary is unknown. Pure + unit-tested — this is the
    /// safety-critical ordering that keeps the write-primary's cert rotation to the very end.
    /// </summary>
    internal static List<NodeRecord> OrderStandbyFirst(IReadOnlyList<NodeRecord> pair, string? primaryIp) =>
        pair.OrderBy(n => primaryIp is not null && n.Vmnet11 == primaryIp ? 1 : 0)
            .ThenBy(n => n.Name, StringComparer.Ordinal)
            .ToList();

    private static async Task<string> SerialAsync(
        ISshClient ssh, Func<string, SshTarget> target, string ip, string certPath, TimeSpan sshTimeout, CancellationToken ct)
    {
        var r = await ssh.ExecuteAsync(target(ip),
            $"sudo openssl x509 -in {certPath} -noout -serial 2>/dev/null | sed 's/serial=//'",
            sshTimeout, ct).ConfigureAwait(false);
        return r.IsOk && r.Value!.Stdout.Trim().Length > 0 ? r.Value.Stdout.Trim() : "(unknown)";
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
