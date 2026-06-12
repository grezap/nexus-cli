# ADR-0016 — SqlFciAdapter: SQL Server FCI (WSFC + shared iSCSI storage) over Windows-SSH

- **Status:** Accepted
- **Date:** 2026-06-12
- **Phase:** 0.G.7 / nexus-cli `v0.6.6`
- **Extends:** [ADR-0009](ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) (SPI), [ADR-0010](ADR-0010-cluster-adapter-patterns-and-redis-adapter.md) (patterns), [ADR-0011](ADR-0011-mongo-adapter-and-operator-credential-model.md) (the Vault-KV operator-credential model — reused), [ADR-0024](../../../nexus-platform-plan/docs/adr) (two adapters per the FCI+AG hybrid)
- **Sibling:** [ADR-0017](ADR-0017-sqlag-adapter-always-on-listener.md) (the Always On AG plane, ClusterId `sqlserver-ag`)
- **Cross-tier:** nexus-platform-plan ADR-0025 (AG Listener = LB-tier HA primitive), ADR-0026 (iSCSI shared LUN from nexus-gateway), ADR-0027 (cert-auth AG endpoints), ADR-0144 (SQL Server 2025 Developer Edition)

## Context

The `sqlserver` cluster (Phase 0.G.7, repo nexus-infra-oltp) is the **first Windows cluster** behind the
`IClusterAdapter` SPI — the access pattern differs fundamentally from the six Linux adapters. Topology
(vms.yaml `sqlserver`, 4× `ws2025-desktop`): `sql-fci-1`/`sql-fci-2` (@ .11/.12) form a 2-node **WSFC
Failover Cluster Instance** sharing one iSCSI LUN (clustered Physical Disk at `S:\`) exported by `tgt` on
nexus-gateway; the FCI virtual server `sqlfci` @ .16 is the SQL endpoint; the WSFC CNO is
`sql-fci-cluster` @ .15; quorum (NodeMajority) spans all **4** nodes (the 2 FCI + the 2 AG replicas).
SQL service identity on the FCI = `nexus.lab\gmsa-sql-engine$`. SQL Server 2025 (MSSQL17), Developer
Edition. The companion AG plane (`sql-ag-rep-1/2` + Listener) is ADR-0017.

`SqlFciAdapter` (ClusterId `sqlserver`) owns the **WSFC + shared-storage** plane.

## Decision

### 1. Two access planes (decided from the live probe 2026-06-12)

There is **no managed `Microsoft.Data.SqlClient` driver** (NetArchTest-enforced, like every adapter). All
remote work goes over Windows-SSH (Win32-OpenSSH on ws2025), and every command is wrapped in
`powershell -NoProfile -EncodedCommand <base64-UTF16>` (the smoke gate's `Invoke-RemoteWin` shape —
plain multi-token commands get mangled by cmd.exe between sshd and the shell; memory:
windows-automation-over-ssh rule #2). Two planes:

- **WSFC / cluster-resource cmdlets** (`Get-Cluster*`, `Get-ClusterGroup`, `Move-ClusterGroup`,
  `Get-IscsiSession`, `Stop/Start-ClusterResource`) run over **plain SSH as the local `nexusadmin`** —
  it carries cluster-admin rights on the *local* node (the cluster service runs as SYSTEM; reads + the
  group-move all succeed). This retired the memory's worry that "cluster RESOURCE cmdlets HANG" — that
  applies to the *schtasks domain-task* context, **not** plain SSH. Cross-machine cmdlets that open a
  remote SCM (`Start-ClusterNode <other>`) do fail (no network identity) — restart `ClusSvc` *locally*
  on the target instead.
- **T-SQL against the FCI** runs as the dedicated **`nexus-cluster-admin`** SQL login — the LOCKED
  Vault-KV operator-credential model (ADR-0011 family). This is what makes the schtasks domain-task
  dance unnecessary for SQL: a SQL login authenticates on the wire regardless of the OS session identity
  (the FCI is mixed-mode — Section-4 of the smoke renders an `sa` password, confirming it). The password
  lives **ONLY in Vault KV** (`nexus/oltp/sqlserver/operator-password`, field `password`), fetched at
  runtime via the optional `INexusVaultClient`. `$env:SQLCMDPASSWORD` carries it (no `-P` argv exposure,
  mirroring `MYSQL_PWD`). `sqlcmd -C` trusts the server cert for ops (strict `-N` is exercised by the AG
  Listener probe in ADR-0017).

> **Why a SQL login, not pure Windows-auth via the schtasks task?** Consistency (all six prior
> password-auth adapters use ADR-0011), reliability (the schtasks create/run/poll/delete cycle is slow +
> fragile over 13 verbs × many queries), and least-surprise (one operator identity, audit-distinct from
> `sa`/`NEXUS\nexusadmin`). The operator login is granted **sysadmin** — the AG/cluster DDL
> (`ALTER AVAILABILITY GROUP`, `BACKUP`/`RESTORE`, `CREATE LOGIN`) realistically needs it, and a single
> sysadmin operator matches the lab's blast radius.

### 2. ClusterId scheme — two adapters, one vms.yaml cluster

The single vms.yaml cluster `sqlserver` is split across **two** `IClusterAdapter` registrations (per
ADR-0024): `SqlFciAdapter` (ClusterId **`sqlserver`** — WSFC + FCI) + `SqlAgAdapter` (ClusterId
**`sqlserver-ag`** — Always On AG + Listener). This mirrors the Kafka per-cluster scheme
(`kafka-east`/`kafka-west` over one tier) and keeps each adapter's verb surface coherent
(`nexus failover-test cluster sqlserver` = FCI move; `… sqlserver-ag` = AG failover). `SqlServerControl`
(shared) + `SqlServerCert` (shared) carry the Windows-SSH + cert primitives both consume.

### 3. Verb → mechanism map (FCI plane)

| Verb | Mechanism |
|---|---|
| `status` | `Get-ClusterGroup 'SQL Server (MSSQLSERVER)'` (owner/state) + `Get-ClusterNode` + an FCI `@@SERVERNAME` operator round-trip → active/passive/online |
| `health` | WSFC quorum (≥ NodeMajority) · SQL-role Online on an FCI node · clustered Physical Disk Online · iSCSI session on both FCI nodes · FCI virtual server answers as `nexus-cluster-admin` |
| `topology` | the 4-node WSFC (2 FCI + 2 AG-as-`wsfc-member`) + the FCI shared disk as the single "shard" (`S:\` iSCSI LUN, owned by the active node) |
| `failover` | `Move-ClusterGroup` to the passive node, poll until the FCI virtual server answers, measure RTO, move back (`--no-recover` to stay) |
| `scale-out` | **skip-with-explanation** — an FCI is a fixed 2-node shared-storage instance (adding a node = `setup.exe /ACTION=AddNode`, not a runtime op); grow read capacity via `sqlserver-ag`, resources via `scale-up` |
| `backup` | `BACKUP DATABASE nexus_demo … WITH COPY_ONLY` to `S:\Backups` (doesn't disturb the AG log chain); restore = `RESTORE FILELISTONLY` → `RESTORE … WITH MOVE` to a throwaway verify DB → row-count → drop (genuine round-trip) |
| `cert-rotate` | **one shared cert on BOTH nodes + a single cluster checkpoint** (see §4) |
| `acl` | `sys.server_principals` + fixed-server-role memberships; grant = `CREATE LOGIN` + `ALTER SERVER ROLE ADD MEMBER` |
| `chaos` | `Stop-Process sqlservr` on the active node → WSFC restarts/fails the SQL resource over → poll to green |

### 4. cert-rotate — one shared cert, one cluster checkpoint (live-caught)

ws2025 has **no openssl** and on-node .NET Framework can't import a PKCS#1 PEM key, so the cert is
issued from Vault PKI (`pki_int/issue/sqlserver-server`) and turned into a **PFX on the build host**
(`X509Certificate2.CreateFromPem` + `Export`), shipped over **SFTP** (an inline base64 EncodedCommand
blows past the Windows command-line limit — live-caught), and imported with `Import-PfxCertificate` +
chain-to-CA/Root + an `icacls` key-grant to the SQL service account — mirroring
`role-overlay-sqlserver-tls.tf`. **The FCI subtlety:** an FCI checkpoints a *single*
`SuperSocketNetLib\Certificate` thumbprint that the cluster replicates to whichever node hosts it — so
both FCI nodes must carry the **same** cert imported under one checkpoint. A naive per-node rotate
(different thumbprints) would break failover. `RotateCertAsync` issues ONE cert (CN `sqlfci.nexus.lab` +
both node names + the cluster CNO + the AG listener SAN + the .16/.17 IP-SANs), imports it on both nodes
with `setCheckpoint:false`, writes the single checkpoint on the active node, then cycles the SQL
resource. (The standalone AG replicas rotate per-node — ADR-0017.)

## Live evidence (2026-06-12, against the running `sqlserver` cluster)

| Verb | Result |
|---|---|
| `status sqlserver` | ✅ sql-fci-1 active/online, sql-fci-2 passive/standby; leader sql-fci-1 |
| `health sqlserver` | ✅ quorum 4/4 NodeMajority · SQL-role Online · 1 Physical Disk · iSCSI ×2 · `SQLFCI|nexus-cluster-admin` → green |
| `topology sqlserver` | ✅ 4 WSFC nodes (2 FCI + 2 wsfc-member) + `fci-shared-disk` (S:\) |
| `failover-test cluster sqlserver` | ✅ Move-ClusterGroup sql-fci-1 → sql-fci-2, **RTO ≈ 4.5 s**, recovered to sql-fci-1 |
| `backup take/restore sqlserver` | ✅ COPY_ONLY 4.74 MB (1.5 s) → restore round-trip **8225 rows** (2.0 s) |
| `cert-rotate sqlserver` | ✅ both nodes → ONE shared serial `1a:6e:db:95…`, single checkpoint, SQL resource cycled, 22.4 s, 0 errors |
| `acl sqlserver list/grant` | ✅ 11 principals (`nexus-cluster-admin` sysadmin); grant `demo_fci` + dbcreator (cleaned up) |
| `chaos sqlserver process-kill` | ✅ killed sqlservr on the active node → WSFC recovered → green |

AOT win-x64 **25.95 MB / 30 MB**; **71/71** tests; NetArchTest no-managed-driver green; read verbs
re-confirmed against the native `nexus.exe`.

## Infra (mirrors the clickhouse/starrocks/patroni operator-user model)

- **nexus-infra-vmware/envs/security** — `role-overlay-vault-sqlserver-cluster-creds-seed.tf` **v2**
  (+`operator-password`, sticky-seeded; the existing `nexus-agent-sqlserver-*` policy already
  wildcard-reads `nexus/data/oltp/sqlserver/*`, so no policy change). Applied live (targeted apply; the
  security env has no VMs — safe).
- **nexus-infra-oltp/envs/oltp-sqlserver** — `role-overlay-sqlserver-operator-login.tf` (read the
  operator password on the FCI active node via the agent token → idempotent `CREATE LOGIN
  nexus-cluster-admin` + `ALTER SERVER ROLE sysadmin ADD MEMBER` → verify auth) + var
  `enable_sqlserver_operator_login`.

## Consequences

- The FCI plane is fully managed without any managed SQL driver, openssl, or schtasks dance.
- cert-rotate is failover-safe (one shared cert/checkpoint) — verified by rotating then leaving the
  cluster green.
- A new `ISshClient.DownloadBytesAsync` was added for the AG manual-seed ferry (ADR-0017) — used by the
  AG adapter, not the FCI one.
