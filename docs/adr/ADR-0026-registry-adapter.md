# ADR-0026 — RegistryAdapter (Harbor container registry HA)

- **Status:** Accepted
- **Phase / version:** Phase 0.L.4 · nexus-cli v0.8.5
- **Date:** 2026-06-26
- **Supersedes / relates:** ADR-0009 (IClusterAdapter SPI), ADR-0011 (Vault-KV operator-credential model), ADR-0024 (ObservabilityAdapter — the SSH-local-curl posture), ADR-0025 (LakehouseAdapter — the component-aware shape + the MinIO cross-tier dependency + the iceberg-pg DR-failover precedent this adapter mirrors), the locked scope in `project_nexus_cli_infra_adapters_scope` §5.

## Context

The registry tier (Phase 0.L.4, repo `nexus-infra-registry`, tier `09-platform`) is the
**fifth and LAST** of the five non-data-tier adapters (Foundation → Swarm → Observability →
Lakehouse → **Registry**). With it, the CLI's deep adapter surface covers EVERY cluster in the
fleet (the goal of the scope audit: "nexus-cli must control + configure everything"). It is a
Harbor container registry HA deployment over 4 VMs + 1 VRRP VIP:

- **Harbor app** (`registry-1/2`) — the Harbor stack as docker-compose (core/portal/registry/
  jobservice/trivy/nginx); stateless behind **round-robin DNS** `registry.nexus.lab` (no VIP).
  HTTPS :443 via nginx; the API is `/api/v2.0/*`.
- **Datastore** (`registry-pg-1/2`) — a dedicated **PostgreSQL 17 streaming pair + co-located
  Redis** master/replica behind keepalived **VRRP VIP `.119`** (`registry-db.nexus.lab`); the PG
  primary + Redis master follow the VIP.
- **External durable state:** image blobs → **MinIO `s3://harbor`** (lakehouse tier, EC-durable);
  metadata → the registry PG; cache → Redis. SSO via **Vault OIDC** (`auth_mode=oidc_auth`).

**Decision question:** how to expose the full `IClusterAdapter` surface over a tier that is a
stateless app pair + a stateful datastore pair + an external object store, where the operator
wants a single `nexus status registry`.

## Decision

**One `RegistryAdapter`** (ClusterId `registry`), registered in `ClusterBootstrapper` next to
`LakehouseAdapter`. The vms.yaml cluster key is `platform-tools` (which also holds unbuilt future
tools — prefect/unleash/marquez/backstage); the adapter resolves that cluster but **filters to the
four `registry-*` nodes** (`ClassifyRole` → `harbor` / `registry-pg`; everything else `other`,
excluded). The VRRP VIP `.119` is a constant (no `virtual_ips` block on `platform-tools`).

**Access posture = the ObservabilityAdapter/LakehouseAdapter shape** (ADR-0024/0025): the Harbor
API is probed over SSH with the node's own `/etc/nexus-registry/tls/ca.crt` (`sudo curl` —
self-consistent regardless of the build host's current CA generation); the Harbor admin password
comes from Vault KV `nexus/registry/harbor-admin` (field `value`) via the build-host
`INexusVaultClient`; PG/Redis/VRRP/chaos/cert control runs over node SSH. **No managed Harbor/
Npgsql/Redis driver** (NetArchTest-clean).

### Verb mapping
- **status / health / topology** — Harbor `/api/v2.0/health` component checklist (8: core/database/
  redis/registry/registryctl/jobservice/portal/trivy) + `/systeminfo` auth_mode + PG streaming
  replication + Redis master/replica + the MinIO `s3://harbor` backend canary + the VRRP VIP.
- **failover `--direction registry-db`** — VRRP cutover of the `.119` VIP (stop keepalived on the
  holder → the peer's `notify_master` promotes PG + re-masters Redis), RTO measured. **PG re-attach
  of the demoted primary is a DR re-seed** (the keepalived `demote.sh` re-attaches Redis but not PG)
  — so live execution is a DR runbook, mirroring the lakehouse iceberg-pg pair (ADR-0025). The app
  tier has no VIP (RR DNS) → an app-direction failover is an actionable refusal.
- **scale-out** — graceful actionable N/A: the 2-node app pair (RR DNS) + 2-node datastore pair
  (VRRP) is the **ADR-0036** standard; capacity scales by MinIO EC storage + vertical `scale-up`,
  not by adding registry nodes.
- **backup take/restore** — `pg_dump` the Harbor **metadata DB** (`registry`: projects, repos,
  artifacts, users, robots, replication rules) on the PG primary → node-local gzip; restore reloads
  into a throwaway verify DB and counts tables (non-destructive round-trip). Blobs are EC-durable in
  MinIO and Redis is ephemeral cache — neither is adapter-snapshotted (the same "durable elsewhere"
  framing as obs/lakehouse; cf. Vitess choosing logical mysqldump when no engine BackupStorage exists).
- **cert-rotate** — force each node's vault-agent to re-render its `pki_int` leaf (the Swarm/obs
  `cp -a`+`rm bundle.pem` idiom), then reload per role: **nginx container restart** on app nodes
  (picks up `harbor.crt`), **PG ssl reload** on datastore nodes; VIP holder LAST.
- **acl** — Harbor users via `/api/v2.0/users` (admin from Vault KV); grant/revoke toggle the
  **sysadmin flag** (`PUT /users/{id}/sysadmin`); the built-in `admin` is revoke-protected; the
  list is enriched with project + robot-account counts. Onboarded (OIDC) users are the grant/revoke
  targets — **local-user creation is disabled in `oidc_auth` mode** (403).
- **chaos** — embedded `nexus-chaos.sh` on a non-VIP node (process-kill = `docker` on an app node →
  the RR pair tolerates one loss; recovery = docker restart + `docker compose up -d` + a health poll).

## Consequences

- **Full-fleet coverage achieved.** Every cluster in `vms.yaml` now has a deep adapter; the five
  non-data tiers are all live-verified.
- **The registry cold-rebuild (CA rollover) folded into this build.** The tier was operationally
  broken (Harbor down, PG split, old-root agents). The from-zero rebuild put both Harbor and MinIO
  on the new Vault root, resolving the cross-tier CA split. It surfaced the **MinIO root-password KV
  drift** (greenfield rotated KV; running MinIO never adopted it) → reconciled KV → the running MinIO
  (Greg-consented; data-preserving; new KV-v2 version). See `docs/verification/0.8.5-registry.md`.
- **AOT 28.04 MB / 30**; **243/243 tests** (+16 parser); NetArchTest clean.
- **One live-caught bug fixed** (the unauthenticated `/systeminfo` omits `harbor_version` → re-gate
  the probe on `auth_mode`). **Two legs intentionally un-run** (registry-db PG failover = DR re-seed;
  acl grant/revoke on a real user = needs OIDC onboarding) — honest + precedented, not adapter bugs.

## Alternatives considered
- **Two adapters (app + datastore).** Rejected — the operator wants one `nexus status registry`; the
  component-aware single-adapter shape (LakehouseAdapter) handles a multi-component tier cleanly.
- **`mc mirror s3://harbor` as the backup.** Rejected as the primary backup — it crosses into the
  lakehouse MinIO cluster and the blobs are already EC-durable. The adapter-ownable authoritative
  state is the Harbor **metadata** DB (`pg_dump`), self-contained on the registry tier.
- **A managed Harbor API client.** Rejected — NetArchTest forbids managed driver linkage; `sudo curl`
  over SSH keeps the AOT footprint flat and the posture identical to the other non-data adapters.
