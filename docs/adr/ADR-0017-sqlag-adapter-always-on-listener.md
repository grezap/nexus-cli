# ADR-0017 — SqlAgAdapter: SQL Server Always On AG + Listener over Windows-SSH

- **Status:** Accepted
- **Date:** 2026-06-12
- **Phase:** 0.G.7 / nexus-cli `v0.6.6`
- **Extends:** [ADR-0016](ADR-0016-sqlfci-adapter-wsfc-shared-storage.md) (the shared Windows-SSH control plane + auth model + `SqlServerControl`/`SqlServerCert` — reused verbatim)
- **Sibling:** [ADR-0016](ADR-0016-sqlfci-adapter-wsfc-shared-storage.md) (the WSFC + FCI plane, ClusterId `sqlserver`)
- **Cross-tier:** nexus-platform-plan ADR-0025 (the AG Listener IS the LB-tier HA primitive), ADR-0027 (cert-auth AG endpoints, TCP 5022)

## Context

`SqlAgAdapter` (ClusterId **`sqlserver-ag`**) owns the **Always On Availability Group** plane of the
single vms.yaml cluster `sqlserver`. AG `nexus-ag`: the FCI virtual server `sqlfci` is the **PRIMARY**
(SYNCHRONOUS_COMMIT); the two standalone replicas `sql-ag-rep-1`/`-2` (@ .13/.14) are
ASYNCHRONOUS_COMMIT secondaries holding async copies of `nexus_demo`. The AG **Listener**
`sql-ag-listener` @ .17:1433 is the client front door — WSFC migrates its IP atomically across AG
failover, and the unified cert's `.17` IP-SAN makes `Encrypt=True;TrustServerCertificate=False` validate
across the move. All of §1's access model (Windows-SSH EncodedCommand, the `nexus-cluster-admin`
Vault-KV SQL login for the FCI/Listener path) is ADR-0016.

The one auth nuance: the **standalone replicas are Windows-auth-only** (no mixed-mode `nexus-cluster-admin`
login), so the few direct-replica ops — the AG `FAILOVER` issued *on* a target secondary, and the
manual-seed `RESTORE`/`SET HADR` — use **Windows-auth `-E`** (local `nexusadmin` IS sysadmin on the
standalone replicas, unlike on the FCI).

## Decision

### Verb → mechanism map (AG plane)

