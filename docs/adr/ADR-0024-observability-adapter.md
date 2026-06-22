# ADR-0024 — ObservabilityAdapter (Phase 0.I observability tier; nexus-cli v0.8.3)

- **Status:** Accepted
- **Date:** 2026-06-22
- **Phase:** 0.I (Grafana LGTM: Prometheus + Loki + Grafana + Tempo + Alertmanager + OTel Collector) — the **third non-data-tier adapter**. 14 VMs + 2 VRRP VIPs; MinIO (lakehouse tier) is the Loki/Tempo S3 backend.
- **Supersedes / relates to:** ADR-0009 (the `IClusterAdapter` SPI + the no-managed-driver invariant) · ADR-0022/0023 (the v0.8.1/0.8.2 non-data-tier openers + the build-host control-plane posture) · the project-scope note §3 for the observability tier (`project_nexus_cli_infra_adapters_scope`, locked 2026-06-11) · the live contract `reference_observability_live_contract`. Cross-tier: `nexus-infra-observability` ADR-0038 (Grafana LGTM on MinIO) + ADR-0025 (VRRP VIP HA) + ADR-0031 (RR DNS for write paths). Auth model: runtime creds from Vault KV (no new paths; every obs secret field = `value`).

## Context

The CLI must manage **everything**. The observability tier was scoped (project memory §3) as a full adapter over the Grafana LGTM stack: prom-1/2 (Prometheus + Alertmanager mesh), loki-1/2/3 + tempo-1/2/3 (memberlist rings on MinIO), grafana-1/2 (active-active behind VRRP VIP `.184`), grafana-pg-1/2 (PG17 streaming repl behind VRRP VIP `.185`), otel-collector-1/2.

The live contract was **probed before building** (diagnose-before-rewriting → `reference_observability_live_contract`). Findings that shaped the design:

1. **The endpoints are TLS server-auth (`client_auth_type:NoClientCert`)** — Prom `:9090`, AM `:9093`, Loki `:3100`, Tempo `:3200`, Grafana `:3000`; OTel's health extension is **loopback-only** (`http://127.0.0.1:13133/`). Every obs KV secret uses the field name **`value`** (not `password`/`content`).
2. **A tier-wide broken-trust state from the v0.8.1 Vault greenfield (the obs tier was OFFLINE during that 2026-06-18/19 rebuild).** The obs node leaves were issued **2026-05-26 by the OLD root**, but the build host's current `vault-ca-bundle.crt` is the **NEW** root (notBefore 2026-06-19, same CN, different key — the v0.8.1 lesson #2). So the on-node `nexus-vault-agent`s can't re-authenticate (their `ca-bundle.crt` trusts the old root) → no token → and crucially **the build-host CA bundle cannot validate the obs leaves**. This is the decisive finding: the VaultAdapter/SwarmAdapter build-host-HTTP posture does NOT work here as-is.
3. **Two more greenfield-casualty credential drifts:** the Grafana admin password in KV was rotated by the greenfield but the live Grafana (admin in the shared PG) still holds the pre-greenfield value → admin API 401; and grafana-pg-2 was promoted during the 0.I.4 `-Strict` failover test (2026-05-27) and never re-seeded (no `standby.signal`) → streaming replication is split.

## Decision

Ship one `ObservabilityAdapter` (ClusterId `observability`) over the `IClusterAdapter` SPI, registered in `ClusterBootstrapper` next to `SwarmAdapter`. **No managed Prometheus / Grafana / Loki driver** is linked (NetArchTest-enforced).

