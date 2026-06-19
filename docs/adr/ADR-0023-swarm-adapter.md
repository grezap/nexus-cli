# ADR-0023 — SwarmAdapter (Phase 0.E orchestration tier; nexus-cli v0.8.2)

- **Status:** Accepted
- **Date:** 2026-06-19
- **Phase:** 0.E (Docker Swarm + Nomad + Consul + Portainer) — the **second non-data-tier adapter** and the **most reusable** (much of the status/health/topology/failover plane already shipped as standalone code in v0.1–v0.5).
- **Supersedes / relates to:** ADR-0009 (the `IClusterAdapter` SPI + the no-managed-driver invariant) · ADR-0022 (the v0.8.1 non-data-tier opener + the build-host control-plane posture) · the project-scope note §2 for the orchestration tier (`project_nexus_cli_infra_adapters_scope`, locked 2026-06-18) · the live contract `reference_swarm_live_contract`. Cross-tier: `nexus-infra-swarm-nomad` ADR-0019 (TLS-on-wire split-script) + the 0.E.2/0.E.3 Consul/Nomad ACL hardening + the Portainer-CE/NFS-state ADR. Auth model: the existing `nexus/swarm/{consul,nomad}-bootstrap-token` Vault KV (no new paths).

## Context

The CLI must manage **everything**. The orchestration tier was scoped (project memory §2) as the *most reusable* of the five non-data tiers because the heavy lifting already existed: `ConsulClient` (`:8501`), `NomadClient` (`:4646`), `PortainerClient` (`:9443`), `ClusterStatusService` (the 3-way rollup) and `FailoverTestService` (consul-leader / nomad-leader / swarm-manager runners) were all built and live in v0.1–v0.5 for the standalone `cluster-status` + `failover-test` commands. v0.8.2 wires them into a single `IClusterAdapter` (ClusterId `swarm`) and adds the remaining verbs (scale-out / backup / cert-rotate / acl / chaos) over the existing `SshTarget`/`ISshClient` + `IVmsCatalog` + `INexusVaultClient` seams.

The live contract was **probed before building** (diagnose-before-rewriting → `reference_swarm_live_contract`). Findings that shaped the design:

