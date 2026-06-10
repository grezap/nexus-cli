# ADR-0013 — PatroniAdapter: PostgreSQL Patroni HA + etcd DCS + HAProxy VIP

- **Status:** Accepted
- **Date:** 2026-06-11
- **Phase:** 0.G.4 / nexus-cli `v0.6.3`
- **Extends:** [ADR-0009](ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) (SPI), [ADR-0010](ADR-0010-cluster-adapter-patterns-and-redis-adapter.md) (patterns), [ADR-0011](ADR-0011-mongo-adapter-and-operator-credential-model.md) (the Vault-KV operator-credential model — reused verbatim), [ADR-0012](ADR-0012-percona-adapter-galera-proxysql.md) (the prior password-auth adapter)

## Context

The `postgres` cluster (cluster scope `nexus-pg`) is the third password-auth adapter and the
first whose engine is a **single-leader streaming-replication** topology coordinated by an
**external consensus store** and fronted by a **leader-routing load balancer**. Three control
planes:

1. **Patroni** (the agent on each PG node) owns leader election + PG lifecycle. The operator plane
   is `patronictl` (talks to the DCS + the Patroni REST API on `:8008`).
2. **etcd** (3 nodes, RBAC-enabled) is the distributed configuration store holding the leader lease.
3. **HAProxy** (2 nodes, keepalived VRRP VIP `.60`) routes `:5432` to whichever node is the current
   leader, via `option httpchk GET /leader` against each node's Patroni REST (`/leader` returns 200
   only on the leader).

Topology (vms.yaml `postgres`): 3 PG nodes (`pg-primary` + `pg-replica-1/2` @ .61/.62/.63, PG 17
streaming replication on TLS :5432) + 3 etcd (`etcd-1/2/3` @ .64/.65/.66) + 2 HAProxy
(`haproxy-pg-1/2` @ .67/.68), VIP `.60`.

## Decision

### 1. Credential model — reuse ADR-0011 verbatim

Authenticate as the dedicated **`nexus-cluster-admin`** role: `LOGIN CREATEROLE CREATEDB
REPLICATION` + member of `pg_monitor` / `pg_read_all_data` / `pg_write_all_data` — **explicitly NOT
a PostgreSQL superuser** (the verb set doesn't need it, and superuser is more dangerous than the
percona `ALL PRIVILEGES` analogue). The bootstrapped superusers `postgres`/`nexusops` are off-limits
as an operator identity (their password is the shared `postgres-superuser` KV secret Patroni uses
internally); `replicator`/`rewind` are narrow streaming-replication accounts. The operator
password lives ONLY in Vault KV (`nexus/oltp/patroni/operator-password`), fetched at runtime via the
optional `INexusVaultClient`. The one-time `CREATE ROLE` bootstrap discovers the current leader (via
`nexus-patronictl`), reads operator-password on that node via the node's own Vault Agent token
(patroni agent-policy v3 grant), and creates the role via the leader's local postgres unix socket
(peer auth); it replicates to the 2 streaming replicas via WAL. Infra: creds-seed v2 +
agent-policies v3 + `role-overlay-patroni-operator-user.tf`.

### 2. Connection paths

- **PG (operator):** `sudo env PGPASSWORD=<kv> psql "host=<ip> port=5432 sslmode=verify-ca
  sslrootcert=/etc/nexus-patroni/tls/ca.pem user=nexus-cluster-admin"` — connect to a node's
  **VMnet11 IP** (not 127.0.0.1) so pg_hba's `hostssl all all 192.168.0.0/16 scram-sha-256` rule
  applies (a genuine scram test; 127.0.0.1 is `trust`). Writes target the **VIP `.60`** (always the
  leader). sudo so root can read the 0640 root:postgres `ca.pem`.
- **Patroni plane:** `sudo /usr/local/sbin/nexus-patronictl <verb>` (= `patronictl -c
  /etc/nexus-patroni/patroni.yml`). Used for `list` (topology) + `switchover` (failover).
- **etcd plane:** `sudo /usr/local/sbin/nexus-etcdctl --user root:<pw> endpoint health --cluster`
  (etcd RBAC requires `root`; the password is read on-node via the etcd node's own Vault Agent
  token — the operator Vault token has no grant on etcd-root-password, by least-privilege design).

### 3. Verb semantics

- **`status`/`topology`:** parse `patronictl list --format json` (Member/Host/Role/State/Lag in MB)
  — PG members are primary (Role Leader) / replica (Replica / Sync Standby). etcd nodes render as
  `dcs`, HAProxy as `router` (the VIP holder as `router*`). Leader = the Patroni Leader member.
  `Shards = null` (streaming replication, not sharding). Replication lag comes straight from
  patronictl's "Lag in MB".
- **`health`:** single-leader (exactly 1) + per-node patroni-state + replication-lag (≤10 MB) + a
  **vip-writable** probe (a real TLS+scram round-trip via the VIP proving operator creds + leader
  routing end-to-end) + **etcd-quorum** (authed `endpoint health`, ≥ majority) + haproxy active.
