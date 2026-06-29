# ADR-0022 — VaultAdapter + FoundationAdAdapter (Phase 0.A-0.D/0.M Foundation tier; nexus-cli v0.8.0 + v0.8.1)

- **Status:** Accepted
- **Date:** 2026-06-18
- **Phase:** 0.A-0.D (Vault HA + AD/DNS) + 0.M (2nd DC) — the **first non-data-tier adapters**. v0.8.0 is the full-fleet roll-up milestone (all 12 data/sharded families sealed; no new adapter, like v0.7.0); v0.8.1 opens the 5-adapter non-data-tier block (next: 0.8.2 Swarm → 0.8.3 Observability → 0.8.4 Lakehouse → 0.8.5 Harbor).
- **Supersedes / relates to:** ADR-0009 (the `IClusterAdapter` SPI + the no-managed-driver invariant) · the project-scope note for the 5 non-data tiers (`project_nexus_cli_infra_adapters_scope`, locked 2026-06-18). Cross-tier: `nexus-infra-vmware` ADR-0015 (transit auto-unseal + Vault Agent) · ADR-0039 (2nd AD DC + the dc-nexus `.10`→`.240` canon-vs-reality IP drift). Auth model: ADR-0004 (the operator `VAULT_TOKEN` from env).

## Context

The CLI must manage **everything**, not just the data tier. The power layer (`infrastructure`/`scale-up`, vmrun) already covers every VM group, but the deep adapter layer (status/health/topology/failover/scale-out/backup/cert-rotate/acl/chaos) only dispatched to the 12 registered data/sharded adapters. The foundation tier — the platform **trust root** — had no deep adapter. v0.8.1 closes that with two adapters over the existing 6-VM foundation base (+ `dc-nexus-2`); no new VMs.

The live contract was **probed before building** (diagnose-before-rewriting, 2026-06-18 → `reference_foundation_live_contract`). Findings that shaped the design:

1. **Vault leaders DRIFT.** The build-host `VAULT_ADDR` (`.121`) is usually a *follower*; the active node was `vault-2`. The API forwards, but per-node status must hit each node's `:8200` directly, and the active must be read dynamically (`sys/leader`).
2. **`vault operator step-down` exists** — the scope's earlier "no step-down API → failover N/A" note was WRONG. Step-down on the active promotes a standby (a real, measurable failover).
3. **No Vault Agent on the vault nodes** (they ARE the servers; no `/run/nexus-vault-agent/token`). So cert-rotate must issue from the **build-host token**, not a node token. Listener certs are `/etc/vault.d/tls/{vault.crt,vault.key}` (NOT `*-cert.pem`); reload is `systemctl reload vault` → SIGHUP, zero-downtime.
4. **vault-transit (`.124`) is OUTSIDE the build-host CA bundle** (Shamir, single-node raft, the seal-key custodian) → it must be probed/driven over SSH, not the HTTP client.
5. **AD: dc-nexus runs at `.240`, NOT the vms.yaml `.10`** (the ADR-0039 drift) → the adapter hardcodes the reality IPs (`.240`/`.242`), like CitusAdapter hardcodes VIPs.
6. **`Get-KdsRootKey` is unreliable over SSH** (the Server-2025-over-SSH limit) → query the AD `Master Root Keys` object instead.

## Decision

Ship two adapters over the `IClusterAdapter` SPI, plus a new opt-in capability interface for the bespoke recovery verb. **No managed Vault driver and no shelled-out `vault` binary** are linked (NetArchTest-enforced); all control is HTTP + SSH.

### VaultAdapter (ClusterId `vault`)

**Access split.** The Vault **control plane runs over HTTP from the build host** (`VaultAdminClient`, reusing the CA-pinned `NexusHttpClientFactory` + the source-gen JSON context) using the operator `VAULT_TOKEN` (ADR-0004) — deliberately so the **root token NEVER reaches a node's process table**. Node-local actions (service stop/start/reload, cert-file push, the chaos helper, the recover-ha restarts, and anything touching vault-transit) go over SSH. Mutating verbs **target STANDBYS** so the active keeps serving.

