# ADR-0010 — Phase 0.G.1 / v0.6.0: cluster-adapter implementation patterns (scale-out provisioning · chaos framework · backup model) + the Redis adapter exemplar

- **Status**: Accepted
- **Date**: 2026-06-05
- **Deciders**: Greg Zapantis
- **Related**: [ADR-0009](./ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) (the `IClusterAdapter` SPI + extended System B demo spec), [ADR-0008](./ADR-0008-kafka-failover-demo-grade-via-ssh.md) (the SSH + on-node-CLI shell-out pattern), [ADR-0007](./ADR-0007-ssh-net-managed-client.md) (SSH.NET), [`nexus-platform-plan` ADR-0024](https://github.com/grezap/nexus-platform-plan/blob/main/docs/adr/ADR-0024-aot-gate-amendment-and-cluster-adapter-framework.md) (≤30 MB gate + framework rationale)

## Context

ADR-0009 declared the `IClusterAdapter` SPI and shipped the framework + a `RedisAdapter`
with **6 verbs implemented** (status / failover / health / topology / cert-rotate / acl-read /
`CanResizeVm`) and **5 stubbed** (scale-out add/remove, backup take/restore, chaos). That
framework has sat in `[Unreleased]` with no tag or verification doc since `v0.5.0`.

Phase 0.G/0.H now builds **10 more concrete adapters** behind the same SPI, in canon order
(Redis → Mongo-RS → Percona → Patroni → ClickHouse → StarRocks → SQL-FCI/AG → mongo-sharded →
Vitess → Citus), **one per-adapter full-surface release each** (decision with Greg, 2026-06-05).
Three verbs were left as stubs in Redis precisely because they need a *cross-adapter pattern
decision* before they should be implemented 11 times. This ADR makes those three decisions
(the patterns every adapter reuses) and records the Redis adapter as the exemplar. Per-cluster
ADRs 0011+ reference this one for the shared patterns and record only engine-specifics.

## Decision

### 1. Scale-out provisioning model — IaC provisions, the adapter joins (role-aware)

**Greg's directive (2026-06-05): "the most stable solution … lively add a node to the cluster
and, if applicable, pinpoint the role of the node" (e.g. StarRocks FE / BE / CN).** The most
stable way to materialize a node identical to every other is the **cold-rebuild-proven
Terraform/Packer graph** — never a clone+firstboot hand-rolled inside the AOT binary (which
would re-implement DHCP pinning, NIC/backplane firstboot, Vault-Agent enrolment, and TLS
issuance fragilely, and is explicitly out of the ADR-0024 "SSH-shell-out to on-node CLIs"
invariant).

Therefore `scale-out add/remove` splits cleanly:

- **Provisioning (stability anchor) = apply-on-demand against the proven IaC graph**
  (Greg's choice, 2026-06-05 — "the most stable solution" + add an *arbitrary* number of
  nodes). `scale-out add --count N` shells out to the cluster's own operator script
  (`pwsh -File <repo>/scripts/<cluster>.ps1 apply -Vars "<role>_extra_count=N"`), which grows
  the cluster by N **brand-new** nodes minted *exactly like a cold-rebuild node* — fresh
  IP/MAC/DHCP pin, firstboot IP-map, Vault PKI `allowed_domains` + per-host AppRole, TLS leaf.
  `scale-out remove` runs the same path with the lower count (Terraform destroys the drained
  node). The node count is **unbounded** (`--count` any value); the only one-time per-cluster
  prep is reserving an **IP/MAC number-range** for growth in `variables.tf` (just numbers — no
  idle VMs; VMs materialize only on apply). This rides the proven graph verbatim, so a
  scaled-out node is as solid as every other node; cost is ~1–2 min/node (a real VM clone). The
  CLI never hand-rolls a `vmrun clone` + firstboot itself. *(Optional fast-path, per cluster:
  a couple of pre-reserved powered-off spares for instant single-node demos — not the default.)*
- **Cluster membership (the adapter's job) = role-aware SSH-shell-out.** `ScaleOutAddRequest.Role`
  pinpoints the role and drives the engine-native join + rebalance: Redis `--cluster add-node`
  (`--cluster-slave --cluster-master-id` for a replica; new-shard primary + `--cluster reshard`
  for a primary), Mongo `rs.add()`, Galera SST join, Patroni replica auto-join, ClickHouse add
  replica/shard, **StarRocks `ALTER SYSTEM ADD {FRONTEND|BACKEND|COMPUTE NODE}`**, Vitess add
  tablet/shard + reshard, Citus `citus_add_node` + online `rebalance_table_shards`. `remove`
  is the inverse: drain/reshard-away → engine-forget (`del-node` / `removeShard` /
  `DROP BACKEND` / `citus_remove_node`) → power the slot off.

This keeps the CLI a pure orchestrator (no managed drivers, no IaC re-implementation), gives
the real "live add a node with a role" capability, and inherits the lab's stability from the
proven graph. `scale-up` (vertical resize) stays the generic `IVmResizer` per ADR-0009 — it
already edits the `.vmx` via vmrun and consults `CanResizeVm`.

### 2. Chaos-injection framework — an on-node helper with idempotent, timed auto-heal

`ApplyChaosAsync` is the one verb that is **not** a thin shell-out to an existing CLI — there
is no engine-native "inject a fault" command. Decision: a small, dependency-free **on-node
helper `nexus-chaos.sh`** (installed/refreshed over SSH on first use; idempotent), invoked with
a scenario + a duration. Scenarios (matching `ChaosScenario.ScenarioType`):

| Scenario | Mechanism | Auto-heal |
|---|---|---|
| `network-partition` | `nft add` a drop rule for the peer/backplane subnet on VMnet10 (per `feedback_nftables_runtime_add_after_drop.md`: patch + `nft -f`, not a bare runtime `add`) | timed `nft` rule delete + ruleset reload |
| `packet-loss` / `slow-disk` (latency) | `tc qdisc add … netem loss <pct>%` / `delay <ms>` on the NIC | `tc qdisc del` |
| `cpu-starve` | a bounded `stress-ng`/busy-loop (or `nice`-pinned hog) | process self-exits at duration |
| `memory-pressure` | bounded `stress-ng --vm` | process self-exits at duration |
| `process-kill` | `systemctl kill`/`kill -STOP` the engine unit | `systemctl start`/`kill -CONT` |

Contract (zero-touch, idempotent, the standing delivery bar): every scenario is **time-boxed and
self-reverting** — the helper schedules its own undo at `DurationSeconds` (so a dropped SSH
session never leaves the lab wedged), AND the adapter issues an explicit lift at the end. The
adapter measures impact via `HealthAsync` probes during the window and reports
`ChaosOutcome.Recovered` = cluster returned to green after the scenario lifted. The helper +
this contract are net-new and live in this repo's adapter layer; first consumer is Redis.

### 3. Backup model — engine-native dump → gateway NFS, restore + verify

`backup take/restore` use the engine's native dump to the gateway NFS export (already mounted
across the fleet), no managed driver: Redis per-primary `BGSAVE` + scp `dump.rdb`; Mongo
`mongodump`; Percona `xtrabackup`/`mysqldump`; Patroni `pg_basebackup`/`pg_dump`; ClickHouse
`BACKUP TO Disk(...)`; StarRocks `BACKUP SNAPSHOT`; SQL `BACKUP DATABASE`; Vitess/Citus per-shard
logical dump. `restore` is the inverse and **verifies a row/key/document round-trip** so the
verb proves the data came back, not just that files moved. `BackupResult`/`RestoreResult`
already carry size / items-restored / duration.

### 4. Redis adapter (the exemplar) + the ≤30 MB gate

The Redis adapter (`src/Nexus.Cli.Adapters/Cluster/RedisAdapter.cs`, cluster id `redis`,
nodes redis-1..6 = shard1/2/3 × primary+replica at .81/.82/.83/.84/.87/.89) finishes its 5
stubbed verbs using the patterns above. The AOT exit gate for the 0.G line is **≤30 MB** per
ADR-0024 — `scripts/cli.ps1` (`$MaxSizeMB` default) + the CI `size-check` are updated from 25
to 30 as part of v0.6.0 (the v0.5.0 ≤25 MB gate stays sealed historically; 22.75 MB was met).

## Consequences

### Positive
- **One stable scale-out pattern for 11 adapters.** Provisioning never leaves the proven graph;
  the adapter only does role-aware membership. Maximally stable per Greg's directive.
- **Chaos is bounded + self-healing** — a lost SSH session can't strand the lab; zero-touch holds.
- **No managed DB drivers** anywhere (NetArchTest-enforced); AOT stays ~flat per adapter.
- **Per-cluster ADRs stay short** — they reference this one for the shared patterns.

### Negative
- **Each cluster needs a small IaC addition** (an `<role>_extra_count` growth var + a reserved
  IP/MAC number-range + firstboot/PKI coverage for that range). Real per-cluster work, but it's
  first-class treatment and rides the proven graph. Tracked per adapter.
- **`scale-out add` is a minutes-long op** (it runs a real Terraform apply / VM clone) — the
  command's 15-min CTS covers it; output is streamed so the operator sees progress.
- **The chaos helper is bespoke code** (not a shell-out) — gets its own unit tests + is the one
  net-new surface to maintain across engine differences (nft/tc availability per template).

### Neutral
- **Apply-on-demand is the default; pre-reserved instant spares are an optional per-cluster
  fast-path** (not the default). Recorded per adapter in ADR-0011+.
- **`scale-up` stays generic** (`IVmResizer`) — orthogonal to this ADR.

## Verification
- Redis: live-verify all 11 verbs against the powered-on `redis` cluster; capture into
  `docs/verification/0.G.1-redis.md` (incl. a reversible scale-out add→remove of the scale-slot
  node and a chaos inject→auto-heal round-trip). AOT size recorded ≤30 MB.
- NetArchTest (`tests/.../ClusterAdapterTests.cs`): RedisAdapter implements `IClusterAdapter`;
  no managed-driver type referenced.
- System B demos `DEMO-10..23` become executable + self-verifying (`expectedExitCode` +
  `expectedOutputContains`) via `nexus demo run`.
