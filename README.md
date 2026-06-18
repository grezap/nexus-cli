# nexus-cli

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/publish-Native%20AOT-blue)](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
[![License](https://img.shields.io/badge/license-MIT-green)](./LICENSE)
[![Blueprint](https://img.shields.io/badge/blueprint-nexus--platform--plan-orange)](https://github.com/grezap/nexus-platform-plan)
[![Phase](https://img.shields.io/badge/phase-0.P%20v0.7.3%20%E2%9C%85%20citus%20adapter%20live-brightgreen)](./CHANGELOG.md)

The operator surface for the **NexusPlatform lab** (88 VMs built through Phase 0.L.4) — a single ≤25 MB Native AOT binary that introspects, drives, and recovers the lab's Tier-1 (Vault, AD, gateway) and Tier-2 (Docker Swarm + Nomad + Consul + Portainer) control planes. No raw `terraform`, no `vault` CLI, no `docker stack` for daily ops; one tool, predictable verbs, panic buttons everywhere.

> **Canon:** This repo implements [Phase 0.F](https://github.com/grezap/nexus-platform-plan/blob/main/MASTER-PLAN.md) (line 156) of the NexusPlatform blueprint. Read [`nexus-platform-plan`](https://github.com/grezap/nexus-platform-plan) first to understand the lab the CLI talks to.
>
> **New to the tool stack (Vault, Consul, Nomad, Portainer)?** See the [tool stack glossary](https://github.com/grezap/nexus-platform-plan/blob/main/docs/glossary.md) for plain-English definitions of each.
>
> **Current state (v0.7.3):** **v0.7.3 = `CitusAdapter`** (ClusterId **`citus`**, Phase 0.P): the
> Citus-sharded PostgreSQL cluster with **full Patroni HA** — 3 etcd DCS + a coordinator Patroni pair (VIP
> `.211` = `pg_dist_node` groupid 0) + 2 worker Patroni pairs (VIPs `.212`/`.213` = groupid 1/2); PG 17 +
> Citus 14.1; `events` hash-distributed on `tenant_id` (32 shards = 16+16 across the worker groups) +
> colocated `event_tags` + reference `tenants`. **Citus = Patroni HA per group + Citus distribution.**
> Operator = the dedicated `nexus-cluster-admin` role (ADR-0011 Vault-KV model, `nexus/citus/operator-password`)
> auto-propagated to the workers with a `~postgres/.pgpass` entry so **distributed queries run as the operator**
> via the coordinator VIP (mTLS+scram+client-cert). Verbs — status · health (etcd quorum **unioned across
> nodes** + per-group HA + operator mTLS + registered workers + the **sharding proof** 16/16 + distributed
> aggregate 800) · **topology (Shards populated — per-worker-group placements)** · failover (graceful
> `patronictl switchover` on a chosen group, RTO≈1.6s; the VIP follows the leader so `pg_dist_node` is
> untouched) · scale-out (Patroni-member add/remove; worker-group growth = apply-on-demand) · backup (operator
> `COPY … TO STDOUT` round-trip of the distributed dataset, 800 events) · cert-rotate (per-node Vault PKI, PG
> reload / etcd restart, coord-leader-last) · acl (PG roles via the operator; `CREATE ROLE` propagates to
> workers) · chaos (process-kill `nexus-patroni`). AOT **26.71 MB / 30**; 137/137 tests; adapter code
> first-try-green on every verb (the one live-caught issue was infra — the patroni.yml `ctl:` block — fixed +
> baked). See [`docs/verification/0.7.3-citus.md`](./docs/verification/0.7.3-citus.md) + ADR-0021. Remaining:
> the 5 non-data tiers, per the canon order.
>
> <details><summary>v0.7.2 VitessAdapter (sealed)</summary>
>
> **v0.7.2 = `VitessAdapter`** (ClusterId **`vitess`**, Phase 0.O): the Vitess-sharded MySQL/Percona cluster
> (3 etcd topo + vtctld/VTOrc + 2 vtgate + 2 shards ×3 tablets; keyspace `commerce` split `-80`/`80-` by a
> hash vindex on `customer_id`). Hybrid operator identity (mTLS gRPC via `nexus-vtctldclient` + the vtgate
> `:15306` SQL plane). `topology` Shards populated; failover = `PlannedReparentShard` (RTO≈0.17s); backup =
> logical `mysqldump` per shard (101 rows); acl = the vtgate static-auth file; chaos = SIGSTOP → VTOrc
> auto-reparent. AOT 26.52 MB; 114/114 tests; 3 live-caught bugs. See ADR-0020.
> </details>
>
> <details><summary>v0.7.0 base + v0.7.1 MongoSharded (sealed)</summary>
>
> **v0.7.0** sealed the Phase 0.G data-tier `IClusterAdapter` expansion (the
> 0.G exit gate — 9 adapter families: Redis · Mongo · Percona · Patroni · ClickHouse · StarRocks · SQL
> Server FCI+AG · Kafka ×2 + ecosystem — each live-verified + cold-rebuild-proven; aggregate AOT validated
> ≤30 MB). The **0.7.x line** now adds the **sharded** adapters. **v0.7.1 = `MongoShardedAdapter`**
> (ClusterId **`mongo-sharded`**, Phase 0.N): the genuinely-sharded MongoDB cluster — 3 config-server RS
> (27019) + 2 shard RSes ×3 (27018) + 2 `mongos` routers (27017), distinct from the 0.G.2 `mongo` RS.
> **Two-headed keyFile auth** (both using the shared keyFile content as the password): `__system`@`local`
> for direct mongod RS ops (config + shards — the only principal the shard mongods accept) and
> `nexus-sharded-admin`@`admin` **through a mongos** for cluster-level ops (`local` can't be used through
> mongos). Verbs — status · health (per-RS quorum + mongos tier + shard-registration/balancer) · **topology
> (Shards populated — the sharded showcase)** · failover (**shard-primary stepdown**, per-shard RTO≈2.8s) ·
> scale-out (shard RS member add/remove) · backup (`mongodump`/`mongorestore` **through mongos** round-trip,
> 200 docs) · acl (config-server admin users via mongos) · chaos (kill a shard secondary) · **cert-rotate =
> graceful N/A** (no TLS in 0.N v1; mTLS is the deferred 0.N.1 hardening). AOT **26.30 MB / 30**; 97/97
> tests; 1 live-caught bug (fixed). See [`docs/verification/0.7.1-mongo-sharded.md`](./docs/verification/0.7.1-mongo-sharded.md)
> + ADR-0019.
>
> </details>
>
> <details><summary>v0.6.x data-tier adapters (sealed)</summary>
>
> Phase 0.G/0.H **data-tier adapter expansion** — the last data tier,
> **Kafka**, is now full-verb. **Redis** (v0.6.0, mTLS-only) + **Mongo** (v0.6.1) + **Percona XtraDB
> Cluster + ProxySQL** (v0.6.2, Galera) + **PostgreSQL Patroni HA** (v0.6.3) + **ClickHouse** (v0.6.4,
> sharded + Keeper RAFT) + **StarRocks** (v0.6.5, MPP warehouse) + **SQL Server FCI/AG** (v0.6.6, first
> Windows cluster) + **Kafka** (v0.6.7) all ship with every verb green against their running clusters.
> **Kafka (v0.6.7)** promotes the v0.5 failover-only retrofit to the full surface: one parameterized
> `KafkaClusterAdapter` registered twice — **`kafka-east`** + **`kafka-west`** (3 combined
> broker+controller KRaft nodes each) — plus a `KafkaEcosystemAdapter` (**`kafka-ecosystem`**: Schema
> Registry, REST, Connect, ksqlDB, MirrorMaker 2); the v0.5 **`kafka`** adapter stays as the cross-region
> MM2 DR meta-cluster. **mTLS-only — no operator password, no `INexusVaultClient`** (like Redis): the
> operator identity is the broker's own Vault-PKI keystore via `sudo kafka-*.sh --command-config`; **no
> managed `Confluent.Kafka`** (NetArchTest). Kafka verbs — status · health (quorum + voter lag + ISR) ·
> topology (topics×partitions/RF) · failover (**controller-leader move**, RTO≈4.5s; complements the MM2
> DR) · scale-out (broker drain/rejoin) · backup (topic round-trip) · cert-rotate (rolling reissue) ·
> **acl** (the KRaft `StandardAuthorizer`, enabled by a new cold-rebuild-proven overlay) · chaos. AOT
> **26.18 MB / 30 MB** gate; 86/86 tests; live-verified zero-bug against both clusters + the ecosystem.
> See [`docs/handbook.md`](./docs/handbook.md) for the verb reference + troubleshooting runbook, and
> [`docs/verification/0.6.7-kafka.md`](./docs/verification/0.6.7-kafka.md) for the live evidence. The
> remaining adapters (mongo-sharded → Vitess → Citus → the 5 non-data tiers) land per the canon order.
>
> </details>
>
> **Phase 0.F (v0.5.0) remains closed: all 5 of 5 master-plan verbs ship.** `cluster-status` (v0.1), `infrastructure {list, status, suspend, resume}` (v0.2.x), `failover-test {consul-leader, nomad-leader, swarm-manager}` (v0.3.x), `demo {list, run, record}` (v0.4.0), and **`kafka failover {east-to-west, west-to-east}`** (v0.5.0; ADR-0008 — region-loss DR via vmrun-suspend × 3 source brokers + produce/consume round-trip on the target + vmrun-resume). Verified live: consul 1.55s · nomad 2.716s · swarm-manager 21.59s · kafka east→west 13.20s · kafka west→east 13.57s — all RTOs auto-recovered, all under their master-plan budgets.

## What's in here

| Layer | Tech | Purpose |
|---|---|---|
| **Entry + UX** | Spectre.Console.Cli 0.50 + .NET 10 | Verb routing, table rendering, help text, AOT publish root |
| **Domain** | `Nexus.Cli.Core` (lib) | Interfaces (`INexusConsulClient`, `INexusNomadClient`, …), `Result<T>`, response records |
| **Adapters** | `Nexus.Cli.Adapters` (lib) | `HttpClient` factory pinned to the operator's CA bundle, source-gen JSON, Vault token resolver |
| **Tests** | xUnit + NetArchTest | Layer-dependency rules, JSON contract round-trips, env-var resolver permutations |
| **Distribution** | GitHub Releases | `linux-x64.tar.gz` + `win-x64.zip` attached to every tag — single static binary |

## Commands

| Command | Status | Slice |
|---|---|---|
| `nexus cluster-status` | ✅ v0.1.0 | Live HTTPS to Consul + Nomad + Portainer; tabular health summary |
| `nexus infrastructure list` | ✅ v0.2.0 | Whole-fleet table from `vms.yaml` decorated with live VMware state |
| `nexus infrastructure status <cluster>` | ✅ v0.2.0 | Single-cluster (or single-node via `--node`) state view |
| `nexus infrastructure suspend <cluster>` | ✅ v0.2.0 | `vmrun suspend` with confirm prompt + per-VM glyph; aliased as `suspend-cluster` |
| `nexus infrastructure resume <cluster>` | ✅ v0.2.0 | `vmrun start <vmx> nogui` for every stopped/suspended VM in scope |
| `nexus failover-test consul-leader` | ✅ v0.3.0 | SSH the current Consul leader, stop, measure raft re-election RTO, auto-recover |
| `nexus failover-test nomad-leader` | ✅ v0.3.1 | Same shape against the Nomad raft; verified 2.716s RTO |
| `nexus failover-test swarm-manager` | ✅ v0.3.2 | HOST-LEVEL outage via vmrun-suspend + SSH+docker discovery; verified 21.59s RTO |
| `nexus demo list` | ✅ v0.4.0 | Enumerate demos in the catalog (JSON files under `docs/demos/` or `NEXUS_DEMOS_PATH`) |
| `nexus demo run <id>` | ✅ v0.4.0 | Sequence a demo's shell-command steps; capture exit + stdout/stderr tails |
| `nexus demo record <id>` | ✅ v0.4.0 | Generate VHS `.tape` + render to GIF via the `vhs` binary (graceful fallback if vhs isn't installed) |
| `nexus kafka failover east-to-west` | ✅ v0.5.0 | Vmrun-suspend the 3 kafka-east brokers, prove kafka-west keeps serving via RF=3 produce/consume round-trip, vmrun-resume; live RTO **13.20 s** (60 s gate) |
| `nexus kafka failover west-to-east` | ✅ v0.5.0 | Symmetric: vmrun-suspend the 3 kafka-west brokers; live RTO **13.57 s**. The more demo-worthy direction (ecosystem stays up) |

**Data-tier cluster verbs (v0.6.x — ADR-0009 `IClusterAdapter` SPI)** — one adapter per cluster,
SSH-shell-out to the on-node CLI, no managed DB drivers. **Redis + Mongo + Percona + Patroni +
ClickHouse + StarRocks + SQL Server FCI + SQL Server AG are live (v0.6.0 → v0.6.6)** — the SQL Server
pair (v0.6.6) is the first **Windows** cluster (Windows-SSH + `sqlcmd`); the remaining adapters land per
the canon order.

| Verb | Status | What it does |
|---|---|---|
| `nexus status <cluster>` | ✅ redis · mongo · percona | per-cluster members + live roles + health |
| `nexus health <cluster>` | ✅ redis · mongo · percona | per-node probes (replication lag, etc.) |
| `nexus topology <cluster> [--watch]` | ✅ redis · mongo · percona | shard/replica map |
| `nexus failover-test cluster <cluster>` | ✅ redis · mongo · percona | controlled primary loss + measured RTO |
| `nexus cert-rotate <cluster>` | ✅ redis · mongo · percona | issue a fresh TLS leaf per node + reload |
| `nexus acl <cluster> <list\|describe\|grant\|revoke>` | ✅ redis (read) · mongo · percona (list+grant) | inspect / mutate access control |
| `nexus backup take\|restore <cluster>` | ✅ redis · mongo · percona | engine-native snapshot + restore round-trip |
| `nexus scale-out add\|remove <cluster>` | ✅ redis · mongo · percona | role-aware live cluster-membership change |
| `nexus scale-up <vm>` | ✅ generic | vertical VM resize (cluster-aware; refuses primaries) |
| `nexus chaos <cluster> <scenario>` | ✅ redis | time-boxed, self-reverting fault injection |

Clusters: `redis` · `mongo` · `percona` · `postgres` · `clickhouse` · `starrocks` ·
`sqlserver` (FCI) · `sqlserver-ag` (AG) · `mongo-sharded` · `vitess` · `citus` · `kafka`. See
[`docs/handbook.md`](./docs/handbook.md) §1 for the analytical per-verb reference.

Run `nexus --help` for the live verb list against the binary you have installed.

## Quickstart

```pwsh
# 1) Authenticate to Vault first (operator's existing flow). nexus-cli reads
#    VAULT_TOKEN/VAULT_ADDR/VAULT_CACERT from your environment.
$env:VAULT_ADDR   = 'https://192.168.70.121:8200'
$env:VAULT_CACERT = "$HOME\.nexus\vault-ca-bundle.crt"
vault login -method=ldap username=nexusadmin

# 2) Run cluster-status
.\nexus.exe cluster-status

# 3) JSON for scripting
.\nexus.exe cluster-status --json | ConvertFrom-Json

# 4) Drive Workstation VMs via vms.yaml (v0.2)
$env:NEXUS_VMS_YAML = "$HOME\src\nexus-platform-plan\docs\infra\vms.yaml"
.\nexus.exe infrastructure list                           # whole fleet
.\nexus.exe infrastructure status foundation              # one cluster
.\nexus.exe infrastructure suspend foundation --yes       # vmrun suspend
.\nexus.exe infrastructure suspend-cluster foundation --yes  # alias
.\nexus.exe infrastructure resume  foundation --yes
```

Expected output (live 0.E.4 cluster, 2026-05-07):

```text
─── Cluster status ─────────────────────────────────  ● GREEN ───
Consul     6 alive · 0 left · leader: swarm-manager-1
Nomad      3 servers alive · 3 clients ready · leader: swarm-manager-1
Portainer  1 manager-pinned replica · 6 agents · API 200 OK
```

## Install

### v0.1.0 — GitHub Releases tarball

```pwsh
# Windows
$ver = '0.1.0'
Invoke-WebRequest "https://github.com/grezap/nexus-cli/releases/download/v$ver/nexus-cli-$ver-win-x64.zip" -OutFile nexus.zip
Expand-Archive nexus.zip -DestinationPath C:\Tools\nexus-cli
$env:Path += ';C:\Tools\nexus-cli'
```

```bash
# Linux
ver=0.1.0
curl -sSL "https://github.com/grezap/nexus-cli/releases/download/v$ver/nexus-cli-$ver-linux-x64.tar.gz" | tar xz -C /usr/local/bin
nexus --version
```

`winget` and `.deb` are deferred to v0.2.

## Build from source

Prerequisites: .NET 10 SDK (`global.json` pins 10.0.100), pwsh 7+ on Windows.

```pwsh
git clone https://github.com/grezap/nexus-cli
cd nexus-cli
pwsh -File scripts\cli.ps1 publish -Rid win-x64
.\artifacts\win-x64\nexus.exe --version
```

Verbs supported by `scripts/cli.ps1`: `build`, `publish`, `test`, `lint`, `clean`, `size-check`. `-Rid all` does both `linux-x64` + `win-x64`.

## Configuration

`nexus-cli` reads only environment variables — no config files, no embedded creds.

| Variable | Required | Purpose |
|---|---|---|
| `VAULT_TOKEN` | `cluster-status`, `failover-test` | Operator's Vault token (from `vault login`) |
| `VAULT_ADDR`  | `cluster-status`, `failover-test` | e.g. `https://192.168.70.121:8200` |
| `VAULT_CACERT` | `cluster-status`, `failover-test` (or `NEXUS_CA_BUNDLE`) | Path to PEM bundle of the lab root CA |
| `NEXUS_CA_BUNDLE` | no | Override; same shape as `VAULT_CACERT` |
| `NEXUS_VMS_YAML` | `infrastructure`, `failover-test` (recommended) | Absolute path to `nexus-platform-plan/docs/infra/vms.yaml`. If unset, falls back to `../nexus-platform-plan/docs/infra/vms.yaml` from the cwd. |
| `NEXUS_VMRUN_PATH` | no | Override `vmrun.exe` discovery. Defaults to the canonical Workstation Pro install paths on Windows. |
| `NEXUS_SSH_KEY` | `failover-test` (recommended) | Absolute path to the operator's SSH private key for the lab. Default discovery: `~/.ssh/id_ed25519` then `~/.ssh/id_rsa` — set explicitly if your lab key has a different filename. |
| `NEXUS_SSH_USER` | no | SSH username (default `nexusadmin`). |
| `NEXUS_DEMOS_PATH` | `demo` (optional) | Directory of demo `<id>.json` files. Default discovery: `./docs/demos/` then `../docs/demos/`. |
| `NEXUS_VHS_PATH` | `demo record` (optional) | Absolute path to the `vhs` binary. Default discovery: PATH walk for `vhs`/`vhs.exe`. |

The CLI **does not** call `vault login` for you — manage your token externally (per ADR-0004).

## Examples

```pwsh
# default human-readable
nexus cluster-status

# JSON for scripting / piping into jq
nexus cluster-status --json

# verbose: dump per-component HTTP timing
nexus cluster-status --verbose
```

## Architecture

3 projects + tests; layer rules enforced by NetArchTest:

```
Nexus.Cli (AOT root) ───▶ Nexus.Cli.Adapters ───▶ Nexus.Cli.Core
                          (HTTP, Vault, JSON)     (interfaces, records)

Nexus.Cli.Core depends only on the BCL.
Nexus.Cli.Adapters may depend on Nexus.Cli.Core.
Nothing depends on Nexus.Cli.
```

ADR index: [`docs/adr/index.md`](./docs/adr/index.md). Twenty ADRs cover framework choice (0001), AOT cadence (0002), layout (0003), auth model (0004), Dapper-on-AOT (0005), hand-rolled vms.yaml reader (0006), SSH.NET over ssh.exe (0007), the v0.5 kafka-failover demo-grade-via-SSH design (0008), the `IClusterAdapter` SPI + extended demo spec (0009), the cross-adapter patterns + Redis exemplar (0010), and the per-adapter records: Mongo + the Vault-KV operator-credential model (0011), Percona Galera/ProxySQL (0012), Patroni PG HA (0013), ClickHouse sharded + Keeper (0014), StarRocks FE quorum + BE (0015), SQL Server FCI / WSFC over Windows-SSH (0016), SQL Server Always On AG + Listener (0017), the per-cluster Kafka adapters + KRaft `StandardAuthorizer` (0018), the **MongoSharded adapter** + its two-headed keyFile auth (0019), and the **Vitess adapter** + its hybrid mTLS-control-plane / vtgate-SQL-plane identity (0020).

## Roadmap

| Version | Scope |
|---|---|
| v0.1.0 | `cluster-status` — Consul + Nomad + Portainer read-only; AOT pipeline; size budget; CI |
| v0.2.0 | `infrastructure {list, status, suspend, resume}` + `suspend-cluster` alias; vmrun.exe adapter; hand-rolled vms.yaml reader (ADR-0006) |
| v0.2.1 | Spectre.Console.Cli 0.55 bump (breaking-change adoption: CT param + protected override); session-suffixed `.vmem` detection so post-suspend status correctly reports `suspended` on Workstation Pro 17.5+ |
| v0.3.0 | `failover-test consul-leader` — SSH.NET adapter (ADR-0007), raft polling, RTO measurement, auto-recovery; 1.55s RTO on the first live run |
| v0.3.1 | `failover-test nomad-leader` — same shape against Nomad raft; folds in CI-runner null-tolerance test fix; 2.716s RTO observed |
| v0.3.2 | `failover-test swarm-manager` — host-level outage via vmrun-suspend + SSH+`docker node ls` discovery; 21.59s RTO observed |
| **v0.4.0** | `demo {list, run, record}` — JSON spec orchestrator + VHS `.tape` recorder; 2 sample demos shipped |
| v0.4+ | `winget` manifest; `.deb`; `--watch` flag; deferred to slice cycles |
| v0.3.0 | `failover-test`; SSH client + raft introspection |
| v0.4.0 | `demo run/record` — VHS .tape orchestration + Playwright bridge |
| v0.5.0 | `kafka failover {east-to-west, west-to-east}` — ADR-0008; live RTOs 13.20 s + 13.57 s (60 s gate); **shipped 2026-05-15**, closes the v0.x roadmap with 5/5 master-plan verbs live |
| **v0.6.0** | Phase 0.G.1 — `IClusterAdapter` SPI + the **Redis adapter** (all 11 data-tier verbs live-verified); AOT gate → ≤30 MB (ADR-0024); **23.77 MB** |
| **v0.6.1** | Phase 0.G.2 — the **Mongo adapter** (first password-auth adapter; Vault-KV operator-credential model + optional `INexusVaultClient`); all data-tier verbs live-verified on `nexus-rs`; **23.9 MB** |
| **v0.6.2** | Phase 0.G.3 — the **Percona XtraDB Cluster + ProxySQL adapter** (Galera multi-primary; ProxySQL writer failover; reuses the Vault-KV operator-credential model); all data-tier verbs live-verified; **24.03 MB** |
| **v0.6.3** | Phase 0.G.4 — the **PostgreSQL Patroni HA adapter** (etcd DCS + HAProxy leader-routing VIP; `patronictl switchover` RTO≈4.6s); all data-tier verbs live-verified; **24.18 MB** |
| **v0.6.4** | Phase 0.G.5 — the **ClickHouse adapter** (first sharded + analytics-tier; 3 shards × 2 replicas + ClickHouse Keeper RAFT; Keeper-leader failover RTO≈1.1s; `topology` populates Shards); all data-tier verbs live-verified; **24.84 MB** |
| **v0.6.5** | Phase 0.G.6 — the **StarRocks adapter** (MPP MySQL-protocol warehouse; 3 FE BDB-JE quorum + 3 BE; FE-leader failover RTO≈1.5s; genuine async `BACKUP/RESTORE SNAPSHOT`); all data-tier verbs live-verified; **25.03 MB** |
| **v0.6.6** | Phase 0.G.7 — the **SQL Server FCI + Always On AG adapters** (the first **Windows** cluster; two adapters over one vms.yaml cluster — `sqlserver` WSFC/FCI + `sqlserver-ag` AG/Listener; Windows-SSH + `sqlcmd`, no managed driver; FCI `Move-ClusterGroup` RTO≈4.5s + AG `ALTER … FAILOVER` RTO≈8.2s; Listener strict-TLS; manual-seed AG scale-out); all verbs live-verified; **25.95 MB** |
| **v0.6.7** | Phase 0.H.7 — the **Kafka adapters** (`KafkaClusterAdapter` registered twice → `kafka-east` + `kafka-west`, the v0.5 `kafka` stays as the MM2 DR meta-cluster; `KafkaEcosystemAdapter` observes SR/REST/Connect/ksqlDB/MM2; mTLS-only, no managed `Confluent.Kafka`; controller-leader failover RTO≈4.5s; topic round-trip backup; enabled the KRaft `StandardAuthorizer` so `acl` enforces); zero live bugs; **26.18 MB** |
| **v0.7.0** | Phase 0.G **base roll-up** — the 0.G exit-gate milestone: the 9 data-tier adapter families sealed + the aggregate AOT validated ≤30 MB (no new adapter); **26.18 MB**, 86/86 tests |
| **v0.7.1** | Phase 0.N — the **MongoSharded adapter** (ClusterId `mongo-sharded`; 3 config-server RS + 2 shard RSes ×3 + 2 mongos; two-headed keyFile auth — `__system`@local for mongods + `nexus-sharded-admin`@admin through mongos; `topology` populates Shards; shard-primary stepdown RTO≈2.8s; mongodump-through-mongos backup; cert-rotate graceful N/A — no TLS in v1); all verbs live-verified (1 bug fixed); **26.30 MB** |
| **v0.7.2** | Phase 0.O — the **Vitess adapter** (ClusterId `vitess`; 3 etcd topo + vtctld/VTOrc + 2 vtgate + 2 shards ×3 tablets; keyspace `commerce` hash-vindex on `customer_id`; hybrid mTLS-control-plane `nexus-vtctldclient` + vtgate `:15306` SQL plane as `nexus`; `topology` populates Shards; graceful `PlannedReparentShard` RTO≈0.17s; logical `mysqldump`-per-shard backup round-trip 101 rows; cert-rotate vttablet-only; acl = vtgate static-auth file; chaos primary-freeze → VTOrc auto-reparent); all verbs live-verified (3 bugs fixed); **26.52 MB**, 114/114 tests |
| **v0.7.3** | Phase 0.P — the **Citus adapter** (ClusterId `citus`; Citus-sharded PostgreSQL + full Patroni HA: 3 etcd DCS + a coordinator Patroni pair + 2 worker Patroni pairs; PG 17 + Citus 14.1; `events` hash-distributed on `tenant_id`, 32 shards = 16+16 across the worker groups; **Citus = Patroni HA per group + Citus distribution**; operator `nexus-cluster-admin` propagated to workers + `.pgpass` so distributed queries run as the operator; `topology` populates Shards; `patronictl switchover` RTO≈1.6s with the VIP following the leader; operator `COPY` round-trip backup of the distributed dataset 800 rows; cert-rotate PG-reload/etcd-restart; acl = PG roles propagated to workers); all verbs live-verified (adapter first-try-green; the 1 infra fix = the patroni.yml `ctl:` block); **26.71 MB**, 137/137 tests |
| v0.8.0+ | full-fleet, then the 5 non-data-tier adapters (Foundation/Vault · Swarm · Observability · Lakehouse · Harbor) |
| v1.0.0 | All five master-plan commands stable; panic-button verbs everywhere |

## Contributing

This is a portfolio project authored solely by Grigoris Zapantis. PRs are welcome but the commit author/owner stays single-named per [CONTRIBUTING.md](./CONTRIBUTING.md).

## License

[MIT](./LICENSE).

## Acknowledgements

- [Spectre.Console](https://spectreconsole.net/) — the table rendering and `CommandApp` host
- [HashiCorp Vault](https://www.vaultproject.io/), [Consul](https://www.consul.io/), [Nomad](https://www.nomadproject.io/) — the control planes this CLI talks to
- [Portainer CE](https://www.portainer.io/) — the lab's Swarm UI
- The [`nexus-platform-plan`](https://github.com/grezap/nexus-platform-plan) blueprint — every command in this CLI exists because the master plan specified it
