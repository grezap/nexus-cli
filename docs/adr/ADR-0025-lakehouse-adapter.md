# ADR-0025 — LakehouseAdapter (MinIO + Iceberg/Nessie + Spark + ZooKeeper)

- **Status:** Accepted
- **Phase / version:** Phase 0.L · nexus-cli v0.8.4
- **Date:** 2026-06-24
- **Supersedes / relates:** ADR-0009 (IClusterAdapter SPI), ADR-0011 (Vault-KV operator-credential model), ADR-0024 (ObservabilityAdapter — the SSH-local-curl access posture + the v0.8.1-greenfield casualty class this adapter also hits), the locked scope in `project_nexus_cli_infra_adapters_scope` §4.

## Context

The lakehouse tier (Phase 0.L, repo `nexus-infra-lakehouse`, tier `08-spark`) is the
**fourth** of the five non-data-tier adapters (Foundation → Swarm → Observability →
**Lakehouse** → Harbor) and the last big multi-component one. It is three distinct
engines plus a coordination ensemble over 16 VMs + 1 VRRP VIP:

- **MinIO** (`minio-1/2/3/4`) — distributed erasure-coded (EC:2) S3 object store, RR
  DNS `minio.nexus.lab`, no VIP. Also the S3 backend for **four other tiers**
  (observability Loki/Tempo, registry Harbor, analytics StarRocks-shared-data) — a
  fact that constrains any rebuild (below).
- **Iceberg/Nessie** (`iceberg-rest-1/2`) — Project Nessie Iceberg REST catalog HA,
  RR DNS `iceberg.nexus.lab`, backed by a dedicated **PG17 streaming pair**
  (`iceberg-pg-1/2`) behind VRRP VIP `.151` (`iceberg-db.nexus.lab`).
- **Spark** (`spark-master-1/2` ZK-elected HA + `spark-worker-1/2/3`) — Spark
  standalone, master URL `spark://…140:7077,…153:7077`, RPC = shared-secret + AES
  (not cert TLS).
- **ZooKeeper** (`zookeeper-1/2/3`) — the one deliberate Apache-ZK exception
  (ADR-0035); coordinates the Spark master election; backplane-only.

**Decision question:** how to expose the full `IClusterAdapter` surface over a tier
that is three engines, where the operator wants a single `nexus status lakehouse`.

## Decision

**One component-aware `LakehouseAdapter`** (ClusterId `lakehouse`), registered in
`ClusterBootstrapper` next to `ObservabilityAdapter`, classifying nodes by
name-prefix (`minio-` / `iceberg-rest-` / `iceberg-pg-` / `spark-master-` /
`spark-worker-` / `zookeeper-`) and dispatching per component internally — the
Greg-locked decision (scope §4: "ONE `lakehouse` adapter spanning all 3 components").

**Access posture = the ObservabilityAdapter shape** (ADR-0024), forced by the same
live contract: **SSH-local-curl** for every service endpoint with the node's own ca
(self-consistent across CA generations — Nessie's mgmt `/q/health` + Spark's UI are
plain HTTP, MinIO is HTTPS validated against `/etc/nexus-minio/certs/CAs/nexus-ca.crt`),
**build-host `INexusVaultClient`** for KV (mount `nexus`, every lakehouse secret field
= `value`), node SSH for systemctl / `mc` / psql / VIP / keepalived / ZK / chaos.
MinIO admin ops go through the on-node `mc` alias `nexuslocal`
(`sudo /usr/local/bin/mc …`). **No managed MinIO/Spark/Iceberg/Nessie driver**
(NetArchTest-enforced); AOT ≤30 MB.

### Verb → tool map

- **status** = per-node service active + MinIO EC online + Spark ALIVE-leader label +
  iceberg-pg VIP holder + ZK ensemble (16 nodes).
- **health** = MinIO `/minio/health/{live,cluster}` + `mc admin info` drives ok ·
  Nessie mgmt `/q/health` per-check (surfaces the S3 object-store check) + app
  `/iceberg/v1/config` · Spark master ALIVE + `aliveworkers` + worker `/json/` · ZK
  quorum (1 leader + rest followers) · iceberg-pg streaming replication · VIP bound.
- **topology** = 16 nodes + roles + VIP `.151` holder + ZK leader/followers + Spark
  master/standby + the `spark://` master URL. Not sharded (`Shards = null`).
