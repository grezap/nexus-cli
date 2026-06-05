# ADR-0011 — MongoAdapter + the Vault-KV operator-credential model for password-auth clusters

- **Status:** Accepted
- **Date:** 2026-06-05
- **Phase:** 0.G.2 / nexus-cli `v0.6.1`
- **Supersedes / extends:** [ADR-0009](ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) (the `IClusterAdapter` SPI), [ADR-0010](ADR-0010-cluster-adapter-patterns-and-redis-adapter.md) (cross-adapter patterns + the Redis exemplar)

## Context

`v0.6.0` shipped the `IClusterAdapter` framework + `RedisAdapter`, an **mTLS-only** engine: the
client cert/key *is* the identity, there is no password. The next canon cluster — MongoDB
(`mongo`, 0.G.2) — is the first **password-authenticated** adapter, and it sets the pattern for
every other password-auth engine still to come (`percona` 0.6.2, `postgres`/Patroni 0.6.3,
`sql-fci`/`sql-ag` 0.6.6). Two decisions had to be made:

1. **Which identity does the operator CLI authenticate as?** MongoDB exposes only `smoke-rw`
   (`readWrite` on `nexus_smoke` — far too narrow to run `rs.status()`/`rs.stepDown()`) and the
   keyFile-derived `__system` cluster user (root-equivalent but "discouraged for operator use" per
   MongoDB docs; the auto-mode classifier correctly refuses it for queries). Neither fits.
2. **Where does that identity's password live, and how does the AOT binary get it** without baking
   a secret into the binary or scattering it across nodes?

## Decision

### 1. A dedicated operator user, provisioned by IaC

Provision a dedicated **`nexus-cluster-admin`** SCRAM user with the **least privilege that covers
the full verb surface**:

| Role (`db: admin`) | Verbs it enables |
|---|---|
| `clusterMonitor` | `status` · `health` · `topology` (`rs.status()`, `replSetGetStatus`) |
| `clusterManager` | `failover-test` (`rs.stepDown`) · `scale-out` (`rs.add` / `rs.remove`) |
| `backup` | `backup take` (`mongodump`) |
| `restore` | `backup restore` (`mongorestore`) |
| `userAdminAnyDatabase` | `acl` (`db.getUsers()` / `createUser` / `grantRolesToUser`) |

This is powerful but is **not** the root-equivalent `__system`/keyFile identity, and it carries no
arbitrary-collection write outside what `backup`/`restore` grant. The user is created idempotently by
`nexus-infra-oltp/terraform/envs/oltp-mongo/role-overlay-mongo-operator-user.tf` (mirrors the
`rs-initiate` Stage 2.5 `__system`-bootstrap createUser; re-apply converges the role set via
`grantRolesToUser`).

### 2. Password lives ONLY in Vault KV; fetched at runtime (the standard for ALL password-auth adapters)

- The password is sticky-seeded at `nexus/oltp/mongo/operator-password` by
  `nexus-infra-vmware/.../role-overlay-vault-mongo-operator-user-seed.tf`. It is **never written to a
  node file** (unlike `smoke-user-password`, which the TLS overlay renders to disk).
- The nexus-cli adapter fetches it **at runtime** via the existing `INexusVaultClient` (built from
  `VAULT_ADDR`/`VAULT_TOKEN`/`VAULT_CACERT`, the same resolver cluster-status + failover-test use)
  and passes it to `mongosh` over SSH — credentials transit, they don't persist on nodes.
- The one-time createUser bootstrap reads the same KV value via the mongo node's **own Vault Agent
  token** (`vault kv get` under the `nexus-agent-mongo-*` policy's `operator-password` read grant,
  added in v3 of `role-overlay-vault-agent-mongo-policies.tf`) — so even the bootstrap never writes
  the password to disk.

### 3. Framework plumbing — an *optional* `INexusVaultClient` through the registry

`ClusterBootstrapper.BuildRegistry()` best-effort-builds a `VaultClient` from the environment
(`TryBuildVaultClient()`, never throws) and passes it (nullable) to the adapters that need it.
mTLS-only adapters (Redis, Kafka) ignore it. When the env vars are absent the client is `null` and
password-needing verbs return an actionable *"set `VAULT_TOKEN`/`ADDR`/`CACERT`"* error instead of
failing obscurely. No new AOT-reachable types (`VaultClient` + `NexusHttpClientFactory` were already
linked by cluster-status), so the footprint is flat.

### 4. Engine specifics (live-verified — see `docs/verification/0.G.2-mongo.md`)

- **Connection contract:** unit `nexus-mongo` (stock `mongod` masked); `requireTLS` on 27017 with a
  **combined** `server.pem` (leaf+key) + `ca.crt`; `mongosh` runs on-node under `sudo`.
- **`--eval` quoting:** the remote shell wraps `--eval '<js>'` in single quotes, so **all JS string
  literals must use double quotes** (`print("OK")`, not `print('OK')`). Single-quoted JS terminates
  the shell quote and mangles the script — caught live on `scale-out`/`failover`.
- **`mongodump` scoping:** the dump URI's database path **scopes** the dump — `/admin` dumps only
  admin system collections; target `/nexus_smoke?...&authSource=admin` to capture app data. A
  `readPreference=secondary` dump returned **0 documents** against this RS, so the dump reads from the
  PRIMARY (default).
- **`mongorestore` ns-remap:** `--nsFrom`/`--nsTo` rename, but **`--nsInclude` is required** to select
  the namespace first — without it the restore matches nothing (0 docs).
- **`cert-rotate`:** genuine re-issue via the node's own Vault token (`pki_int/issue/mongo-server`),
  write the combined `server.pem` + `ca.crt`, rolling `systemctl restart nexus-mongo` one node at a
  time (RS tolerates a single member down). Same `pkiCert`-cache caveat as Redis (handbook §3).

## Consequences

- **Positive:** one credential model for every password-auth adapter (Mongo → Percona → Patroni →
  SQL); secrets never in the binary, never persisted on nodes, rotatable in Vault KV; least-privilege
  operator identity; full 11-verb surface live-verified; AOT stays at **23.9 MB / 30 MB**.
- **Negative / trade-offs:** the operator user is a powerful account (it can manage users via
  `userAdminAnyDatabase`); acceptable for an operator CLI and documented here. Rotating the KV
  password requires a matching `db.updateUser` (documented in the infra handbook).
- **Follow-ups:** `cert-rotate` persistence past the next Agent render (infra: refresh `pkiCert`
  cache / shorten TTL); a `mongo_extra_count` IaC growth var to mint a *new* member VM on demand
  (the join logic is done + verified; the remove→re-add cycle exercises the membership machinery).
