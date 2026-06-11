# ADR-0014 — ClickHouseAdapter: sharded ClickHouse (3×2) + ClickHouse Keeper RAFT

- **Status:** Accepted
- **Date:** 2026-06-11
- **Phase:** 0.G.5 / nexus-cli `v0.6.4`
- **Extends:** [ADR-0009](ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) (SPI), [ADR-0010](ADR-0010-cluster-adapter-patterns-and-redis-adapter.md) (patterns), [ADR-0011](ADR-0011-mongo-adapter-and-operator-credential-model.md) (the Vault-KV operator-credential model — reused verbatim), [ADR-0013](ADR-0013-patroni-adapter-postgres-ha.md) (the prior password-auth adapter)
- **Cross-tier:** nexus-platform-plan ADR-0028 (Keeper, not ZooKeeper), ADR-0029 (3 shards × 2 replicas, Distributed over ReplicatedMergeTree), ADR-0031 (round-robin DNS, no VIP), ADR-0032 (NFS backup repo)

## Context

The `clickhouse` cluster (Phase 0.G.5, repo nexus-infra-analytics) is the first **analytics-tier**
adapter and the first **genuinely sharded** engine — a `Distributed` table (`nexus.events`) over
`ReplicatedMergeTree` (`nexus.events_local`), `internal_replication=true`, spread across **3 shards ×
2 replicas** (`ch-shard{1,2,3}-rep{1,2}` @ .44–.49) and coordinated by a dedicated **3-node
ClickHouse Keeper** RAFT quorum (`ch-keeper-1/2/3` @ .41–.43) — engine-native C++ RAFT, **NOT
ZooKeeper** (ADR-0028, mirrors Kafka's KRaft choice). The front door is **round-robin DNS**
`clickhouse.nexus.lab` with **no VIP** (ADR-0031): every data node is an equal entry point to the
Distributed table, so there is **no single write leader** on the data plane — the only cluster-wide
"leader" is the Keeper RAFT leader (the coordination plane).

ClickHouse 26.5.1.882. Two control surfaces: **clickhouse-client** (native TLS `:9440`) for the SQL +
data plane, and the **Keeper four-letter-word** interface (`echo mntr | nc 127.0.0.1 9181` → reports
`zk_server_state` leader/follower) for the coordination plane.

**Auth model decided from a live probe, not assumption:** ClickHouse is **password-auth**, *not*
mTLS-only like Redis. The wire is mTLS (port 9440, `verificationMode relaxed`), but SQL users
authenticate by `sha256_password`; the `default` user is restricted to loopback (127.0.0.1/::1), so
**every networked client must present a password**. This is the mongo/percona/patroni model.

## Decision

### 1. Credential model — reuse ADR-0011 verbatim

Authenticate as a dedicated **`nexus-cluster-admin`** ClickHouse user (`sha256_password`, `GRANT ALL
ON *.* WITH GRANT OPTION`), created `ON CLUSTER` so it lands on every node's local access storage.
**Distinct from the engine's built-in `admin`** (the schema-bootstrap RBAC account) — the CLI gets
its OWN operator identity, exactly as Patroni keeps `postgres`/`nexusops` alongside its
`nexus-cluster-admin`. The operator password lives **ONLY in Vault KV**
(`nexus/analytics/clickhouse/operator-password`, field `password`), fetched at runtime via the
optional `INexusVaultClient`. Infra: security-env `role-overlay-vault-clickhouse-creds-seed.tf` **v2**
(+`operator-password`, sticky-seeded) — **no agent-policy change** needed (the existing
`nexus-agent-clickhouse-*` policy already wildcard-reads `nexus/data/analytics/clickhouse/*`) — plus
analytics-env `role-overlay-clickhouse-operator-user.tf` (read operator-password on the DDL
coordinator via the node's own Vault Agent token → idempotent `CREATE USER … ON CLUSTER` → `GRANT
ALL` → verify the operator auths + manages access).

### 2. Connection paths

- **SQL / data plane (operator):** `clickhouse-client --secure --accept-invalid-certificate --host
  localhost --port 9440 --user nexus-cluster-admin --password '<kv>' --query '…'` over SSH on a data
  node. (`--accept-invalid-certificate` because the lab CA's IP-SAN-only chain doesn't satisfy the
  Windows/Go strict validation; the wire is still TLS. Identical to the infra readiness probe.)
- **Coordination plane (Keeper):** `echo mntr | nc -w 3 127.0.0.1 9181` (plain 4lw; secure 9281,
  RAFT 9234) → `zk_server_state` = leader|follower, `zk_znode_count`, … No auth on 4lw (read-only
  introspection). `clickhouse-keeper-client` is available for richer queries (unused so far).
- **Node identity:** shard/replica parsed from the hostname `ch-shardN-repM`; the CH `remote_servers`
  cluster name is **`nexus_analytics`** (distinct from the adapter `ClusterId` `clickhouse`).

### 3. Verb semantics

- **`status`/`topology`:** per-data-node `systemctl is-active nexus-clickhouse-server` + shard/replica
  from the hostname; Keeper nodes via 4lw `mntr` (the leader rendered `keeper-leader`). The cluster
  `Leader` = the **Keeper RAFT leader** (the only cluster-wide leader; the data plane is leaderless /
  multi-master). `topology` populates **Shards** (3 shards, each with its 2 replicas; per-shard
  "primary" = the `system.replicas.is_leader` merge-leader replica when queryable, else rep1) — unlike
  Patroni's `Shards=null`.
- **`health`:** keeper-quorum (≥2/3 reachable + exactly 1 leader) · per-node server-active · an
  **operator-auth** round-trip (`SELECT currentUser()` as `nexus-cluster-admin` — proves the Vault-KV
  credential end-to-end) · **distributed-membership** (`system.clusters` = 6 host rows) ·
  **distributed-query** (`SELECT count() FROM nexus.events` fans in across all 3 shards) · per-node
  **replica-health** (0 `is_readonly`/`is_session_expired`, `absolute_delay` ≤ 30 s).
- **`failover-test`:** **Keeper RAFT leader re-election** — stop `nexus-clickhouse-keeper` on the
  current leader (3-of-3 → 2-of-3, still quorate), poll the survivors' `mntr` until one reports
  `leader`; **RTO = the re-election delta** (≈ 1.1 s live); restart the stopped node (rejoins as
  follower). This is the coordination-plane failover; the data plane's replica resilience is exercised
  by `chaos` (the data plane has no single write endpoint to "fail over").
- **`scale-out add/remove`:** start/stop `nexus-clickhouse-server` on a data node (ReplicatedMergeTree
  rejoins + drains its replication queue via Keeper / graceful leave). `remove` refuses a shard's
  **last live replica** (would take that shard's data plane offline). Apply-on-demand for a genuinely
  new replica (ADR-0010 growth-var pattern).
- **`backup`:** native **`BACKUP TABLE nexus.events_local TO Disk('analytics_backups', '<id>.zip')`**
  (the shared NFS repo, ADR-0032) → `RESTORE TABLE … AS nexus.events_restore_verify FROM Disk(…)` →
  count rows. The `{uuid}` zk path on `events_local` means a same-/cross-node `RESTORE AS` does not
  collide with the live replica's znode (no `REPLICA_ALREADY_EXISTS`).
- **`cert-rotate`:** re-issue via the node's own Vault token (`pki_int/issue/clickhouse-server`,
  single allowed domain `clickhouse.nexus.lab` for **all 9** nodes — no domain-mismatch trap) → write
  `server.crt` + `server.key` (**PKCS#8** — Vault issues PKCS#1, converted with `openssl pkcs8
  -topk8`) + `ca.crt` (issuing intermediate **+** the Vault-Agent root anchor; OpenSSL needs the
  self-signed root) → `systemctl restart`. Rolling, 1 node at a time (each shard keeps a live
  replica); **data nodes first, the Keeper leader last** (its restart triggers a re-election).
- **`acl`:** `SHOW USERS`-equivalent (`system.users` ⟕ `system.grants`) for list/describe; idempotent
  `CREATE USER IF NOT EXISTS … ON CLUSTER` + `GRANT … ON nexus.* ON CLUSTER` for grant
  (hyphenated identifiers backtick-quoted).
- **`chaos`:** process-kill `nexus-clickhouse-server` on a replica (default a rep2 — the sibling rep1
  keeps the shard up) → observe → restart → ReplicatedMergeTree/Keeper rejoin → poll back to GREEN.
- **`CanResizeVm`:** refuses the current **Keeper leader** (a resize power-cycle forces a
  re-election); data replicas are safe (the sibling replica + the other shards keep serving).

### 4. Engine quoting + parsing (the 0.G.5 live-verify lesson)

1. **`access_management` is NOT a per-user `SETTINGS` value** (CH 26.5 → `Code 115 … neither a
   builtin setting nor … 'SQL_' …`, live-caught while creating the operator). The XML-only
   `access_management` setting (which the `default` user carries) cannot be set via `CREATE USER …
   SETTINGS access_management = 1`. For a **SQL-created** user the access-management capability comes
   from the **`GRANT ALL` privilege group** itself — so the operator is created with no SETTINGS
   clause and `GRANT ALL ON *.* WITH GRANT OPTION`, which was verified to confer `CREATE USER`/`CREATE
   ROLE`. (The clean infra study up front — mirroring the schema-bootstrap/TLS/backup overlays
   verbatim — kept the other verbs first-try-green; the established Redis/Mongo/Percona/Patroni
   shapes carried over without surprises.)

## Consequences

- **Positive:** the Vault-KV credential model + `INexusVaultClient` carry straight over (no framework
  change). A genuinely *sharded* engine is now in the verb surface — `topology` populates `Shards`,
  `health` proves the Distributed fan-in (600 rows over 3 shards) — and a *Keeper-coordinated*
  failover (RTO ≈ 1.1 s, the fastest of the data-tier adapters because RAFT re-election beats a
  streaming-replication promotion). AOT stays flat: **24.84 MB / 30 MB** (+0.66 MB over patroni — no
  managed `ClickHouse.Client` driver; SSH-shell-out + `JsonDocument` only). **71/71** tests, the
  NetArchTest no-managed-driver invariant intact.
- **Trade-offs:** `failover-test` exercises the *coordination* plane (Keeper) rather than a
  data-plane write-endpoint move — because ClickHouse has none (every replica is writable). The
  data-plane fault story (kill a replica, the shard stays served by its sibling) is the `chaos`
  path. The cert-rotate uses a `systemctl restart` (deterministic) rather than a hot reload.
- **Follow-ups:** a `ch_replica_extra_count` IaC growth var for minting a genuinely new replica on
  demand (the join logic is done + verified); surfacing the per-shard merge-leader in `status` (today
  only `topology` queries `is_leader`); when 0.L migrates the backup Disk `local`→`s3` (MinIO), the
  backup verb is unchanged (Disk type flips under it).