- **failover** `--direction spark-master` = stop the ALIVE master → **ZooKeeper
  promotes the STANDBY** (~30 s), restart the stopped one as the new STANDBY (the
  live-proven HA drill). **`--direction iceberg-pg` = graceful actionable N/A**
  (diagnosed live): a keepalived VRRP cutover of the `.151` catalog-DB pair promotes
  the standby (notify_master) while nopreempt leaves the old primary un-demoted → a
  split-brain, and the promoted standby's `pg_hba` doesn't admit the Nessie hosts →
  the catalog front door lands on a PG Nessie can't use. A real catalog-DB failover is
  a DR runbook (promote + demote/fence + `pg_basebackup` re-seed), the same call obs
  made for grafana-db.
- **scale-out** = graceful actionable **N/A** — the MinIO EC set is fixed at 4 (the set
  size is baked at format time), the Spark worker count + the iceberg-pg/Nessie/ZK
  pairs/ensemble are fixed-size IaC; growth is a terraform/Packer op.
- **cert-rotate** = force the node's own vault-agent to **re-render** its leaf (the
  Swarm v0.8.2 `pkiCert`-persists lesson) + restart. **MinIO is re-certed BIG-BANG**
  (all 4 restarted together — a rolling 1-node re-cert breaks distributed MinIO's
  inter-node mTLS, the v0.8.3 lesson); Nessie re-renders per-node + restarts. **Spark +
  ZooKeeper are graceful N/A** (diagnosed live): Spark has no rotatable server leaf
  (RPC is shared-secret + AES; the only trust material is the JVM truststore CA, not a
  per-node leaf), and ZooKeeper is backplane-only plaintext (no TLS, no vault-agent).
  iceberg-pg is deferred to the PG DR runbook.
- **acl** = MinIO policies + users via `mc admin policy/user` (`list`/`grant`=attach a
  policy/`revoke`=detach; the root + app users protected).
- **chaos** = `nexus-chaos.sh` process-kill a MinIO node (EC tolerates 1) / Spark
  worker / Nessie node + recover-to-green (the iceberg-pg VIP holder + the ALIVE Spark
  master are spared unless `--target` is explicit).
- **backup** = `mc mirror s3://warehouse` to a node-local dir + integrity round-trip
  into a `warehouse-restore-verify` bucket (the Iceberg/Spark data; the S3 store is
  already EC-durable, so this is a portable point-in-time copy + a proof).
- No `recover-ha` (only `VaultAdapter` implements `IRecoverableCluster`).

## Live-contract reality (diagnosed first, per the standing rule)

The tier was offline during the v0.8.1 Vault greenfield, so it carries the **same
casualty class the observability tier hit** (ADR-0024). Diagnosed live 2026-06-24
(`reference_lakehouse_live_contract`):

- **MinIO** was re-certed to the NEW Vault root in the v0.8.3 session (leaf notBefore
  Jun 22; vault-agent re-authed) and is fully GREEN.
- **Nessie / iceberg-pg / Spark / ZooKeeper** were STILL on the OLD root (leaf
  notBefore May 23; vault-agent token absent) → a **cross-tier CA split**: old-root
  Nessie's JVM truststore could not validate the new-root MinIO S3 leaf
  (`PKIX path validation failed: Path does not chain with any of the trust anchors`)
  → Nessie `/q/health` reported the "Warehouses Object Stores" check DOWN. Plus an
  **iceberg-pg replication split** (pg-2 never re-seeded as a standby — both primary).
- The adapter probed the as-is tier and reported both honestly RED; **11 verbs
  verified GREEN with zero adapter-code bugs** against it. The trust re-cert + pg
  re-seed is a **Greg-authorized infra repair**, executed as a **cold-rebuild of the
  Iceberg + Spark envs only** (MinIO kept in place because reformatting its EC drives
  would wipe the four cross-tier buckets it serves). The rebuild produced fresh
  new-root certs that trust the live new-root MinIO → CA split + pg split resolved →
  the full verb matrix (incl. `cert-rotate` + `failover iceberg-pg`) then re-ran GREEN.

## Consequences

- The "manage everything" gap closes one more tier; 4/5 non-data adapters live
  (Harbor v0.8.5 remains).
- The MinIO-as-shared-S3-backend fact is now codified: a lakehouse rebuild must scope
  MinIO out unless the operator accepts wiping observability/registry/analytics data.
- Reused verbatim: `SshTarget`/`ISshClient`, `IVmsCatalog`, `INexusVaultClient`, the
  chaos `nexus-chaos.sh` embed, the cert-rotate vault-agent force-rerender, the
  SSH-local-curl posture — no new managed dependency, AOT stays under the gate.
