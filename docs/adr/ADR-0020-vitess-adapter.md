# ADR-0020 — VitessAdapter (Phase 0.O Vitess-sharded MySQL; nexus-cli v0.7.2)

- **Status:** Accepted
- **Date:** 2026-06-17
- **Phase:** 0.O (Vitess-sharded MySQL/Percona) — second adapter on the 0.7.x sharded line, after v0.7.1 MongoSharded
- **Supersedes / relates to:** ADR-0011 (the Vault-KV operator-credential model) · ADR-0015 (StarRocks — the MySQL-wire + Vault-PKI cert-rotate C# shape this reuses). Cross-tier: `nexus-platform-plan` ADR-0041 (the Vitess topology + full-mTLS-now decision; durability `none`).

## Context

The CLI must manage **everything**. Phase 0.O built a 12-VM Vitess-sharded MySQL cluster (ClusterId `vitess`, vms.yaml; ADR-0041), tier 07-vitess: 3 etcd topo (`nexus-etcd`, global+local cell `nexus`), 1 control (`nexus-vtctld` + `nexus-vtorc`), 2 vtgate routers (`nexus-vtgate`, MySQL `:15306`), and 2 shards × 3 tablets (keyspace `commerce` split `-80` / `80-`; each tablet = `nexus-vttablet` + a local Percona Server 8.4 under `nexus-mysqlctld`). Each shard = 1 PRIMARY + 2 REPLICA; a row's shard is chosen by a **hash vindex on `customer_id`**. VTOrc auto-reparents a shard when its PRIMARY dies. Full Vault-PKI mTLS on every gRPC channel + the mysqld wire + the vtgate listener.

The live contract was **probed before building** (diagnose-before-rewriting, 2026-06-17): vtctldclient v24.0.1, Percona 8.4.8-8, durability `none`. Three findings shaped the design: (1) primaries had drifted off the lowest tablet uid (so role MUST be read from the topo, never assumed); (2) **no Vitess BackupStorage backend is configured** in 0.O (`GetBackups` → "no registered implementation of BackupStorage"; no xtrabackup); (3) **`CREATE USER` via vtgate fails** ("syntax error near 'USER'") — vtgate does not proxy user DDL.

## Decision

Ship **`VitessAdapter`** (ClusterId `vitess`) over the `IClusterAdapter` SPI, SSH-shell-out to the on-node `vtctldclient` (mTLS gRPC control plane) + the `mysql`/`mysqldump` clients (no managed MySql / gRPC driver; NetArchTest-enforced), reusing the StarRocks MySQL-wire + Vault-PKI idioms and the MongoSharded sharded structure.

### Hybrid operator identity (mTLS control plane + Vault-KV SQL plane)

- **gRPC control plane** (status / topology / failover / scale-out / backup-orchestration) — the mTLS-preloaded wrapper `sudo /usr/local/sbin/nexus-vtctldclient` on the control node (dials vtctld `:15999` with the node's per-host PKI leaf; **no password**). Tablets register in the topo by their **VMnet10 backplane** IP, mapped back to a node via vms.yaml's vmnet10.
- **SQL plane** (health write-probe + the sharding proof) — the vtgate MySQL listener `:15306` over **mTLS** as the static-auth user `nexus`; the listener requires a *client* cert (the node's own leaf doubles as it). Run from a tablet node (which has the `mysql` client + the TLS leaf), connecting to a vtgate's vmnet11. Password = the app password held ONLY in Vault KV (`nexus/vitess/mysql-app-password`, field `content`); `mysqldump` uses `vt_dba` (`nexus/vitess/mysql-allprivs-password`). Both fetched via `INexusVaultClient` (ADR-0011 model).

### Node classification

Deterministic from the node-name (unit-tested): `vitess-etcd-*` → etcd; `vitess-control-*` → control; `vitess-vtgate-*` → vtgate; `vitess-shard<K>-tablet-*` → tablet, shard index K (mapped to the K-th keyspace shard, sorted Ordinal: `-80` < `80-`).

### Verb surface

- **status / health / topology** — roll up all 12 nodes. `topology` **populates the Shards array** (one `TopologyShard` per keyspace shard, with the hash-vindex key range as the slot range) — the sharded showcase. `health` proves layers: etcd quorum, vtctld active, VTOrc healthy/no-problems, both vtgate listeners, per-shard 1-PRIMARY-+-2-REPLICA, the operator mTLS round-trip via vtgate, and the **sharding proof** (both shards non-empty — 54 / 47 rows split by the vindex).
- **failover** — a graceful **PlannedReparentShard** to a healthy replica of the targeted shard (`--target` selects a shard or a tablet node; default the first shard), measured to the shard-record primary confirmation (live RTO ≈ 0.17 s). The old PRIMARY is demoted to REPLICA in place. VTOrc auto-reparent-on-kill is the `chaos` path.
- **scale-out add / remove** — tablet membership: `remove` stops `nexus-vttablet`+`nexus-mysqlctld` and `DeleteTablets` from the topo (PRIMARY-guarded + a ≥2-survivor floor); `add --shard <range>` restarts a previously-removed tablet → it re-registers in the topo as a REPLICA (apply-on-demand for genuine growth, ADR-0010).
- **backup take / restore** — **logical `mysqldump` per shard** from each shard PRIMARY's mysqld socket (as `vt_dba`, of `vt_commerce.customer` — the keyspace maps to the `vt_`-prefixed mysqld database) → `/var/backups/nexus-vitess/<id>/<shard>.sql.gz`; restore reloads each shard's dump into a throwaway `commerce_restore_verify` DB (`sql_log_bin=0` so it never replicates), counts rows (101 round-tripped), and drops it. Engine-native `vtctldclient Backup` (builtin/xtrabackup engine on a NFS file repo) is the **0.O.1 infra enhancement** — surfaced and noted, not silently skipped.
- **cert-rotate** — per-node Vault PKI: re-issue via the node's own Agent token (`/run/nexus-vault-agent/token`) → `pki_int/issue/vitess-server` → assemble `bundle.pem` → the infra's `nexus-vitess-tls-split.sh` → restart the serving unit. Order etcd → tablet-replicas → tablet-primaries → vtgate → control. **Non-disruptive choice:** on tablets restart `nexus-vttablet` only (gRPC + db-client certs reload; mysqld stays up so the PRIMARY is never demoted — the mysqld-wire cert reload is deferred to a mysqld restart window).
- **acl** — the **vtgate static-auth file** `/etc/nexus-vitess/vtgate_creds.json` (the real MySQL credentials at the `:15306` front door). `list` parses it; `grant`/`revoke` edit it on **both** vtgate nodes + restart `nexus-vtgate` to apply (vtgate does not proxy `CREATE USER` DDL, and its `--mysql-auth-static-reload-interval` is unset). The built-in `nexus` operator user is revoke-protected.
- **chaos** — process-kill (SIGSTOP) a tablet via the embedded `nexus-chaos.sh`: a replica freezes `nexus-vttablet`; a PRIMARY target freezes `nexus-mysqlctld` (mysqld) → **VTOrc auto-reparents** the shard to a replica → lift + recover to green (proven live: froze the `80-` primary, VTOrc promoted shard2-tablet-2, the old primary rejoined as replica).

## Live-caught bugs (the lessons — 3, the usual 1–4 cadence)

1. **etcd-quorum health probe always red.** The `nexus-etcdctl` wrapper carries all 3 endpoints, so one `endpoint health` reports the whole cluster (returns 3 "healthy" lines), not 1 per node as assumed — the per-node `StartsWith('1')` count failed. Fixed: run once, count `"is healthy"` lines (NOT bare `healthy`, which also matches `unhealthy`).
2. **`mysqldump commerce` → "Unknown database 'commerce'".** Vitess names the underlying mysqld database `vt_commerce` (the keyspace prefixed with `vt_`); vtgate translates the keyspace name but DIRECT mysqld access must use `vt_commerce`. Fixed the dump source db.
3. **chaos on a PRIMARY rejected by systemd.** `nexus-chaos.sh process-kill` SIGSTOPs a *single* unit; the primary path passed two space-separated units (`"nexus-vttablet nexus-mysqlctld"`) → "Unit nexus-vttablet\x20nexus-mysqlctld.service not loaded". Fixed to freeze a single unit — `nexus-mysqlctld` (mysqld) on a primary (its freeze is exactly what triggers VTOrc auto-reparent), `nexus-vttablet` on a replica.

## Consequences

- The CLI now manages the Vitess-sharded MySQL store with the full verb surface; 11/13 adapter families live (`vitess` registered alongside the rest).
- AOT footprint +0.22 MB (26.30 → **26.52 MB / 30**); **114/114 tests** (+17 parser cases: Classify / ParseTabletsJson / ParseShardPrimaryUid / ParseVtgateCreds / MutateVtgateCreds round-trip / ExtractJson). No managed driver linked.
- Backup is logical (mysqldump round-trip) until 0.O.1 configures a Vitess BackupStorage backend; `cert-rotate`'s mysqld-wire reload is deferred to keep rotation non-disruptive. Both are documented limitations, not silent gaps.
