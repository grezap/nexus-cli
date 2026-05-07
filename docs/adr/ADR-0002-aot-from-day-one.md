# ADR-0002 — Native AOT from day one; partial trim mode

- **Status:** Accepted
- **Date:** 2026-05-07
- **Phase:** 0.F

## Context

The master plan §4 row 0.F mandates a single binary ≤25 MB on both
`linux-x64` and `win-x64`. JIT-first development risks a brutal week-2
conversion: third-party libs leak `IL2026/IL3050` warnings late, and the
publish surface area is hard to bring under budget retroactively. CI runs on
each push; size regressions need to fail fast.

Trim modes:

- **`full`** — every assembly is trim-rooted minimal. Spectre.Console.Cli's
  reflection patterns (`TypeDescriptor.GetConverter`,
  `typeof(TCommand).BaseType.GetGenericArguments()`) do not survive: the
  binary builds but throws `CommandRuntimeException` at first invocation.
- **`partial`** — only assemblies with `<IsTrimmable>true</IsTrimmable>`
  participate. User code stays whole; framework + BCL still get trimmed.
  Tested locally — `cluster-status` renders + the binary stays at 9.7 MB.

## Decision

- `<PublishAot>true</PublishAot>` + `<TrimMode>partial</TrimMode>` on the
  `Nexus.Cli` publish project.
- `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>` everywhere — first-party
  code (Core + Adapters) still gets compile-time IL2026/IL3050 errors via
  `.editorconfig`.
- Spectre-induced IL warnings are suppressed via `<NoWarn>` on
  `Nexus.Cli.csproj` only.
- `scripts/cli.ps1 size-check -Rid <rid>` enforces the 25 MB ceiling
  locally. CI runs the same script after every publish (mirrors the
  master plan exit gate).

## Consequences

- **+** Continuous validation of the size budget — every PR's CI run
  publishes both RIDs and asserts ≤25 MB.
- **+** No surprise late conversion; reflection bugs surface in the same
  commit that introduces them.
- **−** The first-time AOT publish on Windows requires the C++ x64/x86
  workload from Visual Studio (MSVC `link.exe` + Windows SDK).
  `scripts/cli.ps1` sources `vsdevcmd.bat` automatically; CI runners ship
  with the dev environment pre-configured.
- **−** Partial trim leaves more user-code metadata than `full` would. The
  9.7 MB Win binary is well under the 25 MB cap, so this is currently
  acceptable; revisit if the binary nears the cap as commands are added.

## Verification

- `pwsh -File scripts\cli.ps1 publish -Rid win-x64` → 9.7 MB binary.
- CI's `size budget` step asserts the same on every push (see
  `.github/workflows/ci.yml`).
- `nexus.exe cluster-status` runs against the live 0.E.4 cluster.
