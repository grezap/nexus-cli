# Changelog

All notable changes to `nexus-cli` are documented in this file. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/grezap/nexus-cli/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/grezap/nexus-cli/compare/v0.1.3...v0.2.0
[0.1.3]: https://github.com/grezap/nexus-cli/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/grezap/nexus-cli/compare/v0.1.0...v0.1.2
[0.1.0]: https://github.com/grezap/nexus-cli/releases/tag/v0.1.0
