# nexus-cli

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/publish-Native%20AOT-blue)](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
[![License](https://img.shields.io/badge/license-MIT-green)](./LICENSE)
[![Blueprint](https://img.shields.io/badge/blueprint-nexus--platform--plan-orange)](https://github.com/grezap/nexus-platform-plan)
[![Phase](https://img.shields.io/badge/phase-v0.8.9%20%E2%9C%85%20completion%20backlog%20COMPLETE%20(LDAPS%20%2B%20mysqld--wire%20cert--rotate)-brightgreen)](./CHANGELOG.md)

The operator surface for the **NexusPlatform lab** (140 VMs built through Phase 0.P) — a single **≤30 MB** Native AOT binary that introspects, drives, and recovers the lab's Tier-1 (Vault, AD, gateway) and Tier-2 (Docker Swarm + Nomad + Consul + Portainer) control planes. No raw `terraform`, no `vault` CLI, no `docker stack` for daily ops; one tool, predictable verbs, panic buttons everywhere.

> **Canon:** This repo implements [Phase 0.F](https://github.com/grezap/nexus-platform-plan/blob/main/MASTER-PLAN.md) (line 156) of the NexusPlatform blueprint. Read [`nexus-platform-plan`](https://github.com/grezap/nexus-platform-plan) first to understand the lab the CLI talks to.
>
> **New to the tool stack (Vault, Consul, Nomad, Portainer)?** See the [tool stack glossary](https://github.com/grezap/nexus-platform-plan/blob/main/docs/glossary.md) for plain-English definitions of each.
>
> **Current state (v0.8.9): the completion backlog is COMPLETE** — every cluster's every verb is
> implemented inside the tool, nothing deferred to an out-of-band script. Batch 5 closed the last two
> cert-rotate gaps: **FoundationAD DC LDAPS** (`cert-rotate foundation-ad` — issue on vault-1 → import
> root/CA/leaf → `Restart-Service NTDS` + ADWS re-cycle in one SSH session → `:636` verify; **standby DC
> first, PDC last**, abort-before-the-PDC; the single-session restart avoids the #10 self-fence) and
> **Vitess mysqld-wire** (`cert-rotate vitess` — the tablet's mysqld reloads its :3306 wire cert online via
> **`ALTER INSTANCE RELOAD TLS`**, no restart, **no reparent**; also fixed a pre-existing non-durability by
> forcing the vault-agent to re-issue). Live-verified 2026-07-07 on both live DCs + the running vitess tier.
> **321/321 tests; AOT 28.25 MB**. See CHANGELOG [0.8.9].
>
> <details><summary>v0.8.8 completion backlog batch 4 — grafana-pg + iceberg-pg PG-ssl cert-rotate</summary>
>
> **v0.8.8 = completion backlog, batch 4** — closed the two deferred **PostgreSQL cert-rotate** gaps: a shared
> `PgSslCertRotator` rotates **grafana-pg** (obs state DB) + **iceberg-pg** (lakehouse catalog DB) — a PG 17
> streaming pair rotated **STANDBY-FIRST then PRIMARY** with a **SIGHUP `reload`** (never a restart), so
> replication is never dropped. Live-verified (iceberg-pg standby→primary, `pg_stat_replication` intact after
> both). 315/315 tests; AOT 28.25 MB.
> </details>
>
> <details><summary>v0.8.7 completion backlog batch 3 — scale-up + swarm guarded restore + kafka resize-gate + FoundationAD backup/FSMO</summary>
>
> **v0.8.7 = completion backlog, batches 2-3** — closed the last of the "big" gaps INSIDE the
> CLI (no out-of-band hops): **`scale-up`** taken from skeleton to a full vertical resizer (`VmrunVmResizer` —
> atomic `.vmx` cpu/ram edit + cold restart; `vmware-vdiskmanager -x` disk grow + a SAFE guest FS extend that
> **never repartitions a live boot disk**, with an honest deb13 root-not-last warning); its **cluster-safety
> gate** refuses the write-primary/KRaft controller-leader unless `--force-primary`; and **swarm `backup
> restore`** became a real guarded restore behind `--confirm-destructive`. Batch 2 also landed the FoundationAD
> `backup take` (`ntdsutil ifm create full`) + `failover-test` (graceful FSMO transfer of the 4 movable roles).
> All live-verified; **310/310 tests; AOT 28.25 MB**. See CHANGELOG [0.8.7].
> </details>
>
> <details><summary>v0.8.6 completeness pass — cluster-status timings + Redis acl + kafka meta delegation</summary>
>
> **v0.8.6 = completeness pass (batch 1)** — closed the first batch of the completion backlog INSIDE
> the CLI (no external-script hops / "planned for vX" stubs): `cluster-status --verbose` renders real
> per-component timings; **Redis `acl grant/revoke`** implemented (cluster-wide `ACL SETUSER`/`DELUSER` +
> injection guard, live-verified); the **`kafka` meta-cluster delegates** status/health/topology/backup/
> cert-rotate/acl/chaos to the two per-region adapters and merges (live-verified east+west); + a live-caught
> backup bug fixed (`test -s`→`test -f` so an empty-topic backup restores). 272/272 tests; AOT 28.10 MB.
> </details>
>
> <details><summary>v0.8.5 RegistryAdapter — Harbor registry HA (the last non-data-tier adapter; full-fleet coverage)</summary>
>
> **v0.8.5 = `RegistryAdapter`** (ClusterId **`registry`**, Phase 0.L.4) — the
> **fifth and LAST non-data-tier adapter**. **Full `IClusterAdapter` coverage of the fleet is now complete
> (5/5 non-data tiers: Foundation/Vault · Swarm · Observability · Lakehouse · Registry).** Manages the Harbor
> container registry HA over 4 VMs + 1 VRRP VIP: 2 stateless Harbor app nodes (RR DNS `registry.nexus.lab`) +
> a PostgreSQL 17 / Redis datastore pair behind VRRP VIP `.119`; blobs in MinIO `s3://harbor`, SSO via Vault
> OIDC. The adapter resolves the vms.yaml `platform-tools` cluster but **filters to the four `registry-*`
> nodes** (the unbuilt prefect/unleash/marquez/backstage reservations are excluded). Same SSH-local-curl
> posture (Harbor `/api/v2.0/*` over each node's own `ca`; admin pw from Vault KV `nexus/registry/harbor-admin`
> field `value`); **no managed Harbor/Npgsql/Redis driver** (NetArchTest). Verbs — status (4 nodes; leader =
> VIP holder) · health (Harbor 8-component checklist + `/systeminfo` auth_mode + PG streaming repl + Redis
> master/replica + the MinIO `s3://harbor` backend canary + VRRP VIP) · topology (4 + VIP + blob-store) ·
> **failover `--direction registry-db`** = VRRP cutover (peer promotes PG + re-masters Redis; **PG re-attach
> of the demoted primary is a DR re-seed** — keepalived `demote.sh` re-attaches Redis only — so live-run is a
> DR runbook, mirrors lakehouse iceberg-pg; the app tier is RR-DNS, no VIP) · scale-out = graceful N/A
> (ADR-0036 2+2 HA) · **backup** = `pg_dump` the Harbor metadata DB round-trip (49 tables; blobs EC-durable in
> MinIO, not snapshotted) · cert-rotate = vault-agent force-rerender + nginx-container restart (app) / PG ssl
> reload (datastore), VIP holder last (4 nodes, 0 errors) · acl = Harbor users via `/api/v2.0/users` + sysadmin
> grant/revoke (admin protected) + project/robot counts · chaos = process-kill docker on an app node (RR pair
> tolerates one). **status/health/topology/scale-out/backup take+restore/acl/cert-rotate/chaos all live-verified
> GREEN.** The **cold-rebuild (CA rollover) folded in** put both Harbor + MinIO on the new Vault root (resolving
> the cross-tier CA split) and surfaced + reconciled the **MinIO root-password KV drift** (greenfield rotated
> KV; the running MinIO never adopted it → reconciled KV → the running MinIO, Greg-consented, data-preserving);
> `smoke-0.L.4` ALL PASSED. **1 live-caught bug** (the unauthenticated `/systeminfo` omits `harbor_version` →
> re-gate the probe on `auth_mode`); **2 legs un-run** (registry-db PG failover = DR re-seed; acl grant/revoke
> on a real user = OIDC onboarding, `oidc_auth` 403s local-user creation). AOT **28.04 MB / 30**; 243/243 tests.
> See [`docs/verification/0.8.5-registry.md`](./docs/verification/0.8.5-registry.md) + ADR-0026.
>
> </details>
>
> <details><summary>v0.8.4 LakehouseAdapter — MinIO + Iceberg/Nessie + Spark + ZooKeeper (sealed)</summary>
>
> **v0.8.4 = `LakehouseAdapter`** (ClusterId **`lakehouse`**, Phase 0.L) — the
> **fourth non-data-tier adapter** and the last big multi-component one: ONE component-aware adapter spanning
> the three-engine lakehouse (MinIO erasure-coded object store + Iceberg/Nessie REST catalog + Spark ZK-HA)
> plus the ZooKeeper ensemble, across 16 VMs + 1 VRRP VIP. Same SSH-local-curl posture as v0.8.3 (each node's
> own `ca`; Nessie mgmt `/q/health` + Spark UI `/json/` are plain HTTP; MinIO admin via the on-node `mc
> nexuslocal` alias; KV via `INexusVaultClient`, field `value`). **No managed MinIO/Spark/Iceberg/Nessie
> driver** (NetArchTest). Verbs — status (16 nodes + VIP holder + ALIVE Spark master) · health (MinIO
> live/cluster + drives · Nessie object-store + catalog · Spark ALIVE + workers · ZK quorum · iceberg-pg
> replication · VIP) · topology (16 + VIP + ZK roles + Spark master/standby + `spark://` URL) · **failover
> `--direction spark-master`** = ZooKeeper auto-promotes the STANDBY master (RTO≈31 s; `--direction iceberg-pg`
> = graceful N/A — a VRRP cutover split-brains the catalog DB) · scale-out = graceful N/A (all roles fixed-size
> IaC) · **cert-rotate** = vault-agent force-rerender, **MinIO big-bang** + Nessie per-node (Spark/ZK N/A — no
> rotatable leaf; iceberg-pg DR-deferred) · acl (MinIO policies + users via `mc admin`) · backup (`mc mirror
> s3://warehouse` round-trip) · chaos (process-kill a MinIO node — EC tolerates 1). **11 verbs live-verified
> GREEN as-is + the full matrix green post-rebuild.** Diagnosed the same v0.8.1-greenfield casualty class as
> the obs tier (MinIO new-root but Nessie/iceberg-pg/Spark/ZK old-root → a cross-tier CA split + an iceberg-pg
> replication split); a Greg-authorized **cold-rebuild of the Iceberg + Spark envs only** (MinIO kept in place
> — its EC drives hold four cross-tier buckets) fixed both + reconciled a MinIO IAM key drift, and surfaced
> 2 live-caught adapter bugs (cert-rotate Spark/ZK N/A; iceberg-pg failover N/A). AOT **27.59 MB / 30**;
> 223/223 tests. See [`docs/verification/0.8.4-lakehouse.md`](./docs/verification/0.8.4-lakehouse.md) +
> ADR-0025.
>
> </details>
>
> <details><summary>v0.8.3 ObservabilityAdapter — Grafana LGTM tier (sealed)</summary>
>
> **v0.8.3 = `ObservabilityAdapter`** (ClusterId **`observability`**, Phase 0.I) — the **third non-data-tier
> adapter**: the CLI manages the Grafana LGTM stack (Prometheus + Loki + Grafana + Tempo + Alertmanager + OTel
> Collector) across 14 VMs + 2 VRRP VIPs, probed over SSH with each node's own `ca.crt` (the obs leaves were on
> the tier's OLD CA generation). status/health/topology/`failover grafana` VRRP cutover (RTO≈1.2 s)/Loki+Tempo
> ring scale-out/cert-rotate (vault-agent force-rerender)/acl (Grafana org users)/backup graceful N/A. Surfaced
> the v0.8.1-greenfield trust-breakage class; cold-rebuild-proven. AOT 27.59 MB; 194/194 tests. See
> `docs/verification/0.8.3-observability.md` + ADR-0024.
> </details>
>
> <details><summary>v0.8.2 SwarmAdapter — orchestration tier (sealed)</summary>
>
> **v0.8.2 = `SwarmAdapter`** (ClusterId **`swarm`**, Phase 0.E) — the **second
> non-data-tier adapter** and the **most reusable**: it wires the already-built `ConsulClient` (`:8501`) +
> `NomadClient` (`:4646`) + `PortainerClient` (`:9443`) + `ClusterStatusService` + `FailoverTestService` (all
> shipped v0.1–v0.5) into the full `IClusterAdapter` surface over the orchestration tier (3 combined
> Consul-server/Nomad-server/Swarm-manager nodes + 3 Consul-client/Nomad-client/Swarm-worker/Portainer-agent
> nodes + a manager-pinned Portainer service). Same build-host control-plane posture as v0.8.1 (the
> Consul/Nomad mgmt tokens stay on the build host, read from Vault KV; **no managed Docker/Consul/Nomad driver**
> — NetArchTest). Verbs — status/health/topology (the 3-way rollup enriched with `docker node ls`) · failover
> (`--direction` → consul-leader / nomad-leader / **swarm-manager** [a vmrun host-level suspend of the raft
> leader VM]; RTO≈2/3/21s) · scale-out (reversible `docker node drain`/`demote` + `nomad node drain`, quorum-
> guarded) · backup (`consul snapshot save` + `consul kv export` + `nomad operator snapshot save`, round-trip-
> verified; restore refused on the live cluster) · cert-rotate (force-reissue the pki_int leaves; consul
> ROLLING + nomad **PARALLEL** big-bang) · acl (Consul + Nomad tokens, bootstrap/agent protected) · chaos
> (nexus-chaos.sh on a WORKER; docker restarted after any nftables scenario). **3 live-caught bugs fixed**
> (cert-rotate pkiCert persists+reuses the leaf → force re-issue; acl grant needs a policy → builtin/dns;
> chaos recovery poll exceeded the command budget → lightweight docker poll). Cold-rebuild-proven
> (`swarm.ps1 cycle` → smoke 0.E.4e GREEN → full verb matrix GREEN). AOT **27.59 MB / 30**; 173/173 tests. See
> [`docs/verification/0.8.2-swarm.md`](./docs/verification/0.8.2-swarm.md) + ADR-0023.
>
> </details>
>
> <details><summary>v0.8.0 full-fleet roll-up + v0.8.1 Vault + AD adapters (sealed)</summary>
>
> **v0.8.0 = the full-fleet roll-up** (the milestone marking all 12 data + sharded
> adapter families sealed + the aggregate AOT re-validated ≤30 MB; no new adapter). **v0.8.1 = `VaultAdapter`
> + `FoundationAdAdapter`** — the **first non-data-tier adapters**, so the CLI now deeply manages the
> Foundation tier (the platform **trust root**), not just the data tier. **`VaultAdapter`** (ClusterId
> **`vault`**, Phase 0.A-0.D/0.M): the Vault HA cluster — 3 Raft nodes (vault-1/2/3) + vault-transit (the
> single-node Shamir **seal-key custodian** that auto-unseals them). The Vault **control plane runs over HTTP
> from the build host** (the operator `VAULT_TOKEN`, ADR-0004 — the root token never reaches a node); only
> node-local actions (service stop/start/reload, cert-file push, chaos, recover-ha, vault-transit) go over
> SSH. **No managed Vault driver and no shelled `vault` binary** (NetArchTest). Mutating verbs **target
> STANDBYS** so the active keeps serving. Verbs — status (leaders DRIFT, read dynamically) · health (per-node
> seal + active-leader + raft 3 voters/1 leader + transit-unseal + operator-auth) · topology (raft
> voter/leader roles; not sharded) · failover (**`vault operator step-down`** on the active → a standby
> promotes, RTO≈2.0s) · scale-out (stop/start a STANDBY raft peer; auto-unseals on rejoin) · backup (**raft
> snapshot** save to a build-host file + non-destructive gzip/tar `meta.json` inspect; restore on the live
> trust root **deliberately refused**) · cert-rotate (`pki_int/issue/vault-server` via the build-host token +
> SIGHUP reload, standbys-first/active-last) · acl (Vault policies + AppRoles) · chaos (process-kill a
> STANDBY) · **`recover-ha`** (a NEW verb — the declarative boot-race recovery: unseal vault-transit from the
> Shamir key file → restart vault-1/2/3 → poll unsealed; the ONLY exposed unseal path). **`FoundationAdAdapter`**
> (ClusterId **`foundation-ad`**): the 2-DC AD forest (`nexus.lab`, WS2025) over **Windows-SSH** + the
> `nexus-gateway` egress over Linux-SSH — status/health (DC reachability + AD replication + DNS + KDS root key
> + all 5 FSMO roles + gateway NAT)/topology/acl (AD users/groups) are real; AD is multi-master so the
> failover/FSMO + DC-add/remove + system-state-backup + NTDS-cert/chaos verbs return a graceful **actionable
> N/A**. AOT **27.36 MB / 30**; 159/159 tests; adapter code first-try-green on every Vault verb (the one
> live-caught bug was the AD-replication `-Server` arg). See
> [`docs/verification/0.8.1-foundation.md`](./docs/verification/0.8.1-foundation.md) + ADR-0022.
> </details>
>
> <details><summary>v0.7.3 CitusAdapter (sealed)</summary>
>
> **v0.7.3 = `CitusAdapter`** (ClusterId **`citus`**, Phase 0.P): the Citus-sharded PostgreSQL cluster with
> **full Patroni HA** — 3 etcd DCS + a coordinator Patroni pair + 2 worker Patroni pairs; PG 17 + Citus 14.1;
> `events` hash-distributed on `tenant_id` (32 shards = 16+16). **Citus = Patroni HA per group + Citus
> distribution.** `topology` Shards populated; failover = `patronictl switchover` (RTO≈1.6s); backup = operator
> `COPY … TO STDOUT` round-trip (800 events). AOT 26.71 MB; 137/137 tests; adapter first-try-green. See
> ADR-0021.
> </details>
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

ADR index: [`docs/adr/index.md`](./docs/adr/index.md). Twenty-seven ADRs cover framework choice (0001), AOT cadence (0002), layout (0003), auth model (0004), Dapper-on-AOT (0005), hand-rolled vms.yaml reader (0006), SSH.NET over ssh.exe (0007), the v0.5 kafka-failover demo-grade-via-SSH design (0008), the `IClusterAdapter` SPI + extended demo spec (0009), the cross-adapter patterns + Redis exemplar (0010), and the per-adapter records: Mongo + the Vault-KV operator-credential model (0011), Percona Galera/ProxySQL (0012), Patroni PG HA (0013), ClickHouse sharded + Keeper (0014), StarRocks FE quorum + BE (0015), SQL Server FCI / WSFC over Windows-SSH (0016), SQL Server Always On AG + Listener (0017), the per-cluster Kafka adapters + KRaft `StandardAuthorizer` (0018), the **MongoSharded adapter** + its two-headed keyFile auth (0019), the **Vitess adapter** + its hybrid mTLS-control-plane / vtgate-SQL-plane identity (0020), the **Citus adapter** (Citus-sharded PG + Patroni HA per group, 0021), the **Vault + Foundation-AD adapters** (the first non-data-tier adapters; `recover-ha` + the build-host HTTP control plane, 0022), the **Swarm adapter** (the orchestration tier — reuses the Consul/Nomad/Portainer clients, 0023), the **Observability adapter** (the Grafana LGTM tier + the SSH-local-curl access posture, 0024), the **Lakehouse adapter** (the three-engine lakehouse — MinIO + Iceberg/Nessie + Spark + ZooKeeper — one component-aware adapter, 0025), the **Registry adapter** (Harbor HA — the last non-data-tier adapter, 0026), and **`scale-up` / `VmrunVmResizer` + the day-2 safety gates** (the generic vertical resizer + the cluster-safety gate / `--force-primary` / swarm `--confirm-destructive`, 0027).

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
| **v0.8.0** | **Full-fleet roll-up** — the milestone marking all 12 data + sharded adapter families sealed + the aggregate AOT re-validated ≤30 MB (no new adapter); **26.71 MB**, 137/137 tests |
| **v0.8.1** | Phase 0.A-0.D/0.M — the **Vault + Foundation-AD adapters** (the first **non-data-tier** adapters): `VaultAdapter` (ClusterId `vault`; the Vault HA trust root — 3 Raft nodes + vault-transit Shamir custodian; control plane over HTTP from the build host, no managed driver / no shelled `vault`; `vault operator step-down` failover RTO≈2.0s; stop/start-a-STANDBY scale-out; raft-snapshot backup + non-destructive `meta.json` inspect, restore refused; `pki_int/vault-server` cert-rotate standbys-first/active-last; Vault-policy/AppRole acl; process-kill-a-STANDBY chaos; + a NEW **`recover-ha`** verb = the declarative boot-race recovery, the only exposed unseal path) + `FoundationAdAdapter` (ClusterId `foundation-ad`; the 2-DC AD forest over Windows-SSH + gateway health; multi-master mutators graceful actionable N/A); all verbs live-verified (adapter first-try-green; 1 AD-replication `-Server` bug fixed); **27.36 MB**, 159/159 tests |
| **v0.8.2** | Phase 0.E — the **Swarm adapter** (ClusterId `swarm`; the orchestration tier — Docker Swarm + Nomad + Consul + Portainer over 3 managers + 3 workers; the **most reusable** adapter — wires the v0.1–v0.5 Consul/Nomad/Portainer clients + ClusterStatusService + FailoverTestService into the full SPI; no managed driver; status/health/topology = the 3-way rollup + `docker node ls`; `--direction` failover to consul-leader/nomad-leader/swarm-manager [vmrun suspend] RTO≈2/3/21s; reversible `docker node drain`/`demote` scale-out; consul+nomad snapshot backup round-trip; force-reissue cert-rotate consul-rolling/nomad-parallel; Consul+Nomad acl; nexus-chaos.sh on a worker + docker-restart-after-nft); all verbs live-verified + cold-rebuild-proven (3 bugs fixed); **27.59 MB**, 173/173 tests |
| **v0.8.3** | Phase 0.I — the **Observability adapter** (ClusterId `observability`; the Grafana LGTM tier over 14 VMs + 2 VRRP VIPs; SSH-local-curl posture; `failover grafana` VRRP cutover RTO≈1.2s; Loki/Tempo ring scale-out; vault-agent force-rerender cert-rotate; Grafana org-user acl; backup graceful N/A); 8 verbs live-verified + cold-rebuild-proven (2 bugs fixed); **27.59 MB**, 194/194 tests |
| **v0.8.4** | Phase 0.L — the **Lakehouse adapter** (ClusterId `lakehouse`; ONE component-aware adapter over MinIO EC + Iceberg/Nessie + Spark ZK-HA + ZooKeeper, 16 VMs + 1 VRRP VIP; `failover spark-master` ZK auto-promote RTO≈31s [iceberg-pg = graceful N/A — VRRP cutover split-brains the catalog DB]; scale-out N/A; cert-rotate MinIO-big-bang + Nessie [Spark/ZK N/A]; `mc admin` acl; `mc mirror s3://warehouse` backup; process-kill-a-MinIO-node chaos — EC tolerates 1); 11 verbs live-verified as-is + full matrix green post-rebuild (Iceberg+Spark cold-rebuild fixed the cross-tier CA split + iceberg-pg replication split; 2 bugs fixed); **27.59 MB**, 223/223 tests |
| **v0.8.5** | Phase 0.L.4 — the **Registry adapter** (ClusterId `registry`; Harbor registry HA — the last non-data-tier adapter, completing full-fleet `IClusterAdapter` coverage); all verbs live-verified + cold-rebuild-proven; **28.04 MB**, 243/243 tests |
| **v0.8.6** | **Completion backlog, batch 1** — `cluster-status --verbose` timings + **Redis `acl grant/revoke`** + the **`kafka` meta-cluster delegates** to the two per-region adapters and merges; 1 live-caught backup bug fixed (`test -s`→`test -f`); **28.10 MB**, 272/272 tests |
| **v0.8.7** | **Completion backlog, batches 2–3** — **`scale-up`** skeleton → full vertical resizer (`VmrunVmResizer`: atomic `.vmx` cpu/ram edit + cold restart; `vmware-vdiskmanager -x` disk grow + a SAFE guest FS extend that never repartitions a live boot disk, honest deb13 root-not-last warning) with a cluster-safety gate (refuses the write-primary/controller-leader unless `--force-primary`); **swarm `backup restore`** guarded behind `--confirm-destructive`; **kafka resize-gate** (controller-leader); FoundationAD **`backup take`** (`ntdsutil ifm`) + **`failover-test`** (graceful FSMO transfer); demos/playbooks/handbook §3.5 for all; **28.25 MB**, 310/310 tests |
| **v0.8.8** | **Completion backlog, batch 4** — the two deferred **PostgreSQL cert-rotate** gaps closed: `cert-rotate` now rotates **grafana-pg** (obs state DB) + **iceberg-pg** (lakehouse catalog DB) via a shared `PgSslCertRotator` — a PG17 streaming pair rotated **standby-first then primary** with a SIGHUP `reload` (not a restart), so replication is never dropped. Live-verified (iceberg-pg standby→primary, `pg_stat_replication` intact after both). **28.25 MB**, 315/315 tests. *(Remaining: #9 FoundationAD LDAPS, #12 Vitess mysqld-wire.)* |
| **v0.8.9** | **Completion backlog COMPLETE** — the last two cert-rotate gaps: **FoundationAD DC LDAPS** (`cert-rotate foundation-ad`: openssl PFX on vault-1 → import root/CA/leaf → `Restart-Service NTDS` + ADWS re-cycle in one SSH session → `:636` verify; **standby DC first, PDC last**, abort-before-the-PDC, single-session restart avoids the #10 self-fence) + **Vitess mysqld-wire** (`cert-rotate vitess`: the tablet's mysqld reloads its :3306 wire cert online via **`ALTER INSTANCE RELOAD TLS`** — no restart, **no reparent**; also fixed a pre-existing non-durability by forcing the vault-agent to re-issue). Live-verified on both live DCs + the running vitess tier. **Every cluster's every verb is now implemented in-CLI — nothing deferred.** **28.25 MB**, 321/321 tests |
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
