# ADR-0012 — PerconaAdapter: Galera multi-primary + ProxySQL-mediated failover

- **Status:** Accepted
- **Date:** 2026-06-05
- **Phase:** 0.G.3 / nexus-cli `v0.6.2`
- **Extends:** [ADR-0009](ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) (SPI), [ADR-0010](ADR-0010-cluster-adapter-patterns-and-redis-adapter.md) (patterns), [ADR-0011](ADR-0011-mongo-adapter-and-operator-credential-model.md) (the Vault-KV operator-credential model — reused verbatim)

## Context

The `percona` cluster is the second password-auth adapter and the first whose engine is
**synchronous multi-primary** (Galera) fronted by an **external router** (ProxySQL) rather than a
single elected leader. Two engine traits drive the design:

1. **No "leader" to fail over.** All 3 PXC nodes are writable Galera members; ProxySQL's
   `mysql_galera_hostgroups` designates exactly ONE *writer* (hostgroup 10), the rest as
   *backup_writer* (20) / *reader* (30), demoting non-Synced nodes to *offline* (40). So "the
   primary" is a ProxySQL routing decision, not a database role.
2. **Two control planes.** Cluster state lives on the PXC nodes (`SHOW STATUS LIKE 'wsrep_%'`);
   routing state lives in ProxySQL's admin interface (`:6032` → `runtime_mysql_servers`).

## Decision

### 1. Credential model — reuse ADR-0011 verbatim

Authenticate as the dedicated **`nexus-cluster-admin`** MySQL user (ALL PRIVILEGES WITH GRANT
OPTION — the operator-admin identity; root@localhost is socket-only/in-band, the service accounts
`wsrep_sst`/`clustercheck`/`smoke-rw` are too narrow). Its password + the **ProxySQL admin
password** live ONLY in Vault KV (`nexus/oltp/percona/operator-password`,
`.../proxysql-admin-password`), fetched at runtime via the optional `INexusVaultClient`. The
one-time createUser bootstrap reads operator-password on-node via the node's own Vault Agent token
(percona PXC agent-policy v2 grant) and creates the user via the root-socket `nexus-pxc-mysql`
wrapper; Galera replicates it to all 3 PXC nodes. Infra: seed (creds-seed v2) + policy v2 +
`role-overlay-percona-operator-user.tf`.

### 2. Two connection paths

- **PXC (operator):** `sudo mysql -h 127.0.0.1 -u nexus-cluster-admin -p<kv> --ssl-ca=ca.pem
  --ssl-mode=VERIFY_CA` on a PXC node (sudo for the 0750 root:mysql cert dir). Used by
  status/health/backup/acl/scale-out.
- **ProxySQL admin:** `mysql -h 127.0.0.1 -P6032 -u admin -p<kv>` on a proxysql node. Used to read
  the writer/reader/offline hostgroup assignment (status/topology) and to observe writer failover.

### 3. Verb semantics that differ from a leader-elected engine

- **`status`/`topology`:** member role = ProxySQL hostgroup (10→primary/writer, 20/30→replica,
  40→offline); the 2 ProxySQL nodes are `router`. Leader = the writer (hostgroup 10) node.
  `Shards = null` (Galera is replication, not sharding).
- **`failover-test`:** the genuine HA primitive (ADR-0025) is ProxySQL writer failover — stop
  `nexus-percona` on the current writer, poll ProxySQL until a *different* node holds hostgroup 10
  (a backup_writer promoted), measure RTO, then restart the stopped node so it rejoins Galera.
- **`scale-out add/remove`:** Galera join (start `nexus-percona` → SST/IST, wait `Synced`) /
  graceful leave (stop the service; ProxySQL marks it offline). Apply-on-demand per ADR-0010;
  remove refuses the current writer.
- **`backup`:** `mysqldump --single-transaction --databases nexus_smoke` → node-local gzip on a
  non-writer; restore round-trips into a `nexus_restore_verify` schema (rewrite the dump's db
  references + strip `CREATE DATABASE`/`USE`) and counts rows.
- **`cert-rotate`:** genuine re-issue via the node's own Vault token (`pki_int/issue/percona-server`)
  → write `server-cert.pem`/`server-key.pem`/`ca.pem`, rolling `restart nexus-percona`
  (one PXC member at a time; ProxySQL nodes restart `nexus-proxysql`).
- **`acl`:** `SELECT user,host FROM mysql.user` (list) / `CREATE USER`+`GRANT` (grant).
- **`chaos`:** process-kill `nexus-percona` on a non-writer + observe + restart-to-rejoin.

### 4. Engine quoting + parsing (mirrors the mongo live-verify lessons)

- The mysql client emits a *"Using a password on the command line … insecure"* warning to stderr;
  with `2>&1` it merges into output — filtered out before parsing.
- `mysql -BNe` (batch, no column names) gives tab-separated rows, parsed directly.

## Consequences

- **Positive:** the Vault-KV credential model + `INexusVaultClient` carry straight over from mongo
  (no framework change); ProxySQL-aware failover demonstrates the real HA primitive; AOT stays flat
  (no managed MySQL driver — SSH-shell-out only).
- **Trade-offs:** the operator user is a powerful account (ALL PRIVILEGES + GRANT OPTION) — the
  MySQL analogue of mongo's broad operator; acceptable + documented for an operator CLI. Failover
  + scale-out depend on ProxySQL's Galera monitor (the `clustercheck` user) being healthy.
- **Latent bug fixed in passing:** the galera-bootstrap overlay's `sed -e '$a\'` newline-ensure had
  its `$a` eaten by PowerShell `@"..."@` interpolation (rendered a malformed sed → "missing
  command"); replaced with a `printf '\n…\n'` append (no `$`).
- **Follow-ups:** xtrabackup (vs mysqldump) for physical backups; a `pxc_extra_count` IaC growth var
  for minting a new Galera member on demand (the join logic is done + verified).
