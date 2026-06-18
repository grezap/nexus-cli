# ADR-0021 — CitusAdapter (Phase 0.P Citus-sharded PostgreSQL + Patroni HA; nexus-cli v0.7.3)

- **Status:** Accepted
- **Date:** 2026-06-18
- **Phase:** 0.P (Citus-sharded PostgreSQL) — third adapter on the 0.7.x sharded line, after v0.7.1 MongoSharded + v0.7.2 Vitess; closes the relational-PostgreSQL-sharding axis
- **Supersedes / relates to:** ADR-0011 (the Vault-KV operator-credential model) · ADR-0013 (PatroniAdapter — the PostgreSQL Patroni HA + `patronictl` C# shape this reuses per group, incl. the `ctl:`-block switchover fix) · ADR-0020 (Vitess — the populated-Shards topology shape). Cross-tier: `nexus-platform-plan` ADR-0042 (the Citus topology — full Patroni HA, 9 VMs, workers registered by VIP).

## Context

The CLI must manage **everything**. Phase 0.P built a 9-VM Citus-sharded PostgreSQL cluster with full Patroni HA (ClusterId `citus`, vms.yaml; ADR-0042), tier 08-citus: 3 etcd DCS (`nexus-etcd`, client-cert-auth mTLS, **no RBAC password**), a coordinator Patroni pair (scope `citus-coord`, VIP `.211` `coord.citus.nexus.lab` = `pg_dist_node` groupid 0), and two worker Patroni pairs (`citus-worker1` VIP `.212` = groupid 1, `citus-worker2` VIP `.213` = groupid 2). Each PG group = 1 Patroni leader + 1 streaming replica over the shared etcd DCS; a keepalived VRRP VIP follows the Patroni leader. PG 17 + Citus 14.1; the distributed DB `citus` holds `events` (hash-distributed on `tenant_id`, 32 shards spread across both worker groups), `event_tags` (colocated), `tenants` (reference). Full Vault-PKI mTLS (`hostssl … scram-sha-256 clientcert=verify-ca`); workers registered in `pg_dist_node` **by VIP** so a worker failover needs no metadata rewrite.

**Citus = Patroni HA per group + Citus distribution** — so the design runs the ADR-0013 PatroniAdapter HA model three times (one coordinator group + two worker groups) and adds the Citus distributed layer (topology Shards populated like ADR-0020 Vitess).

The live contract was **probed before building** (diagnose-before-rewriting, 2026-06-18). Findings that shaped the design: (1) Patroni leaders had **drifted** off the lowest member name (worker1 leader = `citus-worker1-2`) — role MUST be read from `patronictl`, never assumed; (2) `~postgres/.pgpass` held only `postgres` + `replicator`, and `citus.enable_create_role_propagation=on` — so a dedicated operator role can be made to run distributed queries by adding one `.pgpass` line on the coordinators; (3) the etcd wrapper run **on** an etcd node always reports that node's OWN endpoint unhealthy (its hostname maps to `127.0.1.1`, but etcd listens on `127.0.0.1` + real IPs) — a single node only ever sees 2/3; (4) the patroni.yml had **no `ctl:` block** (REST `verify_client: optional`) — graceful switchover would 403, exactly the 0.G.4 PatroniAdapter bug.

## Decision

Ship **`CitusAdapter`** (ClusterId `citus`) over the `IClusterAdapter` SPI, SSH-shell-out to the on-node `patronictl` / `psql` / `etcdctl` (no managed Npgsql driver; NetArchTest-enforced), reusing the PatroniAdapter HA idioms per group + the Vitess populated-Shards topology.

### Operator identity (ADR-0011 Vault-KV model — Greg-approved 2026-06-18)

A dedicated **`nexus-cluster-admin`** role (LOGIN CREATEROLE CREATEDB + `pg_read_all_data`/`pg_write_all_data` + ALL on the `citus` DB + public schema; NOT superuser), password ONLY in Vault KV (`nexus/citus/operator-password`, field `content`) via `INexusVaultClient`. The role auto-propagates to the workers (`citus.enable_create_role_propagation`); a `~postgres/.pgpass` entry on **both** coordinator nodes lets the coordinator dial the workers AS the operator, so **distributed queries run end-to-end as the operator** (proven: `SELECT count(*) FROM events` = 800 via the coordinator VIP over mTLS+scram with the node's leaf as the required client cert). New infra overlays bake this for cold-rebuild: `role-overlay-citus-operator-user.tf` (nexus-infra-citus) + the security creds-seed (v2, +operator-password) + the PG-node agent policy (v2, +operator-password read).

### Node classification

Deterministic from the node-name (unit-tested): `citus-etcd-*` → etcd; `citus-coord-*` → group `citus-coord` (groupid 0); `citus-worker1-*` → `citus-worker1` (groupid 1); `citus-worker2-*` → `citus-worker2` (groupid 2). VIPs/DNS are infra canon (the catalog doesn't surface `virtual_ips`).

### Verb surface

- **status / health / topology** — roll up all 9 nodes (3 etcd + 3 Patroni groups). `topology` **populates the Shards array** — one `TopologyShard` per worker group with its Patroni primary/replica and its `citus_shards` count of `events` (16 + 16 of 32) — the distributed-sharding showcase. `health` proves layers: etcd quorum (unioned across nodes — see bug 1), per-group single-leader + replication lag, the operator mTLS round-trip via the coordinator VIP, the registered-worker count from `pg_dist_node`, the **sharding proof** (events shards span both worker groups), and a distributed cross-shard aggregate.
- **failover** — a graceful **`patronictl switchover`** on a chosen Patroni group (`--node` selects `coord`/`worker1`/`worker2`/scope/node; default the coordinator group), measured to the new leader (live RTO ≈ 1.6 s), then a switch-back. For a worker group the VRRP VIP follows the new leader so `pg_dist_node` needs no rewrite.
- **scale-out add / remove** — Patroni **member** membership: `remove` stops `nexus-patroni` on a replica (leader-guarded); `add` restarts a previously-removed member → it re-streams. Genuine **shard** growth (a 3rd worker group via `citus_add_node` + `rebalance_table_shards`) is apply-on-demand, ADR-0042 (documented in the OutcomeReason, not silently skipped).
- **backup take / restore** — the operator streams `COPY (…) TO STDOUT WITH CSV` for `tenants` (reference), `events` (distributed) and `event_tags` (colocated) via the coordinator VIP — a client-side pull that fans the distributed rows out to the workers through the coordinator (no superuser needed; a server-side `COPY TO file` would require it) → gzip node-local on the coordinator. `restore` recreates plain tables in a throwaway `citus_restore_verify` DB the operator owns, COPYs the rows back in, and counts (800 events round-tripped).
- **cert-rotate** — per-node Vault PKI: re-issue via the node's own Agent token → `pki_int/issue/citus-server` → `bundle.pem` → `nexus-citus-tls-split.sh` → apply. **PG nodes RELOAD** (`systemctl reload nexus-patroni` → SIGHUP, no failover); **etcd RESTARTS**. Order etcd → worker replicas → worker leaders → coord replica → coord leader LAST.
- **acl** — PostgreSQL roles via the operator over the coordinator VIP. `list` reads `pg_roles`; `grant` `CREATE ROLE` + `GRANT CONNECT` (Citus auto-propagates the role to the workers); `revoke` refuses the operator/system/app roles (`nexus-cluster-admin`/`postgres`/`citus_app`/`replicator`/`rewind`).
- **chaos** — process-kill (SIGSTOP) `nexus-patroni` on a worker-group replica (default) via the embedded `nexus-chaos.sh`; lift + restart + recover to green.

## Live-caught issues (the lessons)

The adapter CODE was first-try-green on every verb (the thorough up-front probe paid off — like StarRocks/Kafka). The one genuine live-caught issue was **infra**, predicted from ADR-0013:

1. **Graceful switchover → `403, client certificate required`** (the 0.G.4 PatroniAdapter bug #1). patroni.yml's REST `verify_client: optional` requires a client cert for state-changing POSTs, but there was no `ctl:` block so `patronictl` presented none. Fixed live (append a `ctl:` block — cacert/certfile/keyfile = the node's own TLS — to all 6 PG nodes; ctl is client-only so no restart) and baked into `role-overlay-citus-patroni-bootstrap.tf` (v2). **Lesson reinforced:** patroni.yml MUST stay `0640 postgres:postgres` (the daemon runs as `postgres`) — a stray `chmod root:root` during the live edit crash-looped a restarted member (the running leader, which had the file open, was unaffected), the second lesson of the slice.

Design decisions surfaced (not bugs): etcd health unions the per-node "is healthy" endpoint names (the `127.0.1.1` self-probe artifact); distributed data backup is an operator `COPY` round-trip (pg_dump on a coordinator doesn't dump worker data); scale-out is Patroni-member-level with the worker-group growth path documented.

## Consequences

- The CLI now manages the Citus-sharded PostgreSQL store with the full verb surface; **12/13 adapter families** live (`citus` registered alongside the rest in `ClusterBootstrapper`).
- AOT footprint +0.19 MB (26.52 → **26.71 MB / 30**); **137/137 tests** (+23 parser cases: IsEtcd / GroupOf / ParsePatroniList incl. drifted-leader + noise tolerance / RoleOf / StatusOf). No managed driver linked.
- Backup is an operator `COPY` round-trip (the distributed dataset, pulled through the coordinator); `cert-rotate` reloads PG (no failover). Both are deliberate, documented choices.