1. **Three independent raft rings.** Swarm, Consul and Nomad each elect their own leader, usually on *different* managers; each leader is read from its own source (`docker node ls` ManagerStatus / `/v1/status/leader` ×2).
2. **The bootstrap tokens are a sticky-seed that is EMPTY before the ACL bootstrap runs** (`status=not-bootstrapped`, `management_token=""`). The probe also caught the cluster degraded after >168 h offline (Consul `server_rejoin_age_max` refusal) — so a **cold rebuild** (`swarm.ps1 cycle`) preceded the verify, which also re-bootstrapped + persisted the tokens.
3. **The build host doesn't resolve `*.nexus.lab`** → all three HTTP clients (incl. Portainer, whose cert CN is `portainer.nexus.lab`) target a **manager IP**; the CA-pinned `NexusHttpClientFactory` validates the chain, not the SAN, so the IP works over the ingress mesh.
4. **`pkiCert` persists + reuses the leaf** (see Live-caught issues #1) → a bare vault-agent restart does NOT rotate the cert.

## Decision

Ship one `SwarmAdapter` (ClusterId `swarm`) over the `IClusterAdapter` SPI, registered in `ClusterBootstrapper` next to `VaultAdapter`/`FoundationAdAdapter`. **No managed Docker / Consul / Nomad driver** is linked (NetArchTest-enforced); the reuse clients are HTTP, everything else is SSH-shell-out. The control plane keeps the v0.8.1 posture: the Consul/Nomad mgmt tokens stay on the build host (read from Vault KV via `INexusVaultClient`) and reach the cluster over HTTPS; node-local actions go over SSH.

- **status / health / topology** — REUSE `ClusterStatusService` (the Consul+Nomad+Portainer rollup, built against a reachable manager) **enriched** with `docker node ls --format json` (the authoritative Swarm membership + raft-leader view) and the Portainer `/api/system/status` reachability + best-effort `/api/endpoints` count. `health` is 9 probes (consul members/leader, nomad servers/leader/clients, portainer, swarm managers/workers/leader); `topology` is the 6 nodes (role-annotated consul-server/nomad-server vs consul-client/nomad-client/portainer-agent) + the Portainer service node; **not sharded** (Shards = null).
- **failover** — REUSE `FailoverTestService`, dispatching on `--direction`: `consul-leader` / `nomad-leader` (SSH `systemctl stop` on the discovered raft leader → poll for re-election → restart; RTO ≈ 2–3 s) and `swarm-manager` (a **vmrun host-level SUSPEND** of the Swarm raft-leader VM → poll `docker node ls` for the new leader → vmrun resume; RTO ≈ 21 s). The cluster is allowed to settle after a swarm-manager failover (the consul re-election window can briefly show no leader).
- **scale-out add / remove** — **reversible drain**, NOT `docker node rm`: `remove` = `docker node update --availability drain` (+ `docker node demote` for managers, guarded by a "not the raft leader and ≥2 managers Ready" quorum check) + `nomad node drain -enable -self`; `add` = re-`active` (+ `promote`) + `nomad node eligibility -enable`. Growing the fixed 3-manager + 3-worker fleet is terraform → documented in the OutcomeReason, not silently skipped.
- **backup take / restore** — `take` runs `consul snapshot save` + `consul snapshot inspect` (the round-trip verify) + `consul kv export` + `nomad operator snapshot save` on a manager, downloads them to a build-host dir (`~/.nexus/backups/swarm/<id>/`), and best-effort copies the Portainer boltdb. `restore` is **deliberately refused** (it would overwrite the live KV + job state; the DR runbook restores onto an isolated cluster).
- **cert-rotate** — re-render each node's mTLS leaves via the node's own `nexus-vault-agent`, then restart the services: **consul ROLLING** (workers → non-leader managers → leader) and **nomad PARALLEL big-bang** across all six (Nomad's TLS RPC layer can't survive a rolling flip — `nomad-tls-rolling-restart-must-be-parallel`). Serial proof via `openssl s_client` (path-independent).
- **acl** — Consul + Nomad ACL tokens merged: `list`/`describe` (`consul acl token list -format=json` + `nomad acl token list -json`), `grant` (`consul acl token create … -templated-policy builtin/dns`), `revoke` (`consul acl token delete -accessor-id` / `nomad acl token delete`). Bootstrap/management/agent/anonymous tokens + the global-management/node-identity policies are **revoke-protected**.
- **chaos** — `nexus-chaos.sh` on a **WORKER** (managers spared to keep quorum); process-kill targets the worker's `nomad`; network-partition/packet-loss drop the **VMnet10 backplane** (the management NIC stays up for the lift). After any nftables-based scenario the victim's `docker` is restarted to rebuild the ingress-mesh DNAT (`nftables-flush-ruleset-wipes-docker`); recover-to-green via a lightweight `docker node ls` poll. `CanResizeVm` refuses the current Swarm OR Nomad raft leader.

## Live-caught issues (the lesson)

Three real bugs surfaced during the live verify (within the expected 1–4; the rest was first-try-green thanks to the reuse + the up-front probe):

1. **cert-rotate didn't rotate.** The vault-agent templates issue via the `pkiCert "pki_int/issue/<role>"` function, which **persists the leaf to its destination file and reuses it across agent restarts** (it only re-issues near expiry). So `systemctl restart nexus-vault-agent` left the serial unchanged on all 6 nodes. **Fix:** `cp -a` the bundle to `.bak` then `rm` it → `pkiCert` re-issues on the next render → restart the agent → poll for the re-render → restore the `.bak` if it didn't reappear (Vault unreachable) → restart the service. Serials then change on all six.
2. **acl grant failed.** `consul acl token create` refuses a token with no policy/role/identity. **Fix:** attach the minimal `builtin/dns` templated policy; revoke uses the explicit `-accessor-id` flag (not the hint-emitting `-id`).
3. **chaos was cancelled before output.** The `chaos` command hard-cancels at `Duration + 60 s`, but the recovery loop polled the full `GetStatusAsync` whose Portainer probe can each wait the HTTP timeout — blowing the budget. **Fix:** poll a lightweight `docker node ls` (the victim worker Ready+Active) with a 60 s deadline instead.

(Plus an infra finding, not an adapter bug: the Vault KV Portainer admin `plaintext` does not authenticate against the running Portainer — the `/api/endpoints` enrichment degrades to a version-only label.)

## Consequences

- The CLI now deeply manages the **orchestration tier** — the second of the five non-data-tier adapters. `swarm` is registered in `ClusterBootstrapper`.
- **Maximum reuse, minimum new surface:** `ConsulClient` / `NomadClient` / `PortainerClient` / `ClusterStatusService` / `FailoverTestService` are wired verbatim; the adapter adds only the SSH-based scale-out/backup/cert-rotate/acl/chaos plane + the docker-node enrichment.
- **AOT 27.36 → 27.59 MB / 30** (+0.23). **159 → 173 tests** (+14 parser cases: `ClassifyNode`, `ParseDockerNodes`, `ParseConsulAclTokens`, `ParseNomadAclTokens`). No managed driver (NetArchTest green).
- The swarm tier is individually **cold-rebuildable**; v0.8.2 was verified against a freshly-rebuilt cluster (`swarm.ps1 cycle` → smoke 0.E.4e ALL GREEN → full verb matrix GREEN).
- Next in the non-data-tier block: 0.8.3 Observability → 0.8.4 Lakehouse → 0.8.5 Harbor.
