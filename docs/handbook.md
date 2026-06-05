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
- **What it does:** **vertical** resize of a single VM — vmrun stop → edit `.vmx` CPU/RAM → start →
  (disk) guest `lvextend`/`resize2fs`. Cluster-aware: each adapter's `CanResizeVm` **refuses a
  current primary** unless `--force-primary`.
- **Input:** vm name; `--cpu`/`--ram`/`--disk`; `--force-primary`. **Output:** old→new sizing.
- **Where observed:** stdout; `nexus infrastructure status <cluster>`. **Mutating** (reboots the VM).
- **Prereqs:** Windows build host (vmrun). Generic `IVmResizer` — not per-adapter.

### `backup take <cluster> [--tag T]` / `backup restore <cluster> <id> [--yes]` — MUTATOR
- **What it does:** engine-native dump (Redis `BGSAVE`; Mongo `mongodump`; Patroni `pg_basebackup`;
  CH `BACKUP TO`; SQL `BACKUP DATABASE`; …) to a backup store; restore reverses it and **verifies a
  row/key round-trip**. *(Redis store = node-local snapshot — NFS is not mounted on redis nodes;
  central destination is a documented option.)*
- **Input:** cluster id; `--tag`; (restore) backup id + `--yes`. **Output:** backup id · destination
  · size · duration / items-restored.
- **Where observed:** stdout; the snapshot file on the node. **Proves:** data can be captured and
  restored intact. **restore is DESTRUCTIVE.** *(Redis live-verified 2026-06-05.)*

### `cert-rotate <cluster> [--yes]` — ROTATE
- **What it does:** forces a fresh TLS leaf per node (re-issue via Vault Agent) and reloads the
  engine; reports old→new serial per node.
- **Input:** cluster id; `--yes`. **Output:** per-node old/new serial table.
- **Where observed:** stdout; `sudo openssl x509 -in /etc/nexus-<engine>/tls/server.crt -serial`.
- **Proves:** certs rotate without downtime. *(Redis: verb runs across all 6 nodes; KNOWN ISSUE —
  a bare Vault-Agent restart only re-renders near expiry, so serials came back UNCHANGED. Fix
  pending: force re-issue (remove the rendered leaf, then restart so the Agent must re-issue).)*
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
  *(Implementation in progress — helper authored, adapter wiring pending.)*

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

---

## §2 Verb × cluster status

| Cluster | status | health | topology | failover | scale-out | scale-up | backup | cert-rotate | chaos | acl |
|---|---|---|---|---|---|---|---|---|---|---|
| redis | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ gen | ✅ 06-05 | ✅ 06-05 | ✅ 06-05 | ✅ list |
| mongo · percona · postgres · clickhouse · starrocks · sql-* · mongo-sharded · vitess · citus | ⏳ per canon order | | | | | | | | | |

✅ live-verified · ⚠ coded, fix pending · ⏳ pending. Dates = live-verify date.

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
```
Then **always** run §3.2 (the boot-race recovery) before expecting Vault-backed services.

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
   **→ Recovery script (idempotent, autonomous):**
   ```pwsh
   pwsh -File nexus-infra-vmware/scripts/recover-vault-ha.ps1
   ```
   It unseals vault-transit from `~/.nexus/vault-transit-init.json`, kicks vault-1/2/3, and installs
   a StartLimit drop-in so the next reboot races more gracefully. Memory:
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

### §3.4 AOT size gate
≤30 MB (linux-x64 + win-x64) for the 0.G line (ADR-0024). `pwsh -File scripts/cli.ps1 size-check`.
Recorded per release in `docs/verification/0.G.N-<cluster>.md`.