| Verb | Mechanism |
|---|---|
| `status` | `sys.availability_replicas` ⋈ `sys.dm_hadr_availability_replica_states` ⋈ `sys.dm_hadr_database_replica_states` (role/mode/conn/sync per replica) — read from the FCI primary |
| `health` | exactly-1 primary · each replica CONNECTED + HEALTHY · **the Listener answers under strict TLS** (`-S sql-ag-listener.nexus.lab -N`, Encrypt + chain-validate) returning `PRIMARY=SQLFCI` |
| `topology` | 3 replicas + the Listener as the single "shard" (`.17:1433`, primary → secondaries) |
| `failover` | promote the target secondary to SYNCHRONOUS_COMMIT → wait SYNCHRONIZED → `ALTER AVAILABILITY GROUP FAILOVER` **on the target** (`-E`) → measure RTO → fail back to the FCI + revert async |
| `scale-out remove` | `ALTER AVAILABILITY GROUP REMOVE REPLICA` (the primary + the other secondary keep serving) |
| `scale-out add` | re-add a removed replica via **MANUAL seeding** (see §below — the live-caught bug) |
| `backup` | `BACKUP DATABASE … WITH COPY_ONLY` via the AG primary + a `RESTORE … WITH MOVE` round-trip verify (honors backup-preference=secondary intent) |
| `cert-rotate` | **per-node** rotate of the 2 standalone replicas (each owns its own `SuperSocketNetLib\Certificate`; restart MSSQLSERVER). The FCI's shared cert is rotated by `cert-rotate sqlserver` (ADR-0016) |
| `acl` | same `sys.server_principals` surface via the FCI primary (the AG shares the instance's logins) |
| `chaos` | `Stop-Process sqlservr` on a secondary → its AG replica disconnects → SCM auto-restart → reconnect/resync → poll to green |

### scale-out add — MANUAL seeding (the live-caught bug, 2026-06-12)

A prior build of `ScaleOutAddAsync` used `SEEDING_MODE = AUTOMATIC`. **That cannot work in this hybrid
FCI+AG topology** and was caught live: the FCI primary's `nexus_demo` data files live on the shared
iSCSI `S:\`, and automatic seeding *preserves the primary's file paths* — it tries to create
`S:\SQLData\nexus_demo.mdf` on a standalone replica that has only local `C:\`. There is no `S:\` there,
so seeding ends in `failure_state = Seeding`, leaving the replica joined but its database
**NOT_HEALTHY / NOT SYNCHRONIZING** (exactly the drift found on `sql-ag-rep-2` at session start). The
fix mirrors `role-overlay-ag-bootstrap.tf §6` — **manual seeding is path-agnostic**:

1. `ADD REPLICA … SEEDING_MODE = MANUAL` (on the primary) → `JOIN` (on the replica, `-E`).
2. `BACKUP DATABASE` + `BACKUP LOG` on the FCI primary to the active node's **local** `C:\Windows\Temp`
   (NOT `S:\` — must be SFTP-ferryable).
3. **Ferry** the `.bak`/`.trn` from the active FCI node → build host → the candidate replica via SFTP
   (the only viable path: `S:\` has no peer path, and a plain-SSH session has no network identity to
   reach a peer admin share). `icacls … /grant *S-1-1-0:(R)` (Everyone-read, single wildcard arg) so the
   replica's `NT AUTHORITY\NETWORK SERVICE` SQL service can read them during RESTORE.
4. On the replica (`-E`): `RESTORE DATABASE … WITH MOVE … NORECOVERY` (to the replica's own default
   data/log dir) → `RESTORE LOG … NORECOVERY` → `ALTER DATABASE … SET HADR AVAILABILITY GROUP`.

This required a new **`ISshClient.DownloadBytesAsync`** (SFTP download), alongside the existing
`UploadBytesAsync`. `scale-out add` is now also the **named operator recovery command** for a
failed/drifted secondary (zero-touch, idempotent — it cleans stale local AG state first).

## Live evidence (2026-06-12, against the running `sqlserver-ag` AG)

| Verb | Result |
|---|---|
| `status sqlserver-ag` | ✅ sqlfci primary + 2 secondaries syncing; leader sqlfci |
| `health sqlserver-ag` | ✅ 1 primary · 3 replicas CONNECTED+HEALTHY · Listener strict-TLS `PRIMARY=SQLFCI` → green |
| `topology sqlserver-ag` | ✅ 3 replicas + `sql-ag-listener` (.17:1433) shard |
| `failover-test cluster sqlserver-ag` | ✅ ALTER FAILOVER sqlfci → sql-ag-rep-1, **RTO ≈ 8.2 s**, failed back + reverted async, recovered |
| `scale-out remove sqlserver-ag sql-ag-rep-2` | ✅ REMOVE REPLICA (1.4 s) |
| `scale-out add sqlserver-ag --role replica` | ✅ **MANUAL seeding** re-add of sql-ag-rep-2 → CONNECTED + SYNCHRONIZING (19.4 s) — **repaired the drifted replica** |
| `backup take/restore sqlserver-ag` | ✅ COPY_ONLY via primary → restore round-trip **8225 rows** (2.0 s) |
| `cert-rotate sqlserver-ag` | ✅ per-node replica certs (distinct serials `20:e5:a8…` / `01:9f:5e…`), MSSQLSERVER restart, 31.2 s, 0 errors |
| `acl sqlserver-ag list/grant` | ✅ grant `demo_ag` + dbcreator (cleaned up) |
| `chaos sqlserver-ag process-kill` | ✅ killed sqlservr on a secondary → SCM restart → resync → recovered green |

## Consequences

- The AG plane (replica state, Listener strict-TLS, planned failover, replica add/remove) is fully
  managed; the manual-seed `scale-out add` doubles as the recovery command for a NOT_HEALTHY secondary.
- The Listener strict-TLS health probe (`-N`) is the live proof that the unified cert's `.17` IP-SAN +
  full chain validate — the HA-promise-covers-the-LB-tier check (ADR-0025).
