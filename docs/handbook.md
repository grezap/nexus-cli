# nexus-cli operator handbook

The software equivalent of the `nexus-infra-*` tier handbooks. It lets an operator drive the
cluster-adapter verb surface, understand exactly what each verb does, and recover the lab when a
verb returns nothing. Canon: [ADR-0009](adr/ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md)
(the `IClusterAdapter` SPI), [ADR-0010](adr/ADR-0010-cluster-adapter-patterns-and-redis-adapter.md)
(scale-out / chaos / backup patterns), and `nexus-platform-plan` ADR-0024 (the ≤30 MB gate).

> **How the CLI talks to a cluster:** every verb dispatches over **SSH** to the target node and
> shells out to the engine's own CLI (`redis-cli` / `mongosh` / `mysql` / `patronictl` /
> `clickhouse-client` / `sqlcmd`). **No managed DB drivers are linked** (ADR-0024). Auth to the
> engine is the cluster's own mTLS / domain identity, read from `/etc/nexus-<engine>/` on the node.

---

## §0 Prerequisites

Before any cluster verb runs:

| Requirement | Why | How to verify |
|---|---|---|
| The **6-VM foundation base** is up (nexus-gateway + dc-nexus + vault-1/2/3 + vault-transit) | DNS/DHCP/PKI/identity | `vmrun list` shows them; `ssh … vault-1 'vault status'` → `Sealed false` |
| **Vault is unsealed** | cert-rotate + any Vault-Agent-backed TLS render | see §3.1 |
| The **target data cluster** is powered on + its engine service active | the verb SSHes into it | `nexus status <cluster>` returns members |
| Build-host env vars (below) | locate vms.yaml + the SSH key | `echo $env:NEXUS_VMS_YAML` |

**Environment variables**

| Var | Required for | Value |
|---|---|---|
| `NEXUS_VMS_YAML` | every cluster verb | abs path to `nexus-platform-plan/docs/infra/vms.yaml` |
| `NEXUS_SSH_KEY` | every cluster verb | `~/.ssh/nexus_gateway_ed25519` (the lab key — **not** your personal `id_ed25519`) |
| `NEXUS_SSH_USER` | optional | default `nexusadmin` |
| `VAULT_ADDR` / `VAULT_CACERT` | `cluster-status`, `failover-test` (infra leaders) | `https://192.168.70.121:8200` + the CA bundle |

Run from source during development: `dotnet run --project src/Nexus.Cli -- <verb> …`. Released:
the single AOT `nexus` binary. Build/test/size: `pwsh -File scripts/cli.ps1 {build,test,cycle,size-check}`.

---

## §1 Verb reference (analytical)

The data-tier surface is the **13 verb groups** of the `IClusterAdapter` SPI. Each entry states
**what it does · input · output · where observed · what it proves · prerequisites**. Cluster id =
the `vms.yaml` cluster name (`redis`, `mongo`, `percona`, `postgres`, `clickhouse`, `starrocks`,
`sql-fci`/`sql-ag`, `mongo-sharded`, `vitess`, `citus`, `kafka`).

### `status <cluster>` — READ
- **What it does:** SSHes to a reachable node, runs the engine's membership query (Redis
  `CLUSTER NODES`; Mongo `rs.status()`; Patroni `patronictl list`; …) and renders every member with
  its **live** role + health + shard. Roles come from the engine, not the static vms.yaml labels.
- **Input:** cluster id. `--json` for machine output.
- **Output:** a table — hostname · IP · role (primary/replica/…) · status (alive/fail/…) · shard.
- **Where observed:** stdout. Cross-check on a node: `sudo redis-cli … CLUSTER NODES`.
- **Proves:** the cluster is formed and every member's current role/health.
- **Prereqs:** §0. *(Redis: live-validated 2026-06-05.)*

### `health <cluster>` — READ
- **What it does:** per-node probes (replication lag, role agreement, disk/memory pressure —
  per-engine probe set) folded into an overall green/yellow/red.
- **Input:** cluster id; `--json`.
- **Output:** probe table — probe · target · status · value · threshold.
- **Where observed:** stdout. (Redis lag from `INFO replication` `master_last_io_seconds_ago`.)
- **Proves:** replicas are keeping up and no node is degraded. *(Redis live-validated — surfaced a
  real redis-5 lag=8.0s YELLOW post-boot.)*
- **Prereqs:** §0.

### `topology <cluster> [--watch]` — READ
- **What it does:** renders the shard/replica map (which replicas follow which primary, slot/shard
  ranges). `--watch` redraws every 2s.
- **Input:** cluster id; `--watch`; `--json`.
- **Output:** a nodes table + a shards table (shard · primary · replicas · slot range).
- **Where observed:** stdout. **Proves:** the sharding/replication layout is correct.
- **Prereqs:** §0. *(Redis live-validated.)*

### `failover-test cluster <cluster> [--node N] [--yes]` — FAILOVER
- **What it does:** triggers a **controlled** primary loss and measures RTO. Redis runs
  `CLUSTER FAILOVER` on a replica and polls until it reports `master`; other engines use their
  native promote (`rs.stepDown`, `patronictl switchover`, `ALTER AVAILABILITY GROUP … FAILOVER`,
  Vitess `PlannedReparentShard`, …). Reports the timeline + RTO.
- **Input:** cluster id; `--node` to pick the promotion target; `--yes` to skip the prompt; `--json`.
- **Output:** result badge + timeline (pre-flight → failure injected → new primary observed →
  recovery → healthy) + RTO.
- **Where observed:** stdout; confirm new roles with `nexus status <cluster>`.
- **Proves:** the cluster survives a primary loss and how fast it recovers. *(Redis live-validated —
  new primary redis-6, RTO ≈ 2.1s. Known issue: "original primary: unknown" — the old-primary
  resolver uses a hostname heuristic instead of the `CLUSTER NODES` master-id; fix pending.)*
- **Prereqs:** §0. **Mutating** (changes which node is primary; reversible by symmetry).

### `scale-out add <cluster> --role <role> [--count N] [--shard S] [--yes]` — MUTATOR
- **What it does:** **live, role-aware horizontal growth.** Per ADR-0010 it provisions N new nodes
  through the **proven Terraform graph** (`<cluster>.ps1 apply -Vars "<role>_extra_count=N"` —
  unbounded; reserve an IP/MAC range, no idle VMs), then performs the engine join: Redis
  `--cluster add-node` (+reshard for a new primary), StarRocks `ADD {FRONTEND|BACKEND|COMPUTE NODE}`,
  Citus `citus_add_node` + `rebalance_table_shards`, Vitess add tablet/shard, etc.
- **Input:** cluster id; `--role` (engine-specific: primary/replica/fe/be/cn/worker/…); `--count`;
  `--shard`; `--yes`.
- **Output:** affected nodes + outcome + duration. **Where observed:** stdout + `nexus topology`.
- **Proves:** the cluster can grow on demand with the operator choosing the node's role.
- **Prereqs:** §0 + the per-cluster growth var/range (one-time IaC). **Mutating; minutes-long**
  (real VM clone). *(Redis live-verified 2026-06-05.)*

### `scale-out remove <cluster> <node> [--yes]` — MUTATOR
- **What it does:** drains/reshards data off the node, has the engine forget it
  (`del-node`/`removeShard`/`citus_remove_node`/…), then deprovisions the VM via Terraform.
- **Input:** cluster id; node name; `--yes`. **Output:** outcome + duration. **Mutating.**
  *(Redis live-verified 2026-06-05.)*

### `scale-up <vm> [--cpu N] [--ram MB] [--disk GB] [--force-primary]` — GENERIC
- **What it does:** **vertical** resize of a single VM (`VmrunVmResizer`, GAP #13 — fully implemented
  batch 3). CPU/RAM: `vmrun stop` → an **atomic `.vmx` edit** (`numvcpus`/`memsize`) → **cold** start
  (a suspend would not apply the edits). Disk: **`vmware-vdiskmanager -x`** grows the backing `.vmdk`
  offline (grow-only), then a **SAFE** in-guest FS extend — `growpart --dry-run` gates it (auto-installs
  `cloud-guest-utils`), handles plain-partition (`growpart`+`resize2fs`) **and** LVM
  (`growpart`+`pvresize`+`lvextend -r`), Windows (`Resize-Partition`). **Never repartitions a live boot
  disk:** when root is not the last partition (the **deb13 default swap-after-root layout**), the vmdk
  grows but the guest FS is left alone and the result says so plainly (Outcome `ok` + a warning) — no
  false success.
