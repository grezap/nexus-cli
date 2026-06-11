# ADR-0015 — StarRocksAdapter: StarRocks FE BDB-JE quorum + BE

- **Status:** Accepted
- **Date:** 2026-06-12
- **Phase:** 0.G.6 / nexus-cli `v0.6.5`
- **Extends:** [ADR-0009](ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) (SPI), [ADR-0010](ADR-0010-cluster-adapter-patterns-and-redis-adapter.md) (patterns), [ADR-0011](ADR-0011-mongo-adapter-and-operator-credential-model.md) (the Vault-KV operator-credential model — reused verbatim), [ADR-0014](ADR-0014-clickhouse-adapter-sharded-keeper.md) (the sibling analytics-tier adapter)
- **Cross-tier:** nexus-platform-plan ADR-0030 (3 FE + 3 BE, hash-distributed × replicated), ADR-0031 (round-robin DNS, no VIP), ADR-0032 (NFS backup repo)

## Context

The `starrocks` cluster (Phase 0.G.6, repo nexus-infra-analytics) is the second **analytics-tier**
adapter — a MySQL-protocol MPP warehouse. Topology (vms.yaml `starrocks`): 3 FE nodes (`sr-fe-leader`
+ `sr-fe-follower-1/2` @ .31/.32/.33) running `nexus-starrocks-fe.service` — a **BDB-JE replicated
metadata quorum** (1 LEADER + 2 FOLLOWER, dynamic election) — plus 3 BE nodes (`sr-be-1/2/3` @
.34/.35/.36) running `nexus-starrocks-be.service` that hold the tablet data. A table is
`DISTRIBUTED BY HASH(...) BUCKETS n` (sharded across the BE as tablets) × `replication_num=3`
(replicated). Front door = round-robin DNS `starrocks-fe.nexus.lab`, no VIP (ADR-0031). StarRocks
3.5.17.

The single control surface is the **FE MySQL-protocol query port `:9030`**: `SHOW FRONTENDS` /
`SHOW BACKENDS` for topology, `ALTER SYSTEM` for node ops, `BACKUP/RESTORE SNAPSHOT` for backups, the
SQL RBAC for `acl`.

**Auth model decided from a live probe:** StarRocks is **password-auth** — `root` *requires* a
password over the wire (the no-password probe returns "Access denied … using password: NO"), and that
password lives in Vault KV. This is the clickhouse/mongo/percona/patroni model.

## Decision

### 1. Credential model — reuse ADR-0011 verbatim

Authenticate as a dedicated **`nexus-cluster-admin`** StarRocks user, granted the built-in
**`cluster_admin` + `db_admin` + `user_admin`** roles with **`DEFAULT ROLE ALL`** (so the roles are
active on login — StarRocks requires default roles to be set, else granted roles are inert).
`cluster_admin` carries the **NODE** privilege (`SHOW FRONTENDS`/`SHOW BACKENDS` + `ALTER SYSTEM`),
`db_admin` covers DDL + `BACKUP`/`RESTORE`, `user_admin` covers `CREATE USER` (the `acl` verb).
**Distinct from the built-in `root`** — the CLI gets its own operator identity. The operator password
lives **ONLY in Vault KV** (`nexus/analytics/starrocks/operator-password`), fetched at runtime via the
optional `INexusVaultClient`. Infra: security-env `role-overlay-vault-starrocks-creds-seed.tf` **v2**
(+operator-password, sticky-seeded; **no agent-policy change** — the existing policy already
wildcard-reads `nexus/data/analytics/starrocks/*`) + analytics-env
`role-overlay-starrocks-operator-user.tf` (CREATE USER + GRANT + DEFAULT ROLE ALL via root, reading
both passwords on-node via the agent token).

### 2. Connection paths

- **mysql (operator):** `MYSQL_PWD='<kv>' mysql --skip-ssl -h 127.0.0.1 -P 9030 -u nexus-cluster-admin
  -e '<sql>'` over SSH on an FE node (any FE forwards DDL/node-ops to the leader). **`--skip-ssl` is
  required** — the deb13 MariaDB 11.8 client otherwise negotiates TLS the FE query port doesn't
  enforce ("SSL is required, but the server does not support it"). **`MYSQL_PWD`** (not `-p<pw>`)
  keeps the password out of argv *and* suppresses the "password on the command line" warning.
- **Node-IP mapping:** `SHOW FRONTENDS`/`SHOW BACKENDS` report the **VMnet10 backplane** IP (.10.x),
  not the service IP — mapped back to a node via vms.yaml's `vmnet10`. The FE leader is the
  `Role=LEADER` row (the bootstrap name `sr-fe-leader` is just a name; election is dynamic — the
  leader was observed on `sr-fe-follower-1`).

### 3. Verb semantics

