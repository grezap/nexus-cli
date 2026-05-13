# ADR-0007 — SSH client: SSH.NET (managed) over `ssh.exe` shell-out or native libssh

- **Status:** Accepted
- **Date:** 2026-05-08
- **Phase:** 0.F slice v0.3.0 (Master plan §4 line 156)

## Context

The v0.3 `failover-test` slice needs to SSH into a lab VM, run a service-
management command (`sudo systemctl stop consul`), poll a cluster API for
the new leader, then run the recovery command. v0.3.1 + v0.3.2 will extend
the same SSH path to nomad-leader + swarm-manager scenarios.

Three approaches were evaluated:

1. **Shell out to `ssh.exe` via `Process.Start`.** Zero NuGet deps; Windows
   OpenSSH already on PATH; symmetric with the existing
   `VmrunProcessClient`. But MEMORY.md catalogues four production-grade
   pain points with this shape:
   - `feedback_windows_ssh_automation.md` — five structural patterns
     needed to drive Windows over `ssh.exe`.
   - `feedback_ssh_stage1_size_limit.md` — `ssh user@host "<cmd>"`
     fails past ~6 KB on Windows `ssh.exe` with cryptic shell errors.
   - `feedback_pwsh_ssh_stdin_cr_injection.md` — pwsh piping multi-line
     strings to `ssh.exe` stdin re-introduces CR even after `-replace`
     normalisation; remote bash sees `cmd\r` and errors.
   - General quoting hell on Windows when commands include backslashes,
     dollar signs, or backticks.
2. **Native libssh / libssh-rs bindings.** Fastest; native libssh2.
   But adds platform-specific native DLLs to the AOT distribution, one
   per RID. Operational cost (cross-compile, distribute, version) too
   high for a Tier-2 lab CLI.
3. **SSH.NET (`Renci.SshNet`) 2025.1.0.** Pure-managed library; first-class
   typed `SshClient` + `PrivateKeyAuthenticationMethod` +
   `SshCommand.ExecuteAsync`; declares `IsAotCompatible=true` from 2024.2.0
   onwards. One NuGet dep; no native bindings; trim profile clean under
   `partial` mode.

## Decision

Use **SSH.NET 2025.1.0** as the SSH transport for v0.3 onwards. Wrap it
behind `Core.Abstractions.ISshClient` so the library choice stays
swappable; expose only key-based auth (password auth is intentionally
disabled — the lab is key-only `nexusadmin` per canon).

The adapter is stateless: each `ExecuteAsync` opens a fresh connection,
runs one command, disconnects. Acceptable for failover-test's ~5-10
command budget. If a future verb (e.g., the v0.4 demo orchestrator)
needs many commands per session, add an `OpenSessionAsync` path on
the interface — don't bottleneck v0.3 on a session model it doesn't need.

Operator key discovery via `SshKeyDiscovery`: `NEXUS_SSH_KEY` env var
takes precedence, then `~/.ssh/id_ed25519`, then `~/.ssh/id_rsa`. Key
contents are never read or stored by the CLI itself — SSH.NET handles
the cryptographic side.

## Consequences

- **+** Zero `ssh.exe` pain. The four MEMORY.md feedback files become
  historical reading rather than ongoing constraints.
- **+** AOT-clean. SSH.NET 2025.1.0's `IsAotCompatible=true` flows
  through Adapter trim analysis without new suppressions.
- **+** Typed exit codes + structured stdout/stderr surface. No parsing
  `ssh.exe` stderr for "Permission denied" markers — failures arrive as
  typed exceptions which `ISshClient` maps to `Result.Fail` with a
  context-rich message.
- **+** Cross-platform: same code path on Linux operator workstations
  once they exist (v0.3+ runtime probing extension).
- **−** One more NuGet dep. Mitigated by central package management
  (`Directory.Packages.props`) and the v0.2.1 cleanup that proves
  breaking-change adoption is tractable.
- **−** Slightly larger AOT footprint (TBD; budget ≤25 MB has 14 MB of
  headroom on `win-x64`).

## Verification

- v0.3.0 ships a live destructive smoke against the lab: SSH to the
  current Consul leader, stop the service, measure RTO via Consul HTTP
  polling, restart, verify health. Documented in
  `docs/verification/0.3.0-failover-test.md`.
- Unit tests cover the wrapper's error-path semantics (missing key,
  malformed target) without spawning a real SSH server.
- AOT publish trim warnings remain rolled-up via the existing
  `TrimmerSingleWarn` setting; no new `[UnconditionalSuppressMessage]`
  attributes needed on the publish project.