- **Cluster-aware gate:** resolves the VM's owning adapter (`ResolveOwningAdapterId` — 1:1 by vms.yaml
  cluster, plus the documented splits: `sqlserver`/`sqlserver-ag` by `sql-ag` prefix,
  `foundation`→`vault`/`foundation-ad`, `platform-tools`→`registry`; edge/workstations = no gate),
  warms its status, and consults `CanResizeVm`. **Refuses the current write-primary/leader** (or a
  cluster it can't reach to prove otherwise) unless `--force-primary`.
- **Input:** vm name; ≥1 of `--cpu`/`--ram`(MB, ×4)/`--disk`(GB); `--force-primary`; `--yes` (skips the
  interactive confirm — required non-interactively). **Output:** `resource | old | new` table (cpu / ram
  (MB) / disk (GB)) + Outcome (`ok`/`skipped`/`failed`) + reason. `--json` → `{vmName, outcome,
  outcomeReason, old/newCpu, old/newRamMb, old/newDiskGb, durationSec}`.
- **Where observed:** stdout; the guest (`nproc`/`free -m`/`lsblk`); VMware Workstation library.
  **Mutating** (cold-restarts the VM). **Prereqs:** Windows build host (vmrun + vmware-vdiskmanager,
  resolved by `VmrunPaths`); `NEXUS_VMS_YAML`; SSH to the guest for a disk grow. **Playbook: §3.5.**

### `backup take <cluster> [--tag T]` / `backup restore <cluster> <id> [--yes]` — MUTATOR
- **What it does:** engine-native dump (Redis `BGSAVE`; Mongo `mongodump`; Patroni `pg_basebackup`;
  CH `BACKUP TO`; SQL `BACKUP DATABASE`; …) to a backup store; restore reverses it and **verifies a
  row/key round-trip**. *(Redis store = node-local snapshot — NFS is not mounted on redis nodes;
  central destination is a documented option.)*
- **Input:** cluster id; `--tag`; (restore) backup id + `--yes`. **Output:** backup id · destination
  · size · duration / items-restored.
- **Where observed:** stdout; the snapshot file on the node. **Proves:** data can be captured and
  restored intact. **restore is DESTRUCTIVE.** *(Redis live-verified 2026-06-05.)*
- **`swarm` restore is double-gated (GAP #11, batch 3):** `consul`/`nomad snapshot restore` OVERWRITE
  the live Consul KV + Nomad jobs in place, so `backup restore swarm <id>` additionally requires
  **`--confirm-destructive`** on top of `--yes` — refused (exit 2) without it, pointing at the DR runbook
  for isolated-cluster recovery. **Playbook: §3.5.** *(live-verified 2026-07-06.)*

### `cert-rotate <cluster> [--yes]` — ROTATE
- **What it does:** forces a fresh TLS leaf per node (re-issue via Vault Agent) and reloads the
  engine; reports old→new serial per node.
- **Input:** cluster id; `--yes`. **Output:** per-node old/new serial table.
- **Where observed:** stdout; `sudo openssl x509 -in /etc/nexus-<engine>/tls/server.crt -serial`.
- **Proves:** certs rotate without downtime. *(Redis: FIXED v0.6.0 — the verb force-re-issues a fresh
  leaf via the node's own Vault token, because a bare Vault-Agent restart re-uses the cached `pkiCert`
  leaf and won't rotate; see §3.3. Live-verified across all 6 nodes.)*
- **Prereqs:** §0 + Vault unsealed. **Mutating** (brief per-node TLS reload).

### `chaos <cluster> <scenario> [--duration S] [--intensity N] [--yes]` — MUTATOR
- **What it does:** injects a fault via the on-node `nexus-chaos.sh` helper (pushed over SSH):
  `network-partition` (nft drop on the backplane), `packet-loss`/`slow-disk` (`tc netem`),
  `cpu-starve`/`memory-pressure` (stress-ng or shell fallback), `process-kill` (`systemctl kill`).
  **Every fault is time-boxed and self-reverts** via a `systemd-run` timer (a dropped SSH session
  cannot strand the node); the adapter measures impact via health probes + reports whether the
  cluster returned to green.
- **Input:** cluster id; scenario; `--duration`; `--intensity`; `--yes`.
- **Output:** scenario · target · observed impact · recovered? **Where observed:** stdout +
  `nexus health <cluster>` during the window; on-node `nexus-chaos.sh status`.
- **Proves:** the cluster degrades + self-heals as designed. **Mutating; self-reverting.**
  *(Live-verified across the fleet — see the `chaos` column in §2.)*

### `acl <cluster> <list|describe|grant|revoke> [--user U] [--perms …]` — READ + MUTATOR
- **What it does:** inspects (list/describe) or mutates (grant/revoke) the engine's access control.
  Redis `ACL LIST`/`SETUSER`; Mongo `getUsers`/`createUser`; …
- **Input:** cluster id; verb; `--user`; `--perms`. **Output:** user · enabled · permissions.
- **Where observed:** stdout. **Proves:** who can do what. *(Redis list/describe live-validated —
  confirmed `default … nopass … +@all`, i.e. mTLS-only, no password. grant/revoke pending.)*
- **Prereqs:** §0.

### `demo {list,run,record}` — ORCHESTRATOR
- The existing v0.4.0 demo engine. `demo run <id>` executes a System B JSON spec's steps and (per
  ADR-0009) **self-verifies** them via `expectedExitCode` + `expectedOutputContains`. Use it to
  replay any cluster's demos (`docs/demos/<id>.json`).

### `deploy <project> [--path DIR] [--execute --yes] [--json]` — ORCHESTRATOR (v0.9.0)
- Plans (and, with `--execute --yes`, runs) an **application project's** end-to-end deploy — build the
  container images, run the migrations, deploy the Api tier — from the project's own committed `deploy/`
  recipes. **Dry-run by default** (prints the five-step plan table); nothing runs without `--execute --yes`.
  Currently knows `dataflow-studio` (Phase 1). Layered like the SPI verbs: `IDeployPlanner`/`IDeployRunner`
  (Core) → `DataflowStudioDeployPlanner`/`DeployRunner` (Adapters) → command (host); source-gen JSON,
  AOT-clean. Demo: `demo run DEMO-DFS-01-dataflow-studio`. This is the first Phase-1 (application) verb —
  the CLI moves from operating the lab to deploying what runs on it.

---

## §2 Verb × cluster status

| Cluster | status | health | topology | failover | scale-out | scale-up | backup | cert-rotate | chaos | acl |
|---|---|---|---|---|---|---|---|---|---|---|
| redis | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ gen | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ list |
| mongo | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ gen | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ list+grant |
| percona | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ gen | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ list+grant |
| postgres | ✅ 06-11 | ✅ 06-11 | ✅ 06-11 | ✅ 06-11 | ✅ 06-11 | ✅ gen | ✅ 06-11 | ✅ 06-11 | ✅ 06-11 | ✅ list+grant |
| clickhouse | ✅ 06-11 | ✅ 06-11 | ✅ 06-11 | ✅ 06-11 | ✅ 06-11 | ✅ gen | ✅ 06-11 | ✅ 06-11 | ✅ 06-11 | ✅ list+grant |
| starrocks | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ gen | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ list+grant |
| sqlserver (FCI) | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | n/a¹ | ✅ gen | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ list+grant |
| sqlserver-ag (AG) | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ 06-12² | ✅ gen | ✅ 06-12 | ✅ 06-12 | ✅ 06-12 | ✅ list+grant |
| kafka-east | ✅ 06-15 | ✅ 06-15 | ✅ 06-15 | ✅ 06-15³ | ✅ 06-15⁴ | ✅ gen | ✅ 06-15⁵ | ✅ 06-15 | ✅ 06-15 | ✅ list+grant+revoke⁶ |
| kafka-west | ✅ 06-15 | ✅ 06-15 | ✅ 06-15 | ✅ 06-15³ | ✅ 06-15⁴ | ✅ gen | ✅ 06-15⁵ | ✅ 06-15 | ✅ 06-15 | ✅ list+grant+revoke⁶ |
| kafka-ecosystem | ✅ 06-15 | ✅ 06-15⁷ | ✅ 06-15 | n/a⁸ | n/a⁸ | ✅ gen | n/a⁸ | ✅ 06-15⁹ | ✅ 06-15 | n/a⁸ |
| kafka (DR meta) | — | — | — | ✅ v0.5 MM2 | — | — | — | — | — | — |
| mongo-sharded | ✅ 06-16 | ✅ 06-16 | ✅ 06-16¹⁰ | ✅ 06-16¹¹ | ✅ 06-16¹² | ✅ gen | ✅ 06-16¹³ | ✅ 07-10¹⁴ | ✅ 06-16 | ✅ list+grant+revoke¹⁵ |
| vitess | ✅ 06-17 | ✅ 06-17¹⁶ | ✅ 06-17¹⁷ | ✅ 06-17¹⁸ | ✅ 06-17¹⁹ | ✅ gen | ✅ 06-17²⁰ | ✅ 06-17²¹ | ✅ 06-17²² | ✅ list+grant+revoke²³ |
| citus | ✅ 06-18 | ✅ 06-18²⁴ | ✅ 06-18²⁵ | ✅ 06-18²⁶ | ✅ 06-18²⁷ | ✅ gen | ✅ 06-18²⁸ | ✅ 06-18²⁹ | ✅ 06-18³⁰ | ✅ list+grant+revoke³¹ |
| **vault** | ✅ 06-18 | ✅ 06-18³² | ✅ 06-18³³ | ✅ 06-18³⁴ | ✅ 06-18³⁵ | ✅ gen | ✅ 06-18³⁶ | ✅ 06-18³⁷ | ✅ 06-18³⁸ | ✅ list+grant+revoke³⁹ |
| **foundation-ad** | ✅ 06-18 | ✅ 06-18⁴⁰ | ✅ 06-18 | ✅ fsmo⁴¹ | n/a⁴¹ | ✅ gen | ✅ take⁴¹ | ✅ ldaps⁴¹ | n/a⁴¹ | ✅ list+grant+revoke⁴² |
| **swarm** | ✅ 06-19 | ✅ 06-19⁴⁴ | ✅ 06-19⁴⁵ | ✅ 06-19⁴⁶ | ✅ 06-19⁴⁷ | ✅ gen | ✅ 06-19⁴⁸ | ✅ 06-19⁴⁹ | ✅ 06-19⁵⁰ | ✅ list+grant+revoke⁵¹ |
| **observability** | ✅ 06-22 | ✅ 06-23⁵² | ✅ 06-22⁵³ | ✅ 06-23⁵⁴ | ✅ 06-22⁵⁵ | ✅ gen | n/a⁵⁶ | ✅ 06-23⁵⁷ | ✅ 06-22 | ✅ list+grant+revoke⁵⁸ |
| **lakehouse** | ✅ 06-24 | ✅ 06-25⁵⁹ | ✅ 06-24⁶⁰ | ✅ 06-25⁶¹ | n/a⁶² | ✅ gen | ✅ 06-24⁶³ | ✅ 06-25⁶⁴ | ✅ 06-24⁶⁵ | ✅ list+grant+revoke⁶⁶ |
| **registry** | ✅ 06-26 | ✅ 06-26⁶⁷ | ✅ 06-26⁶⁸ | ⏸ DR⁶⁹ | n/a⁷⁰ | ✅ gen | ✅ 06-26⁷¹ | ✅ 06-26⁷² | ✅ 06-26⁷³ | ✅ list+grant+revoke⁷⁴ |

> The **`vault`** cluster also has a bespoke **`recover-ha`** verb (not a matrix column) — the declarative
> boot-race recovery via the `IRecoverableCluster` capability: unseal vault-transit from the Shamir key file
> → restart vault-1/2/3 → poll unsealed. Idempotent; the ONLY exposed unseal path. `recover-ha <other>`
> returns a graceful "not applicable".⁴³

✅ live-verified · ⚠ coded, fix pending · ⏳ pending · n/a not-applicable (graceful). Dates = live-verify date.

> **`scale-up ✅ gen`** = the per-cluster scale-up demo is generator-emitted. `scale-up` is a **generic**
> verb (`VmrunVmResizer`), not per-adapter, so it shares one column across every tier. As of **batch 3
> (GAP #13)** it is **fully implemented and live-verified** (redis-1, 2026-07-05: cpu/ram round-trip +
> the deb13 root-not-last disk warning) — see §1 `scale-up` and the **§3.5** playbooks. Its cluster-safety
> gate is exercised on Kafka's controller-leader (**§3.5.3**, `DEMO-161`).
¹ an FCI is a fixed 2-node shared-storage instance — `scale-out` returns a skip-with-explanation (grow via `sqlserver-ag` or `scale-up`).
² AG `scale-out add` re-seeds a replica via **manual seeding** (also the named recovery command for a drifted/NOT_HEALTHY secondary).
³ kafka failover = a controlled **controller-leader move** (`kafka-metadata-quorum`, RTO ≈ 4.5 s) — complements the cross-region `failover-test cluster kafka` MM2 DR drill (the `kafka` meta-cluster row).
⁴ kafka scale-out = broker **drain/rejoin** (`scale-out add … --role broker`); the combined controller quorum is fixed at 3 at format time, so a genuine 4th broker is an apply-on-demand IaC op.
⁵ kafka backup = a topic→`.jsonl`→verify-topic **produce/consume round-trip** (not a binary snapshot); the topic defaults to `dr-gate-test`, override with `--tag <topic>`.
⁶ requires the KRaft `StandardAuthorizer` (enabled by `nexus-infra-kafka/.../role-overlay-kafka-acl-authorizer.tf`); without it `acl` returns `SecurityDisabledException`.
⁷ ecosystem health = systemctl + each service's HTTPS health endpoint (SR :8081, REST :8082, Connect :8083, ksqlDB :8088) + the MM2 journal.
⁸ failover/scale-out/backup/acl on `kafka-ecosystem` return a clear pointer (ecosystem state lives on the brokers; ACLs are enforced there).
⁹ ecosystem cert-rotate rebuilds both the PEM and the PKCS#12 keystores (Connect/ksqlDB REST listeners need P12).
¹⁰ mongo-sharded `topology` **populates the Shards table** (one row per data shard RS) — the sharded showcase the 0.G.2 `mongo` RS (Shards=null) doesn't demonstrate.
¹¹ mongo-sharded failover = a **shard-primary** `rs.stepDown` (default the first data shard; `--node`/`--target` selects a node or RS) measured to a per-shard election RTO (≈ 2.8 s). The config RS + other shards are unaffected.
¹² mongo-sharded scale-out = RS-**member** add/remove within a shard (`--shard shard-1`; PRIMARY guarded); apply-on-demand for `add`. (Adding/removing a whole shard via `sh.addShard`/`removeShard` is an IaC op, not this verb.)
¹³ mongo-sharded backup = `mongodump`/`mongorestore` **through a mongos** (the standard sharded-cluster path) of `nexus_n_smoke`, round-tripped into a verify namespace (200 docs). `backup restore` takes the backup-id as a **positional** argument.
¹⁴ mongo-sharded `cert-rotate` (**0.N.1, implemented**) = per-node Vault-PKI leaf re-issue + **online reload**. The 0.N.1 hardening added wire mTLS (`requireTLS` + per-host `mongo-sharded-server` leaf certs; parity with the 0.G.2 mongo RS). For each of the 11 nodes (config → shards → mongos): force the node's OWN vault-agent to re-issue a fresh leaf (rm `bundle.pem` → restart `nexus-vault-agent` → wait for the `server.pem` serial to change — the durable Swarm/vitess pattern), then reload it ONLINE via MongoDB's `db.adminCommand({rotateCertificates:1})` — **no restart, no shard re-election**. Live-proven 2026-07-10: all 11 nodes rotated (serials change), `health` 16/16 GREEN before + after (no re-election). Every mongosh/mongodump/mongorestore now dials over TLS.
¹⁵ mongo-sharded `acl` = config-server admin users (where sharded-cluster client users live) read+mutated **through mongos** as `nexus-sharded-admin` (`local` can't be used through mongos).
¹⁶ vitess `health` proves every layer: etcd quorum (one `nexus-etcdctl endpoint health` reports all 3), vtctld active, VTOrc `/debug/health` + no `/api/problems`, both vtgate `:15306` listeners, per-shard 1 PRIMARY + 2 REPLICA, the operator mTLS round-trip via vtgate, and the **sharding proof** (each shard non-empty: 54 / 47 rows).
¹⁷ vitess `topology` **populates the Shards table** (one row per keyspace shard `-80` / `80-`, slot range = the hash-vindex key range). Tablets register in the topo by their **VMnet10** IP (mapped back via vms.yaml); primaries are read from the topo (`GetShard.primary_alias`), never assumed — they drift off the lowest uid.
¹⁸ vitess failover = a graceful **`PlannedReparentShard`** to a healthy replica of the targeted shard (`--node`/`--target` selects a shard or a tablet node; default the first shard), measured to the shard-record primary change (RTO ≈ 0.17 s). The old PRIMARY is demoted to REPLICA in place. The VTOrc auto-reparent-on-kill path is exercised by `chaos` against a primary.
¹⁹ vitess scale-out = **tablet membership**: `remove` stops `nexus-vttablet`+`nexus-mysqlctld` and `DeleteTablets` from the topo (PRIMARY guarded + a ≥2-survivor floor); `add --shard <range>` restarts a previously-removed tablet so vttablet re-registers it as a REPLICA. Genuine growth = apply-on-demand IaC.
²⁰ vitess backup (**engine-native as of 0.O.1**) = `vtctldclient BackupShard commerce/<shard>` per shard against a real Vitess **`file` BackupStorage** repo on shared NFSv4 (`/vt-backups`; NFS server co-located on the control node, all 6 tablets mount it) driven by the **`xtrabackup`** engine — `BackupShard` auto-selects a REPLICA per shard (the PRIMARY is untouched, serving uninterrupted) and streams a compressed xtrabackup image (`backup.xbstream.gz` + `MANIFEST`) to the repo; the CLI reads the new backup name/size from `GetBackups`. `restore` is **safe by default** — `RestoreFromBackup --dry-run` per shard resolves + validates the restorable backup with no changes — and does a **real** `RestoreFromBackup` onto a REPLICA per shard (never the primary → the shard stays writable) only with **`--confirm-destructive`**, then waits for the tablet to rejoin as a serving replica and counts the restored rows (101 = 54 `-80` + 47 `80-`); `--at <YYYY-mm-DD.HHMMSS>` selects a specific backup. Replaces the pre-0.O.1 logical `mysqldump`. **Three Vitess/xtrabackup gotchas** (wired by `nexus-infra-vitess role-overlay-vitess-backup-storage.tf`, live-proven 2026-07-09): per-tablet `--mycnf-file` (mysqld is mysqlctld-owned), **drop `--db-socket`** so vttablet enters managed mode and loads the my.cnf (else `Cnf==nil` → "cannot perform backup without my.cnf"), and put the `[xtrabackup]` creds in **`ssl.cnf`** (already in the live mysqlctld's `EXTRA_MY_CNF` → survives every my.cnf regeneration with no disruptive mysqld restart; Vitess runs xtrabackup with a clean env + `--defaults-file=<my.cnf>` and never passes a password). `backup restore` takes the id **positionally**.
²¹ vitess `cert-rotate` (v0.8.9) = **force each node's own vault-agent to re-issue** a fresh leaf (rm `bundle.pem` → restart `nexus-vault-agent` → its post-render `nexus-vitess-tls-split.sh` writes a DURABLE `server-cert/server-key(PKCS#8)/ca`; `pkiCert` otherwise reuses its cached leaf and reverts a direct write — the Swarm v0.8.2 lesson, a pre-existing non-durability the GAP-#12 verify caught), order etcd → tablet-replicas → tablet-primaries → vtgate → control, then restart the serving unit. **The tablet's mysqld-WIRE cert (:3306 — replication + vt_dba + vtgate→mysqld) is reloaded ONLINE via `ALTER INSTANCE RELOAD TLS`** (Percona 8.4 — no restart, **no reparent**, the PRIMARY is never demoted; GAP #12). Live-proven 2026-07-07: all 12 nodes rotated, tablet serials change + persist, the primary's mysqld serves the new serial (`openssl s_client -starttls mysql` == on-disk), shards stay 1P+2R (no reparent), health green.
²² vitess `chaos` process-kill SIGSTOPs a single unit (`nexus-chaos.sh`): a replica freezes `nexus-vttablet`; a PRIMARY target freezes `nexus-mysqlctld` (mysqld) → **VTOrc auto-reparents** the shard to a replica (proven live: VTOrc promoted shard2-tablet-2 when the `80-` primary froze) → lift + recover to green.
²³ vitess `acl` = the **vtgate static-auth file** `/etc/nexus-vitess/vtgate_creds.json` (the real MySQL credentials at the `:15306` front door) — `list` parses it; `grant`/`revoke` edit it on **both** vtgate nodes + restart `nexus-vtgate` to apply. vtgate does NOT proxy `CREATE USER` DDL; the built-in `nexus` operator user is revoke-protected.

²⁴ citus `health` proves every layer: etcd quorum (**unioned across nodes** — running `nexus-etcdctl endpoint health` ON an etcd node always reports that node's OWN endpoint unhealthy via `127.0.1.1`, so each node sees only 2/3; union the "is healthy" endpoint names → 3/3), per-group single-leader + replication lag, the operator scram+mTLS round-trip via the coordinator VIP, the registered-worker count from `pg_dist_node` (2), the **sharding proof** (`events` shards span both worker groups — worker1=16 worker2=16), and a distributed cross-shard aggregate (800 rows).

²⁵ citus `topology` **populates the Shards table**: one row per worker group with its Patroni primary + replica and its `citus_shards` count of the distributed `events` table (16 + 16 of 32). Coordinator + etcd appear in the Nodes table only. Leaders drift — read from `patronictl`, never assumed.

²⁶ citus failover = a graceful **`patronictl switchover`** on a chosen Patroni group (`--node` selects `coord`/`worker1`/`worker2`, a scope, or a node; default the coordinator group), measured to the new leader (RTO ≈ 1.6 s), then a switch-back. For a worker group the keepalived **VRRP VIP follows** the new Patroni leader, so `pg_dist_node` (which registers workers by VIP) needs no rewrite. Requires the patroni.yml `ctl:` block (without it, the state-changing REST POST 403s "client certificate required" — the 0.G.4 lesson, baked into `role-overlay-citus-patroni-bootstrap.tf` v2).

²⁷ citus scale-out = **Patroni member** membership: `remove <node>` stops `nexus-patroni` on a replica (leader-guarded — fail it over first); `add --role replica` restarts a previously-removed member so Patroni re-streams it. Genuine **shard** growth (a 3rd worker group) is apply-on-demand (ADR-0042): provision the VMs + overlays, `citus.ps1 apply`, then on the coordinator `SELECT citus_add_node('<vip>',5432)` + `SELECT rebalance_table_shards()`.

²⁸ citus backup = an **operator `COPY (…) TO STDOUT` round-trip** of the distributed dataset: the operator (`nexus-cluster-admin`) streams `tenants` (reference) + `events` (distributed) + `event_tags` (colocated) via the coordinator VIP — a client-side pull that fans the distributed rows out to the workers through the coordinator (no superuser needed; a server-side `COPY TO file` would require it) → gzip node-local on the coordinator. `restore` recreates plain tables in a throwaway `citus_restore_verify` DB the operator owns, COPYs the rows back, and counts (800 events). `backup restore` takes the id **positionally**. (pg_dump on a coordinator doesn't dump worker data, so the `COPY` pull is the faithful distributed round-trip.)

²⁹ citus `cert-rotate` = per-node Vault PKI (`pki_int/issue/citus-server` via the node's own Agent token → `nexus-citus-tls-split.sh`), order etcd → worker-replicas → worker-leaders → coord-replica → coord-leader LAST. **PG nodes RELOAD** (`systemctl reload nexus-patroni` → SIGHUP; PG re-reads `ssl_cert_file` with no restart, so no leader is demoted); **etcd RESTARTS** (reads certs at boot).

³⁰ citus `acl` = PostgreSQL roles via the operator over the coordinator VIP. `list` reads `pg_roles` (attribute flags); `grant` `CREATE ROLE … LOGIN` (idempotent) + `GRANT CONNECT` — Citus **auto-propagates** the role to the workers (`citus.enable_create_role_propagation`); `revoke` removes the DB grant. The operator/system/app roles (`nexus-cluster-admin`/`postgres`/`citus_app`/`replicator`/`rewind`) are revoke-protected.

³¹ citus `chaos` process-kill SIGSTOPs a single `nexus-patroni` unit (`nexus-chaos.sh`): a worker-group replica by default (the group stays writable on its leader); `--target` may name any PG member. Lift + restart + recover to green — Patroni HA absorbing a member loss.

³² vault `health` proves every layer of the trust root: per-node seal-status (vault-1/2/3 unsealed, active/standby), exactly 1 active, the **Raft peer set** (`sys/storage/raft/configuration` — 3 voters + 1 leader), the **transit-unseal** custodian (vault-transit serving the auto-unseal), and the **operator-auth** round-trip (`sys/policies/acl` readable with the env `VAULT_TOKEN` → 128 policies). The Vault control plane is **HTTP from the build host** (`VaultAdminClient`); vault-transit (outside the build-host CA bundle) is probed over SSH.

³³ vault `topology` enriches each node with its Raft role (`active/raft-leader`, `standby/voter`); Shards = null (Vault is not sharded). Leaders **drift** — the active is read dynamically per node (`sys/leader`), never assumed (the build-host `VAULT_ADDR` is usually a follower; the API forwards).

³⁴ vault failover = **`PUT sys/step-down`** on the active node → poll until a standby becomes active (live RTO ≈ 2.0 s). Raft leadership is location-independent: the old active becomes a healthy standby and the cluster serves throughout (clients follow the active-node redirect), so there is no forced "return" — `Recovery = skipped`, not a defect. Mutating verbs target standbys, but `step-down` is the one exception (it must hit the active to trigger the election; the active is briefly demoted in place, never stopped).

³⁵ vault scale-out = stop/start a **STANDBY** `vault.service` (never the active, never the transit custodian): `remove <vault-N>` stops it (it stays a Raft peer, offline; the cluster keeps quorum on the other two); `add` restarts a stopped standby → it **auto-unseals via vault-transit** and rejoins Raft (≈ 3.6 s). Growing the quorum (a 4th voter) is a terraform/Packer op (documented in the OutcomeReason, not silently skipped).

³⁶ vault backup = **`GET sys/storage/raft/snapshot`** streamed to a build-host file (`~/.nexus/backups/vault/<id>.snap`) + a **non-destructive inspect** — a Vault raft snapshot is a gzip(tar) whose `meta.json` carries {Index, Term, Size}, parsed via `System.Formats.Tar` (the safe equivalent of `vault operator raft snapshot inspect`, never a restore). `backup restore` is **deliberately refused**: `raft snapshot restore` overwrites every secret/policy/PKI mount of the live trust root in place — the DR runbook restores onto an ISOLATED cluster, never the live one.

³⁷ vault `cert-rotate` = re-issue each listener cert from **`pki_int/issue/vault-server`** via the build-host token (`IssuePkiCertAsync`) → SSH-push `vault.crt`/`vault.key` to `/etc/vault.d/tls/` (chown `vault:vault`, 644/600) → `systemctl reload vault` (SIGHUP, zero-downtime — no leadership change). Order: **standbys first, active LAST**. The vault nodes have NO Vault Agent (they ARE the servers), so the cert is issued with the operator token, not a node token.

³⁸ vault `chaos` process-kill SIGKILLs a **STANDBY** `vault.service` (`nexus-chaos.sh`; never the active, never the transit custodian) → lift + restart + the standby re-auto-unseals and rejoins Raft → recover to green.

³⁹ vault `acl` = **Vault ACL policies + AppRoles**. `list`/`describe` read `sys/policies/acl` + `auth/approle/role` (the policy HCL on describe); `grant` writes a demonstrative ACL policy; `revoke` deletes it. The operator/system policies (`root`/`default`/`nexus-admin`/`nexus-operator`/`nexus-reader`/`nexus-foundation-reader`/`nomad-jobs`/`nexus-bootstrap`) and the per-node `nexus-agent-*` policies are revoke-protected.

⁴⁰ foundation-ad `health` (Windows-SSH to the DCs + Linux-SSH to the gateway) proves: both DCs reachable (ADWS), **AD replication** (`Get-ADReplicationPartnerMetadata` LastReplicationResult = 0, failures = 0 — run ON each DC with the default `-Server`; an explicit `-Server <ip>` returns empty fields, the one live-caught bug), DNS zones AD-integrated, the **KDS root key** (via the AD `Master Root Keys` object — `Get-KdsRootKey` is unreliable over SSH), all **5 FSMO roles**, and the gateway (dnsmasq + nftables + the NAT masquerade rule).

⁴¹ foundation-ad **`failover-test cluster foundation-ad` = a GRACEFUL FSMO transfer drill**: `Move-ADDirectoryServerOperationMasterRole` relocates the **4 operator-movable FSMO roles** (PDCEmulator/RIDMaster/InfrastructureMaster/DomainNamingMaster) from the current holder to the other DC, verifies, then transfers them BACK (`--no-recover` leaves them moved; `--node` picks the target) — the planned-maintenance "evacuate this DC" drill (online transfer, AD serves auth throughout; needs ≥2 reachable DCs). **SchemaMaster is deliberately excluded** — moving it needs Schema Admins (kept restricted by AD design; live-caught 2026-06-29 that an all-5 batch run as Domain/Enterprise Admin splits at SchemaMaster, so the verb scopes to exactly what the operator can move, keeping the transfer atomic). FSMO *seize* (permanent-loss last resort) stays manual `ntdsutil`. Live-proven 2026-06-29: 4 roles dc-nexus→dc-nexus-2 and back, recovered ~6.8 s. **`backup take` = `ntdsutil ifm create full`** on a reachable DC (prefers a non-PDC when ≥2 are up; falls back to the sole reachable DC) → a non-destructive full copy of the AD database (`ntds.dit` + registry hives) under `C:\nexus-backups\ad\<id>` on the DC — the AD analogue of the Vault raft-snapshot verb (live-proven 2026-06-28: 96 MiB `ntds.dit`, ~12 s). The remaining mutators stay graceful **actionable N/A**: DC add/remove is terraform (`Install`/`Uninstall-ADDSDomainController`, ADR-0039), `backup restore`/authoritative restore is the **console-only DSRM** path (Server 2025 blocks `ntdsutil` ConsoleMode over SSH), and **DC chaos is a genuine N/A for the SSH-managed adapter** — a meaningful DC chaos stops ADDS/NTDS, which also stops Netlogon and severs the domain secure channel OpenSSH uses to auth `nexusadmin`, so the chaos self-fences the adapter's own recovery (live-proven 2026-06-29 on dc-nexus-2: `Permission denied (publickey)` → recovery needed an out-of-band `vmrun reset`); the 2-DC HA is validated out-of-band by smoke-0.M instead. Each names the right out-of-band tool. **`cert-rotate` = the DC LDAPS leaf (GAP #9, v0.8.9) — now implemented, guarded:** for each DC, **standby DC first then the PDC** (a failed standby aborts before the PDC's auth plane), issue the leaf + build the PFX on vault-1 with openssl (the proven Schannel path), SFTP the PFX + intermediate + root, import root→Root / intermediate→CA / leaf→My (all three load-bearing — the 36886 fix), verify the chain, `Restart-Service NTDS` + re-cycle ADWS in ONE SSH session (sshd is independent of NTDS, so the session survives the ~20-30 s restart — no new SSH auth during the window, so the self-fence can't occur), then verify the **:636 handshake from the build host**. Live-proven 2026-07-07: dc-nexus-2 (first-time install) + dc-nexus both serve the new leaf, AD auth uninterrupted. Needs `VAULT_TOKEN` + `VAULT_CACERT`; the `vault-server` PKI role gained the `dc-nexus-2` domains.

⁴² foundation-ad `acl` = AD users + groups via Windows-SSH. `list` reads the enabled users + the `nexus-*` security groups; `describe --user <u>` reads MemberOf; `grant`/`revoke --user <u> --permissions <group[,group]>` = `Add`/`Remove-ADGroupMember`. Protected principals (`Administrator`/`krbtgt`/`nexusadmin`/`Domain Admins`/`Enterprise Admins`/`Schema Admins`/…) are refused.

⁴³ `recover-ha vault` (the `IRecoverableCluster` capability) replicates `nexus-infra-vmware/scripts/recover-vault-ha.ps1`: read the Shamir keys from the operator's `~/.nexus/vault-transit-init.json` → unseal vault-transit over SSH → `reset-failed` + `start vault` on vault-1/2/3 → poll until unsealed. Idempotent (already-unsealed = no-op). It is the ONLY exposed unseal path (raw `vault operator unseal` is never surfaced).

⁴⁴ swarm `health` = 9 probes rolling up the **three** control planes: Consul (`/v1/agent/members` 6 alive/0 failed + `/v1/status/leader`), Nomad (3 servers + exactly 1 leader + 3 ready clients), Portainer (`/api/system/status` reachable), and Docker Swarm from `docker node ls` (3 managers + 3 workers Ready + exactly 1 raft leader). The reused `ClusterStatusService` provides the Consul/Nomad/Portainer rollup; the docker view is the authoritative quorum source. All three HTTP clients target a **manager IP** (the build host doesn't resolve `*.nexus.lab`; the CA-pinned factory validates the chain, not the SAN).

⁴⁵ swarm `topology` lists the 6 nodes role-annotated (manager = consul-server/nomad-server with its raft state; worker = consul-client/nomad-client/portainer-agent) + a **Portainer service** node (best-effort `/api/endpoints` count, else its version). Shards = null (the orchestration tier is not data-sharded). The three rafts (Swarm/Consul/Nomad) elect independently — each leader read from its own source.

⁴⁶ swarm failover dispatches **`--direction`** to the reused `FailoverTestService`: `consul-leader` / `nomad-leader` SSH-`systemctl stop` the discovered raft leader → poll a different manager for re-election → restart (RTO ≈ 2–3 s); **`swarm-manager`** is a **vmrun host-level SUSPEND** of the Swarm raft-leader VM → poll `docker node ls` for the new leader → vmrun resume (RTO ≈ 21 s — the only host-level failover). Let the cluster settle after a swarm-manager run (the Consul re-election window can briefly show no leader).

⁴⁷ swarm scale-out = **reversible drain** (not `docker node rm`): `remove <node>` = `docker node update --availability drain` (+ `docker node demote` for managers, guarded by "not the raft leader AND ≥2 managers Ready") + `nomad node drain -enable -self`; `add --role <manager|worker>` re-`active`s (+ `promote`s) + re-enables Nomad eligibility on the drained node. Growing the fixed 3-manager + 3-worker fleet is a terraform op (documented in the OutcomeReason).

⁴⁸ swarm backup = `consul snapshot save` + **`consul snapshot inspect`** (the round-trip verify) + `consul kv export` + `nomad operator snapshot save` on a manager → downloaded to `~/.nexus/backups/swarm/<id>/` (+ best-effort Portainer boltdb copy). `backup restore` is **guarded (GAP #11, batch 3)**: `consul`/`nomad … snapshot restore` overwrite the live KV + job state in place, so it requires an explicit **`--confirm-destructive`** on top of `--yes` (refused, exit 2, without it); with it, it uploads the snapshots and restores online against the leader, counting restored KV keys + jobs. To recover onto an ISOLATED cluster instead, follow the DR runbook. Live-verified 2026-07-06; playbook §3.5.4, demo `DEMO-162`.

⁴⁹ swarm `cert-rotate` **force-reissues** each node's pki_int leaves: the vault-agent templates use the `pkiCert` function, which **persists + reuses** the leaf across restarts, so a bare `systemctl restart nexus-vault-agent` does NOT rotate — the verb `cp -a`+`rm`s the rendered bundle (with a `.bak` restore safety) so `pkiCert` re-issues on the next render, then restarts the services: **consul ROLLING** (workers → non-leader managers → leader) and **nomad PARALLEL big-bang** across all six ([[feedback_nomad_tls_rolling_restart_must_be_parallel]] — a rolling flip strands the first TLS node and raft can't elect). New vs old wire serials (via `openssl s_client`) prove the rotation.

⁵⁰ swarm `chaos` runs `nexus-chaos.sh` on a **WORKER** (managers are spared to keep raft quorum): process-kill targets the worker's `nomad`; network-partition/packet-loss drop the **VMnet10 backplane** CIDR (the management NIC stays up so the lift + recovery work). After any nftables-based scenario the victim's `docker` is restarted to rebuild the ingress-mesh DNAT the `flush ruleset` wiped ([[feedback_nftables_flush_ruleset_wipes_docker]]); recover-to-green via a lightweight `docker node ls` poll (the victim Ready+Active) — kept under the chaos command's `Duration+60 s` budget.

⁵¹ swarm `acl` = **Consul + Nomad ACL tokens** merged: `list`/`describe` parse `consul acl token list -format=json` + `nomad acl token list -json`; `grant --user <name>` creates a Consul token with the minimal `builtin/dns` templated policy (Consul refuses a policy-less token); `revoke --user <accessor|description>` = `consul acl token delete -accessor-id` / `nomad acl token delete`. Bootstrap/management/agent/anonymous tokens + the global-management/node-identity policies are revoke-protected. (`CanResizeVm` refuses the current Swarm OR Nomad raft leader.)

⁵² observability `health` rolls the whole Grafana LGTM stack into one report: Prometheus ready ×2 + scrape-targets-up (`/api/v1/targets`), Alertmanager gossip-mesh peers (`/api/v2/status`, on the VMnet10 backplane `:9094`), Loki + Tempo `/ready` ×3 + `/memberlist` ring counts, Grafana `/api/health` `database`=ok ×2, OTel loopback health (`http://127.0.0.1:13133/`, on-node only), **Grafana-PG streaming replication** (dynamic primary detection → `pg_stat_replication` streaming count), **MinIO S3 reachability** (the Loki/Tempo backend, `/minio/health/live`), and both VRRP VIPs bound. The endpoints are probed **over SSH with each node's own `ca.crt`** — the obs leaves are on the tier's OLD CA generation (the tier was offline during the v0.8.1 Vault greenfield) while the build host trusts the NEW root, so the build-host CA bundle can't validate them (the diagnose-first divergence). KV creds (Grafana admin etc.) come from Vault via `INexusVaultClient` (every obs secret field = `value`). On the as-is degraded tier `health` is correctly RED on `grafana-pg-replication` (the standby was promoted in the 0.I.4 `-Strict` test and never re-seeded — split).

⁵³ observability `topology` = 14 role-annotated nodes + **2 VIP pseudo-nodes** (the live `.184` grafana / `.185` grafana-db front-door holders) + the Loki/Tempo memberlist member counts + the Prometheus scrape-target count. Shards = null (the observability tier is not data-sharded).

⁵⁴ observability `failover-test cluster observability --direction grafana|grafana-db` = a **keepalived VRRP cutover**: stop keepalived on the live VIP MASTER → poll the VIP onto the backup → restart (nopreempt keeps it put); RTO measured. `grafana` (.184) is live-proven (RTO ≈ 1.2 s, recovered); `grafana-db` (.185) uses the identical code path but is not live-run on the split-replication tier (promoting the divergent standby would be unsafe).

⁵⁵ observability `scale-out` = the Loki/Tempo **memberlist rings** only: `remove <loki-N|tempo-N>` stops the ring service (guarded by a ≥2-ready floor; the ring self-heals ~60 s), `add --role loki|tempo` restarts a stopped member and polls `/ready` for the rejoin. Prometheus (scrape-all), Grafana (VRRP active-active), Grafana-PG (streaming pair) and OTel (RR-DNS pair) are **fixed at 2** → graceful actionable N/A naming the terraform path.

⁵⁶ observability `backup` = graceful actionable **N/A**: every piece of durable state already has its own recovery story — Loki/Tempo blocks → MinIO erasure-coded (the lakehouse tier's backup), the Grafana state DB → streaming-replicated PG (RPO ≈ 0; pg_basebackup belongs to the grafana-pg DR runbook), dashboards + datasources → provisioned-as-code from `nexus-infra-observability` (re-applied, not snapshotted), and the Prometheus TSDB is intentionally ephemeral (HA = both Proms scrape every target; ADR-0038). Nothing is adapter-ownable to snapshot that isn't already durable or reproducible.

⁵⁷ observability `cert-rotate` **forces each node's own vault-agent to re-render its leaf** (the Swarm `pkiCert` pattern): back up + `rm` the rendered `bundle.pem`(s) — `pkiCert` persists+reuses the leaf otherwise — `systemctl restart nexus-vault-agent` → poll the re-render (the post-render splits `bundle.pem` → server.crt/server.key) → restore any `.bak` that didn't reappear → reload/restart the service(s). A prom node carries BOTH leaves (nexus-prometheus + nexus-alertmanager). **Cold-rebuild caught two cert-rotate bugs (both fixed):** (1) the original **build-host issue** (`IssuePkiCertAsync` + SSH-push) wrote leaves with **incomplete SANs** (dropped the RR-DNS aliases `prometheus`/`alertmanager`/`loki`/`tempo`/`otel.nexus.lab`) + a non-PKCS#8 key → broke smoke-0.I.{1,2,3,5}; the force-rerender re-issues from the on-node TEMPLATE (full SANs + PKCS#8). (2) **Loki/Tempo don't cleanly SIGHUP-reload a rotated cert** (a reload leaves them inactive) → they are **RESTARTED** (rolling; the ring tolerates it), while Prom/AM SIGHUP-reload fine. **grafana-pg is rotated too (GAP #5, v0.8.8) via the shared `PgSslCertRotator`** — STANDBY-FIRST then PRIMARY, force-rerender + a **SIGHUP `systemctl reload` postgresql@17-main** (not a restart): PG re-reads the leaf for new connections while existing sessions + the streaming-replication connection keep running (no drop). Old vs new wire serials (from `server.crt`) prove the rotation. Live-proven on the cold-rebuilt tier: new serials on all 12 rotatable service nodes + the grafana-pg pair; smoke-0.I.{1,2,3,5} green after.

⁵⁹ lakehouse `health` rolls all three engines + ZooKeeper into one report: MinIO `/minio/health/{live,cluster}` ×4 + `mc admin info` (mode online, 0 drives offline), Nessie mgmt `/q/health` per-check (the **"Warehouses Object Stores"** S3 check + the DB-connection check) + app `/iceberg/v1/config` ×2, the Spark ALIVE master + `aliveworkers` (queried on each master's **VMnet11** IP — the UI binds there, not loopback) + the worker count, the ZooKeeper quorum (1 leader + rest followers via `echo srvr | nc`), the iceberg-pg streaming replication, and the VRRP VIP. The endpoints are probed **over SSH with each node's own ca** (MinIO HTTPS validates `/etc/nexus-minio/certs/CAs/nexus-ca.crt`; Nessie mgmt + Spark UI are plain HTTP). The **`nessie-objectstore`** probe is the cross-tier S3 trust canary — DOWN under a CA split (old-root Nessie truststore vs new-root MinIO leaf → PKIX), UP once the tier is on the same Vault root as MinIO.

⁶⁰ lakehouse `topology` = 16 role-annotated nodes + the iceberg-db **VIP `.151` pseudo-node** (live holder) + the ZooKeeper leader/follower roles + the Spark ALIVE/STANDBY masters + the multi-master `spark://…:7077,…:7077` URL. Shards = null (the lakehouse tier is not data-sharded). Leaders/holders drift — the Spark ALIVE leader (`/json/` status) and the VIP holder are read live, never assumed.

⁶¹ lakehouse has **two** one-shot failovers. **`--direction spark-master`**: stops `nexus-spark-master` on the ALIVE leader → **ZooKeeper promotes the STANDBY** to ALIVE (the workers re-register), measured to the standby reaching ALIVE (RTO ≈ 31 s), then restarts the old leader as the new STANDBY — the live-proven HA drill. **`--direction iceberg-pg` (catalog-DB VRRP cutover `.151`; 0.L.2.1 fencing hardening — was N/A until 2026-07-08):** stop keepalived on the VIP-holding primary → its peer's `notify_master` promotes the standby (`pg_ctl promote`) → the adapter **deterministically fences + `pg_basebackup` re-seeds the OLD primary as a fresh streaming standby** (`/usr/local/sbin/nexus-iceberg-reseed.sh`, guarded so it can never wipe a live primary) → restart keepalived (nopreempt keeps the VIP on the new primary). RTO ≈ 2–3 s, ~8.5 s end-to-end; symmetric (re-run to fail back). **The two prior blockers were fixed in `nexus-infra-lakehouse` (0.L.2.1):** the `NEXUS-ICEBERG-HBA` block now exists on **both** nodes (a promoted standby admits the Nessie role — the catalog stays served, no crash-loop) and there is a fence/re-seed so there is no split-brain; keepalived `notify_fault` is the unattended-crash self-heal backstop. **Live-verified 2026-07-08** (4 drills both directions GREEN; Nessie `GET /api/v2/trees` → 200 after the cutover; see `docs/verification/0.L.2.1-iceberg-pg-failover-fencing.md`). MinIO (EC, no leader), Nessie (RR-DNS HA), ZooKeeper (its own Zab quorum) and the Spark workers have no operator-driven failover.

⁶² lakehouse `scale-out` = graceful actionable **N/A** for every role: the MinIO erasure set is FIXED at 4 (EC:2; the set size is baked at format time — growing it is a new server pool), and the Spark worker count + the iceberg-pg/Nessie pairs + the ZooKeeper ensemble are fixed-size IaC. Add capacity by adding the VM + overlay in `nexus-infra-lakehouse` and re-applying.

⁶³ lakehouse `backup` = **`mc mirror s3://warehouse`** (the Iceberg/Spark data bucket) to a node-local dir on a MinIO node + the object count/size; `restore` mirrors that back into a fresh `warehouse-restore-verify` bucket and counts (the integrity round-trip). The S3 store itself is already EC-durable, so this is a portable point-in-time copy + a proof. `backup restore` takes the tag **positionally**.

⁶⁴ lakehouse `cert-rotate` **forces each node's own vault-agent to re-render its leaf** (the Swarm `pkiCert` pattern: `cp -a`+`rm` the rendered `bundle.pem` → restart `nexus-vault-agent` → poll the re-render → restore the `.bak` if absent). **MinIO is re-certed BIG-BANG** — all 4 bundles re-rendered, then all 4 `nexus-minio` **restarted together** (a rolling 1-node re-cert breaks distributed MinIO's inter-node mTLS, the v0.8.3 lesson); **Nessie** re-renders + restarts per-node. **Spark + ZooKeeper are graceful N/A** (diagnosed live, the v0.8.4 cold-rebuild-caught bug): Spark has no rotatable server leaf — its RPC is **shared-secret + AES** (`spark.authenticate`/`spark.network.crypto`) and its only on-node trust material is the JVM truststore CA (the vault-agent renders `ca-bundle.crt`, not a per-node leaf); **ZooKeeper is backplane-only plaintext** (ADR-0035 — no TLS, no vault-agent). **iceberg-pg is rotated too (GAP #6, v0.8.8) via the shared `PgSslCertRotator`** — STANDBY-FIRST then PRIMARY, force-rerender + a **SIGHUP `systemctl reload` postgresql@17-main** (not a restart), so the streaming-replication connection + Nessie's live catalog connections are never dropped. New vs old wire serials on the MinIO/Nessie **and iceberg-pg** nodes prove the rotation; the verb exits non-zero on the Spark/ZK N/A rows. **Live-verified 2026-07-07:** iceberg-pg standby `.150` then primary `.149` both rotated, replication (`pg_stat_replication` streaming) intact after both. **Note:** `cert-rotate` restarts `nexus-nessie` (Quarkus + S3 cold start ~min), so a `status`/`health` run immediately after briefly shows the Nessie nodes `failed` until they settle — wait for `/q/health` 200 before the next read.

⁶⁵ lakehouse `chaos` runs `nexus-chaos.sh` on a **MinIO node** by default (the EC:2 set tolerates one node loss — `/minio/health/cluster` stays 200 while the node is down) and recovers via a `/minio/health/cluster` poll; `--target` may name any node (a process-kill on a Spark worker / Nessie node also works). The **iceberg-pg VIP holder and the ALIVE Spark master are spared** unless explicitly targeted.

⁶⁶ lakehouse `acl` = **MinIO policies + users** via the on-node `mc nexuslocal` alias. `list`/`describe` parse `mc admin policy ls` + `mc admin user ls --json`; `grant --user <key> --permissions <policy>` = `mc admin policy attach` (default `readwrite`); `revoke` = `mc admin policy detach`. The MinIO root + the `nexus-lakehouse-app` service identity are detach-protected. (`CanResizeVm` refuses the iceberg-pg VIP holder + the ALIVE Spark master.)

⁶⁷ registry `health` rolls the whole Harbor tier into one report: the Harbor `/api/v2.0/health` component checklist (**8/8** healthy — core/database/redis/registry/registryctl/jobservice/portal/trivy) ×2 app nodes + `/api/v2.0/systeminfo` `auth_mode` (= **`oidc_auth`**, the Vault-OIDC SSO signal) + the registry-pg streaming replication (1 streaming standby) + the Redis master/replica link + the **MinIO `s3://harbor` blob-backend canary** (HTTP 200) + the keepalived VRRP VIP `.119`. Endpoints are probed **over SSH with each node's own `/etc/nexus-registry/tls/ca.crt`** (Harbor HTTPS :443 via nginx); the Harbor admin password comes from Vault KV `nexus/registry/harbor-admin` (field `value`) via `INexusVaultClient`. **One live-caught bug fixed here:** the UNAUTHENTICATED `/systeminfo` omits `harbor_version` (admin-only), so the probe was re-gated on `auth_mode` (the meaningful unauthenticated SSO signal) → green.

⁶⁸ registry `topology` = the 4 `registry-*` nodes (harbor-app ×2 RR-DNS `registry.nexus.lab`; registry-pg primary+replica) + the `.119` VIP pseudo-node (`registry-db.nexus.lab`, live holder) + the MinIO `s3://harbor` blob-store. Shards = null (not data-sharded). The vms.yaml cluster is `platform-tools`; the adapter filters to the four `registry-*` members (the unbuilt prefect/unleash/marquez/backstage reservations classify `other` and are excluded).

⁶⁹ registry `failover-test cluster registry --direction registry-db` is a **real self-healing one-shot** (0.L.4.1 fencing hardening, 2026-07-08 — was "code-verified but DR-deferred"): stop keepalived on the `.119` holder → the peer's `notify_master` promotes PG (`pg_ctl promote`) + re-masters Redis → the **adapter then fences + `pg_basebackup` re-seeds the OLD primary as a streaming standby** (`/usr/local/sbin/nexus-registry-reseed.sh`, guarded so it can never wipe a live primary) → restart keepalived (nopreempt; its `notify_backup` `demote.sh` re-points the old node's Redis to the new master + is a no-op reseed). RTO ≈ 1.3–3 s; symmetric (re-run to fail back). **The prior split-brain gap was closed in `nexus-infra-registry` (0.L.4.1):** the `NEXUS-REGISTRY-HBA` block now on **both** nodes (a promoted standby admits the Harbor DB user) + the guarded reseed helper + `demote.sh` PG re-attach self-heal. **Live-verified 2026-07-08** (found split-brained; overlay re-apply fixed it; 2 drills both directions GREEN; the `harbor` role is admitted over TLS on the promoted node; see `docs/verification/0.L.4.1-registry-db-failover-reseed.md`). The **app tier needs no failover** (RR-DNS `registry.nexus.lab`; clients retry) — the verb refuses an app-direction with that pointer.

⁷⁰ registry `scale-out` = graceful actionable **N/A** (ADR-0036): the 2-node app pair (RR-DNS) + 2-node datastore pair (VRRP) is the fixed-HA standard; capacity scales by MinIO EC storage + vertical `scale-up`, not by adding registry nodes. Add capacity by adding the VM + overlay in `nexus-infra-registry` and re-applying.

⁷¹ registry `backup` = **`pg_dump` of the Harbor metadata DB** (`registry`: projects, repos, artifacts, users, robots, replication rules) on the PG primary → node-local gzip (49 tables); `restore` reloads into a throwaway `registry_restore_verify` DB and counts tables (a non-destructive round-trip, 49 tables, dropped). Blobs are EC-durable in MinIO `s3://harbor` and Redis is ephemeral cache — neither is adapter-snapshotted (the same "durable elsewhere" framing as obs/lakehouse). The adapter-ownable authoritative state is the Harbor **metadata**.

⁷² registry `cert-rotate` **forces each node's vault-agent to re-render its `pki_int` leaf** (the Swarm/obs `cp -a`+`rm bundle.pem` idiom: restart `nexus-vault-agent` → poll the re-render → restore the `.bak` if absent), then reloads per role: **nginx container restart** on the app nodes (picks up `harbor.crt`), **PG ssl reload** on the datastore nodes; the **VIP holder LAST**. Live-proven: fresh leaf serials on all **4 nodes**, 0 errors, ≈ 28.7 s (app nodes first, VIP holder `registry-pg-1` last).

⁷³ registry `chaos` runs the embedded `nexus-chaos.sh` on a **non-VIP node** by default (process-kill = `docker` on a Harbor app node → the RR pair tolerates one loss, `health` shows harbor-app 1/2); recovery = docker restart + `docker compose up -d` + a health poll. The datastore VIP holder + the PG primary are spared unless explicitly targeted. Live-proven: docker killed on registry-2 → impact observed → recovered; datastore/VIP/S3 unaffected.

⁷⁴ registry `acl` = **Harbor users** via `/api/v2.0/users` (admin from Vault KV); `list`/`describe` enrich each user with project + robot-account counts; `grant`/`revoke` toggle the **sysadmin flag** (`PUT /users/{id}/sysadmin`); the built-in `admin` is revoke-protected (break-glass). The grant/revoke API path, user-resolution, and the protected-admin guard are all verified; toggling a **real** target is deferred because Harbor in `oidc_auth` mode returns **403 on local-user creation** (users onboard via the AD→Vault-OIDC browser flow) — the same partial-proof as the obs `acl`.

⁵⁸ observability `acl` = Grafana **org** users via `/api/org/users` (admin basic-auth from Vault KV `value`; the `admin` login is revoke-protected); `grant`/`revoke` = PATCH `/api/org/users/<id> {role:Admin|Viewer}`. **Cold-rebuild-caught fix:** `/api/admin/users` 404s under basic auth even for a Grafana server admin (the server-admin route is hidden), so the verb uses the org-scoped endpoints. Pre-rebuild `acl list` correctly reported the Grafana admin-password drift (HTTP 401, with the `grafana-cli admin reset-admin-password <kv-value>` reconcile — a v0.8.1-greenfield casualty); the cold-rebuild re-initialised Grafana's admin from the current KV, so `list`/`grant`/`revoke` are now green. (`CanResizeVm` refuses the current `.184`/`.185` VIP holders.)

---

## §3 Operator runbooks

### §3.1 Cold-start the lab for a verify session
```pwsh
$vmrun = 'C:/Program Files/VMware/VMware Workstation/vmrun.exe'
# Base (in order), then the target cluster — sequentially, to avoid the vmrun power-on storm:
'00-edge\nexus-gateway','01-foundation\vault-transit','01-foundation\vault-1',
'01-foundation\vault-2','01-foundation\vault-3','01-foundation\dc-nexus' |
  % { & $vmrun start "H:\VMS\NexusPlatform\$_\$(Split-Path $_ -Leaf).vmx" nogui; Start-Sleep 4 }
1..6 | % { & $vmrun start "H:\VMS\NexusPlatform\05-oltp\redis-$_\redis-$_.vmx" nogui; Start-Sleep 4 }
# mongo-sharded (11 VMs) — power on in batches to avoid the vmrun power-on storm:
'mongo-cfg-1','mongo-cfg-2','mongo-cfg-3','mongo-shard-1-1','mongo-shard-1-2','mongo-shard-1-3',
'mongo-shard-2-1','mongo-shard-2-2','mongo-shard-2-3','mongo-mongos-1','mongo-mongos-2' |
  % { & $vmrun start "H:\VMS\NexusPlatform\05-oltp\$_\$_.vmx" nogui; Start-Sleep 4 }
# vitess (12 VMs, tier 07-vitess) — etcd first, then control/vtgate, then tablets:
'vitess-etcd-1','vitess-etcd-2','vitess-etcd-3','vitess-control-1','vitess-vtgate-1','vitess-vtgate-2',
'vitess-shard1-tablet-1','vitess-shard1-tablet-2','vitess-shard1-tablet-3',
'vitess-shard2-tablet-1','vitess-shard2-tablet-2','vitess-shard2-tablet-3' |
  % { & $vmrun start "H:\VMS\NexusPlatform\07-vitess\$_\$_.vmx" nogui; Start-Sleep 4 }
# citus (9 VMs, tier 08-citus) — etcd DCS first, then coordinator pair, then the 2 worker pairs:
'citus-etcd-1','citus-etcd-2','citus-etcd-3','citus-coord-1','citus-coord-2',
'citus-worker1-1','citus-worker1-2','citus-worker2-1','citus-worker2-2' |
  % { & $vmrun start "H:\VMS\NexusPlatform\08-citus\$_\$_.vmx" nogui; Start-Sleep 4 }
# swarm (6 VMs, tier 06-orchestration) — 3 managers then 3 workers (Portainer is a Swarm service, no VM):
1..3 | % { & $vmrun start "H:\VMS\NexusPlatform\06-orchestration\swarm-manager-$_\swarm-manager-$_.vmx" nogui; Start-Sleep 4 }
1..3 | % { & $vmrun start "H:\VMS\NexusPlatform\06-orchestration\swarm-worker-$_\swarm-worker-$_.vmx" nogui; Start-Sleep 4 }
# observability (14 VMs, tier 01-foundation) + the 4 lakehouse MinIO nodes (Loki/Tempo S3 backend) —
# power on in staggered batches to avoid the power-on storm; MinIO MUST be up for the S3 health probe:
'minio-1','minio-2','minio-3','minio-4' | % { & $vmrun start "H:\VMS\NexusPlatform\08-spark\$_\$_.vmx" nogui; Start-Sleep 4 }
'prom-1','prom-2','loki-1','loki-2','loki-3','tempo-1','tempo-2','tempo-3',
'grafana-1','grafana-2','grafana-pg-1','grafana-pg-2','otel-collector-1','otel-collector-2' |
  % { & $vmrun start "H:\VMS\NexusPlatform\01-foundation\$_\$_.vmx" nogui; Start-Sleep 4 }
# lakehouse (16 VMs, tier 08-spark) — staggered: ZooKeeper + MinIO first (Spark's coordinator + S3 backend),
# then Iceberg (Nessie + catalog PG), then Spark (masters depend on ZK + Nessie + MinIO):
'zookeeper-1','zookeeper-2','zookeeper-3','minio-1','minio-2','minio-3','minio-4',
'iceberg-pg-1','iceberg-pg-2','iceberg-rest-1','iceberg-rest-2',
'spark-master-1','spark-master-2','spark-worker-1','spark-worker-2','spark-worker-3' |
  % { & $vmrun start "H:\VMS\NexusPlatform\08-spark\$_\$_.vmx" nogui; Start-Sleep 4 }
# registry (4 VMs, tier 09-platform) + the 4 lakehouse MinIO nodes (Harbor's s3://harbor blob backend) —
# MinIO MUST be up first; then the datastore pair (PG + Redis + VIP), then the Harbor app pair:
'minio-1','minio-2','minio-3','minio-4' | % { & $vmrun start "H:\VMS\NexusPlatform\08-spark\$_\$_.vmx" nogui; Start-Sleep 4 }
'registry-pg-1','registry-pg-2','registry-1','registry-2' |
  % { & $vmrun start "H:\VMS\NexusPlatform\09-platform\$_\$_.vmx" nogui; Start-Sleep 4 }
```
Then **always** run §3.2 (the boot-race recovery) before expecting Vault-backed services. (mongo-sharded
needs `VAULT_*` set — the keyFile / operator password is read from Vault KV `nexus/oltp/mongo/keyfile`;
citus needs `VAULT_*` too — the operator password is read from Vault KV `nexus/citus/operator-password`;
**swarm** needs `VAULT_*` too — the Consul/Nomad mgmt tokens are read from Vault KV
`nexus/swarm/{consul,nomad}-bootstrap-token`. NOTE: if the swarm tier has been **offline > 168 h**, Consul
refuses to rejoin [`server_rejoin_age_max`] — cold-rebuild it via `pwsh scripts/swarm.ps1 cycle` in
nexus-infra-swarm-nomad, which also re-bootstraps + re-seeds those tokens. **registry** needs `VAULT_*`
too — the Harbor admin password is read from Vault KV `nexus/registry/harbor-admin` (field `value`) — and
its 4 lakehouse MinIO nodes must be up for the `s3://harbor` blob-backend health canary.)

### §3.2 TROUBLESHOOTING — "cluster verb returns nothing / cluster-status empty"
The diagnostic ladder (run top-down; each rung names the fix):

1. **Are the VMs running?** `& $vmrun list` → if not, §3.1.
2. **Is the node up + key auth working?**
   `ssh -i ~/.ssh/nexus_gateway_ed25519 nexusadmin@<ip> 'echo SSH_OK; hostname'`.
   No `SSH_OK` → VM still booting, or wrong key (use the **lab** key, not your personal `id_ed25519`).
3. **Is Vault sealed? (the most common cause after a host reboot — the transit boot-race.)**
   `ssh … vault-1 'VAULT_ADDR=https://127.0.0.1:8200 vault status -tls-skip-verify'`.
   Symptoms: nothing listening on 8200, or `journalctl -u vault` shows
   `Code: 503 … Vault is sealed` against `…/transit/encrypt/…`. vault-transit is Shamir-sealed and
   the HA nodes crash-loop until it's unsealed.
   **→ Recovery — CLI-native (v0.8.1, the declarative `IRecoverableCluster` verb):**
   ```pwsh
   nexus recover-ha vault --yes
   ```
   It unseals vault-transit from `~/.nexus/vault-transit-init.json` over SSH, `reset-failed` + `start vault`
   on vault-1/2/3, and polls until unsealed — idempotent (already-unsealed = no-op), the ONLY exposed unseal
   path. The original PowerShell script `pwsh -File nexus-infra-vmware/scripts/recover-vault-ha.ps1` still
   works (and additionally installs the StartLimit drop-in so the next reboot races more gracefully). Memory:
   `feedback_vault_transit_boot_race_recovery.md`.
4. **Is the engine service active?**
   `ssh … '<ip>' 'systemctl is-active nexus-<engine>'`. If `inactive`/`masked`: the **stock package
   unit is masked** — the cluster runs under the custom **`nexus-<engine>`** unit (e.g. `redis-server`
   is masked → `nexus-redis` owns it). Start it:
   `ssh … 'sudo systemctl reset-failed nexus-<engine>; sudo systemctl start nexus-<engine>'`
   (on the redis cluster: loop the 6 IPs `.81 .82 .83 .84 .87 .89`).
5. **Is the engine reachable + is the auth/cert contract right?** Probe the on-node CLI under `sudo`
   and read the config — never assume the auth model:
   ```bash
   sudo grep -E 'tls-port|port|tls-auth-clients|tls-(cert|ca)-file' /etc/nexus-redis/redis.conf
   sudo /usr/bin/redis-cli --tls --cacert /etc/nexus-redis/tls/ca.crt \
        --cert /etc/nexus-redis/tls/server.crt --key /etc/nexus-redis/tls/server.key PING
   ```
   **Redis reality (live-verified 2026-06-05):** `port 0` + `tls-port 6379` + `tls-auth-clients yes`
   ⇒ **mTLS-only, NO password** (`default … nopass`). The client cert+key are required; the CA file
   is `ca.crt` (not `ca.pem`); `/etc/nexus-redis/auth-password.txt` does **not** exist. Confirm the
   real cert filenames + auth model from `/etc/nexus-<engine>/` for every cluster — they differ.
   **Mongo reality (live-verified 2026-06-05):** unit `nexus-mongo` (stock `mongod` masked);
   `requireTLS` on 27017 with a **combined** `server.pem` (leaf+key) + `ca.crt` under
   `/etc/nexus-mongo/tls/`; keyFile internal auth + `authorization=enabled`. The operator CLI auths as
   **`nexus-cluster-admin`** whose password is in **Vault KV** (`nexus/oltp/mongo/operator-password`),
   so the verbs need `VAULT_ADDR`/`VAULT_TOKEN`/`VAULT_CACERT` set (a missing token yields an
   actionable error). On-node probe:
   ```bash
   KF=$(sudo cat /etc/nexus-mongo/keyfile | tr -d '\n')          # __system bootstrap identity (off-limits for ops)
   OPWD=$(VAULT_ADDR=https://192.168.70.121:8200 VAULT_SKIP_VERIFY=true \
          vault kv get -field=content nexus/oltp/mongo/operator-password)
   sudo mongosh --quiet --tls --tlsCAFile /etc/nexus-mongo/tls/ca.crt \
        --tlsCertificateKeyFile /etc/nexus-mongo/tls/server.pem \
        --username nexus-cluster-admin --password "$OPWD" --authenticationDatabase admin \
        'mongodb://mongo-1:27017,mongo-2:27017,mongo-3:27017/admin?replicaSet=nexus-rs' \
        --eval 'print(rs.status().ok)'
   ```
   If the operator user is missing (`Authentication failed`), re-run the oltp-mongo apply
   (`pwsh -File nexus-infra-oltp/scripts/oltp-mongo.ps1 apply`) — its `mongo_operator_user` overlay is
   idempotent (createUser → else converge roles). If Vault KV has no operator-password, apply the
   nexus-infra-vmware **security** env first (the seed + agent-policy v3 live there).

   **mongo-sharded reality (the SHARDED cluster; live-verified 2026-06-16; ClusterId `mongo-sharded`,
   distinct from `mongo`).** 11 nodes: config-server RS `config` (mongo-cfg-1/2/3 @ 27019, unit
   `nexus-mongo`) + shard RSes `shard-1`/`shard-2` (×3 @ 27018, `nexus-mongo`) + 2 `mongos` routers
   (mongo-mongos-1/2 @ 27017, unit **`nexus-mongos`**). **keyFile-only, NO TLS** in 0.N v1 (mTLS is the
   deferred 0.N.1 hardening, ADR-0040). **Two-headed auth — both use the keyFile content as the password**
   (Vault KV `nexus/oltp/mongo/keyfile` field `content`, so `VAULT_*` must be set):
   - **Direct mongod RS ops** (config + shards): `__system`@`local` (SCRAM-SHA-256). This is the ONLY
     principal the **shard** mongods accept (`nexus-sharded-admin` exists only on the config RS).
   - **Cluster-level ops** (sh.status, balancer, acl, backup): `nexus-sharded-admin`@`admin` **through a
     mongos** (`local` is rejected through mongos — *"Can't use 'local' database through mongos"*).

   On-node probes:
   ```bash
   KF=$(sudo cat /etc/nexus-mongo/keyfile)
   # shard RS member (direct mongod, __system):
   sudo mongosh --quiet --host 127.0.0.1:27018 --username __system --password "$KF" \
        --authenticationDatabase local --authenticationMechanism SCRAM-SHA-256 \
        --eval 'print(rs.status().set)'                       # -> shard-1 / shard-2
   # cluster view (through mongos, nexus-sharded-admin):
   sudo mongosh --quiet --host 127.0.0.1:27017 --username nexus-sharded-admin --password "$KF" \
        --authenticationDatabase admin \
        --eval 'printjson(db.getSiblingDB("config").shards.find().toArray()); print(sh.getBalancerState())'
   ```
   **Gotcha (live-caught):** mongosh `--eval` is wrapped in `--eval '...'`, so JS string literals MUST be
   **double-quoted** (`"config"`); single quotes terminate the shell quoting early (this bit the health
   query's `shards-registered` probe before the fix). `cert-rotate` is a graceful N/A (no TLS in v1).
   Cluster bring-up + cold-rebuild live in `nexus-infra-oltp` (handbook §1n/§3.N; `scripts/mongo-sharded.ps1`).

   **vitess reality (the Vitess-SHARDED MySQL cluster; live-verified 2026-06-17; ClusterId `vitess`).**
   12 nodes (tier 07-vitess): 3 etcd topo (vitess-etcd-1/2/3 @ .190-.192, unit `nexus-etcd`, cell `nexus`) +
   1 control (vitess-control-1 @ .193, `nexus-vtctld` + `nexus-vtorc`) + 2 vtgate (vitess-vtgate-1/2 @
   .194/.195, `nexus-vtgate`, MySQL `:15306`) + 2 shards ×3 tablets (vitess-shard1-tablet-1/2/3 @ .196-.198
   shard `-80`; vitess-shard2-tablet-1/2/3 @ .199-.201 shard `80-`; each `nexus-vttablet` + a Percona 8.4
   under `nexus-mysqlctld`). Keyspace `commerce`, table `customer`, hash vindex on `customer_id`; durability
   `none`. **Hybrid auth:** the control plane is mTLS-only (no password) via the preloaded wrapper
   `sudo /usr/local/sbin/nexus-vtctldclient`; the SQL plane uses the vtgate `:15306` mTLS listener as
   static-auth user `nexus` (password = Vault KV `nexus/vitess/mysql-app-password` field `content`, so
   `VAULT_*` must be set). Tablets report their **VMnet10** IP in the topo; primaries **drift off the lowest
   uid** — read them from `GetShard.primary_alias`.
   On-node probes:
   ```bash
   # control: topology (mTLS gRPC, no password) — the underlying mysqld db is vt_commerce:
   sudo /usr/local/sbin/nexus-vtctldclient GetTablets --keyspace commerce --format json
   sudo /usr/local/sbin/nexus-vtctldclient GetShard commerce/-80      # .shard.primary_alias.uid
   # etcd quorum (one call reports all 3 — count "is healthy" NOT bare "healthy"):
   sudo /usr/local/sbin/nexus-etcdctl endpoint health
   # SQL via vtgate from a TABLET node (mysql client + TLS leaf), mTLS as nexus:
   APP=$(sudo cat /etc/nexus-vitess/mysql-app-password)
   sudo env MYSQL_PWD="$APP" mysql --host=192.168.70.194 --port=15306 --user=nexus \
     --ssl-mode=REQUIRED --ssl-cert=/etc/nexus-vitess/tls/server-cert.pem \
     --ssl-key=/etc/nexus-vitess/tls/server-key.pem --ssl-ca=/etc/nexus-vitess/tls/ca.pem \
     --batch --skip-column-names 'commerce/80-' -e 'SELECT COUNT(*) FROM customer'   # -> 47
   ```
   **Gotchas (live-caught):** (1) `nexus-etcdctl endpoint health` reports ALL 3 endpoints from any one node
   (count `"is healthy"`, not bare `healthy` — that matches `unhealthy` too). (2) `mysqldump` must target
   **`vt_commerce`** (the `vt_`-prefixed mysqld db), NOT `commerce` (the keyspace name vtgate translates).
   (3) `nexus-chaos.sh process-kill` SIGSTOPs ONE unit — freeze `nexus-mysqlctld` on a primary (→ VTOrc
   auto-reparent), `nexus-vttablet` on a replica; never a space-separated pair. (4) `CREATE USER` via vtgate
   fails ("syntax error near 'USER'") → `acl` manages `vtgate_creds.json`, not SQL DDL. `cert-rotate` restarts
   vttablet-only on tablets (mysqld stays up → no reparent). Cluster bring-up + cold-rebuild live in
   `nexus-infra-vitess` (handbook §0-§3.1; `scripts/vitess.ps1`).

   **SQL Server FCI+AG reality (the first WINDOWS cluster; live-verified 2026-06-12).** Two ClusterIds
   over one vms.yaml cluster `sqlserver`: **`sqlserver`** (FCI) + **`sqlserver-ag`** (AG). The nodes are
   `ws2025-desktop`, reached over **Windows-SSH** (`nexusadmin`) — every remote command is
   `powershell -NoProfile -EncodedCommand <base64-UTF16>` (plain multi-token commands get mangled by
   cmd.exe). **Two access planes:** (a) WSFC/cluster-resource cmdlets (`Get-Cluster*`,
   `Move-ClusterGroup`, `Get-IscsiSession`) run over **plain SSH** as the local `nexusadmin` (it is
   cluster-admin on the local node); (b) **T-SQL** runs as the dedicated `nexus-cluster-admin` SQL login
   (sysadmin), password ONLY in **Vault KV** `nexus/oltp/sqlserver/operator-password` — so the verbs
   need `VAULT_ADDR`/`VAULT_TOKEN`/`VAULT_CACERT`. The FCI is mixed-mode (SQL-login auth); the standalone
   AG replicas are Windows-auth-only (`-E`, local nexusadmin IS sysadmin there). Contract: FCI virtual
   server `sqlfci` @ .16, WSFC CNO `sql-fci-cluster` @ .15, AG `nexus-ag`, Listener `sql-ag-listener`
   @ .17, demo DB `nexus_demo`, backups on `S:\Backups`. On-node probe (run a scheduled-task or use the
   adapter — local `sqlcmd -E` on the FCI is NOT sysadmin):
   ```pwsh
   # cluster plane (plain SSH, local nexusadmin):
   ssh -i ~/.ssh/nexus_gateway_ed25519 nexusadmin@192.168.70.11 "powershell -NoProfile -Command (Get-ClusterGroup -Name 'SQL Server (MSSQLSERVER)').State"
   # operator auth (from the build host, via the CLI — exercises Vault KV + the SQL login):
   nexus health sqlserver ; nexus health sqlserver-ag
   ```
   - **`health sqlserver-ag` shows a replica `CONNECTED NOT SYNCHRONIZING / NOT_HEALTHY`** (and
     `nexus_demo` absent on that replica) ⇒ a **failed automatic seed** (`sys.dm_hadr_automatic_seeding`
     → `failure_state=Seeding`). Root cause: automatic seeding can't work here — the FCI primary's
     `nexus_demo` files live on the shared iSCSI `S:\`, and seeding tries to recreate `S:\SQLData\*.mdf`
     on a replica that has only local `C:\`. **→ Named recovery (zero-touch, idempotent):**
     ```pwsh
     nexus scale-out remove sqlserver-ag <rep> --yes    # e.g. sql-ag-rep-2
     nexus scale-out add    sqlserver-ag --role replica --yes
     ```
     `scale-out add` re-seeds via **manual seeding** (backup → SFTP-ferry the .bak/.trn build-host-
     mediated → `RESTORE WITH MOVE … NORECOVERY` → `SET HADR AVAILABILITY GROUP`) → CONNECTED +
     SYNCHRONIZING. If the operator login is missing (`Login failed for user 'nexus-cluster-admin'`),
     apply `nexus-infra-oltp/envs/oltp-sqlserver` (the `role-overlay-sqlserver-operator-login.tf` is
     idempotent); if Vault KV has no operator-password, apply the nexus-infra-vmware **security** env
     first (the `…-sqlserver-cluster-creds-seed` v2 seed).
   - **`Move-ClusterGroup` hangs / `Login failed`** — you're in the wrong plane. Cluster cmdlets need
     **plain SSH** (NOT the schtasks domain-task context, where cluster-resource cmdlets hang); T-SQL
     needs the **SQL login** (NOT plain `sqlcmd -E` on the FCI = local nexusadmin = not sysadmin).
   - **cert-rotate:** `cert-rotate sqlserver` rotates the **one shared FCI cert** (both nodes, single
     cluster checkpoint — a per-node rotate would break failover); `cert-rotate sqlserver-ag` rotates
     the **two standalone replicas** per-node. ws2025 has no openssl — certs are issued via the build-
     host Vault HTTP API and shipped as a PFX over SFTP (`SqlServerCert`).

### §3.3 Verb behaviour notes + limitations
- `failover-test cluster redis` — **FIXED v0.6.0**: original primary resolved from the replica's
  live `INFO replication` master_host (was a hostname heuristic).
- `cert-rotate redis` — **FIXED v0.6.0**: issues a genuine fresh leaf via the node's own Vault token
  (`/run/nexus-vault-agent/token` → `pki_int/issue/redis-server`), because the Agent's `pkiCert`
  template caches the 90-day cert and won't rotate on a bare restart. **Limitation:** the on-node
  Agent re-asserts its cached cert on its next render — *persistent* rotation needs the Agent's
  `pkiCert` cache refreshed (shorten the leaf TTL, or re-run the redis-tls overlay). The verb rotates
  the live cert immediately (new serial + reload); the later revert is an infra coupling, not a CLI bug.
- `scale-out add redis` — new-VM provisioning rides the IaC growth var (`redis_extra_count`, a
  per-cluster Terraform follow-up); the role-aware join + the remove→re-add cycle are live-verified.
- **Mongo (`v0.6.1`) — engine gotchas caught only by live-verify:**
  - **`--eval` is single-quoted by the remote shell**, so every embedded JS literal must use
    **double** quotes (`print("OK")`). Single-quoted JS mangles the script (`SyntaxError`).
  - **`mongodump` is scoped by the URI database path** — a `/admin` path dumps only admin system
    collections; target `/nexus_smoke?…&authSource=admin`. A `readPreference=secondary` dump returned
    **0 documents** against this RS, so `backup take` reads from the PRIMARY.
  - **`mongorestore` ns-remap needs `--nsInclude`** to *select* the namespace before `--nsFrom`/
    `--nsTo` rename it — without it, 0 docs restored. `backup restore` also **discovers which node
    holds the (node-local) archive** and runs there.
  - `cert-rotate mongo` — same `pkiCert`-cache caveat as Redis (immediate rotation + reload; persistent
    rotation needs the Agent cache refreshed). Rolling `systemctl restart nexus-mongo`, one member at
    a time (RS tolerates a single member down).
  - `failover-test cluster mongo` runs `rs.stepDown(60)` on the PRIMARY (which holds the old primary
    down 60s); RTO ≈ 2.8s live. `scale-out remove` refuses the current PRIMARY (step it down first).
- **Percona (`v0.6.2`) — Galera + ProxySQL gotchas caught only by live-verify:**
  - **Two control planes:** cluster state on the PXC nodes (`SHOW STATUS LIKE 'wsrep_%'` via
    `nexus-cluster-admin` over mTLS :3306), routing state in ProxySQL admin (`:6032` →
    `runtime_mysql_servers`). `status`/`topology` map a node's role from its ProxySQL hostgroup;
    operator + ProxySQL-admin passwords both come from Vault KV.
  - **ProxySQL `SHUNNED` rows:** a node lingers in the writer hostgroup (10) as SHUNNED while it
    actually serves from backup_writer (20) — read **ONLY `ONLINE` rows** to derive the real writer,
    else all 3 look like the writer.
  - **`scale-out add` discovery:** an exact `is-active` match — `"inactive".Contains("active")` is
    `true`, so a substring check sees a *stopped* node as joined ("all already joined").
  - **`backup`:** `mysqldump --skip-add-locks --no-tablespaces --single-transaction` — PXC
    `strict_mode=ENFORCING` rejects the explicit `LOCK TABLES` mysqldump emits by default (the restore
    fails `ERROR 1105` between LOCK/UNLOCK → 0 rows). Restore strips `USE`/`CREATE DATABASE` into a
    verify schema.
  - **`failover-test`** = ProxySQL writer failover (stop the hostgroup-10 writer, poll for a promoted
    backup_writer); RTO ≈ 2.3s live. `cert-rotate` rolls all 5 nodes (PXC one at a time; Galera
    tolerates a single member restart). `scale-out remove` refuses the current writer.
- **Patroni / postgres (`v0.6.3`) — PG + etcd + HAProxy gotchas caught only by live-verify:**
  - **Three control planes:** Patroni (`patronictl list`/`switchover`), etcd (RBAC — `nexus-etcdctl
    --user root:<pw>`, password read on-node via the etcd node's own agent token), HAProxy VIP `.60`
    (routes `:5432` to the current leader via `httpchk GET /leader`). Operator connects as
    `nexus-cluster-admin` over TLS+scram to a node's **VMnet11 IP** (not 127.0.0.1, which is
    pg_hba `trust`); writes target the VIP.
  - **`failover-test` 403 "client certificate required":** Patroni REST `verify_client: optional`
    **requires** a client cert for POST `/switchover`. The fix is a **`ctl:` block** in patroni.yml
    (`cacert`/`certfile`/`keyfile` = the node's own TLS; the server cert doubles as the client cert),
    baked into `role-overlay-patroni-bootstrap.tf`. Also: **patronictl exits 0 even on a refused
    switchover** — the adapter validates the `"Successfully switched over"` banner. RTO ≈ 4.6s live,
    measured at the VIP; auto-switches back.
  - **`backup`:** `pg_dump -t nexus_smoke --no-owner --no-privileges` (the dump's `OWNER TO nexusops`
    would fail under the non-owner operator). **Restore goes into a fresh DATABASE the operator OWNS**
    (it has CREATEDB) — NOT a schema-in-postgres: the operator's `pg_*_all_data` grants are DATA, not
    DDL, so `CREATE SCHEMA` in db postgres is denied. items restored = 3 live.
  - **`cert-rotate`:** all 8 nodes from the single PKI role `patroni-server` whose only allowed domain
    is **`patroni.nexus.lab`** (a foreign domain like `etcd.nexus.lab` 500s "common name not allowed
    by this role"). Per-role apply: PG **reloads** (SIGHUP picks up `ssl_cert_file` — no restart),
    etcd **restarts**, haproxy **reloads**; the PG **leader rotates last**.
  - **`scale-out`** start/stop `nexus-patroni` on a replica (rejoin → streaming / graceful leave;
    refuses the leader). **`chaos process-kill`** kills `nexus-patroni` on a replica + restarts it to
    rejoin. `status` renders etcd as `dcs`, haproxy as `router` (VIP holder `router*`).
  - **Cold-rebuild gotcha (HAProxy):** the `nexus-haproxy.service` unit runs `User=haproxy`, which is
    incompatible with a `chroot` directive in `haproxy.cfg` (chroot needs root/CAP_SYS_CHROOT → a fresh
    node 500s "Cannot chroot"). Fixed by dropping `chroot` from the rendered cfg
    (`nexus-infra-oltp` `role-overlay-haproxy-config.tf` v3); `User=haproxy` + `RuntimeDirectory=` are
    the privilege drop + tmpfs `/run` dir. Surfaced by the v0.6.3 cold-rebuild.
- **ClickHouse / clickhouse (`v0.6.4`) — sharded + Keeper gotchas (analytics tier):**
  - **Two control planes + two leaders that aren't the same thing:** the SQL/data plane
    (`clickhouse-client --secure --accept-invalid-certificate --port 9440` — the `--accept-invalid-certificate`
    is required; the lab CA's IP-SAN chain fails strict validation) and the **Keeper** coordination
    plane (`echo mntr | nc 127.0.0.1 9181` → `zk_server_state`). The data plane is **leaderless**
    (3 shards × 2 replicas, every replica writable); the cluster's only leader is the **Keeper RAFT
    leader**. `status`/`topology` render that as `keeper-leader`; the CH `remote_servers` cluster name
    is **`nexus_analytics`** (≠ the adapter ClusterId `clickhouse`).
  - **`acl` / operator creation — `access_management` is NOT a `SETTINGS` value** (CH 26.5 →
    `Code 115 UNKNOWN_SETTING`). A SQL-created user gets access-management from the **`GRANT ALL`
    privilege group**, so the operator `nexus-cluster-admin` is `CREATE USER … ON CLUSTER` (no SETTINGS)
    + `GRANT ALL ON *.* WITH GRANT OPTION`. Hyphenated identifiers are backtick-quoted.
  - **`failover-test` = Keeper RAFT leader re-election** (not a data-plane move — there's no single
    write endpoint). Stop `nexus-clickhouse-keeper` on the leader (3-of-3 → 2-of-3, still quorate),
    poll the survivors' `mntr` for the new leader; **RTO ≈ 1.1s** live (fastest of the data tier);
    restart → rejoins as follower.
  - **`backup`:** native `BACKUP TABLE nexus.events_local TO Disk('analytics_backups', '<id>.zip')`
    (the shared NFS repo, x-tier ADR-0032) → `RESTORE … AS nexus.events_restore_verify` → count. The
    `{uuid}` zk path on `events_local` means `RESTORE AS` doesn't collide (no `REPLICA_ALREADY_EXISTS`).
    items restored = 211 live (shard1's local slice of the 600-row Distributed table).
  - **`cert-rotate`:** all 9 nodes from the single PKI role `clickhouse-server`, one allowed domain
    **`clickhouse.nexus.lab`** (no domain-mismatch trap — unlike Patroni's etcd). The key must be
    **PKCS#8** (Vault issues PKCS#1 → `openssl pkcs8 -topk8`); `ca.crt` = issuing intermediate **+** the
    Vault-Agent root anchor (OpenSSL needs the self-signed root). `systemctl restart`, **data nodes
    first / Keeper leader last** (its restart re-elects).
  - **`scale-out`** start/stop `nexus-clickhouse-server` on a data node (ReplicatedMergeTree rejoins +
    drains its queue via Keeper / graceful leave); `remove` refuses a shard's **last live replica**.
    **`chaos process-kill`** kills the server on a replica + restarts → rejoin. `CanResizeVm` refuses
    the current Keeper leader.
  - **Cold-rebuild gotchas (analytics-clickhouse, surfaced at the v0.6.4 cold-rebuild):** (1) the env
    carried the **stale x86 `vmrun_path`** in clone_vm state — fixed the variables.tf default to the
    non-x86 path; power off the VMs cleanly via the correct path BEFORE `terraform destroy` (the
    clone_vm destroy-provisioner's `Remove-Item` catch-all only cleans dirs if the VMs aren't holding
    .vmdk locks). (2) **operator-user ↔ backup-repo IaC race:** both overlays only depended on
    schema-bootstrap, so they ran in parallel — backup-repo's `systemctl restart
    nexus-clickhouse-server` on all 6 nodes killed the operator-user's clickhouse-client mid-DDL
    (rc=138). Fixed: operator-user now `depends_on` backup-repo. A warm cluster hides this (the operator
    is created by hand after restarts settle).
- **StarRocks / starrocks (`v0.6.5`) — MPP warehouse gotchas (analytics tier):**
  - **One control surface = the FE query port `:9030`** via `mysql --skip-ssl -h 127.0.0.1 -P 9030 -u
    nexus-cluster-admin` (the **`--skip-ssl` is required** — the deb13 MariaDB 11.8 client otherwise
    negotiates a TLS the FE query port doesn't enforce → "SSL is required, but the server does not
    support it"). The operator password rides **`MYSQL_PWD`** (no argv exposure, no warning). Root is
    password-auth (Vault KV).
  - **`SHOW FRONTENDS`/`SHOW BACKENDS` report the VMnet10 backplane IP** (.10.x), not the service IP —
    map to a node via vms.yaml's vmnet10. The **FE leader is dynamic** (`Role=LEADER`; the bootstrap
    name `sr-fe-leader` may be a follower). `topology` Shards=null — StarRocks shards by tablet hash
    (`DISTRIBUTED BY HASH BUCKETS` × `replication_num`); the BE TabletNum is the sharding evidence.
  - **`failover-test` = FE leader re-election** (BDB-JE). Stop `nexus-starrocks-fe` on the LEADER →
    poll `SHOW FRONTENDS` *from a surviving FE* for a new LEADER; **RTO ≈ 1.5 s** live; restart →
    rejoins as follower.
  - **`backup` = genuine async `BACKUP SNAPSHOT … TO nexus_backups ON (events)`** → poll `SHOW BACKUP`
    until State=FINISHED. `restore` reads the snapshot `backup_timestamp` (`SHOW SNAPSHOT`), runs
    `RESTORE SNAPSHOT … AS events_restore_verify PROPERTIES("backup_timestamp"=…, "replication_num"=1)`,
    polls `SHOW RESTORE` → FINISHED, counts. ~19 s take / ~22 s restore. 60 rows live.
  - **`cert-rotate`** all 6 from `pki_int/issue/starrocks-server` (one domain `starrocks.nexus.lab`),
    PKCS#8, `systemctl restart`, **BE first / FE leader last**. **`acl`** = `SHOW USERS` + `SHOW GRANTS
    FOR` (no `mysql.user`); grant = `CREATE USER … + GRANT … ON nexus.*`. **`scale-out`** stop/start
    `nexus-starrocks-be` (remove refuses dropping below 2 live BE). **`chaos process-kill`** kills the
    BE + restarts → rejoin. `CanResizeVm` refuses the FE leader.
  - **Cold-rebuild gotchas (analytics-starrocks, surfaced at the v0.6.5 cold-rebuild):** same stale-x86
    `vmrun_path` trap as ClickHouse — fix the variables.tf default to the non-x86 path + power off the
    VMs cleanly before `terraform destroy`. Two **operator-recovered VMware transients** on the fresh
    clones: (1) the sporadic vmrun "Unknown error" power-on (re-run the apply — tainted retries clean);
    (2) **a fresh FE clone booting with no service-NIC IP** (running per vmrun, but unreachable via
    SSH/ping — the known StarRocks transient, analytics handbook §3.B S7) → `vmrun connectNamedDevice
    <vmx> ethernet0/ethernet1` + `vmrun reset <vmx> hard`; it rejoins in ~85 s and the parked
    nftables-backplane overlay (waiting on all 6 within its 25-min window) proceeds. The operator-user
    overlay's `depends_on backup-repo` (added from the ClickHouse lesson) means no operator/backup race.
- **Kafka / kafka-east · kafka-west · kafka-ecosystem (`v0.6.7`) — KRaft mTLS gotchas:**
  - **mTLS-only, no password.** Every CLI runs ON a broker as `sudo /opt/kafka/bin/kafka-*.sh
    --bootstrap-server SSL://<vmnet10>:9092 --command-config /etc/nexus-kafka/client-ssl.properties`.
    **Bootstrap with the broker's own VMnet10 backplane IP** — `ssl.endpoint.identification.algorithm=
    https` requires the bootstrap host to be a cert SAN (the VMnet10 IP is; a random IP/hostname fails the
    handshake). `sudo` is mandatory (`/etc/nexus-kafka` is `0750 root:kafka`).
  - **CLI-flag trap:** the admin tools (`kafka-topics`/`kafka-acls`/`kafka-metadata-quorum`/`kafka-configs`)
    take **`--command-config`**, but `kafka-console-producer` takes **`--producer.config`** and
    `kafka-console-consumer` takes **`--consumer.config`**. Passing `--command-config` to a console tool
    silently prints usage + processes nothing (looked like "MM2 stopped mirroring" until diagnosed).
  - **`acl` needs the authorizer.** If `acl <cluster> list` errors `SecurityDisabledException: No
    Authorizer is configured`, the StandardAuthorizer overlay hasn't been applied — run
    `nexus-infra-kafka`'s `kafka.ps1 apply` (it carries `role-overlay-kafka-acl-authorizer.tf`,
    `var.enable_kafka_acl_authorizer=true`). **`super.users` = all 15 platform principals** (6 brokers +
    9 ecosystem); never trim it to just brokers or the ecosystem services lose broker access.
  - **`failover-test cluster kafka-east|kafka-west` = controller-leader move** (`kafka-metadata-quorum`):
    stop `kafka.service` on the leader → poll a survivor for a new `LeaderId` → **RTO ≈ 4.5 s** → restart
    + wait rejoin (lag 0). This is the per-cluster drill; **`failover-test cluster kafka`** is the
    unchanged cross-region MM2 east↔west DR.
  - **`cert-rotate` is rolling** (one broker at a time, KRaft tolerates 1 down): re-issue from the node's
    own agent token `pki_int/issue/kafka-broker` → write `bundle.pem` → `/usr/local/sbin/kafka-tls-split.sh`
    (PKCS#1→PKCS#8 + assembles keystore.pem; on ecosystem nodes also rebuilds the `.p12`) → restart.
  - **`scale-out add … --role broker`** rejoins a stopped broker (drained by `scale-out remove`, the
    failover leader, or a chaos victim). All 3 up → it explains the fixed-quorum apply-on-demand path.
  - **Live ports (corrected from the scoping note):** Kafka Connect REST = **:8083** (not 8088), ksqlDB
    REST = **:8088** (not 8090); Schema Registry :8081, REST Proxy :8082. `kafka-ecosystem health` probes
    these over HTTPS with `--cacert /etc/ssl/certs/kafka-ca.pem`.
  - **Cold-rebuild gotchas (kafka env):** same stale-x86 `vmrun_path` trap in
    `nexus-infra-kafka/terraform/envs/kafka/variables.tf` (line ~35) — fix the default to the non-x86 path
    before a from-zero apply; the `role-overlay-kafka-acl-authorizer.tf` overlay runs after `kafka_tls`
    (depends_on) and rolling-restarts; watch the standard VMware power-on transients (vmrun "Unknown
    error" → re-run; fresh-clone no-NIC-IP → `vmrun connectNamedDevice` + `reset`).

### §3.4 AOT size gate
≤30 MB (linux-x64 + win-x64) for the 0.G line (ADR-0024). `pwsh -File scripts/cli.ps1 size-check`.
Recorded per release in `docs/verification/0.G.N-<cluster>.md`.

### §3.5 Batch-3 verb playbooks (scale-up · swarm guarded restore · kafka resize-gate)

Human-readable mirrors of the batch-3 System B demos (input · expected · **where observed** · output ·
prerequisites). Each was live-verified on the dates noted; the JSON demos are the auto-runnable form.

#### §3.5.1 `scale-up` — vertical CPU/RAM resize (round-trip) · demo `DEMO-17-redis-scale-up`
- **Prereqs:** the target's OLTP tier up (redis 6 VMs); Windows build host with `vmrun`; `NEXUS_VMS_YAML`,
  `NEXUS_SSH_KEY`. The target must be a **live replica** (or pass `--force-primary`) — vms.yaml labels
  redis-1 the shard1 primary by design, but Redis Cluster roles drift; the gate reads the live role.
- **Step 1 — confirm the live role.** Input: `nexus status redis --json`. Expected: the target's
  `role` is `replica`. Observed: **stdout**.
- **Step 2 — resize up.** Input: `nexus scale-up redis-1 --cpu 4 --ram 3072 --yes --json`. Expected:
  `"outcome":"ok"`, `"oldCpu":2`→`"newCpu":4`, `"oldRamMb":2048`→`"newRamMb":3072`, `durationSec` ~30–60.
  Observed: **stdout** (the JSON), the guest (`ssh nexusadmin@192.168.70.81 'nproc; free -m'` → 4 CPUs /
  ~3072 MB), the **VMware Workstation library** (redis-1 = 4 proc / 3 GB).
- **Step 3 — resize back down.** Input: `nexus scale-up redis-1 --cpu 2 --ram 2048 --yes --json`.
  Expected: `"newCpu":2`, `"newRamMb":2048` (re-running with the same values → `"outcome":"skipped"`).
- **Proves:** a real, bidirectional vertical resizer (atomic `.vmx` edit + cold restart) that resizes a
  replica with no impact to the shard's surviving primary. **Live-verified 2026-07-05 on redis-1.**

#### §3.5.2 `scale-up --disk` — honest deb13 root-not-last warning · demo `DEMO-160-scale-up-disk-deb13`
- **Prereqs:** as §3.5.1 + `vmware-vdiskmanager` on PATH (resolved by `VmrunPaths`).
- **Step 1 — baseline.** Input: `ssh nexusadmin@192.168.70.81 'lsblk; findmnt -no SIZE /'`. Observed:
  **stdout** — disk ~40 GiB, root FS size.
- **Step 2 — grow the disk.** Input: `nexus scale-up redis-1 --disk 42 --yes --json`. Expected:
  `"outcome":"ok"`, `"newDiskGb":42`, and `outcomeReason` = *"vmdk grown to 42 GB, but the in-guest root
  filesystem was NOT auto-extended: root … a swap/extended partition likely follows it (root is not the
  last partition …)"*. Observed: **stdout**.
- **Step 3 — confirm honesty.** Input: `ssh … 'lsblk; findmnt -no SIZE /'`. Expected: the **disk** is
  ~42 GiB but the **root FS is unchanged** — matching the warning. Observed: **stdout**.
- **Proves:** the vmdk grows but the guest FS is left alone and reported truthfully (Outcome `ok` +
  warning) rather than faking a resize — the never-repartition-a-live-boot-disk contract. **Follow-up
  shipped:** deb13 preseed now uses a swapfile so root is the single growable partition; future clones
  auto-extend. **Live-verified 2026-07-05 on redis-1 (40→42 GB).**

#### §3.5.3 `scale-up` cluster-safety gate — refuse the Kafka controller-leader · demo `DEMO-161-kafka-resize-gate`
- **Prereqs:** kafka-east 3 VMs up; `VAULT_ADDR`/`VAULT_TOKEN`/`VAULT_CACERT`. kafka-east-1 is the
  current controller-leader (KRaft leadership drifts — else target the leader from step 1).
- **Step 1 — identify the leader.** Input: `nexus status kafka-east --json`. Expected: exactly one member
  `role=controller-leader`. Observed: **stdout**.
- **Step 2 — attempt to resize the leader.** Input: `nexus scale-up kafka-east-1 --ram 6144 --yes --json`.
  Expected: **REFUSED, exit 2** — *"'kafka-east-1' is the current primary/leader of cluster 'kafka-east';
  resizing it now would disrupt the write window. Fail over first, or pass --force-primary to override."*
  The VM is **not** powered off. Observed: **stdout**. (The meta `kafka` adapter routes the gate to the
  region owner by vm-name match; locked by a unit test.)
- **Follower + override (documented, not auto-run — it cold-restarts a live broker):** a controller
  **follower** passes the gate — `nexus scale-up kafka-east-2 --ram <same-as-current> --yes` → `skipped`;
  and `--force-primary` overrides the leader refusal: `nexus scale-up kafka-east-1 --ram 6144
  --force-primary --yes`.
- **Proves:** scale-up refuses to power-cycle the KRaft controller-leader (protecting the write window)
  and honours `--force-primary`. **Live-verified 2026-07-06 on kafka-east.**

#### §3.5.4 Swarm guarded `backup restore --confirm-destructive` · demo `DEMO-162-swarm-restore-confirm-destructive`
- **Prereqs:** swarm 6 VMs up (smoke-0.E.4e GREEN); `VAULT_*`; Consul/Nomad ACL bootstrap tokens in KV;
  `pwsh` on PATH. **DESTRUCTIVE** — overwrites live Consul KV + Nomad jobs in place.
- **Step 1 — the guard.** Input: `nexus backup restore swarm <any-id> --yes` (no
  `--confirm-destructive`). Expected: **REFUSED, exit 2** — *"swarm restore OVERWRITES the live Consul KV
  + Nomad job state in place — refused without an explicit opt-in. Re-run with --confirm-destructive …"*.
  The guard fires ahead of the backup-id lookup, so even a bogus id is refused. Observed: **stdout**.
- **Step 2 — the GREEN restore (self-restore of a fresh snapshot).** Input (pwsh captures the take's id):
  `pwsh -NoProfile -Command "$j=(nexus backup take swarm --tag restoredemo --json | ConvertFrom-Json).backupId; nexus backup restore swarm $j --yes --confirm-destructive; exit $LASTEXITCODE"`.
  Expected: a GREEN `backup restore …` with `items restored : N` (restored Consul KV keys + Nomad jobs).
  Observed: **stdout**; on a manager, `consul kv export | grep -c key` matches the count.
- **Proves:** a real, guarded, online restore — refused without the extra opt-in, GREEN with it (runs
  `consul`/`nomad snapshot restore` against the leader). **Live-verified 2026-07-06 on the swarm tier.**

### §3.6 Infra-hardening verb playbooks (pre-Phase-1)

#### §3.6.1 lakehouse `failover-test cluster lakehouse --direction iceberg-pg` — catalog-DB VRRP cutover + fence/re-seed · demo `DEMO-167-lakehouse-failover-iceberg-pg`
- **Prereqs:** iceberg-pg-1 (.149) + iceberg-pg-2 (.150) up as a healthy 1-primary + 1-streaming-standby
  pair (VIP iceberg-db.nexus.lab .151); `NEXUS_SSH_KEY`/`NEXUS_SSH_USER`/`NEXUS_VMS_YAML`. Add
  iceberg-rest-1/2 (Nessie) only to OBSERVE the catalog staying served. MinIO **not** required. Depends
  on the `nexus-infra-lakehouse` 0.L.2.1 overlay (pg_hba on both nodes + `nexus-iceberg-reseed.sh` +
  keepalived `notify_fault`) — re-apply `role-overlay-iceberg-pg-replication.tf` first if a node lacks
  `/usr/local/sbin/nexus-iceberg-reseed.sh` or the `NEXUS-ICEBERG-HBA` block.
- **Step 1 — pre-state.** Input: `ssh nexusadmin@.149 "sudo -u postgres psql -tAc 'SELECT pg_is_in_recovery()'"`
  (= `f`, holds the VIP) and `.150` (= `t`, `pg_stat_wal_receiver` streaming). Observed: **SSH/psql** — exactly
  1 primary + 1 streaming standby. If BOTH read `f` you have a split-brain — re-apply the overlay to re-seed
  the second node before drilling.
- **Step 2 — the failover.** Input: `nexus failover-test cluster lakehouse --direction iceberg-pg --yes`.
  Expected: GREEN `vrrp-cutover:iceberg-pg`, `original primary`/`new primary` swap, `recovery = recovered`,
  hint *"… re-seeded as a streaming standby of …"*, a 5-instant timeline (RTO ≈ 2–3 s, ~8.5 s total).
  Observed: **stdout**.
- **Step 3 — no split-brain.** Input: re-query `pg_is_in_recovery()` + `pg_stat_wal_receiver` on both nodes.
  Expected: the roles have swapped — the OLD primary is now `t` (streaming standby), the NEW primary is `f`
  and holds the VIP; the new primary's `pg_stat_replication` shows the old primary's backplane IP streaming.
  Observed: **SSH/psql**. Re-run Step 2 to fail back (symmetric).
- **Step 4 — Nessie stays served (the pg_hba fix).** Input (with Nessie up): `curl -sk https://iceberg.nexus.lab:19120/api/v2/trees`.
  Expected: **HTTP 200** with the branch list AFTER the cutover; `pg_stat_activity` on the new primary then
  shows a `nessie/192.168.70.147|148` connection. Observed: **curl + SSH/psql**. Direct proof: a
  `psql "host=<new-primary> dbname=nessie user=nessie sslmode=require"` connection is admitted on the
  promoted node (pre-0.L.2.1 it was refused — no pg_hba entry for `nessie` on the standby).
- **Safety:** running `sudo /usr/local/sbin/nexus-iceberg-reseed.sh <src>` **on the node holding the VIP**
  refuses (`REFUSE: this node holds VIP …`, exit 3) — the helper can never wipe a live primary.
- **Proves:** a real one-shot catalog-DB failover (was graceful N/A) — VRRP cutover + deterministic fence +
  `pg_basebackup` re-seed with no split-brain, and a promoted standby that serves the Nessie catalog.
  **Live-verified 2026-07-08** (4 drills both directions GREEN). See `docs/verification/0.L.2.1-iceberg-pg-failover-fencing.md`.

#### §3.6.2 registry `failover-test cluster registry --direction registry-db` — datastore VRRP cutover + fence/re-seed · demo `DEMO-168-registry-failover-registry-db`
- **Prereqs:** registry-pg-1 (.117) + registry-pg-2 (.118) up as a healthy 1-primary + 1-streaming-standby
  pair (VIP registry-db.nexus.lab .119, carrying PG :5432 + Redis :6379); `NEXUS_SSH_KEY`/`NEXUS_SSH_USER`/
  `NEXUS_VMS_YAML`. The Harbor app nodes (registry-1/2) are RR-DNS and only needed to observe Harbor
  end-to-end (not required for the drill). Depends on the `nexus-infra-registry` 0.L.4.1 overlay
  (pg_hba on both + `nexus-registry-reseed.sh` + `demote.sh` PG re-attach) — re-apply
  `role-overlay-registry-pg-replication.tf` first if a node lacks the reseed helper or the HBA block.
- **Step 1 — pre-state.** Input: `ssh nexusadmin@.117 "sudo -u postgres psql -tAc 'SELECT pg_is_in_recovery()'"`
  (= `f`, holds VIP) and `.118` (= `t`, streaming). Observed: **SSH/psql** — 1 primary + 1 standby. If BOTH
  read `f` you have a split-brain (as found 2026-07-08) — re-apply the overlay to re-seed the second node.
- **Step 2 — the failover.** Input: `nexus failover-test cluster registry --direction registry-db --yes`.
  Expected: GREEN `vrrp-cutover:registry-db`, `original primary`/`new primary` swap, `recovery = recovered`,
  hint *"… re-seeded as a streaming standby of … + its Redis re-pointed to the new master"*, timeline RTO
  ~1.3–3 s. Observed: **stdout**.
- **Step 3 — no split-brain + Redis re-attach.** Input: re-query `pg_is_in_recovery()` + `pg_stat_wal_receiver`
  + `redis-cli … info replication | grep role` on both nodes. Expected: roles swapped — old primary now `t`
  (streaming standby) + `role:slave`, new primary `f` + VIP + `role:master`; `pg_stat_replication` on the new
  primary shows the old primary streaming. Observed: **SSH/psql + redis-cli**. Re-run Step 2 to fail back.
- **Step 4 — Harbor DB admitted on the promoted node (the pg_hba fix).** Input: a
  `psql "host=<new-primary> dbname=registry user=harbor sslmode=require"` connection is admitted on the
  promoted node (pre-0.L.4.1 it was refused — no pg_hba entry for `harbor` on the standby). Observed:
  **psql** (the harbor password is at KV `nexus/registry/harbor-db-password`). Harbor's core reconnects
  through the VIP the same way (RR-DNS app nodes retry).
- **Safety:** `sudo /usr/local/sbin/nexus-registry-reseed.sh <src>` **on the VIP holder** refuses
  (`REFUSE: this node holds VIP …`, exit 3) — the helper can never wipe a live primary.
- **Proves:** a real self-healing one-shot datastore failover (was DR-deferred) — VRRP cutover + PG promote
  + Redis re-master + deterministic fence/`pg_basebackup` re-seed of the old primary (no split-brain).
  **Live-verified 2026-07-08** (2 drills both directions GREEN). See `docs/verification/0.L.4.1-registry-db-failover-reseed.md`.