- **status / health / topology** — per-node seal-status + active/standby (read directly per node, leaders drift), raft peer set (`sys/storage/raft/configuration`: 3 voters + 1 leader), transit-unseal probe (SSH), and the operator-token round-trip. `topology` enriches each node with its raft voter/leader role; **not sharded** (Shards = null).
- **failover** — `PUT sys/step-down` on the active node → poll until a *different* node is active (live RTO ≈ 2.0 s). Raft leadership is location-independent, so there is no forced "return" (the old active becomes a healthy standby and the cluster serves throughout) — `Recovery = skipped` with a hint, not a defect.
- **scale-out add / remove** — `remove` stops `vault.service` on a chosen STANDBY (it stays a raft peer, offline; the cluster keeps quorum on the other two); `add` restarts a stopped standby → it auto-unseals via vault-transit and rejoins (live ≈ 3.6 s). Growing the quorum (a 4th voter) is terraform/Packer → documented in the OutcomeReason, not silently skipped.
- **backup take / restore** — `take` streams `GET sys/storage/raft/snapshot` to a build-host file (`~/.nexus/backups/vault/…snap`) and verifies it **non-destructively** by parsing the gzip(tar) `meta.json` (Index/Term/Size) via `System.Formats.Tar` — the safe equivalent of `raft snapshot inspect`, never a restore. `restore` is **deliberately refused**: `raft snapshot restore` overwrites every secret/policy/PKI mount of the live trust root in place; surfacing it as a one-liner verb is too dangerous (the DR runbook restores onto an isolated cluster).
- **cert-rotate** — re-issue each listener cert from `pki_int/issue/vault-server` via the build-host token (`IssuePkiCertAsync`) → SSH-push `vault.crt`/`vault.key` (chown `vault:vault`, 644/600) → `systemctl reload vault` (SIGHUP, no leadership change). Order: **standbys first, active LAST**.
- **acl** — Vault ACL policies + AppRoles. `list`/`describe` read `sys/policies/acl` + `auth/approle/role`; `grant` writes a demonstrative ACL policy; `revoke` deletes it. The operator/system policies (`root`/`default`/`nexus-admin`/`nexus-operator`/`nexus-reader`/`nexus-foundation-reader`/`nomad-jobs`/`nexus-bootstrap`) and the per-node `nexus-agent-*` policies are revoke-protected.
- **chaos** — process-kill a STANDBY `vault.service` (never the active, never the transit custodian) via the embedded `nexus-chaos.sh`; lift + restart + recover to green.

### recover-ha — a NEW verb via `IRecoverableCluster`

A new opt-in capability interface (only `VaultAdapter` implements it; the `recover-ha` command returns a graceful "not applicable" for any other cluster). It is the declarative form of `scripts/recover-vault-ha.ps1` (the post-reboot boot-race recovery): read the Shamir keys from the operator's `~/.nexus/vault-transit-init.json` → unseal vault-transit over SSH → `reset-failed` + `start vault` on vault-1/2/3 → poll until unsealed. It is **idempotent** (already-unsealed is a no-op) and is the **ONLY exposed unseal path** — raw `vault operator unseal` is never surfaced.

### FoundationAdAdapter (ClusterId `foundation-ad`)

The 2-DC AD DS forest (`nexus.lab`, Windows Server 2025) over **Windows-SSH** (the EncodedCommand idiom from `SqlServerControl`) as the local `nexusadmin`, plus the Debian `nexus-gateway` egress over Linux-SSH (dnsmasq DNS/DHCP + nftables NAT, folded into health). DC IPs are hardcoded to reality (`.240`/`.242`, the ADR-0039 drift).