- **`failover-test`:** **`patronictl switchover`** (planned, graceful, repeatable) to a streaming
  replica; RTO = time from the switchover until the **VIP** serves a *different, writable* leader
  (poll `inet_server_addr()` + `pg_is_in_recovery()` via `.60`); then switch back (recovery). Live
  RTO ≈ 4.6 s. (Unplanned variant — stop the leader's `nexus-patroni` — is the `chaos` path.)
- **`scale-out add/remove`:** start/stop `nexus-patroni` on a replica (Patroni rejoins via
  pg_rewind/basebackup → streaming / graceful leave). Apply-on-demand per ADR-0010; remove refuses
  the current leader.
- **`backup`:** `pg_dump -t nexus_smoke --no-owner --no-privileges` over TLS+scram → node-local gzip
  on a streaming replica; restore round-trips into a throwaway **database** `nexus_restore_verify`
  the operator OWNS (it has CREATEDB) — *not* a schema-in-postgres (the operator's
  `pg_*_all_data` grants are DATA, not DDL) — and counts rows.
- **`cert-rotate`:** re-issue via the node's own Vault token (`pki_int/issue/patroni-server`, single
  allowed domain `patroni.nexus.lab` for all 8 nodes) → write `server-cert.pem`/`server-key.pem`/
  `ca.pem` per-role TLS dir, rolling: PG reloads on SIGHUP (`systemctl reload nexus-patroni` — PG
  picks up `ssl_cert_file` without a restart), etcd restarts, haproxy reloads. PG **leader rotates
  last**.
- **`acl`:** `\du`-equivalent (`pg_roles` + attribute flags) for list/describe; idempotent
  `CREATE ROLE … LOGIN` + `GRANT … ON DATABASE` for grant.
- **`chaos`:** process-kill `nexus-patroni` on a replica + observe + restart-to-rejoin.

### 4. Engine quoting + parsing (the 0.G.4 live-verify lessons — 4 bugs)

1. **patronictl switchover 403 "client certificate required."** Patroni's REST API runs
   `verify_client: optional`, which **requires** a client cert for state-changing methods
   (POST `/switchover`). patroni.yml had no `ctl:` section, so patronictl presented no client cert
   → 403. Fix: add a `ctl:` block (`cacert`/`certfile`/`keyfile` = the node's own TLS files; the
   CA-signed server cert doubles as a client cert) — baked into `role-overlay-patroni-bootstrap.tf`
   (`patroni_bootstrap_v` → "2"). `ctl:` is client-only, so no service restart is needed.
2. **patronictl exits 0 even when the switchover is REFUSED.** Validate the `"Successfully
   switched over"` banner in stdout, not the exit code.
3. **`backup restore` permission denied / "must be owner of table."** The operator's
   `pg_read/write_all_data` are DATA grants, not DDL — it cannot `CREATE SCHEMA` in db `postgres`.
   Restore into a fresh **database** it owns (CREATEDB), and dump with `--no-owner --no-privileges`
   (else the dump's `ALTER TABLE … OWNER TO nexusops` fails).
4. **`cert-rotate` vault issue 500.** Using domain `etcd.nexus.lab` for etcd nodes — the PKI role
   `patroni-server` only allows `patroni.nexus.lab` (the original etcd/haproxy certs are
   `<node>.patroni.nexus.lab`). All 8 nodes use `patroni.nexus.lab`.

## Consequences

- **Positive:** the Vault-KV credential model + `INexusVaultClient` carry straight over from
  mongo/percona (no framework change). A genuine three-plane HA story (Patroni election + etcd
  quorum + HAProxy leader-routing VIP) is demonstrated by a single adapter. AOT stays flat
  (24.18 MB / 30 MB; +0.15 MB over percona — no managed Npgsql driver, SSH-shell-out + `JsonDocument`
  only). The operator is genuinely least-privilege (no superuser), unlike the percona ALL-PRIVILEGES
  analogue.
- **Trade-offs:** `failover-test` is a *planned* switchover (clean, repeatable) rather than an
  unplanned kill — the unplanned path is `chaos process-kill`. The VIP-poll RTO granularity is
  bounded by the SSH round-trip (~0.5–1 s) plus HAProxy's `httpchk` interval; the measured ≈4.6 s is
  dominated by Patroni's election + HAProxy reprobe, not the harness.
- **Latent infra gap fixed in passing:** the missing `ctl:` block (bug #1) — without it, *no*
  operator could run `patronictl switchover` against this cluster, only direct REST with a client
  cert. The fix lands in the bootstrap overlay so cold rebuilds inherit it.
- **Follow-ups:** physical `pg_basebackup` (vs logical `pg_dump`) for full-cluster backups; a
  `pg_replica_extra_count` IaC growth var for minting a new replica on demand (the join logic is
  done + verified); surfacing `virtual_ips` from the vms.yaml catalog instead of the hard-defaulted
  VIP.