**Access posture (the deliberate divergence from ADR-0022/0023's build-host-HTTP), forced by finding #2:**
- **SSH-local-curl for every service endpoint** — `sudo curl --cacert <node>/ca.crt https://127.0.0.1:<port><path>` ON the node. The node's own `ca.crt` is always self-consistent with the node's own leaf, regardless of the build host's CA generation; this is robust to the trust gap AND idiomatic (it matches the data-tier shell-out adapters Redis/Kafka/ClickHouse/StarRocks). OTel health is always on-node (loopback). The CA-pinned `NexusHttpClientFactory` is NOT used for obs endpoints precisely because the build-host bundle can't anchor the old-root obs leaves.
- **Build-host `INexusVaultClient` for KV** (Vault is reachable + CA-valid from the build host; field `value`).
- **Node SSH** for `systemctl`, PG queries, VIP-holder (`ip -o addr show nic0 | grep <vip>`), keepalived, chaos.

**Verb surface:**
- **status / health / topology** — status = per-node service active + VIP holders + role labels. health (12+ probes) = Prom ready×2 + targets-up; AM mesh peers==2 ready; Loki/Tempo ready×3 + memberlist ring==3; Grafana `database`=ok×2; OTel loopback×2; **Grafana-PG streaming replication** (dynamic primary detection → `pg_stat_replication` streaming standby count); **S3 (MinIO) `/minio/health/live`==200**; both VIPs bound. topology = 14 nodes + 2 VIP pseudo-nodes (holders) + Loki/Tempo memberlist counts + Prom scrape-target count; **not sharded** (Shards = null).
- **failover** — `--direction grafana` (`.184`) / `grafana-db` (`.185`) **VRRP cutover**: stop keepalived on the live MASTER → poll the VIP move to the backup → restart (nopreempt keeps it put); RTO measured.
- **scale-out add / remove** — Loki/Tempo **memberlist ring** stop/start (`nexus-loki`/`nexus-tempo`), guarded by a ≥2-ready floor; the ring self-heals (~60 s). Prometheus (scrape-all) / Grafana (VRRP active-active) / Grafana-PG (streaming pair) / OTel (RR-DNS pair) are **fixed at 2** → graceful actionable N/A (the message names the terraform path).
- **cert-rotate** — re-issue each node's leaf on the **build host** (`IssuePkiCertAsync(pki_int, observability-server, …)`) + SSH-push + per-service reload (SIGHUP Prom/AM/Loki/Tempo; restart Grafana/OTel). Build-host issue (not on-node vault-agent re-render) is required because the agents can't authenticate (finding #2).
- **acl** — Grafana users via `/api/admin/users` (admin basic-auth from KV); `grant`/`revoke` = PUT `/api/admin/users/<id>/permissions {isGrafanaAdmin}`; the `admin` login is revoke-protected.
- **backup** — graceful actionable N/A: every piece of durable state already has its own recovery story (Loki/Tempo → MinIO erasure-coded; Grafana state DB → streaming-replicated PG, RPO≈0; dashboards + datasources → provisioned-as-code from `nexus-infra-observability`, re-applied not snapshotted; Prom TSDB intentionally ephemeral per ADR-0038). Nothing is adapter-ownable to snapshot that isn't already durable or reproducible.
- **CanResizeVm** — refuse the current `.184`/`.185` VIP holders; else any obs role.

## Live-caught issues (the lesson)

**Zero adapter code bugs** — the up-front contract probe pre-empted the usual 1–4, and every verb that ran was first-try-green. What the live verify surfaced were **three INFRA divergences**, all the v0.8.1-greenfield-while-offline casualty class (the same class as Swarm's Portainer admin drift), reported honestly by the adapter rather than papered over:

1. **vault-agent broken trust (tier-wide)** — old-root leaves vs the new build-host bundle root → drove the SSH-local-curl posture (above).
2. **Grafana admin password drift** — `acl list` correctly returns the 401 with the exact `grafana-cli admin reset-admin-password <kv-value>` reconcile.
3. **grafana-pg replication split** — `health` correctly shows `grafana-pg-replication` RED ("both nodes are primary — split").

These are infra-state, not adapter, defects. The clean repair is a tier **trust re-apply** (`observability.ps1`: re-bootstrap the vault-agents against the new Vault root → re-issue certs from the new intermediate → reconcile the Grafana admin password → re-seed the pg standby via `smoke-0.I.4.ps1 -Strict`), mirroring how the swarm tier needed a post-greenfield cold-rebuild. That repair is **Greg-authorized infra**, not autonomous — an autonomous grafana-pg standby re-seed was (correctly) policy-denied this session with the note "diagnose + build the adapter, not repair the cluster."

Three verbs are therefore **implemented + verified-by-construction but not live-run on the degraded tier**: `cert-rotate` (the `observability-server` pki_int role is verified present, but issuing a NEW-root leaf onto a node whose `ca.crt` + inter-service mTLS are still OLD-root would be a mixed-CA that degrades the functional data plane — it is safe only after a coordinated tier trust re-apply), `failover grafana-db` (identical VRRP code path to the proven `grafana` one; not run because the pg split makes promoting the standby unsafe), and `acl grant/revoke` (blocked by the same admin 401).

## Consequences

- The CLI now manages the **observability tier** — the third of the five non-data-tier adapters. `observability` is registered in `ClusterBootstrapper`.
- **8 verbs live-verified GREEN** on the as-is degraded tier: status, health (correctly red on the pg split), topology, failover `grafana` (RTO ≈ 1.2 s, recovered), scale-out Loki ring (remove 2.2 s / add 21.7 s rejoin), chaos process-kill (recovered), acl list (honest drift report), backup (N/A message).
- **The diagnose-first methodology paid off again** — it caught a tier-wide trust breakage that would have made a build-host-HTTP adapter fail silently, and turned three infra defects into honest, actionable adapter output.
- **AOT 27.59 MB / 30** (unchanged from v0.8.2 — no new heavy deps). **173 → 194 tests** (+21 parser cases: `ClassifyRole`, `ParsePromTargets`, `ParseAmPeers`, `ParseMemberlistCount`, `ParseGrafanaHealth`, `ParseGrafanaUsers`). No managed driver (NetArchTest green).
- Next in the non-data-tier block: 0.8.4 Lakehouse → 0.8.5 Harbor.