- **`status`/`topology`:** parse `SHOW FRONTENDS\G` (Role LEADER→leader / FOLLOWER→follower, Alive) +
  `SHOW BACKENDS\G` (Alive, TabletNum). Leader = the LEADER FE node. `Shards=null` — StarRocks shards
  by tablet hash across the BE (no fixed named shards; the BE TabletNum in status/health is the
  sharding evidence), like Patroni.
- **`health`:** fe-quorum (≥majority alive + exactly 1 leader) · an **operator-auth** round-trip
  (`SELECT current_user()`) · per-BE liveness + TabletNum>0 · a **distributed-query** round-trip
  (`SELECT count(*)`).
- **`failover-test`:** **FE leader re-election** — stop `nexus-starrocks-fe` on the LEADER (3-of-3 →
  2-of-3, still quorate), poll `SHOW FRONTENDS` *from a surviving FE* until a different node reports
  LEADER; **RTO = the re-election delta** (≈1.5 s live); restart → rejoins as follower.
- **`scale-out add/remove`:** start/stop `nexus-starrocks-be` (the BE goes Alive=true/false in
  `SHOW BACKENDS`; surviving replicas keep tablets served). `remove` refuses dropping below 2 live BE
  (so each tablet keeps a surviving replica). Apply-on-demand for a genuinely new BE (ADR-0010).
- **`backup`:** genuine StarRocks **`BACKUP SNAPSHOT … TO nexus_backups ON (events)`** (the file://
  NFS repository, ADR-0032), then **poll `SHOW BACKUP` until State=FINISHED** (async). `restore` reads
  the snapshot's `backup_timestamp` via `SHOW SNAPSHOT`, runs `RESTORE SNAPSHOT … ON (events AS
  events_restore_verify) PROPERTIES("backup_timestamp"=…, "replication_num"="1")`, polls
  `SHOW RESTORE` until FINISHED, counts rows.
- **`cert-rotate`:** re-issue via the node's own Vault token (`pki_int/issue/starrocks-server`, one
  domain `starrocks.nexus.lab` for all 6 — no domain-mismatch trap) → write server.crt + PKCS#8
  server.key + ca.crt (intermediate+root) → `systemctl restart`. BE first, FE followers, **FE leader
  last** (its restart re-elects).
- **`acl`:** `SHOW USERS` (StarRocks has no `mysql.user`) enriched with `SHOW GRANTS FOR <user>` (the
  last tab field is the GRANT statement) for list/describe; `CREATE USER … + GRANT … ON nexus.*` for
  grant.
- **`chaos`:** process-kill `nexus-starrocks-be` on a BE + restart → rejoin.
- **`CanResizeVm`:** refuses the current FE leader (a resize power-cycle forces a re-election); BE are
  safe (surviving replicas keep serving).

### 4. Engine notes (the 0.G.6 live-verify lesson)

Unlike the prior adapters' ~4-bug cadence, **StarRocks went first-try-green on all twelve verb
invocations** — the thorough up-front study of the schema-bootstrap / TLS / backup-repo / FE-bootstrap
overlays + the proven adapter shape carried over with no surprises. The two contract specifics that
*would* have bitten a naive implementation were caught from the infra read, not a live failure:
`--skip-ssl` (the MariaDB-client TLS requirement) and the VMnet10-backplane node-IP mapping. The
genuine async `BACKUP/RESTORE SNAPSHOT` round-trip (flagged "best-effort" at the 0.G.6 infra
ratification) worked end-to-end against the now-established repository.

## Consequences

- **Positive:** the Vault-KV credential model + `INexusVaultClient` carry straight over (no framework
  change). A second MPP analytics engine is in the verb surface with a *genuine* async SNAPSHOT backup
  (vs ClickHouse's synchronous native BACKUP) and an FE-quorum failover (RTO ≈1.5 s — comparable to
  ClickHouse's Keeper re-election). AOT stays flat: **25.03 MB / 30 MB** (+0.19 MB over clickhouse —
  no managed MySqlConnector; SSH-shell-out + `JsonDocument` only). **71/71** tests, NetArchTest
  no-managed-driver intact.
- **Trade-offs:** `failover-test` exercises the metadata (FE) plane — the BE data plane has no single
  write endpoint to "fail over" (its fault story is `chaos`/`scale-out`). `topology` has no named
  shards (StarRocks shards by tablet hash); the BE TabletNum is the sharding evidence. The SNAPSHOT
  backup is async (polled), so the verb wall-clock includes the job time (~19 s take / ~22 s restore).
- **Follow-ups:** a `sr_be_extra_count` IaC growth var for minting a genuinely new BE on demand (the
  join logic is done); surfacing per-tablet distribution in `topology`; when 0.L's shared-data
  StarRocks (CN tier) lands, a sibling `starrocks-sd` adapter (the CN compute nodes vs BE).
