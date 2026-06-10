# Changelog

All notable changes to `nexus-cli` are documented in this file. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
