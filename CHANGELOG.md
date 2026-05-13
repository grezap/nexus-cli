# Changelog

All notable changes to `nexus-cli` are documented in this file. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/grezap/nexus-cli/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/grezap/nexus-cli/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/grezap/nexus-cli/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/grezap/nexus-cli/compare/v0.1.3...v0.2.0
[0.1.3]: https://github.com/grezap/nexus-cli/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/grezap/nexus-cli/compare/v0.1.0...v0.1.2
[0.1.0]: https://github.com/grezap/nexus-cli/releases/tag/v0.1.0
