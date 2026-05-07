# Changelog

All notable changes to `nexus-cli` are documented in this file. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.2] — 2026-05-07

### Fixed

- **TLS chain validation** — the HTTP factory was loading every cert from the
  CA bundle into `X509ChainPolicy.CustomTrustStore`, which mistakenly treats
  intermediates as roots. The `nexus-cluster` cert chain is
  `leaf → NexusPlatform Intermediate CA → NexusPlatform Root CA`, and the
  bundle ships both. Live cluster-status against v0.1.1 returned
  `net_http_ssl_connection_failed` because chain build refused the
  intermediate-as-root. Fix: split the bundle on `Subject == Issuer`; roots
  go to `CustomTrustStore`, intermediates to `ExtraStore` (per memory note
  `feedback_smoke_gate_probe_robustness.md`).

## [0.1.1] — 2026-05-07

### Fixed

- **`cluster-status`** — read Consul + Nomad bootstrap tokens from the
  canonical `management_token` field on KV `nexus/swarm/{consul,nomad}-bootstrap-token`
  (was incorrectly reading `value`). Live-cluster runs against the v0.1.0
  binary failed with `Vault KV at nexus/swarm/consul-bootstrap-token has no
  field 'value'`. The `management_token` field name matches the master
  plan's pre-flight pattern (`vault kv get -field=management_token …`) and
  the Phase 0.E.2.3 / 0.E.3.2 bootstrap persistence shape.

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

[Unreleased]: https://github.com/grezap/nexus-cli/compare/v0.1.2...HEAD
[0.1.2]: https://github.com/grezap/nexus-cli/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/grezap/nexus-cli/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/grezap/nexus-cli/releases/tag/v0.1.0
