# ADR-0019 — MongoShardedAdapter (Phase 0.N sharded cluster; nexus-cli v0.7.1)

- **Status:** Accepted
- **Date:** 2026-06-16
- **Phase:** 0.N (sharded MongoDB) — first adapter on the 0.7.x line, after the v0.7.0 base roll-up
- **Supersedes / relates to:** ADR-0011 (MongoAdapter + the Vault-KV operator-credential model). Cross-tier: `nexus-platform-plan` ADR-0040 (the sharded-cluster topology + the keyFile-only / no-TLS v1 decision + the 0.N.1 mTLS hardening).

## Context

The CLI must manage **everything**. v0.6.1 shipped `MongoAdapter` for the 0.G.2 3-node replica set (ClusterId `mongo`). Phase 0.N added a **separate, genuinely-sharded** MongoDB cluster (ClusterId `mongo-sharded`, vms.yaml; ADR-0040): a 3-member config-server replica set (`config`, port 27019) + two 3-member shard replica sets (`shard-1` / `shard-2`, port 27018) + two stateless `mongos` query routers (port 27017) — 11 VMs. The replica-set adapter does not model shards, routers, or the config-server plane, so a dedicated adapter is warranted (one adapter per ClusterId, the established convention).

The live contract was **probed before building** (diagnose-before-rewriting): keyFile-only internal auth, **no TLS on the wire** in 0.N v1 (mTLS is the deferred 0.N.1 hardening), `authorization=enabled`.

## Decision

Ship **`MongoShardedAdapter`** (ClusterId `mongo-sharded`) over the same `IClusterAdapter` SPI, SSH-shell-out to on-node `mongosh` / `mongodump` / `mongorestore` (no managed driver; NetArchTest-enforced), reusing `MongoAdapter`'s parse/SSH/chaos/Vault-KV idioms.

### Two-headed auth (both use the shared keyFile content as the password)

The cluster's single secret is the keyFile (Vault KV `nexus/oltp/mongo/keyfile`, field `content`; also at `/etc/nexus-mongo/keyfile` on every node). The adapter fetches it via `INexusVaultClient` (VAULT_ADDR/TOKEN/CACERT), trims it to match the on-node value, and authenticates two ways:

1. **Direct mongod RS operations** (config + both shards) — the `__system` principal against `local` (SCRAM-SHA-256), connecting to `127.0.0.1:<rs-port>`. This is the ONLY principal the **shard** mongods accept (`nexus-sharded-admin` was created only on the config-server RS). Used for `rs.status` / `rs.stepDown` / `rs.add` / `rs.remove`.
2. **Cluster-level operations** (sh.status, balancer, config metadata, ACL, backup) — the `nexus-sharded-admin` **root** user against `admin`, **through a `mongos`** (`127.0.0.1:27017`). `__system`/`local` cannot be used through mongos (*"Can't use 'local' database through mongos"*, 0.N transient N9), so cluster ops MUST use this user.

### Node classification

vms.yaml carries no structured role/port, so the adapter derives both from the node-name prefix (deterministic, unit-tested): `mongo-cfg-*` → (configsvr, `config`, 27019); `mongo-shard-K-*` → (shardsvr, `shard-K`, 27018); `mongo-mongos-*` → (mongos, 27017).

### Verb surface

- **status / health / topology** — roll up all three RSes + the 2 routers. `topology` **populates the Shards array** (one `TopologyShard` per data shard RS) — the sharded showcase the 0.G.2 RS (Shards=null) doesn't demonstrate. `health` proves three layers: per-RS quorum/single-primary/lag, the mongos routing tier, and the config-server metadata (shard-registration count + balancer state read through mongos).
- **failover** — a **shard-primary** `rs.stepDown(60)` (default the first data shard; `--target` selects a node or RS) measured to a per-shard re-election RTO (live ≈ 2.8 s). The other shards + config RS are unaffected.
- **scale-out add / remove** — RS-member-level within a shard (apply-on-demand, ADR-0010): `rs.remove` a secondary (PRIMARY guarded), `rs.add` an unjoined reachable mongod (or actionable provisioning guidance). Uniform with the other adapters' member semantics.
- **backup take / restore** — `mongodump`/`mongorestore` **through mongos** (the standard sharded-cluster backup path) of `nexus_n_smoke`, round-tripped into a verify namespace (200 docs).
- **acl** — config-server admin users (list/grant/revoke) through mongos.
- **chaos** — process-kill a shard secondary + RS rejoin (embedded `nexus-chaos.sh`).
- **cert-rotate** — **graceful not-applicable** Result (like the deferred Kafka verbs): the v1 cluster has no TLS; mTLS + per-node cert rotation is the 0.N.1 hardening. Never a silent failure.

## Live-caught bug (the lesson)

`HealthAsync`'s cluster-level mongos query used **single-quoted** JS string literals (`'config'`, `'SHARDS='`), which collide with the outer `--eval '...'` shell quoting → the `shards-registered` probe reported red ("unreachable via mongos") even though the routers were alive. Fixed to double-quoted JS literals (the rest of the adapter already follows this rule). Every other verb was first-try-green against the running cluster — the thorough up-front contract probe (the two-headed auth split, the shard-mongod `__system`-only finding, the no-TLS contract) pre-empted the usual 1–4.

## Consequences

- The CLI now manages the sharded document store with the full verb surface; `mongo` (RS) and `mongo-sharded` are distinct registered ClusterIds.
- AOT footprint +0.12 MB (26.18 → **26.30 MB / 30**); 97/97 tests (+11 parser tests). No managed driver linked.
- When 0.N.1 lands mTLS, `cert-rotate` graduates from the graceful N/A to a real per-node re-issue (parity with `MongoAdapter`), and the auth helpers gain TLS flags.