- **status / health / topology** — both DCs (PDC + replica) + the gateway. `health` proves: DC reachability (ADWS), AD replication (`Get-ADReplicationPartnerMetadata` LastReplicationResult = 0, failures = 0), DNS zones (AD-integrated), the KDS root key (via the AD `Master Root Keys` object — not the SSH-unreliable cmdlet), all 5 FSMO roles, and gateway services + NAT.
- **acl** — AD users/groups: `list` (enabled users + the `nexus-*` security groups), `describe` (MemberOf), `grant`/`revoke` = `Add`/`Remove-ADGroupMember`. Protected principals (`Administrator`/`krbtgt`/`nexusadmin`/`Domain Admins`/…) are refused.
- **failover-test** — a graceful FSMO transfer drill: `Move-ADDirectoryServerOperationMasterRole` relocates the **4 operator-movable FSMO roles** (PDCEmulator/RIDMaster/InfrastructureMaster/DomainNamingMaster) holder→target, verifies, then transfers them back (mirrors the failover-test recover pattern; `--no-recover` / `--node` honored; needs ≥2 reachable DCs). **SchemaMaster is excluded** — it needs Schema Admins (restricted by design; an all-5 batch as Domain/Enterprise Admin splits at SchemaMaster, live-caught 2026-06-29), so the verb scopes to exactly what the operator identity can move, keeping the transfer atomic. FSMO *seize* stays the manual permanent-loss last resort (v0.8.7, live-proven 2026-06-29: 4 roles dc-nexus→dc-nexus-2 and back, ~6.8 s).
- **backup take** — `ntdsutil ifm create full` on a reachable DC (prefers a non-PDC when ≥2 are up) → a non-destructive full copy of the AD database (`ntds.dit` + registry hives) under `C:\nexus-backups\ad\<id>`; the AD analogue of the Vault raft-snapshot verb, verified by the resulting `ntds.dit` artifact (size + path) rather than ntdsutil's chatty stdout (v0.8.7, live-proven 2026-06-28: 96 MiB, ~12 s). `backup restore` stays a deliberate N/A — authoritative restore is the console-only DSRM path (Server 2025 blocks `ntdsutil` ConsoleMode over SSH, [[feedback_ntdsutil_dsrm_console_mode_ssh]]).
- **graceful, ACTIONABLE N/A** — `scale-out` (DC add/remove = terraform), `backup restore` (authoritative restore = console-only DSRM), `cert-rotate` (LDAPS = the security overlay; an unguarded NTDS restart is refused), and `chaos` each return a `Result.Fail` that names the right out-of-band tool — never a silent stub. **`chaos` is a genuine N/A specific to the SSH-managed adapter** (live-proven 2026-06-29): a meaningful DC chaos stops ADDS/NTDS, which also stops Netlogon and severs the domain secure channel OpenSSH authenticates `nexusadmin` through — so the chaos self-fences the adapter's own recovery path (dc-nexus-2 went `Permission denied (publickey)`; recovery needed an out-of-band `vmrun reset`, outside ADR-0009's SSH-shell-out architecture). The 2-DC HA is validated out-of-band by smoke-0.M (host-kill of a DC → auth + DNS continue on the survivor).

## Live-caught issues (the lesson)

The VaultAdapter CODE was **first-try-green on every verb** (the thorough up-front probe paid off, like StarRocks/Kafka/Citus). The one genuine live-caught bug was in FoundationAdAdapter:

1. **AD replication probe returned empty metadata.** `Get-ADReplicationPartnerMetadata -Target <dc> -Scope Server -Server <ip> -PartnerType Inbound` returned an object (`count = 1`) whose fields were all blank. The explicit `-Server <ip>` degrades the result; since the Windows-SSH session already runs *on* the target DC, the **default** `-Server` (localhost) populates `LastReplicationResult`. Fixed by dropping `-Server` (and `-PartnerType`).

## Consequences

- The CLI now deeply manages the foundation **trust root** (Vault HA) + the **identity plane** (AD forest) + the **egress** (gateway health) — the first two of the five non-data-tier adapters. `vault` + `foundation-ad` are registered in `ClusterBootstrapper`.
- **AOT 26.71 → 27.36 MB / 30** (+0.65, from `System.Formats.Tar` + `GZipStream` for the non-destructive snapshot inspect). **137 → 159 tests** (+22 parser cases: `ClassifyNode`/`ParseSealed`/`ParseTransitInit`; `ParseDcLines`/`ParseReplMetadata`/`ParseAclUser`). No managed driver, no shelled `vault` binary (NetArchTest green).
- The HTTP-from-the-build-host control plane is a deliberate security posture (the root token stays on the build host) and the model for the remaining non-data-tier adapters where an HTTP control API exists.
- `recover-ha` + `IRecoverableCluster` establish the pattern for cluster-specific recovery verbs that the generic SPI shouldn't force on every adapter.
