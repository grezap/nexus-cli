# Changelog

All notable changes to `nexus-cli` are documented in this file. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Completion backlog, batch 2 (continuing the nexus-cli completeness pass — closing the FoundationAD
deferred verbs INSIDE the adapter, no out-of-band hops).

- **FoundationAD `backup take` (GAP #8)** (was a graceful N/A pointing at out-of-band `wbadmin`/`ntdsutil`)
  — now a real **`ntdsutil ifm create full`** on a reachable DC: a non-destructive point-in-time copy of the
  AD database (`ntds.dit` + registry hives) under `C:\nexus-backups\ad\<id>`, the AD analogue of the Vault
  raft-snapshot verb. Chooses an alive DC, **preferring a non-PDC** when ≥2 are up (keeps the snapshot load
  off the PDC emulator — the "back up from a secondary" hygiene the data adapters follow), falling back to the
  sole reachable DC. Verified by the resulting `ntds.dit` artifact (size + path), not ntdsutil's chatty stdout;
  `WinPsAsync` gained an optional timeout (IFM allowed up to 5 min vs the 60 s status default). **Live-verified
  2026-06-28** against the running `dc-nexus`: `backup take foundation-ad` → GREEN, **96.0 MiB** `ntds.dit`,
  ~12 s. `backup restore` stays a deliberate N/A — authoritative restore is the console-only DSRM path
  (Server 2025 blocks `ntdsutil` ConsoleMode over SSH).
- **FoundationAD `failover-test` (GAP #7)** (was a graceful N/A pointing at `Move-ADDirectoryServerOperationMasterRole`)
  — now a real **graceful FSMO transfer drill**: relocates the 4 operator-movable FSMO roles
  (PDCEmulator/RIDMaster/InfrastructureMaster/DomainNamingMaster) from the current holder to the other DC,
  verifies the target holds them, then transfers them BACK (`--no-recover` leaves them moved; `--node` picks
  the target). Mirrors the failover-test recover pattern (original→new→recovered timeline). Requires ≥2
  reachable DCs. **SchemaMaster is deliberately excluded** — moving it needs Schema Admins (restricted by AD
  design); **live-caught 2026-06-29** that an all-5 batch run as Domain/Enterprise Admin moves the first 4
  then aborts `Access is denied` on SchemaMaster, leaving a SPLIT placement → the verb scopes to exactly what
  the operator identity can move, keeping the transfer atomic (the split was detected + fully restored during
  bring-up). **Live-verified 2026-06-29** with dc-nexus-2 powered on: 4 roles dc-nexus→dc-nexus-2 and back,
  recovered ~6.8 s, all 5 FSMO consolidated back on dc-nexus afterward.
- **FoundationAD `chaos` (GAP #10) — classified GENUINE N/A** (was already N/A; now with hard live evidence +
  a sharper refusal message). A meaningful DC chaos stops ADDS/NTDS, which also stops **Netlogon** and severs the
  domain secure channel OpenSSH authenticates `nexusadmin` through — so the chaos **self-fences the adapter's own
  recovery path**: live-proven 2026-06-29 that an in-adapter NTDS stop on the non-PDC dc-nexus-2 left it
  `Permission denied (publickey)`, and recovery required an out-of-band `vmrun reset` (outside ADR-0009's
  SSH-shell-out architecture; dc-nexus-2 was fully restored). The 2-DC HA is validated out-of-band by smoke-0.M
  (host-kill of a DC → auth + DNS continue on the survivor). This is a sanctioned refusal, not a skip.
- AOT win-x64 **28.14 MB / 30**; **281/281 tests** (+9: `ParseIfmResult` ×2, `Sanitize` ×4, `ParseFsmoHolders` ×3).
  Pre-flight proved both DC SSH sessions run **elevated** (full admin token), so `ntdsutil`/FSMO transfers need
  no extra elevation hop.

## [0.8.6] — 2026-06-26

Completeness pass (the first batch of the nexus-cli completion backlog) — three previously
deferred/stubbed verb gaps closed INSIDE nexus-cli (no external-script hops, no "planned for
vX" stubs), all live-verified, plus a live-caught backup bug fixed.

- **`cluster-status --verbose` timings** (was a `"not yet wired (planned v0.2)"` stub) — `ClusterStatusService`
  now records per-component fetch latency (`ComponentTimings` on `ClusterStatusReport` via `TimedAsync`), and the
  command renders `timings: consul N ms · nomad N ms · portainer N ms`. Live-verified (real measured ms).
- **Redis `acl grant/revoke`** (was `"not implemented … lands in 0.G.1.x"`) — `ACL SETUSER` (grant, with the
  operator's rules) / `ACL DELUSER` (revoke) applied across **all cluster nodes** (Redis Cluster ACLs are
  per-node, not replicated) + best-effort `ACL SAVE`; the built-in `default` user is protected; an
  `IsSafeAclToken` guard blocks ACL-rule injection into the `sudo bash -c '…'` wrapper. **Live-verified** on
  the 6-node cluster: list · grant (created cluster-wide) · revoke (DELUSER) · protected-default refusal.
- **KafkaAdapter meta-cluster (`kafka`) delegate-don't-defer** (was 7 verbs punting to `kafka.ps1` /
  `kafka-acls.sh on a broker`) — status/health/topology/backup take+restore/cert-rotate/acl/chaos/CanResizeVm
  now **delegate to the two `KafkaClusterAdapter`s (kafka-east + kafka-west) and merge** (health probes
  region-prefixed `east/`+`west/`; `WorseOf` overall; combined backup-id `<east>||<west>`; chaos routes to the
  region owning the target). scale-out routes to the per-region ClusterId (no external hop). **Live-verified**
  across both regions: status/health(green)/topology/acl/backup take+restore all merge; chaos region-routes;
  cert-rotate delegates correctly (the rotation itself is gated on the kafka tier's pending CA-rollover —
  it is still old-root, so the new-root Vault PKI issue is correctly refused).
- **Fixed (live-caught): `KafkaClusterAdapter.BackupRestoreAsync` used `test -s`** (exists AND non-empty), so
  a valid backup of an **empty** topic (a 0-byte file) spuriously reported `MISSING-BACKUP` → changed to
  `test -f` (exists). Verified: an empty-topic backup now restores as 0 records.
- AOT win-x64 **28.10 MB / 30**; **272/272 tests** (+29: `WorseOf`, `SplitBackupId`, `IsSafeAclToken`,
  `ParseAclList`). NetArchTest clean. See the completion backlog for the remaining gaps.

## [0.8.5] — 2026-06-26

Phase 0.L.4: **`RegistryAdapter`** (ClusterId `registry`) — the **fifth and LAST non-data-tier adapter**.
Full `IClusterAdapter` coverage of the platform is now complete (5/5 non-data tiers: Foundation/Vault,
Swarm, Observability, Lakehouse, **Registry**). Manages the Harbor container registry HA over 4 VMs +
1 VRRP VIP; live-verified against the rebuilt tier + cold-rebuild-proven (CA rollover to the new Vault root).

- **`RegistryAdapter`** resolves the vms.yaml cluster `platform-tools` and **filters to the four
  `registry-*` nodes** (`ClassifyRole` → `harbor` / `registry-pg`; the unbuilt prefect/unleash/marquez/
  backstage reservations classify `other`, excluded). Same SSH-local-curl posture as the obs/lakehouse
  adapters: the Harbor API (HTTPS :443) is probed over SSH with each node's own `ca.crt`; the Harbor admin
  password from Vault KV `nexus/registry/harbor-admin` (field `value`) via `INexusVaultClient`; PG/Redis/
  VRRP/chaos/cert over node SSH. **No managed Harbor/Npgsql/Redis driver** (NetArchTest).
- **status** (4 nodes: harbor-app ×2 + datastore primary+vip/replica; leader = VIP holder) · **health**
  (Harbor `/api/v2.0/health` component checklist 8/8 + `/systeminfo` auth_mode=oidc_auth + PG streaming
  replication + Redis master/replica + the MinIO `s3://harbor` backend canary + the VRRP VIP) · **topology**
  (4 nodes + VIP pseudo-node + MinIO blob-store; not sharded).
- **failover `--direction registry-db`** = VRRP cutover of the `.119` VIP (peer promotes PG + re-masters
  Redis; RTO measured) — but PG re-attach of the demoted primary is a **DR re-seed** (keepalived `demote.sh`
  re-attaches Redis only), so live execution is a DR runbook (mirrors lakehouse iceberg-pg); the app tier
  has no VIP (RR DNS) → app-direction refused. **scale-out** = graceful actionable N/A (ADR-0036 2+2 HA;
  grow via MinIO EC + `scale-up`). **backup take/restore** = `pg_dump` the Harbor metadata DB (`registry`)
  round-trip into a verify DB (49 tables; blobs EC-durable in MinIO, Redis ephemeral — not snapshotted).
  **cert-rotate** = vault-agent force-rerender + nginx-container restart (app) / PG ssl reload (datastore),
  VIP holder last (4 nodes, fresh serials, 0 errors). **acl** = Harbor users via `/api/v2.0/users` +
  sysadmin grant/revoke (admin protected) + project/robot counts. **chaos** = `nexus-chaos.sh` process-kill
  (docker on an app node; RR pair tolerates one) + recover.
- **Cold-rebuild (CA rollover, folded in):** the tier was found operationally broken (Harbor down, PG split,
  old-root agents); the from-zero rebuild put both Harbor + MinIO on the new Vault root, resolving the
  cross-tier CA split. Surfaced + reconciled the **MinIO root-password KV drift** (greenfield rotated KV;
  running MinIO never adopted it → `mc` signature mismatch on the bucket step → reconciled **KV → the running
  MinIO's actual root**, Greg-consented, data-preserving, new KV-v2 version) + the `nexus-lakehouse-app` IAM
  key. vmrun_path x86→non-x86 fixed in `nexus-infra-registry`. `smoke-0.L.4` ALL PASSED.
- **1 live-caught adapter bug fixed:** the `harbor-systeminfo` probe gated on `harbor_version`, which the
  UNAUTHENTICATED `/systeminfo` omits → re-gated on `auth_mode` (the SSO signal) → green.
- **2 verbs intentionally un-run** (honest, precedented): registry-db PG failover (DR re-seed by design),
  acl grant/revoke on a real user (Harbor `oidc_auth` mode 403s local-user creation → needs OIDC onboarding).
- AOT win-x64 **28.04 MB / 30**; **243/243 tests** (+16 `RegistryAdapterParseTests`). ADR-0026. See
  [`docs/verification/0.8.5-registry.md`](./docs/verification/0.8.5-registry.md).

## [0.8.4] — 2026-06-25

Phase 0.L: **`LakehouseAdapter`** (ClusterId `lakehouse`) — the **fourth non-data-tier adapter** and the last
big multi-component one: ONE component-aware adapter spanning the three-engine lakehouse (MinIO erasure-coded
object store + Iceberg/Nessie REST catalog + Spark ZK-HA) plus the ZooKeeper ensemble, across 16 VMs + 1 VRRP
VIP. Live-verified against the running tier + cold-rebuild-proven (Iceberg + Spark envs).

- **`LakehouseAdapter`** classifies nodes by name-prefix (minio-/iceberg-rest-/iceberg-pg-/spark-master-/
  spark-worker-/zookeeper-) and dispatches per component. **No managed MinIO/Spark/Iceberg/Nessie driver**
  (NetArchTest); same SSH-local-curl access posture as the observability adapter (each node's own ca; Nessie
  mgmt `/q/health` + Spark UI `/json/` are plain HTTP; MinIO admin via the on-node `mc nexuslocal` alias;
  KV via `INexusVaultClient`, every lakehouse secret field = `value`).
- **status** = 16 nodes + the iceberg-pg VIP holder + the ZK-elected ALIVE Spark master. **health** = MinIO
  `/minio/health/{live,cluster}` + `mc admin info` drives · Nessie `/q/health` per-check (the cross-tier S3
  object-store canary) + `/iceberg/v1/config` · Spark ALIVE master + aliveworkers + workers · ZK quorum ·
  iceberg-pg streaming replication · VIP. **topology** = 16 + the VIP pseudo-node + ZK leader/followers +
  Spark master/standby + the `spark://` URL.
- **failover `--direction spark-master`** = stop the ALIVE master → ZooKeeper auto-promotes the STANDBY
  (RTO ≈ 31 s — the live-proven HA drill). **`--direction iceberg-pg` = graceful actionable N/A** (a
  keepalived VRRP cutover of the catalog-DB pair promotes the standby into a split-brain + the promoted
  standby's pg_hba rejects Nessie → a DR runbook, not a one-shot). **scale-out** = graceful N/A (EC set +
  worker count + pairs/ensemble all fixed-size IaC). **backup** = `mc mirror s3://warehouse` round-trip.
- **cert-rotate** = vault-agent force-rerender, **MinIO big-bang restart** (a rolling 1-node re-cert breaks
  distributed MinIO inter-node mTLS) + Nessie per-node; **Spark + ZooKeeper graceful N/A** (Spark RPC is
  shared-secret + AES with only a JVM-truststore CA, no per-node leaf; ZK is backplane-only plaintext);
  iceberg-pg deferred to its PG DR runbook. **acl** = MinIO policies + users via `mc admin` (root + app
  protected). **chaos** = process-kill a MinIO node (the EC:2 set tolerates 1) + recover.
- Diagnosed the v0.8.1-Vault-greenfield casualty class (same as the observability tier): MinIO was already on
  the new root but Nessie/iceberg-pg/Spark/ZooKeeper were old-root → a **cross-tier CA split** (old-root
  Nessie's truststore couldn't validate the new-root MinIO S3 leaf — PKIX) + an **iceberg-pg replication
  split**. A Greg-authorized **cold-rebuild of the Iceberg + Spark envs only** (MinIO kept in place —
  reformatting its EC drives would wipe the four cross-tier buckets it serves) resolved both; it also surfaced
  + reconciled a **MinIO IAM key drift** (greenfield-rotated app secret → S3 403; data-preserving `mc admin
  user add` re-sync) and **2 live-caught adapter bugs** (cert-rotate Spark/ZK N/A; iceberg-pg failover N/A).
- ADR-0025 + `docs/verification/0.8.4-lakehouse.md` + 13 System B demos (DEMO-134..146). AOT win-x64
  **27.59 MB / 30**; **223/223 tests** (+18 LakehouseAdapter parser tests).

## [0.8.3] — 2026-06-22

Phase 0.I: **`ObservabilityAdapter`** (ClusterId `observability`) — the **third non-data-tier adapter**,
extending the CLI over the Grafana LGTM stack (Prometheus + Loki + Grafana + Tempo + Alertmanager + OTel
Collector) across 14 VMs + 2 VRRP VIPs. Live-verified against the running tier (8 verbs green; 3 gated by a
documented infra trust-breakage, not adapter bugs).

- **`ObservabilityAdapter`** manages prom-1/2 (Prometheus + Alertmanager mesh), loki-1/2/3 + tempo-1/2/3
  (memberlist rings on a MinIO S3 backend), grafana-1/2 (active-active, VRRP VIP `.184`), grafana-pg-1/2
  (PG17 streaming repl, VRRP VIP `.185`), otel-collector-1/2. **No managed Prometheus/Grafana/Loki driver**
  (NetArchTest).
- **Access posture (a deliberate divergence from the v0.8.1/0.8.2 build-host-HTTP shape, forced by the live
  contract):** the service endpoints are probed **over SSH with each node's own `ca.crt`** (always
  self-consistent), runtime creds come from Vault KV via `INexusVaultClient` (every obs secret field =
  `value`), and OTel's loopback health is always on-node. Reason: the obs leaves are on the tier's OLD CA
  generation (the tier was offline during the v0.8.1 Vault greenfield) while the build host now trusts the
  NEW root — so the build-host CA bundle can't validate them. The diagnose-first probe caught this.
- **Verbs** — status (14 nodes + VIP holders); health (Prom ready + scrape-targets-up, Alertmanager mesh
  peers, Loki/Tempo memberlist rings, Grafana `database`=ok, OTel loopback, **Grafana-PG streaming
  replication**, **MinIO S3 reachable**, both VIPs bound); topology (14 nodes + 2 VIP pseudo-nodes + ring
  counts + scrape count); **failover** = Grafana / Grafana-PG **VRRP cutover** (`--direction grafana` proven,
  RTO ≈ 1.2 s); scale-out = Loki/Tempo **memberlist ring** add/remove (the fixed-HA roles → graceful N/A);
  cert-rotate = build-host `pki_int/observability-server` issue + SSH-push + per-service reload; acl = Grafana
  users via `/api/admin/users`; backup = graceful actionable N/A (state durable in MinIO EC + PG repl RPO≈0 +
  dashboards-as-code + ephemeral Prom TSDB).
- **8 verbs live-verified GREEN; zero adapter code bugs.** The verify surfaced **three infra divergences**
  (all v0.8.1-greenfield-while-offline casualties, the same class as the swarm tier's Portainer drift):
  the tier-wide vault-agent broken trust (drove the SSH-local-curl posture), the Grafana admin password drift
  (`acl` honestly returns 401 + the reconcile command), and the grafana-pg replication split (`health`
  correctly red). `cert-rotate` / `failover grafana-db` / `acl grant` are implemented but not live-run on the
  degraded tier (they need the Greg-authorized tier trust re-apply first). See
  [`docs/verification/0.8.3-observability.md`](./docs/verification/0.8.3-observability.md) + ADR-0024.
- **AOT 27.59 MB / 30** (unchanged — no new heavy deps). **194/194 tests** (+21 ObservabilityAdapter parser
  cases). 10 System B demos `DEMO-124..133`.

## [0.8.2] — 2026-06-19

Phase 0.E: **`SwarmAdapter`** (ClusterId `swarm`) — the **second non-data-tier adapter** and the **most
reusable**, extending the CLI over the orchestration tier (Docker Swarm + Nomad + Consul + Portainer).
Live-verified end-to-end against a **freshly cold-rebuilt** 6-VM tier (`swarm.ps1 cycle` → smoke 0.E.4e
ALL GREEN → full verb matrix GREEN).

- **`SwarmAdapter`** manages 3 combined Consul-server/Nomad-server/Swarm-manager nodes (swarm-manager-1/2/3)
  + 3 Consul-client/Nomad-client/Swarm-worker/Portainer-agent nodes (swarm-worker-1/2/3) + a manager-pinned
  Portainer service. **Maximum reuse:** the already-built `ConsulClient` (`:8501`), `NomadClient` (`:4646`),
  `PortainerClient` (`:9443`), `ClusterStatusService` (the 3-way rollup) and `FailoverTestService`
  (consul-leader / nomad-leader / swarm-manager runners) — shipped v0.1–v0.5 for the standalone
  `cluster-status` + `failover-test` commands — are wired verbatim into the full `IClusterAdapter` surface.
- **Build-host control-plane posture** (the v0.8.1 model): the Consul/Nomad mgmt tokens stay on the build host
  (read from Vault KV `nexus/swarm/{consul,nomad}-bootstrap-token` via `INexusVaultClient`) and reach the
  cluster over HTTPS (targeting a manager IP — the build host doesn't resolve `*.nexus.lab`); node-local
  actions go over SSH. **No managed Docker/Consul/Nomad driver** (NetArchTest).
- **Verbs** — status/health/topology = the Consul+Nomad+Portainer rollup **enriched** with `docker node ls`
  (the authoritative Swarm membership + raft-leader view); failover dispatches `--direction` to
  consul-leader / nomad-leader (SSH `systemctl stop` → re-election, RTO ≈ 2–3 s) / **swarm-manager** (a vmrun
  host-level SUSPEND of the raft-leader VM, RTO ≈ 21 s); scale-out = reversible `docker node drain`/`demote` +
  `nomad node drain` (quorum-guarded; growing the fixed 3+3 fleet = terraform → graceful N/A); backup =
  `consul snapshot save` + `consul kv export` + `nomad operator snapshot save` round-trip-verified to a
  build-host file (restore on the live cluster **refused**); cert-rotate = force-reissue each node's pki_int
  leaves then **consul ROLLING + nomad PARALLEL big-bang** restart; acl = Consul + Nomad ACL tokens
  (bootstrap/agent/management protected); chaos = `nexus-chaos.sh` on a WORKER (managers keep quorum), with a
  `docker` restart after any nftables scenario (the `flush ruleset` wipes the ingress-mesh DNAT).
- **3 live-caught bugs fixed:** (1) cert-rotate didn't rotate — the vault-agent `pkiCert` function persists +
  reuses the leaf across restarts; fixed by force-deleting the bundle (with a `.bak` restore safety) so it
  re-issues. (2) acl grant — Consul refuses a policy-less token; fixed with the `builtin/dns` templated policy
  + the explicit `-accessor-id` revoke flag. (3) chaos was cancelled before output — the recovery poll's full
  status rollup (Portainer HTTP timeout each cycle) blew the command's `Duration+60 s` budget; fixed with a
  lightweight `docker node ls` recovery poll.
- **AOT 27.59 MB / 30** (+0.23). **173 tests** (+14 SwarmAdapter parser cases: `ClassifyNode`,
  `ParseDockerNodes`, `ParseConsulAclTokens`, `ParseNomadAclTokens`). ADR-0023;
  `docs/verification/0.8.2-swarm.md`; demos DEMO-110..123.

## [0.8.1] — 2026-06-18

Phase 0.A-0.D/0.M: **`VaultAdapter`** (ClusterId `vault`) + **`FoundationAdAdapter`** (ClusterId `foundation-ad`)
— the **first non-data-tier adapters**, extending the CLI from the data tier to the Foundation tier (the
platform **trust root** + the identity plane). Live-verified end-to-end against the running 6-VM foundation
base (+ `dc-nexus-2`); the VaultAdapter code was **first-try-green on every verb**.

- **`VaultAdapter`** manages the Vault HA cluster: 3 Raft nodes (vault-1/2/3) + vault-transit (the single-node
  Shamir **seal-key custodian** that auto-unseals them). The Vault **control plane runs over HTTP from the
  build host** (new `VaultAdminClient`, reusing the CA-pinned `NexusHttpClientFactory` + the source-gen JSON
  context) using the operator `VAULT_TOKEN` (ADR-0004) — deliberately so the root token NEVER reaches a
  node's process table; node-local actions (service stop/start/reload, cert-file push, the chaos helper, the
  recover-ha restarts, and vault-transit which is outside the build-host CA bundle) go over SSH. **No managed
  Vault driver and no shelled-out `vault` binary** (NetArchTest). Mutating verbs **target STANDBYS** so the
  active keeps serving:
  - status (per-node seal + active/standby — **leaders drift**, read dynamically) · health (seal×3 +
    active-leader + raft 3 voters/1 leader + transit-unseal + operator-auth) · topology (raft voter/leader
    roles; not sharded).
  - failover = `PUT sys/step-down` on the active → a standby promotes (live RTO ≈ 2.0 s; Raft leadership is
    location-independent so there is no forced return — the old active becomes a healthy standby).
  - scale-out = stop/start a STANDBY's `vault.service` (it leaves/rejoins Raft; auto-unseals via vault-transit
    on restart). Growing the quorum (a 4th voter) is terraform → documented, not silently skipped.
  - backup = `GET sys/storage/raft/snapshot` to a build-host file + a **non-destructive** gzip/tar `meta.json`
    inspect (Index/Term/Size, via `System.Formats.Tar`). `restore` is **deliberately refused** (it overwrites
    every secret/policy/PKI mount of the live trust root).
  - cert-rotate = re-issue each listener cert from `pki_int/issue/vault-server` via the build-host token →
    SSH-push `vault.crt`/`vault.key` → SIGHUP reload, **standbys first, active LAST**.
  - acl = Vault ACL policies + AppRoles (list/describe/grant/revoke; the operator/system + `nexus-agent-*`
    policies are revoke-protected). chaos = process-kill a STANDBY + Raft rejoin.
- **`recover-ha`** — a NEW verb via the new `IRecoverableCluster` capability interface (only `VaultAdapter`
  implements it; other clusters get a graceful "not applicable"). It is the declarative form of
  `scripts/recover-vault-ha.ps1` — the post-reboot boot-race recovery: read the Shamir keys from
  `~/.nexus/vault-transit-init.json` → unseal vault-transit over SSH → `reset-failed` + `start vault` on
  vault-1/2/3 → poll until unsealed. Idempotent; the **ONLY exposed unseal path**.
- **`FoundationAdAdapter`** manages the 2-DC AD DS forest (`nexus.lab`, Windows Server 2025) over
  **Windows-SSH** + the `nexus-gateway` egress over Linux-SSH. status/health (both DCs reachable + AD
  replication result=0 + DNS zones AD-integrated + the KDS root key via the AD object + all 5 FSMO roles +
  gateway dnsmasq/nftables/NAT)/topology/acl (AD users + the `nexus-*` security groups) are real; AD is
  multi-master so failover/FSMO, DC add/remove, system-state backup, and NTDS cert-rotate/chaos return a
  graceful **actionable N/A**. DC IPs hardcoded to the `.240`/`.242` reality (the ADR-0039 drift).
- **1 live-caught bug (fixed):** the AD-replication probe's explicit `Get-ADReplicationPartnerMetadata
  -Server <ip>` degraded the result to empty fields; dropped it (the session already runs on the DC).
- AOT **26.71 → 27.36 MB / 30** (+0.65 from `System.Formats.Tar` + `GZipStream`); **137 → 159 tests** (+22
  parser cases). NetArchTest green. ADR-0022. No infra `.tf` changed (adapter-only).

## [0.8.0] — 2026-06-18

**Full-fleet roll-up** — the milestone marking all **12 data + sharded adapter families** sealed (Redis ·
Mongo · Percona · Patroni · ClickHouse · StarRocks · SQL FCI + AG · Kafka ×2 + ecosystem · MongoSharded ·
Vitess · Citus), with the aggregate AOT re-validated ≤ 30 MB (the HEAD at v0.7.3: build 0/0, 137/137 tests,
**26.71 MB**). No new adapter — mirrors the v0.7.0 base roll-up; the gate before the 0.8.x line adds the five
non-data-tier adapters (Foundation/Vault · Swarm · Observability · Lakehouse · Harbor).

## [0.7.3] — 2026-06-18

Phase 0.P: **`CitusAdapter`** (ClusterId `citus`) — the third adapter on the 0.7.x sharded line, closing the
relational-PostgreSQL-sharding axis: a **Citus-sharded PostgreSQL cluster with full Patroni HA** (3 etcd DCS
+ a coordinator Patroni pair + 2 worker Patroni pairs; PG 17 + Citus 14.1; `events` hash-distributed on
`tenant_id` into 32 shards across the 2 worker groups + colocated `event_tags` + reference `tenants`).
**Citus = Patroni HA per group + Citus distribution** — reuses the ADR-0013 PatroniAdapter HA idioms three
times + the ADR-0020 populated-Shards topology. Operator = the dedicated `nexus-cluster-admin` role
(ADR-0011 Vault-KV model, `nexus/citus/operator-password`) auto-propagated to the workers with a
`~postgres/.pgpass` entry so **distributed queries run as the operator** via the coordinator VIP
(mTLS+scram+client-cert). SSH-shell-out to on-node `patronictl`/`psql`/`etcdctl`, **no managed Npgsql
driver** (NetArchTest). Live-verified end-to-end against the running 9-VM cluster — **adapter code
first-try-green on every verb**; the one live-caught issue was infra (the missing patroni.yml `ctl:` block →
switchover 403, the 0.G.4 lesson), fixed live + baked into the overlay. AOT **26.71 MB / 30** (+0.19 over
v0.7.2); **137/137** tests (+23 Citus parser tests). Cold-rebuild PENDING consent. See ADR-0021 +
`docs/verification/0.7.3-citus.md`.

### Added — CitusAdapter (Phase 0.P)

- ClusterId `citus`, registered next to `VitessAdapter`. Nodes classified by name
  (`citus-etcd-*` / `citus-coord-*` / `citus-worker1-*` / `citus-worker2-*`); the 3 PG groups (coordinator +
  2 workers, registered in `pg_dist_node` by VIP = groupid 0/1/2) each run a Patroni leader + streaming
  replica over the shared etcd DCS. **Leaders drift — read from `patronictl`**, never assumed.
- `status` / `health` / `topology` roll up all 9 nodes. `topology` **populates the Shards array** (one row
  per worker group with its Patroni primary/replica + its `citus_shards` count of `events` — 16 + 16 of 32).
  `health` proves etcd quorum (**unioned across nodes** to beat the `127.0.1.1` self-probe artifact),
  per-group HA, the operator mTLS round-trip via the coordinator VIP, the registered-worker count, the
  sharding spread, and a distributed aggregate.
- `failover` = a graceful `patronictl switchover` on a chosen group (RTO ≈ 1.6 s; the VRRP VIP follows the
  new leader so `pg_dist_node` is untouched). `scale-out` = Patroni-member add/remove (worker-group growth =
  apply-on-demand `citus_add_node` + `rebalance_table_shards`, ADR-0042). `backup` = an operator
  `COPY … TO STDOUT` round-trip of the distributed dataset (800 events). `cert-rotate` = `pki_int/issue/
  citus-server` + `nexus-citus-tls-split.sh`, PG **reload** (no failover) / etcd restart, coord-leader-last.
  `acl` = PG roles via the operator (`CREATE ROLE` auto-propagates to workers). `chaos` = process-kill
  `nexus-patroni`.
- 11 System B demos `docs/demos/DEMO-85..95-citus-*.json` (one per verb).

### Fixed / infra (cold-rebuild bake)

- The patroni.yml **`ctl:` block** (added to `nexus-infra-citus` `role-overlay-citus-patroni-bootstrap.tf`
  v2) so `patronictl` presents a client cert for state-changing REST calls — without it graceful switchover
  403s (the 0.G.4 PatroniAdapter lesson). Also baked: `role-overlay-citus-operator-user.tf` (NEW — the
  operator role + `.pgpass`), the security `role-overlay-vault-citus-cluster-creds-seed.tf` v2
  (+operator-password) + `role-overlay-vault-agent-citus-policies.tf` v2 (+operator-password read).

## [0.7.2] — 2026-06-17

Phase 0.O: **`VitessAdapter`** (ClusterId `vitess`) — the second adapter on the 0.7.x sharded line: the
Vitess-sharded MySQL/Percona cluster (3 etcd topo + vtctld/VTOrc control + 2 vtgate routers + 2 shards ×3
tablets; keyspace `commerce` split `-80`/`80-` by a **hash vindex on `customer_id`**; Percona Server 8.4
under `mysqlctld`). Drives the full `IClusterAdapter` surface over a **hybrid operator identity** — the
mTLS gRPC control plane via `sudo nexus-vtctldclient` (no password) + the SQL plane via the vtgate `:15306`
mTLS listener as the static-auth user `nexus` (password in Vault KV `nexus/vitess/mysql-app-password`, the
ADR-0011 model) — by SSH-shell-out to on-node `vtctldclient`/`mysql`/`mysqldump`, **no managed driver**
(NetArchTest). Live-verified end-to-end against the running 12-VM cluster — **3 live-caught bugs** (the
etcd-quorum line count, the `vt_commerce` dump db name, the single-unit chaos freeze), fixed. AOT
**26.52 MB / 30** (+0.22 over v0.7.1); **114/114** tests (+17 Vitess parser tests). Cold-rebuild PENDING
consent. See ADR-0020 + `docs/verification/0.7.2-vitess.md`.

### Added — VitessAdapter (Phase 0.O)

- ClusterId `vitess`, registered next to `MongoShardedAdapter`. Nodes classified by name
  (`vitess-etcd-*`/`vitess-control-*`/`vitess-vtgate-*`/`vitess-shard<K>-tablet-*`); tablets register in the
  topo by their **VMnet10** IP, mapped back via vms.yaml; **primaries read from the topo**
  (`GetShard.primary_alias` / the tablet `type`), never assumed (they drift off the lowest uid).
- **status / health / topology** roll up all 12 nodes; `topology` **populates the Shards array** (one
  `TopologyShard` per keyspace shard, slot range = the hash-vindex key range — the sharded showcase);
  `health` proves etcd quorum + vtctld + VTOrc + both vtgate listeners + per-shard 1P+2R + the operator
  mTLS round-trip + the **sharding proof** (both shards non-empty: 54 / 47 rows).
- **failover** = graceful `PlannedReparentShard` to a healthy replica (RTO ≈ 0.17 s; old primary demoted in
  place). **scale-out** add/remove = tablet membership (`DeleteTablets` + service start; PRIMARY-guarded,
  ≥2-survivor floor). **backup** take/restore = **logical `mysqldump` per shard** (no Vitess BackupStorage
  configured in 0.O → engine-native Backup is the 0.O.1 enhancement) reloaded into a `commerce_restore_verify`
  DB (101 rows round-tripped). **cert-rotate** = per-node Vault PKI (`pki_int/issue/vitess-server` via the
  node Agent token + `nexus-vitess-tls-split.sh`), vttablet-only tablet restart (mysqld stays up → no
  reparent). **acl** = the **vtgate static-auth file** (`vtgate_creds.json` on both vtgate nodes + reload;
  vtgate doesn't proxy `CREATE USER`). **chaos** = SIGSTOP a tablet — a primary freeze (`nexus-mysqlctld`)
  triggers **VTOrc auto-reparent** (proven live: VTOrc promoted shard2-tablet-2 when the `80-` primary froze).
- 11 System B demos `docs/demos/DEMO-74..84-vitess-*.json` (one per verb).

## [0.7.1] — 2026-06-16

Phase 0.N: **`MongoShardedAdapter`** (ClusterId `mongo-sharded`) — the first adapter on the 0.7.x line and
the genuinely-**sharded** MongoDB cluster, distinct from the 0.G.2 `mongo` replica set. Drives the full
`IClusterAdapter` surface over the 11-node topology (3 config-server RS @ 27019 + 2 shard RSes ×3 @ 27018
+ 2 `mongos` routers @ 27017) via SSH-shell-out to on-node `mongosh`/`mongodump`/`mongorestore` — **no
managed driver** (NetArchTest). Live-verified end-to-end against the running cluster — **1 live-caught bug**
(a single-quote/`--eval` collision in the health mongos query, fixed) — and **cold-rebuild-proven** (destroy →
from-zero `apply -parallelism=3`, zero transients → smoke 61/61 → full verb matrix re-ran green). AOT
**26.30 MB / 30** (+0.12 over v0.7.0); **97/97** tests (+11 MongoSharded parser tests). See ADR-0019 +
`docs/verification/0.7.1-mongo-sharded.md`.

### Added — MongoShardedAdapter (Phase 0.N)

- ClusterId `mongo-sharded`, registered next to `MongoAdapter`. **Two-headed auth**, both using the shared
  keyFile content as the password (Vault KV `nexus/oltp/mongo/keyfile`, field `content`, via
  `INexusVaultClient`): `__system`@`local` (SCRAM-SHA-256) for direct mongod RS ops (config + shards — the
  only principal the shard mongods accept), and `nexus-sharded-admin`@`admin` **through a mongos** for
  cluster-level ops (`local` can't be used through mongos). Nodes classified by name prefix
  (`mongo-cfg-*`/`mongo-shard-K-*`/`mongo-mongos-*` → role/RS/port).
- **status / health / topology** roll up all 3 RSes + the 2 routers; `topology` **populates the Shards
  array** (one `TopologyShard` per data shard — the sharded showcase); `health` proves per-RS quorum +
  the mongos tier + the config-server shard-registration/balancer state.
- **failover** = shard-primary `rs.stepDown` + per-shard re-election (RTO ≈ 2.8 s); **scale-out** add/remove
  a shard RS member (PRIMARY guarded, apply-on-demand); **backup** take/restore = `mongodump`/`mongorestore`
  **through mongos** round-trip (200 docs); **acl** = config-server admin users via mongos; **chaos** =
  process-kill a shard secondary + rejoin.
- **cert-rotate** returns a **graceful not-applicable** result — the 0.N v1 cluster has no TLS (mTLS is the
  deferred 0.N.1 hardening, ADR-0040); it never fails silently.
- 11 System B demos `docs/demos/DEMO-63..73-mongo-sharded-*.json`.

## [0.7.0] — 2026-06-16

**Phase 0.G data-tier base roll-up** — the milestone tag for the 0.G exit gate (*"nexus-cli tagged
`v0.7.0`, ≤30 MB AOT validated"*). No new adapter: this seals the data-tier `IClusterAdapter` expansion
(Redis · Mongo · Percona · Patroni · ClickHouse · StarRocks · SQL Server FCI+AG · Kafka ×2 + ecosystem —
9 adapter families across ClusterIds, each shipped + live-verified + cold-rebuild-proven in its own v0.6.x
slice) and validates the aggregate AOT artifact against the ≤30 MB gate before the 0.7.x line begins adding
the sharded adapters (0.N mongo-sharded → 0.O Vitess → 0.P Citus).

### Validated

- AOT win-x64 **26.18 MB / 30** [OK]; **86/86** tests; NetArchTest no-managed-driver green; `dotnet build`
  0 warnings / 0 errors. The historical v0.5.0 ≤25 MB gate stays sealed (22.75 MB); the 0.G line uses the
  ≤30 MB gate (ADR-0024 / ADR-0009).

## [0.6.7] — 2026-06-15

Phase 0.H.7: promoted **Kafka** from the thin v0.5 retrofit (failover-only) to the full verb surface.
A single parameterized **`KafkaClusterAdapter`** is registered **twice** — ClusterId `kafka-east` +
`kafka-west` (matching the vms.yaml keys + the ClusterId-convention) — each driving all of
`IClusterAdapter` against its 3 combined broker+controller nodes; the v0.5 `KafkaAdapter` (ClusterId
`kafka`) stays as the cross-region MirrorMaker-2 DR meta-cluster. A lighter **`KafkaEcosystemAdapter`**
(ClusterId `kafka-ecosystem`) observes the 9 ecosystem nodes (Schema Registry, REST, Connect, ksqlDB,
MM2). **mTLS-only — no operator password, no `INexusVaultClient`** (like Redis): the operator identity
is the broker's own Vault-PKI keystore via `sudo kafka-*.sh --command-config`. **No managed
`Confluent.Kafka`** (NetArchTest-enforced). Live-verified end-to-end against both running KRaft clusters
+ the ecosystem — **zero live-caught bugs** (thorough up-front contract probe). AOT **26.18 MB / 30**
(+0.23 over v0.6.6). **86/86** tests (+15 Kafka parser tests). See ADR-0018 +
`docs/verification/0.6.7-kafka.md`.

### Added — per-cluster Kafka adapters + ecosystem observe (Phase 0.H.7)

- `KafkaClusterAdapter` (ClusterId `kafka-east` + `kafka-west`): status/health/topology via
  `kafka-metadata-quorum` + `kafka-topics`; **failover** = controlled controller-leader move (RTO ≈ 4.5 s);
  **scale-out** = broker drain/rejoin (`--role broker`); **backup** = topic→`.jsonl`→verify-topic
  produce/consume round-trip; **cert-rotate** = node-token `pki_int/issue/kafka-broker` + `kafka-tls-split.sh`
  rolling restart; **acl** = `kafka-acls` (needs the authorizer); **chaos** = `nexus-chaos.sh` process-kill.
- `KafkaEcosystemAdapter` (ClusterId `kafka-ecosystem`): status/health (systemctl + HTTPS endpoints
  SR :8081 / REST :8082 / Connect :8083 / ksqlDB :8088 + MM2 journal) / topology / cert-rotate (rebuilds
  PEM + PKCS#12) / chaos; failover/scale-out/backup/acl defer with a pointer.
- 25 System B demos (`docs/demos/demo-0.6.7-kafka-*.json`); ADR-0018; `docs/verification/0.6.7-kafka.md`;
  handbook §2 matrix + §3 Kafka troubleshooting ladder.

### Infra (cross-repo)

- `nexus-infra-kafka`: new `role-overlay-kafka-acl-authorizer.tf` (+ `var.enable_kafka_acl_authorizer`)
  enables the KRaft `StandardAuthorizer` on both clusters (rolling restart) with `super.users` = all 15
  platform principals, so the `acl` verb enforces while ordinary app principals stay deny-by-default.

## [0.6.6] — 2026-06-12

Phase 0.G.7: the **first Windows cluster** — SQL Server **FCI** + **Always On AG** — shipped as **two
adapters** over the single vms.yaml cluster `sqlserver` (`SqlFciAdapter` = ClusterId `sqlserver`,
`SqlAgAdapter` = ClusterId `sqlserver-ag`; per nexus-platform-plan ADR-0024). Live-verified end-to-end
against the running 4-node cluster (`sql-fci-1`/`sql-fci-2` + `sql-ag-rep-1`/`sql-ag-rep-2`, ws2025).
The access pattern differs fundamentally from the six Linux adapters: Windows-SSH (`powershell
-EncodedCommand`) + on-node `sqlcmd` (ODBC Driver 18) — **no managed `Microsoft.Data.SqlClient`**
(NetArchTest-enforced). AOT **25.95 MB / 30 MB** (+0.92 over v0.6.5). 71/71 tests. smoke-0.G.7 56/56.

### Added — SQL Server FCI + AG adapters (Phase 0.G.7)

- **`SqlFciAdapter`** (ClusterId `sqlserver`, ADR-0016) — the WSFC + shared-iSCSI plane: `status`/
  `topology` (`Get-ClusterGroup`/`Get-ClusterNode` + the 4-node WSFC + the FCI shared disk) · `health`
  (NodeMajority quorum + clustered Physical Disk + iSCSI sessions + operator-auth round-trip) ·
  `failover-test cluster sqlserver` (**`Move-ClusterGroup`** between sql-fci-1/2, RTO ≈ 4.5 s) ·
  `backup take`/`restore` (`BACKUP DATABASE … WITH COPY_ONLY` to `S:\Backups` + RESTORE-WITH-MOVE
  round-trip, 8225 rows) · `cert-rotate` (**one shared cert on both nodes + a single cluster
  checkpoint** — a per-node rotate would break failover) · `acl list/grant` (`sys.server_principals`
  + fixed-server-roles) · `chaos` (kill `sqlservr` on the active node → WSFC recovery) · `scale-out`
  skip-with-explanation (fixed 2-node FCI).
- **`SqlAgAdapter`** (ClusterId `sqlserver-ag`, ADR-0017) — the Always On AG + Listener plane: `status`/
  `topology`/`health` (`sys.dm_hadr_*` + the **Listener strict-TLS** probe `-S sql-ag-listener.nexus.lab
  -N` → `PRIMARY=SQLFCI`) · `failover-test cluster sqlserver-ag` (promote-to-sync → `ALTER AVAILABILITY
  GROUP FAILOVER` → fail back, RTO ≈ 8.2 s) · `scale-out remove`/`add` (the add re-seeds via **manual
  seeding**) · `backup`/`restore` (via the AG primary) · `cert-rotate` (per-node replica certs) ·
  `acl` · `chaos` (kill a secondary → SCM restart → resync).
- **Auth model** (decided from a live probe): two planes — WSFC/cluster cmdlets over **plain SSH** as
  local `nexusadmin`; FCI T-SQL as the dedicated **`nexus-cluster-admin`** SQL login (the ADR-0011
  Vault-KV operator-credential model; password in `nexus/oltp/sqlserver/operator-password`,
  `$env:SQLCMDPASSWORD`). Standalone AG replicas are Windows-auth-only (`-E`).
- **`SqlServerControl`** (shared Windows-SSH + sqlcmd primitives) + **`SqlServerCert`** (Vault-PKI → PFX
  build-host issue → SFTP ship → `Import-PfxCertificate`; ws2025 has no openssl).
- **`ISshClient.DownloadBytesAsync`** (SFTP download) — for the AG manual-seed ferry (active FCI node →
  build host → replica). Plus `IssuePkiCertAsync`/`ReadKvFieldAsync` on the Vault client + the
  `PkiIssue` model.
- 17 System B demos (`docs/demos/demo-0.G.7-sqlserver*-*.json`) + ADR-0016 + ADR-0017 +
  `docs/verification/0.G.7-sqlserver.md` + handbook §2 matrix & §3 SQL troubleshooting ladder.

### Fixed — AG scale-out automatic-seeding bug (live-caught)

- `SqlAgAdapter.ScaleOutAddAsync` used `SEEDING_MODE = AUTOMATIC`, which **cannot work** in this hybrid
  FCI+AG topology (the FCI primary's `nexus_demo` files live on the shared iSCSI `S:\`; automatic
  seeding preserves the primary's paths and tries to create `S:\SQLData\*.mdf` on a standalone replica
  that has only local `C:\` → `failure_state=Seeding`). Found live as a drifted `sql-ag-rep-2`
  (CONNECTED but NOT_HEALTHY). Rewrote to **manual seeding** (`SEEDING_MODE=MANUAL` → `JOIN` → backup →
  SFTP-ferry → `RESTORE WITH MOVE … NORECOVERY` → `SET HADR AVAILABILITY GROUP`), mirroring
  `role-overlay-ag-bootstrap`. `scale-out add` is now the named operator recovery command for a drifted
  secondary.

## [0.6.5] — 2026-06-12

Phase 0.G.6: the **StarRocks (3 FE BDB-JE quorum + 3 BE) adapter** — the second **analytics-tier**
adapter, an MPP MySQL-protocol warehouse — live-verified end-to-end against the running `starrocks`
cluster (3 FE `sr-fe-leader`/`sr-fe-follower-1/2` + 3 BE `sr-be-1/2/3`). Auth model decided from a
live probe: **password-auth** (root requires a password over the MySQL wire), so it reuses the v0.6.1
Vault-KV operator-credential model verbatim. AOT **25.03 MB / 30 MB** (+0.19 MB over v0.6.4). 71/71
tests.

### Added — StarRocks adapter (Phase 0.G.6)

- **`StarRocksAdapter`** implements all of `IClusterAdapter` over SSH + the on-node `mysql` client
  against an FE's MySQL-protocol query port (`:9030`) — no managed MySqlConnector/JDBC driver
  (NetArchTest-enforced): `status`/`topology` (`SHOW FRONTENDS`/`SHOW BACKENDS`, dynamic FE leader,
  VMnet10-backplane IP mapping; Shards=null — tablet-hash sharded) · `health` (fe-quorum + operator-auth
  + per-BE TabletNum + distributed-query) · `failover-test cluster starrocks` (**FE leader re-election**,
  RTO ≈1.5 s) · `scale-out add`/`remove` (start/stop `nexus-starrocks-be`, ≥2-live-BE guard) ·
  `backup take`/`restore` (genuine async `BACKUP/RESTORE SNAPSHOT` to the file:// NFS repo, polled to
  FINISHED; 60 rows round-tripped) · `cert-rotate` (Vault re-issue → `pki_int/issue/starrocks-server`,
  all 6 nodes, PKCS#8, BE-first/FE-leader-last) · `acl list/grant` (`SHOW USERS` + `SHOW GRANTS` +
  `CREATE USER … ON CLUSTER`) · `chaos` (process-kill `nexus-starrocks-be` + rejoin).
- **Operator-credential model** reused from v0.6.1 (ADR-0011): authenticate as the dedicated
  `nexus-cluster-admin` StarRocks user (granted `cluster_admin`+`db_admin`+`user_admin`, `DEFAULT ROLE
  ALL`, distinct from the built-in `root`); password ONLY in Vault KV
  (`nexus/analytics/starrocks/operator-password`) via the optional `INexusVaultClient`. Connection via
  `mysql --skip-ssl` (MariaDB-client TLS requirement) with `MYSQL_PWD` (no argv exposure).
- **Infra:** nexus-infra-vmware security `role-overlay-vault-starrocks-creds-seed.tf` **v2**
  (+operator-password; no agent-policy change — existing policy wildcard-reads the starrocks KV subtree)
  + nexus-infra-analytics `role-overlay-starrocks-operator-user.tf` (CREATE USER + GRANT + DEFAULT ROLE
  ALL on the FE leader via the agent token).
- **ADR-0015** + `docs/verification/0.G.6-starrocks.md` (live evidence) + 11 System B demos
  (`docs/demos/demo-0.G.6-starrocks-*.json`, `--skip-ssl` patched) + handbook §2/§3.
- **No live-verify bugs** — first-try-green on all 12 verb invocations; the `--skip-ssl` +
  backplane-IP-mapping contract specifics were caught from the infra read, not a live failure.

### Cold-rebuild — ✅ PROVEN (2026-06-12)

- From-zero cold-rebuild of the `analytics-starrocks` env: corrected the stale x86 `vmrun_path`,
  destroy (35 res) → apply → `smoke-0.G.6.ps1` ALL GREEN → the full verb matrix re-run GREEN against
  the rebuilt cluster. The FE bootstrap + BE join + schema-bootstrap + **operator-user** all ran
  in-graph (EXIT GATE GREEN) — the preemptive `depends_on backup-repo` ordering (from the 0.G.5
  lesson) avoided any operator/backup race. **Cold-rebuild-surfaced transient (VMware, recovered):** a
  fresh FE clone (`sr-fe-follower-2`) booted with no service-NIC IP (the known StarRocks 0.G.6
  transient) → `vmrun connectNamedDevice` + `vmrun reset` → rejoined in ~85 s, apply proceeded. See
  `docs/verification/0.G.6-starrocks.md`.

## [0.6.4] — 2026-06-11

Phase 0.G.5: the **ClickHouse (3 shards × 2 replicas) + ClickHouse Keeper RAFT adapter** — the first
**analytics-tier** and first **genuinely sharded** adapter — live-verified end-to-end against the
running `clickhouse` cluster (6 data nodes `ch-shard{1,2,3}-rep{1,2}` + 3-node ClickHouse Keeper
`ch-keeper-1/2/3`). Auth model decided from a live probe: **password-auth** (sha256_password over the
mTLS wire; `default` loopback-only), so it reuses the v0.6.1 Vault-KV operator-credential model
verbatim (no framework change). AOT **24.84 MB / 30 MB** (+0.66 MB over v0.6.3). 71/71 tests.

### Added — ClickHouse adapter (Phase 0.G.5)

- **`ClickHouseAdapter`** implements all of `IClusterAdapter` over SSH + on-node `clickhouse-client`
  (native TLS `:9440`) + the Keeper four-letter-word interface (`echo mntr | nc 127.0.0.1 9181`) — no
  managed `ClickHouse.Client` driver (NetArchTest-enforced): `status`/`topology` (per-node liveness +
  shard/replica from the hostname + Keeper leader; **`topology` populates `Shards` — 3 shards × 2
  replicas** — the first sharded adapter to do so) · `health` (keeper-quorum + per-node server-active +
  an **operator-auth** round-trip + **distributed-membership** `system.clusters`=6 + **distributed-query**
  `nexus.events`=600 + per-node replica-health) · `failover-test cluster clickhouse` (**Keeper RAFT
  leader re-election**, RTO ≈ 1.1s — the fastest of the data tier) · `scale-out add`/`remove`
  (start/stop `nexus-clickhouse-server`, ReplicatedMergeTree rejoin via Keeper / graceful leave,
  last-replica guard) · `backup take`/`restore` (native `BACKUP/RESTORE TABLE … TO/FROM
  Disk('analytics_backups', …)` round-trip on the shared NFS repo) · `cert-rotate` (Vault re-issue →
  `pki_int/issue/clickhouse-server`, all 9 nodes, PKCS#8 key + intermediate+root ca, rolling restart,
  data-first/Keeper-leader-last) · `acl list/grant` (`system.users`/`system.grants` + idempotent
  `CREATE USER … ON CLUSTER`) · `chaos` (process-kill `nexus-clickhouse-server` + ReplicatedMergeTree
  rejoin). Two control planes: the `clickhouse-client` data plane + the Keeper coordination plane.
- **Operator-credential model** reused from v0.6.1 (ADR-0011): authenticate as the dedicated
  `nexus-cluster-admin` ClickHouse user (`sha256_password`, `GRANT ALL WITH GRANT OPTION`, distinct
  from the engine's built-in `admin`); password ONLY in Vault KV
  (`nexus/analytics/clickhouse/operator-password`) via the optional `INexusVaultClient`.
- **Infra:** nexus-infra-vmware security `role-overlay-vault-clickhouse-creds-seed.tf` **v2**
  (+operator-password, sticky-seeded; no agent-policy change — the existing policy already wildcard-reads
  the clickhouse KV subtree) + nexus-infra-analytics `role-overlay-clickhouse-operator-user.tf`
  (idempotent `CREATE USER … ON CLUSTER` reading the password on-node via the agent token).
- **ADR-0014** + `docs/verification/0.G.5-clickhouse.md` (live evidence) + 11 System B demos
  (`docs/demos/demo-0.G.5-clickhouse-*.json`) + handbook §2/§3.
- **Live-verify bug:** `access_management` is not a per-user `SETTINGS` value in CH 26.5 (Code 115) —
  the `GRANT ALL` privilege group confers access-management for SQL-created users.

### Cold-rebuild — ✅ PROVEN (2026-06-11)

- From-zero cold-rebuild of the `analytics-clickhouse` env: corrected the stale x86 `vmrun_path`
  default → non-x86 (baked into fresh state), destroy (50 res) → apply → `smoke-0.G.5.ps1` ALL GREEN
  → the full verb matrix re-run GREEN against the rebuilt cluster. The operator-user overlay ran
  in-graph (EXIT GATE GREEN). **Cold-rebuild-surfaced bug:** the operator-user + backup-repo overlays
  raced (both only depended on schema-bootstrap; backup-repo restarts all 6 servers → killed the
  operator-user's clickhouse-client, rc=138) — fixed by ordering operator-user after backup-repo
  (operator_v→2). See `docs/verification/0.G.5-clickhouse.md`.

## [0.6.3] — 2026-06-11

Phase 0.G.4: the **PostgreSQL Patroni HA + etcd DCS + HAProxy VIP adapter** — the third
password-auth adapter and the first **single-leader streaming-replication** engine — live-verified
end-to-end against the running `postgres` cluster (scope `nexus-pg`: 3 PG + 3 etcd + 2 HAProxy, VIP
`.60`). Reuses the v0.6.1 Vault-KV operator-credential model verbatim (no framework change). AOT
**24.18 MB / 30 MB** (+0.15 MB over v0.6.2). 71/71 tests.

### Added — Patroni adapter (Phase 0.G.4)

- **`PatroniAdapter`** implements all of `IClusterAdapter` over SSH + on-node `patronictl` / `psql` /
  `pg_dump` / `etcdctl` (no managed Npgsql driver): `status` (patronictl list + etcd/haproxy liveness
  + VIP holder) · `health` (single-leader + per-node patroni-state + replication-lag + a TLS+scram
  `vip-writable` round-trip + **authed etcd quorum** + haproxy) · `topology` · `failover-test cluster
  postgres` (**patronictl switchover**, RTO ≈ 4.6s measured at the VIP, auto-switch-back) · `scale-out
  add`/`remove` (start/stop `nexus-patroni`, rejoin streaming / graceful leave, leader-guard) ·
  `backup take`/`restore` (`pg_dump --no-owner` → operator-owned verify **database** round-trip) ·
  `cert-rotate` (Vault re-issue → `pki_int/issue/patroni-server`, all 8 nodes, per-role reload/restart,
  leader-last) · `acl list/grant` (`pg_roles` `\du`-equivalent + idempotent `CREATE ROLE`) · `chaos`
  (process-kill `nexus-patroni` + Patroni rejoin). Three control planes: Patroni + etcd (RBAC) +
  HAProxy leader-routing VIP.
- **Operator-credential model** reused from v0.6.1 (ADR-0011): authenticate as the dedicated
  `nexus-cluster-admin` role (LOGIN CREATEROLE CREATEDB REPLICATION + pg_monitor/pg_read_all_data/
  pg_write_all_data — **not** superuser); its password lives only in Vault KV
  (`nexus/oltp/patroni/operator-password`), fetched at runtime via `INexusVaultClient`. Infra
  (nexus-infra-vmware security creds-seed v2 + Patroni agent-policy v3; nexus-infra-oltp
  `role-overlay-patroni-operator-user.tf` + the patroni.yml `ctl:` block).
- **[ADR-0013]** — PatroniAdapter (Patroni + etcd + HAProxy). **`docs/verification/0.G.4-postgres.md`**
  — full live evidence + the bugs live-verify caught. **`docs/demos/DEMO-52..62`** (11) — System B demos.

### Fixed (surfaced by live-verify against the running cluster)

- **patronictl switchover 403 "client certificate required"** — Patroni REST `verify_client: optional`
  requires a client cert for state-changing calls; patroni.yml had no `ctl:` section. Added the `ctl:`
  block (node's own TLS as the client cert) to the bootstrap overlay.
- **patronictl exits 0 even on a refused switchover** — validate the `"Successfully switched over"`
  banner in stdout, not the exit code.
- **`backup restore` permission denied / "must be owner of table"** — the operator's `pg_*_all_data`
  grants are DATA, not DDL (can't `CREATE SCHEMA` in db postgres). Restore into a fresh
  operator-owned **database** (CREATEDB); `pg_dump --no-owner --no-privileges`.
- **`cert-rotate` vault issue 500** — used domain `etcd.nexus.lab`; the PKI role `patroni-server`
  only allows `patroni.nexus.lab` (all 8 nodes).

### Verified — cold-rebuild PROVEN

- The Patroni infra (operator-user overlay + patroni.yml `ctl:` block) was proven in a **from-zero
  cold-rebuild** of the `oltp-patroni` cluster (destroy → apply → `smoke-0.G.4.ps1` ALL GREEN), which
  also baked the correct non-x86 `vmrun_path` into fresh state. The adapter verb matrix re-ran green
  against the rebuilt cluster. A 5th, latent infra bug surfaced + fixed in passing: the HAProxy
  `chroot` directive is incompatible with the unit's `User=haproxy` (cold start 500'd "Cannot chroot")
  — dropped in `nexus-infra-oltp` `role-overlay-haproxy-config.tf` v3.

## [0.6.2] — 2026-06-05

Phase 0.G.3: the **Percona XtraDB Cluster (Galera) + ProxySQL adapter** — the second password-auth
adapter and the first **synchronous multi-primary** engine — live-verified end-to-end against the
running `percona` cluster. Reuses the v0.6.1 Vault-KV operator-credential model verbatim (no
framework change). AOT **24.03 MB / 30 MB** (+0.13 MB over v0.6.1). 71/71 tests.

### Added — Percona adapter (Phase 0.G.3)

- **`PerconaAdapter`** implements all of `IClusterAdapter` over SSH + on-node `mysql` (PXC backends +
  the ProxySQL `:6032` admin) + `mysqldump` (no managed driver): `status` · `health` (per-PXC
  wsrep-state/size/status/ready + ProxySQL liveness) · `topology` · `failover-test cluster percona`
  (ProxySQL writer failover, live RTO ≈ 2.3s) · `scale-out add`/`remove` (Galera SST join /
  graceful leave, writer-guard) · `backup take`/`restore` (`mysqldump --skip-add-locks` + restore
  round-trip) · `cert-rotate` (Vault re-issue → `pki_int/issue/percona-server`, rolling restart of
  all 5 nodes) · `acl list/grant` (`mysql.user` + `CREATE USER`/`GRANT`) · `chaos` (process-kill
  `nexus-percona` + Galera rejoin). Two control planes: PXC `wsrep_%` status + ProxySQL admin
  `runtime_mysql_servers` (ONLINE-only) for the writer/reader hostgroup map.
- **Operator-credential model** reused from v0.6.1 (ADR-0011): authenticate as the dedicated
  `nexus-cluster-admin` MySQL user (ALL PRIVILEGES WITH GRANT OPTION); its password + the ProxySQL
  admin password live only in Vault KV (`nexus/oltp/percona/operator-password`,
  `.../proxysql-admin-password`), fetched at runtime via `INexusVaultClient`. Infra (nexus-infra-vmware
  security creds-seed v2 + PXC agent-policy v2; nexus-infra-oltp `role-overlay-percona-operator-user.tf`)
  — proven in a from-zero cold-rebuild apply graph.
- **[ADR-0012]** — PerconaAdapter (Galera + ProxySQL). **`docs/verification/0.G.3-percona.md`** —
  full live evidence + the bugs live-verify caught. **`docs/demos/DEMO-41..51`** (11) — System B demos.

### Fixed (surfaced by live-verify against the running cluster)

- **ProxySQL `SHUNNED` status read as a writer** — only `ONLINE` rows in `runtime_mysql_servers`
  reflect a node's effective hostgroup (a node lingers in writer-10 as SHUNNED while serving from
  backup-20). Without the filter all 3 PXC nodes looked like the writer.
- **`"inactive".Contains("active") == true`** — `scale-out add` node-discovery + ProxySQL liveness
  used a substring check; fixed with an exact `is-active` prefix match.
- **PXC `strict_mode=ENFORCING` rejects `LOCK TABLES`** — mysqldump's default `--add-locks` aborted
  the restore (0 rows). Fixed: `--skip-add-locks --no-tablespaces`.
- **(infra) galera-bootstrap `sed -e '$a\'`** — `$a` eaten by PowerShell `@"..."@` interpolation
  ("sed: missing command") failed the from-zero apply; replaced with `printf '\n…\n'`.

## [0.6.1] — 2026-06-05

Phase 0.G.2: the **MongoDB adapter** — the first **password-authenticated** cluster adapter —
live-verified end-to-end against the running `mongo` replica set (`nexus-rs`). Establishes the
**Vault-KV operator-credential model** for every password-auth engine to come (Percona / Patroni /
SQL). AOT **23.9 MB / 30 MB** (+0.13 MB over v0.6.0). 71/71 tests.

### Added — Mongo adapter (Phase 0.G.2)

- **`MongoAdapter`** implements all of `IClusterAdapter` over SSH + on-node `mongosh` / `mongodump` /
  `mongorestore` (no managed driver): `status` · `health` (quorum / single-primary / per-secondary
  lag) · `topology` · `failover-test cluster mongo` (`rs.stepDown`, live RTO ≈ 2.8s) ·
  `scale-out add`/`remove` (`rs.add`/`rs.remove`, primary-guard) · `backup take`/`restore`
  (`mongodump --archive --gzip` + `mongorestore` ns-remap round-trip) · `cert-rotate` (genuine
  re-issue via the node's own Vault token → `pki_int/issue/mongo-server`, rolling restart) ·
  `acl list/describe/grant/revoke` (`getUsers`/`createUser`/`grantRolesToUser`) · `chaos`
  (process-kill `nexus-mongo` + observe + self-revert).
- **Operator-credential model** — adapter authenticates as the dedicated least-privilege
  **`nexus-cluster-admin`** user (clusterMonitor + clusterManager + backup + restore +
  userAdminAnyDatabase); its password lives **only** in Vault KV (`nexus/oltp/mongo/operator-password`)
  and is fetched at runtime via the new **optional `INexusVaultClient`** plumbed through
  `ClusterBootstrapper` (`TryBuildVaultClient` from `VAULT_ADDR`/`TOKEN`/`CACERT`; mTLS-only Redis +
  Kafka unaffected; a missing token yields an actionable error). Infra (nexus-infra-vmware security +
  nexus-infra-oltp oltp-mongo): operator-password seed + agent-policy v3 (read grant) + idempotent
  `nexus-cluster-admin` createUser overlay — proven in a from-zero cold-rebuild apply graph.
- **[ADR-0011]** — MongoAdapter + the Vault-KV operator-credential model.
  **`docs/verification/0.G.2-mongo.md`** — full live evidence + the four bugs live-verify caught.
  **`docs/demos/DEMO-30..40`** (11) — executable, self-verifying System B demos for the mongo verb surface.

### Fixed (surfaced by live-verify against the running RS)

- **`--eval` single-quote mangling** — the remote shell wraps `--eval '<js>'` in single quotes, so all
  embedded JS now uses double-quoted literals (single-quoted JS terminated the shell quote →
  `SyntaxError` on `scale-out`/`failover`).
- **`mongodump` dumped 0 app docs** — the URI's `/admin` database path scoped the dump to admin system
  collections (fixed: target `/nexus_smoke?…&authSource=admin`); a `readPreference=secondary` dump
  returned 0 docs (fixed: read from PRIMARY).
- **`mongorestore` ns-remap restored 0 docs** — `--nsInclude` is required to select the namespace
  before `--nsFrom`/`--nsTo` rename it.
- **`backup restore` archive discovery** — backups are node-local on the secondary that ran the dump;
  restore now finds the node holding the archive and runs there.

## [0.6.0] — 2026-06-05

Phase 0.G data-tier adapter expansion begins: the **`IClusterAdapter` SPI framework** (0.G.0)
plus the **first concrete adapter — Redis (0.G.1)** — live-verified end-to-end against the
running cluster. AOT exit gate raised to ≤30 MB (ADR-0024); **23.77 MB** observed (was 22.75 at
v0.5.0). 71/71 tests.

### Added — Redis adapter (Phase 0.G.1)

- **`RedisAdapter`** implements all of `IClusterAdapter` over SSH + on-node `redis-cli` (no managed
  driver): `status` · `health` · `topology` · `failover-test cluster redis` (live RTO ≈ 2.1s) ·
  `cert-rotate` (genuine re-issue via the node's own Vault token → `pki_int/issue/redis-server`) ·
  `acl list/describe` · `backup take`/`restore` (node-local BGSAVE snapshot + restore round-trip) ·
  `scale-out add`/`remove` (apply-on-demand, role-aware membership) · `chaos` (network-partition /
  packet-loss / slow-disk / cpu-starve / memory-pressure / process-kill — time-boxed + self-reverting).
- **[ADR-0010]** — cross-adapter patterns (scale-out = IaC-provisions + role-aware join; chaos =
  on-node `nexus-chaos.sh` with `systemd-run` self-revert; backup = engine dump + restore verify) +
  the Redis exemplar. **`Cluster/Resources/nexus-chaos.sh`** embedded on-node chaos helper.
  **`docs/handbook.md`** (new) — analytical verb reference + troubleshooting runbook.
  **`docs/verification/0.G.1-redis.md`** — full live evidence + the three bugs live-verify caught.

### Fixed (surfaced by live-verify against the running cluster)

- **Redis connection contract** — mTLS-only (`ca.crt` + client cert/key; NO AUTH password); the unit
  is `nexus-redis` (stock `redis-server` is masked). The framework-ship verbs assumed an AUTH
  password + `ca.pem` + `redis-server` and silently failed against the real cluster.
- **`failover` original-primary** — resolved from the replica's live `INFO replication` master_host
  (was a hostname-index heuristic that breaks once roles move).
- **`cert-rotate` did not rotate** — the Agent's `pkiCert` template caches the 90-day leaf; v0.6.0
  issues a fresh leaf directly via the node's Vault token (SSH-shell-out; JSON parsed in-AOT).

### Changed

- AOT exit gate **25 → 30 MB** (ADR-0024 / ADR-0009) in `scripts/cli.ps1` + CI. win-x64 **23.77 MB**.

### Added — framework pre-flight canon (Phase 0.G.0, 2026-05-15)

- **[ADR-0009](docs/adr/ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md)** — *Phase 0.G v0.6: `IClusterAdapter` SPI for the data-tier verb expansion; System B JSON demo spec extended* — formalises the cluster-adapter framework (one adapter per cluster, SSH-shell-out per ADR-0008, no managed DB drivers linked), the 13 verb groups per cluster (`cluster-status` · `failover-test` · **`scale-out`** add/remove · **`scale-up`** · `backup` take/restore · `health` · `topology --watch` · `cert-rotate` · `chaos` · `acl` · `demo`), and the extended System B JSON spec shape (5 new optional fields: `prerequisites`, per-step `expectedExitCode`, `expectedOutputContains`, `observe[]`, `whatProves`). Implementation-side mirror of [`nexus-platform-plan` ADR-0024](https://github.com/grezap/nexus-platform-plan/blob/main/docs/adr/ADR-0024-aot-gate-amendment-and-cluster-adapter-framework.md). AOT exit gate raised to **≤30 MB** for v0.6.0 → v0.7.0; the v0.5.0 historical ≤25 MB gate (22.75 MB observed) stays sealed.
- **Code (0.G.0d):** `Nexus.Cli.Core.Models.Demo` extended with optional fields on `DemoSpec` / `DemoStep` / `DemoStepResult`; new `DemoObservation` + `DemoPrerequisites` records. `Nexus.Cli.Adapters.Json.NexusJsonContext` extended with the new DTOs. `JsonDemoCatalog.Load()` maps the new optional JSON fields onto the extended model (backwards compatible — existing specs parse unchanged). `DemoRunner.RunAsync()` enforces `expectedExitCode` + `expectedOutputContains` per step; specs without expectations behave exactly as in v0.4.0. New tests in `tests/Nexus.Cli.Tests/Demos/`.

## [0.5.0] — 2026-05-15

Phase 0.F finished: the **fifth and last master-plan verb** ships, closing
the v0.x roadmap. **All 5 of 5 verbs now live.** Newly unblocked by Phase
0.H (`nexus-infra-kafka` `v0.1.0`, 2026-05-15).

### Added

- **`nexus kafka failover east-to-west [--yes] [--json] [--no-color]`** and
  **`nexus kafka failover west-to-east [...]`** — drive a region-loss Kafka
  DR failover between the East + West KRaft clusters and measure RTO. Both
  directions verified live against the running tier:
  - **east → west: RTO 13.20 s** (52.04 s end-to-end, including
    vmrun-suspend × 3 brokers + target produce/consume round-trip +
    vmrun-resume + KRaft quorum reform on the source).
  - **west → east: RTO 13.57 s** (53.67 s end-to-end). The more
    demo-worthy direction — the whole ecosystem tier (Schema Registry,
    REST Proxy, Connect, ksqlDB) is `kafka-east`-pinned, so they all keep
    serving uninterrupted while `kafka-west` is down.
  - Workflow per [ADR-0008](docs/adr/ADR-0008-kafka-failover-demo-grade-via-ssh.md):
    pre-flight (target healthy?) → vmrun-suspend × 3 source brokers
    (sequential, 2 s inter-suspend gap to dodge the `0.H.6`
    VMware-under-load flake) → produce/consume round-trip on a fresh
    RF=3 probe topic on the target (3 s poll, 90 s deadline) → vmrun
    start nogui × 3 to recover → wait for source KRaft quorum
    re-form (4 min deadline).
  - **Master-plan gate (line 258 — "Kafka DR east→west < 60 s via
    `nexus-cli kafka failover`"): MET**, with ~46 s headroom against the
    60 s budget.
  - Exit codes: `0` failover OK + recovery clean, `1` target did NOT
    serve under source-loss (DR posture broken), `2` service-side error
    (pre-flight failed, vmrun suspend failed, recovery failed), `3`
    operator aborted at the interactive prompt.
- **`KafkaFailoverService`** in `Nexus.Cli.Adapters.Cluster` — ~250-LOC
  orchestrator with a single monotonic `Stopwatch` driving the 5-phase
  `KafkaFailoverTimeline` (preFlight → failureInjected → targetHealthy →
  recoveryAttempted → sourceHealthyAgain).
- **`KafkaFailoverBootstrapper`** — DI wiring, lighter than
  `FailoverTestBootstrapper` (no Vault tokens consumed; the kafka CLI
  tools on each broker authenticate to their own KRaft cluster via the
  broker's on-disk PEM keystore, rendered by Vault Agent at the OS
  layer).
- **ADR-0008** records the design: demo-grade scope (vs
  production-grade per-consumer-group offset translation, deferred to
  v0.5.1); SSH + on-broker `kafka-*` CLI shell-out (vs adding the
  `Confluent.Kafka` NuGet, which would blow the 25 MB AOT exit gate and
  introduce `librdkafka` AOT risk); subcommand shape mirroring the
  established `failover-test` family.
- **`docs/verification/0.5.0-kafka-failover.md`** — full live evidence
  for both directions, the CLI surface, the exit-code semantics, the
  `--json` shape, and the "what got fixed during the run" recovery
  playbook.

### Fixed

- **`SshKeyDiscovery` preferred the operator's personal `~/.ssh/id_ed25519`
  over the lab-canonical `~/.ssh/nexus_gateway_ed25519`.** The system
  `ssh.exe` resolves the right key via `~/.ssh/config`'s `Host
  192.168.70.*` stanza (→ `IdentityFile ~/.ssh/nexus_gateway_ed25519`);
  SSH.NET (used by the CLI) does not parse `ssh_config`, so it was
  falling back on the first `id_ed25519` it found — the operator's
  personal/GitHub key, NOT authorized on any lab VM. Surface symptom:
  every SSH-using verb's pre-flight failed with `Permission denied
  (publickey)`. **Fix:** `SshKeyDiscovery.DefaultRelativePaths` now
  prefers `nexus_gateway_ed25519` first, then `id_ed25519`, then
  `id_rsa`. The unavailable-message names the lab-canonical path
  explicitly. Surfaced by the v0.5.0 kafka failover smoke test; the
  same bug latently affected every SSH-using verb on any build host
  where the operator's personal key differs from the lab key.
- **Pre-flight health probe matched the wrong field name.**
  `kafka-metadata-quorum.sh ... describe --status` on Apache Kafka 3.8
  emits `LeaderId:`; the v0.5.0 initial code grepped for
  `CurrentLeader:` (an older KRaft-draft field name). Surface symptom:
  "pre-flight: target cluster is not healthy" against a perfectly
  healthy cluster. Fix: the probe now regex-matches `LeaderId:\s+(\d+)`
  and verifies the parsed integer is positive.

### Changed

- AOT publish footprint: **win-x64 22.75 MB** (was 22.65 MB at v0.4.0;
  +0.10 MB for the v0.5.0 verb — entirely new C# code, no new NuGets).
  **2.25 MB headroom** vs the 25 MB master-plan exit gate. The
  "managed-vs-native-CLI decision" the memory pre-flagged is resolved in
  favour of native; the v0.x roadmap closes without needing to add
  `Confluent.Kafka` or drop AOT.
- Version bumped 0.4.0 → 0.5.0.
- `Program.cs`: replaced the stub `kafka.AddCommand<KafkaFailoverCommand>`
  registration with the new `failover east-to-west` / `failover
  west-to-east` branch.
- `AotRoots.cs`: removed the stub `KafkaFailoverSettings` root; added
  the two new command + settings type pairs.

### Deferred to v0.5.x

- **Real per-consumer-group offset translation** via MM2's
  `<src>.checkpoints.internal` topic. Building it now would ship
  under-exercised — no real consumer app exists yet to translate
  offsets FOR. The first real consumer is `streamcore` (Phase 12);
  building this alongside that lets us test against actual consumer
  behaviour. See ADR-0008 § "Fork 1 — what should the verb DO?" for the
  full reasoning.
- **A `--reverse` reconcile mode** for "east came back up, replay
  west's drift back into east" (per DEMO-08 § 4). Same reason — needs
  a real consumer to be meaningful.

## [0.4.0] — 2026-05-14

Phase 0.F slice 4: the `demo` verb ships in full. **4 of 5 master-plan
verbs are now live** (`cluster-status`, `infrastructure`, `failover-test`,
`demo`); the last (`kafka failover` v0.5) remains stubbed pending
Phase 0.H Kafka ecosystem build-out.

### Added

- **`nexus demo list [--json]`** — enumerates demos from the catalog
  (default discovery: `NEXUS_DEMOS_PATH` env, then `./docs/demos/`,
  then `../docs/demos/`). Shows id + title + step count as a Spectre
  table, or JSON array via source-gen.
- **`nexus demo run <demo-id> [--json]`** — orchestrates a demo
  spec's steps sequentially. Each step is a shell command line
  executed through `cmd.exe /c` (Windows) or `/bin/sh -c` (Linux),
  so redirects + pipes + env-var expansion work naturally. Captures
  the last 12 lines each of stdout + stderr per step (avoids
  bloating the report). Per-step timeout 5 minutes; top-level
  CTS 15 minutes. Exit codes: `0` ok, `1` step failed,
  `2` load/spec error, `3` aborted (timeout or Ctrl-C).
- **`nexus demo record <demo-id> [--output-dir DIR] [--json]`** —
  generates a VHS `.tape` script (Type+Enter+Sleep per step) and
  invokes the `vhs` binary to render a GIF. If `vhs` isn't on PATH,
  the `.tape` file still lands on disk and the report includes
  `VhsAvailable=false` with the install hint
  (`winget install charmbracelet.vhs` / `brew install vhs`).
- **`Nexus.Cli.Adapters.Demos.JsonDemoCatalog`** — reads
  `<dir>/<id>.json` files via source-gen JSON (`NexusJsonContext.
  DemoSpecJson`). ~80 LOC, BCL-only, AOT-clean.
- **`Nexus.Cli.Adapters.Demos.DemoRunner`** — sequences steps;
  generates the VHS `.tape` for record mode. ~150 LOC.
- **`Nexus.Cli.Adapters.Vhs.VhsProcessClient`** + **`VhsPaths`** —
  shells out to vhs binary, with `NEXUS_VHS_PATH` env override and
  install-hint message on absence.
- **`DemoBootstrapper`** — no Vault dependency; pure local-shell +
  vhs subprocess orchestration. Methods static (no shared instance
  state per CA1822).
- **Sample demos** in `docs/demos/`:
  - `DEMO-01-cluster-status.json` — runs `nexus cluster-status`.
  - `DEMO-02-infrastructure.json` — runs `nexus infrastructure list`
    then `... status foundation`.
- **`docs/demos/README.md`** — demo spec format documentation +
  discovery rules + VHS install instructions.

### Changed

- AOT publish footprint: **win-x64 22.65 MB** (was 22.39 MB at v0.3.2;
  +0.26 MB for demo orchestration + VHS adapter + JSON contracts).
  Headroom under the 25 MB master plan exit gate now ~2.35 MB.
- Version bumped 0.3.2 → 0.4.0 (last bump before v1.0; v0.5 closes
  the verb list).

### Deferred

- **Playwright bridge for web-UI demos** (operator surface E30
  envisioned both terminal + web). Defer to v0.4.x; Playwright as
  a managed library is AOT-hostile and would likely push the binary
  over the 25 MB exit gate. Alternative for web UI: shell out to
  `playwright` CLI binary similar to the vhs pattern.
- **`--all` recursion** (`nexus demo record --all`) — render every
  demo in the catalog. Stub from v0.1 retired; this lands in v0.4.x
  once we have enough demos to justify it.
- **CI-friendly demos** that don't require Vault auth (so the
  release.yml could produce a demo GIF as a release asset). Probably
  the right shape is a separate `--mode dry-run` flag that doesn't
  actually execute the shell commands, just types them in the VHS
  recording. v0.4.x candidate.

## [0.3.2] — 2026-05-14

Phase 0.F slice 3 complete: the third and final `failover-test` scenario
ships. **All five master-plan verbs from `failover-test` are now live**
(consul-leader + nomad-leader + swarm-manager); the remaining 2 of 5
top-level verbs (`kafka failover` v0.5, `demo run/record` v0.4) stay
stubbed.

### Added

- **`nexus failover-test swarm-manager [--node NAME] [--yes] [--json]`** —
  drives a HOST-LEVEL planned failure of the current Docker Swarm raft
  leader and measures RTO. Structurally different from consul-leader
  and nomad-leader:
  - **Failure injection:** `vmrun.exe suspend` the leader's VM (reuses
    `VmrunProcessClient` from the v0.2 infrastructure adapter).
    Service-level scenarios used `sudo systemctl stop`.
  - **Leader discovery:** SSH + `docker node ls --format` (Docker
    Swarm raft has no public HTTP API like Consul/Nomad). Parses for
    `ManagerStatus=Leader`.
  - **Recovery:** `vmrun.exe start <vmx> nogui` to resume the
    suspended VM.
  - **Healthy check:** all 3 swarm-managers show `Status=Ready` in
    `docker node ls`. Uses `VmRecoveryWaitDeadline=3 min` (longer than
    service-level's 45s) for VM boot + Docker engine startup +
    Swarm rejoin.
  - **Linux build host:** `IVmrunClient.IsAvailable` returns false on
    Linux, so the scenario fails fast with a clear "Windows-only build
    host" message — same posture as the v0.2 infrastructure verb's
    suspend/resume.
- `FailoverTestService.RunSwarmManagerAsync` + private helpers
  `TryGetSwarmLeaderAsync` (probe each manager until one returns a
  Leader line), `GetSwarmLeaderFromAsync` (probe a specific node),
  `GetSwarmManagerStatusesAsync` (poll all managers' status for the
  healthy-wait check).
- `FailoverTestService` constructor now takes `IVmrunClient` alongside
  `ISshClient`; consul-leader + nomad-leader callers unaffected (they
  never touch vmrun).
- `FailoverTestBootstrapper` instantiates `VmrunProcessClient` and
  plumbs it through.

### Changed

- AOT publish footprint: **win-x64 22.39 MB** (was 22.37 MB at v0.3.1;
  +0.02 MB for the third command class). 2.61 MB headroom under the
  25 MB master plan exit gate.
- Version bumped 0.3.1 → 0.3.2.

### Engineering note

Although swarm-manager is the third scenario, **no shared engine was
extracted**. The two HTTP-based scenarios (consul-leader, nomad-leader)
overlap ~90% but the host-level scenario's primitives diverge:
- vmrun for failure injection (vs SSH+systemctl)
- SSH+docker for leader discovery (vs HTTP probe)
- Three independent raft clusters affected by the failure (vs one)

A unified `FailoverEngine` covering all three would need too many
"if scenario is X" branches. Better as 3 parallel methods sharing
the report shape (`FailoverTestReport`, `FailoverTimeline`) and
rendering layer (`FailoverTestRender`). Revisit if a 4th scenario
lands.

### Deferred

- `--election-timeout`, `--recovery-timeout` etc. as CLI flags
  (currently private constants). Track for v0.3.x if real-world use
  demands tuning.
- `~/.ssh/config` `IdentityFile` honouring in `SshKeyDiscovery`
  (operator currently must set `NEXUS_SSH_KEY` explicitly when the
  lab key isn't named `id_ed25519` or `id_rsa`).

## [0.3.1] — 2026-05-13

Phase 0.F slice 3 continues: the second `failover-test` scenario ships.
Also folds in a CI-runner null-tolerance fix that prevented v0.3.0's
release.yml from creating a GitHub Release.

### Added

- **`nexus failover-test nomad-leader [--node NAME] [--yes] [--json]`** —
  drives a planned failure of the current Nomad raft leader and measures
  RTO. Same shape as v0.3.0's `consul-leader`: SSH-stop on the leader,
  poll a different manager's `/v1/status/leader` (Nomad HTTPS:4646),
  auto-restart, wait for 3 servers reconverged + leader re-elected.
  Reuses `SshNetClient`, `VmsYamlCatalog`, `FailoverTestService`'s
  timing infrastructure, and the `FailoverTestRender` human + JSON
  output. ~70% code reuse as forecast.
- `FailoverTestService.RunNomadLeaderAsync` — Nomad-specific
  orchestration. Differences from `RunConsulLeaderAsync`:
  - Probe port 4646 (HTTPS) vs 8501.
  - Leader address parsed from `NomadHealth.LeaderAddress` (e.g.,
    `192.168.10.111:4647`) vs `ConsulHealth.Leader`.
  - `systemctl stop|start nomad` vs `consul`.
  - Healthy check = `Servers.Count == 3 && LeaderAddress != null`
    (vs Consul's gossip count of 6 alive).
- **`FailoverTestBootstrapper`** now also resolves the Nomad mgmt token
  from Vault KV (`nexus/swarm/nomad-bootstrap-token`, field
  `management_token`) alongside the existing Consul mgmt token fetch.

### Fixed

- **`SshKeyDiscoveryTests.Resolve_Falls_Through_When_Env_Var_Points_At_Missing_File`**
  guards the `NotStartWith(tempPath)` assertion behind a null check.
  v0.3.0's `release.yml` failed on both linux-x64 and win-x64 runners
  because CI runners have no `~/.ssh/id_*` files, so `Resolve()` returns
  `null`, and FluentAssertions' `NotStartWith` errors on null. Same
  shape of regression as v0.2.0's `GetVmxPath` cross-platform fix
  (c124faa). The v0.3.0 tag stays on origin pointing at `ae5c4a9` with
  no Release attached; v0.3.1 supersedes it with a working CI path.

### Changed

- AOT publish footprint: **win-x64 22.37 MB** (was 22.34 MB at v0.3.0;
  +0.03 MB for the second command class). Headroom under the 25 MB
  exit gate unchanged at ~2.63 MB.
- Version bumped 0.3.0 → 0.3.1.

### Deferred

- **`nexus failover-test swarm-manager`** — v0.3.2. Bigger jump:
  host-level outage via vmrun-suspend (reuses the v0.2 infrastructure
  adapter); different recovery shape; first scenario where Vault HA's
  auto-unseal might briefly degrade if a swarm-manager VM hosts a
  Vault Agent path.
- **Engine extraction.** Per rule-of-three, the next slice (swarm-manager)
  is when extracting a shared `FailoverEngine.RunAsync(scenario,
  serviceName, probePort, leaderFn, healthFn)` becomes worthwhile.
  v0.3.1 keeps the two methods parallel-but-duplicated for now.

## [0.3.0] — 2026-05-13

Phase 0.F slice 3: the `failover-test` verb ships its first scenario.

### Added

- **`nexus failover-test consul-leader [--node NAME] [--yes] [--json]`** —
  drives a planned failure of the current Consul raft leader and measures
  RTO (recovery time objective). Workflow:
  1. Read the Consul mgmt token from Vault KV
     (`nexus/swarm/consul-bootstrap-token`).
  2. Identify the current leader via `/v1/status/leader` (probes each
     swarm-manager-N until one responds).
  3. Map the leader's RPC address (192.168.10.X:8300) back to a
     `vms.yaml` node. Refuses to act if the leader's IP isn't in canon —
     never SSHes blind.
  4. Pick a different manager as the polling endpoint (otherwise the
     500 ms-interval poll queries the very agent we're about to stop).
  5. SSH the leader → `sudo systemctl stop consul`. 20s timeout.
  6. Poll the non-leader endpoint every 500 ms until `/v1/status/leader`
     returns a different address; 60s election deadline.
  7. SSH the leader → `sudo systemctl start consul` (auto-recovery). On
     failure, the JSON output's `recoveryHint` carries the exact recovery
     command for the operator.
  8. Wait for the recovered agent to rejoin gossip (alive count back to
     full); 45s deadline.
  - Exit codes: `0` ok, `1` no new leader within deadline, `2`
    recovery failed (operator must run `recoveryHint`).
  - `--node NAME` asserts which node the operator expects to be leader
    before injecting failure; aborts if mismatched.
  - `--yes` skips the confirm prompt (mirrors the v0.2 infra confirm UX).
  - `--json` emits `FailoverTestJsonOutput` (source-gen, no reflection).
- **SSH adapter** — `Nexus.Cli.Adapters.Ssh.SshNetClient`, a thin
  wrapper around SSH.NET 2025.1.0. Pure-managed library; declares
  `IsAotCompatible=true`; trim profile clean under `partial` mode.
  Stateless: each `ExecuteAsync` opens a fresh connection, runs one
  command, disconnects. `SshKeyDiscovery` resolves the operator's
  private key (NEXUS_SSH_KEY env → `~/.ssh/id_ed25519` →
  `~/.ssh/id_rsa`). Rationale in **ADR-0007**.
- **Failover service** — `FailoverTestService` in
  `Nexus.Cli.Adapters.Cluster`. ~150-LOC orchestrator with a single
  monotonic Stopwatch driving the 5-phase `FailoverTimeline` (preflight
  → failure → newLeader → recovery → healthy).
- **ADR-0007** records the SSH.NET decision over ssh.exe shell-out
  (which would reintroduce every MEMORY SSH pain point) or native
  libssh (cross-RID native DLL distribution cost).
- **3 new unit tests** — `SshKeyDiscovery` (env-var honoured, falls
  through on missing path, UnavailableMessage mentions both env and
  canonical paths) + 1 JSON round-trip for `FailoverTestJsonOutput`.
  58/58 unit tests total (was 54; +4).
- **NEXUS_SSH_USER env var** (default `nexusadmin`) lets the operator
  override the lab username if needed.

### Changed

- AOT publish footprint: **win-x64 22.34 MB** (was 10.92 MB at v0.2.1;
  +11.4 MB attributed to SSH.NET 2025.1.0 internals reachable now that
  we actually call it — at v0.2 it trimmed to ~0 because only the type
  was referenced). Still under the 25 MB master plan exit gate but
  headroom dropped from 14 MB to 2.66 MB. Tracked in the verification
  doc; the v0.4 demo and v0.5 kafka slices need to fit in that 2.66 MB
  or the exit gate needs revisiting.
- Version bumped 0.2.1 → 0.3.0.

### Deferred

- **`nexus failover-test nomad-leader`** — v0.3.1. Same SSH/raft/timing
  infrastructure as consul-leader; only the leader-discovery API + the
  systemd unit name change. ~70% code reuse.
- **`nexus failover-test swarm-manager`** — v0.3.2. Bigger jump:
  vmrun-suspend the host (host-level outage vs service-level), longer
  recovery, different state observability.
- **`--mode host` flag** for host-level failure injection (vmrun
  suspend instead of systemctl stop). Tracked for v0.3.x.
- **Tunables as CLI flags** (election deadline, recovery wait, poll
  interval). Currently private constants. Move to `--election-timeout`
  etc. if real-world use demands it.

## [0.2.1] — 2026-05-08

Phase 0.F v0.2.x carryover landed: both deferred items from the v0.2.0
CHANGELOG are now resolved. No new commands; no new verbs; same operator
surface as v0.2.0.

### Changed

- **Spectre.Console + Spectre.Console.Cli bumped 0.50 → 0.55.** Two
  breaking signature changes propagated through every command:
  - `Command<T>.Execute` and `AsyncCommand<T>.ExecuteAsync` now take a
    framework-supplied `CancellationToken` as their last parameter.
    Spectre wires the token to the host's Ctrl-C signal, so long-running
    commands can be interrupted cleanly. Each command links the
    framework token to its existing internal timeout via
    `CancellationTokenSource.CreateLinkedTokenSource`.
  - Both methods moved from `public override` to `protected override`.
    Spectre invokes them through a public trampoline; user code no
    longer exposes the args directly.
- AOT publish footprint: win-x64 10.92 MB (was 10.12 MB; +0.80 MB
  attributed to Spectre 0.55 internals), still well under the 25 MB
  master plan exit gate.

### Fixed

- **Suspended-vs-stopped state inference is now correct on Workstation
  Pro 17.5+.** v0.2.0's heuristic checked for `<vm-name>.vmss` /
  `<vm-name>.vmem` next to the .vmx, but Workstation Pro 17.5+ session-
  suffixes the memory paging file (e.g. `vault-3-3c85c1f6.vmem`).
  The exact-name lookup never matched, so post-suspend status defaulted
  to `stopped`. New implementation does a directory-prefix search
  (`<basename>*.vmss` OR `<basename>*.vmem`) — catches both the older
  un-suffixed shape and the 17.5+ session-suffixed shape. Each VM lives
  in its own subdir per the `vmware_per_vm_folders` canon, so the search
  is bounded.
- Verified by a live `suspend → status → resume → status` round-trip on
  `foundation/vault-3`: post-suspend status now reports `suspended`
  (was `stopped` in v0.2.0). Vault Raft kept quorum on vault-1 + vault-2
  during the suspend window.

### Tests

- 54 unit tests pass (51 + 3 new): `GetVmemSidecar`,
  `HasSuspendedStateSidecar` (5-fixture truth table covering bare and
  session-suffixed shapes for both .vmss and .vmem), and
  `SuspendAsync_Recognises_Session_Suffixed_Vmem_As_Already_Suspended`
  (uses the canonical `vault-3-3c85c1f6.vmem` shape from real-world
  inspection of the build host).
- The previous v0.2.0 cross-platform fix (`GetVmxPath` test using
  `Path.Combine` on both sides instead of a Windows-literal expectation,
  shipped as `c124faa` to recover the v0.2.0 release.yml run) carries
  forward.

### Deferred

Phase 0.F v0.2.x backlog is now empty. Next slice = v0.3 = `failover-test`
(SSH client + Nomad/Consul raft introspection + RTO measurement).

## [0.2.0] — 2026-05-08

Phase 0.F slice 2: the `infrastructure` verb ships in full.

### Added

- **`nexus infrastructure list`** — render the entire fleet declared in
  `nexus-platform-plan/docs/infra/vms.yaml` as a Spectre table decorated
  with live VMware state (`running` / `suspended` / `stopped` / `missing`
  / `unknown`). 81 VMs across 12 clusters in the canonical file; the live
  build host returns a mix of running (the 0.E.4-deployed nodes) and
  missing (planned-but-not-deployed clusters such as kafka-east, starrocks,
  clickhouse).
- **`nexus infrastructure status <cluster> [--node X]`** — single-cluster
  view, optionally filtered to one node. Same state-decoration logic as
  `list` but bigger column widths and full `.vmx` paths.
- **`nexus infrastructure suspend <cluster> [--node X] [--yes]`** —
  `vmrun.exe suspend` for every running VM in scope. Pre-flight: shows the
  exact list of VMs about to be touched and asks for interactive
  confirmation (default *no*); `--yes` skips the prompt for scripted /
  CI use; non-interactive shells (stdin redirected) abort with exit 3
  unless `--yes` is passed. Idempotent: VMs already stopped/suspended
  return Ok with `already X` instead of failing.
- **`nexus infrastructure suspend-cluster <cluster>`** — Spectre alias of
  `suspend`. Mirrors master plan §5.3:245's literal panic-button wording.
- **`nexus infrastructure resume <cluster> [--node X] [--yes]`** —
  symmetric to `suspend`; `vmrun.exe start <vmx> nogui` for every
  stopped/suspended VM in scope.
- **`--json` on every infrastructure verb** — source-gen JSON via
  `NexusJsonContext` (no reflection); shapes documented in
  `Nexus.Cli.Adapters.Json` (`InfrastructureListJsonOutput`,
  `InfrastructureStatusJsonOutput`, `InfrastructureOpsJsonOutput`).
- **Hand-rolled `vms.yaml` flow-mapping reader** — `VmsYamlCatalog` in
  `Nexus.Cli.Adapters.Inventory`. ~150 LOC, BCL-only, AOT-clean. Tolerates
  the canon's two top-level `clusters:` roots (merged in file order) and
  quoted strings containing commas. Path discovery: explicit ctor arg →
  `NEXUS_VMS_YAML` env → sibling-repo fallback. Decision recorded in
  ADR-0006.
- **`vmrun.exe` adapter** — `VmrunProcessClient` in
  `Nexus.Cli.Adapters.Vmware`; uses `ProcessStartInfo.ArgumentList` (no
  shell escape ambiguity). `VmrunPaths` centralises path discovery
  (`NEXUS_VMRUN_PATH` env override + canonical Workstation install paths)
  and provides .vmx / .vmss helpers. On Linux + macOS, `Resolve()`
  returns `null` and every call short-circuits with a clear
  "vmrun.exe is Windows-only" message; nothing is spawned.
- **`InfrastructureBootstrapper`** in `Nexus.Cli.Infrastructure` — the
  no-Vault parallel of `NexusBootstrapper`. Wires `VmsYamlCatalog` +
  `VmrunProcessClient` + `InfrastructureService` for the four leaf
  commands. Reuses the existing `TypeRegistrar` + `AotRoots` plumbing.
- **15 new unit tests** — YAML parser fixtures (8), vmrun argv +
  parser (12), service truth-table + filtering (8), JSON contracts
  (3 new). 51 unit tests total, up from 36.
- **ADR-0006** — hand-rolled vms.yaml reader rationale.
- **`docs/verification/0.2.0-infrastructure.md`** — acceptance evidence
  including live suspend / resume round-trip on `foundation/vault-3`.

### Changed

- **Stub `infrastructure` command removed.** The four leaves replace it.
- **`scripts/cli.ps1`** path discovery: works from any cwd via absolute
  path; no functional change.
- **Version** bumped 0.1.3 → 0.2.0.

### Deferred to v0.2.x

- **Spectre.Console.Cli 0.55 bump** (AsyncCommand<T>.ExecuteAsync gains a
  CancellationToken parameter; touches all 6 commands). Kept on 0.50 for
  v0.2.0 to keep the new-feature commit clean from breaking-change
  adoption. Tracked separately.
- **Suspended-vs-stopped state inference refinement.** Current heuristic
  (`File.Exists(vmxPath.replace_extension(".vmss"))`) is best-effort;
  VMware Workstation Pro 17.5+ does not always emit `.vmss` next to
  `.vmx` after `suspend`, so the post-suspend status currently shows
  `stopped`. Functional behaviour is correct (the VM does suspend, RAM
  state is preserved, `resume` recovers running state); only the label
  is approximate. Refinement deferred to v0.3.
- **Linux runtime probing.** `list` works catalog-only on Linux (every
  state renders `unknown`); `status`/`suspend`/`resume` exit 2 with the
  Windows-only-build-host message. Deferred until a Linux operator
  workstation exists in the fleet.

## [0.1.3] — 2026-05-07

### Fixed

- **Spectre glyph rendering on Windows pwsh.** The default code page (cp1252)
  emitted `?` for `●`, `─`, and other box-drawing/status characters Spectre
  uses, so the `cluster-status` overall-health badge and table borders showed
  up garbled on Windows even though they rendered fine on Linux. Fix: force
  `Console.OutputEncoding = Encoding.UTF8` at process start. No-op on Linux
  (already UTF-8). Verified locally: `── ● RED  Cluster status …` now renders
  the bullet glyph cleanly.

## [0.1.2] — 2026-05-07

### Fixed

- **`cluster-status`** — read Consul + Nomad bootstrap tokens from the
  canonical `management_token` field on KV `nexus/swarm/{consul,nomad}-bootstrap-token`
  (was incorrectly reading `value`). Live-cluster runs against the v0.1.0
  binary failed with `Vault KV at nexus/swarm/consul-bootstrap-token has no
  field 'value'`. The `management_token` field name matches the master
  plan's pre-flight pattern (`vault kv get -field=management_token …`) and
  the Phase 0.E.2.3 / 0.E.3.2 bootstrap persistence shape.
- **TLS chain validation** — the HTTP factory was loading every cert from
  the CA bundle into `X509ChainPolicy.CustomTrustStore`, which mistakenly
  treats intermediates as roots. The cluster cert chain is
  `leaf → NexusPlatform Intermediate CA → NexusPlatform Root CA`, and a
  bundle that ships both was returning `PartialChain` because the chain
  builder refused the intermediate-as-root. Fix: split the bundle on
  `Subject == Issuer`; roots go to `CustomTrustStore`, intermediates to
  `ExtraStore` (per memory note `feedback_smoke_gate_probe_robustness.md`).
- After both fixes, `cluster-status` renders the live 0.E.4 cluster cleanly
  (Consul 6/6 alive, Nomad 3 servers + 3 ready clients). Verification
  evidence in `docs/verification/0.1.0-cluster-status.md`. Portainer:9443
  remains unreachable from the build host — separate cluster-side issue.

> Note: a transient `0.1.1` version bump was made for the Vault-KV-field
> fix alone, but the TLS-chain bug surfaced before tagging, so both fixes
> shipped together as `0.1.2`. No `v0.1.1` GitHub Release exists.

## [0.1.0] — 2026-05-07

First public release of `grezap/nexus-cli` — the operator surface for the
NexusPlatform 66-VM lab (Phase 0.F slice 1 of the master plan).

### Added

- **`cluster-status` command** — first vertical slice. HTTPS introspection of
  the live 0.E.4 cluster: Consul (members + leader), Nomad (servers, clients,
  leader), Portainer (system status, agent task count). Mgmt tokens for
  Consul + Nomad resolved on demand from Vault KV at
  `nexus/swarm/{consul,nomad}-bootstrap-token`. Output modes: human table
  (Spectre.Console) and `--json` (System.Text.Json source-gen).
- **Native AOT publish pipeline** for `linux-x64` and `win-x64`. Single static
  binary; size budget enforced at ≤25 MB by `scripts/cli.ps1 size-check` and
  in CI.
- **3-project layered solution** — `Nexus.Cli` (AOT root) + `Nexus.Cli.Core`
  (interfaces + records) + `Nexus.Cli.Adapters` (HTTP/JSON/Vault) +
  `Nexus.Cli.Tests` (xUnit + NetArchTest). Layer rules enforced.
- **Operator wrapper** `scripts/cli.ps1` with verbs `build`, `publish`, `test`,
  `lint`, `clean`, `size-check`. `-Rid all|linux-x64|win-x64`. Mirrors the
  shape of the operator wrappers in `nexus-infra-vmware` and
  `nexus-infra-swarm-nomad`.
- **CI** — `.github/workflows/ci.yml` builds + tests + AOT-publishes on every
  push (matrix per RID on its native runner). `release.yml` attaches the
  tarballs to GitHub Releases on every `v*` tag.
- **ADRs 0001–0005** — framework choice, AOT cadence, project layout, auth
  model, Dapper-on-AOT future-DB mandate.
- **Stub commands** for the four remaining master-plan verbs
  (`infrastructure`, `failover-test`, `kafka failover`, `demo run/record`)
  that print a not-yet-implemented banner.

### Acceptance evidence

- `docs/verification/0.1.0-cluster-status.md` — live-cluster smoke output
  pasted by the operator after the v0.1.0 tag built.

[Unreleased]: https://github.com/grezap/nexus-cli/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/grezap/nexus-cli/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/grezap/nexus-cli/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/grezap/nexus-cli/compare/v0.3.2...v0.4.0
[0.3.2]: https://github.com/grezap/nexus-cli/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/grezap/nexus-cli/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/grezap/nexus-cli/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/grezap/nexus-cli/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/grezap/nexus-cli/compare/v0.1.3...v0.2.0
[0.1.3]: https://github.com/grezap/nexus-cli/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/grezap/nexus-cli/compare/v0.1.0...v0.1.2
[0.1.0]: https://github.com/grezap/nexus-cli/releases/tag/v0.1.0
