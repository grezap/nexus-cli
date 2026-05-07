# ADR-0003 — 3-project layered solution enforced by NetArchTest

- **Status:** Accepted
- **Date:** 2026-05-07
- **Phase:** 0.F

## Context

Master plan §3 row "*.NET engineering + architecture*" requires the
architecture pattern to be enforced by tests (NetArchTest dependency rules,
xUnit architecture tests). The same row in §6 acceptance gate names ADRs to
explain trade-offs.

Two end states were considered:

1. **Single project** — fewer files, faster build, simpler AOT compile.
   Layer rules become aspirational documentation; nothing fails CI when a
   future PR reaches from "domain types" into Spectre or `HttpClient`.
2. **Modular pragmatic split** — Cli host (Spectre + Program.cs) /
   Adapters (HTTP/JSON/Vault) / Core (interfaces + records). Each project
   has bounded responsibilities, and NetArchTest assertions in
   `tests/Nexus.Cli.Tests/Architecture/LayerTests.cs` fail any merge that
   inverts the dependency arrows.

## Decision

Three production projects + one test project:

```
src/Nexus.Cli           — AOT publish root, Spectre.Console.Cli wiring
src/Nexus.Cli.Adapters  — HttpClient factory, JSON source-gen, Vault/Consul/Nomad/Portainer clients
src/Nexus.Cli.Core      — pure abstractions + records + Result<T>; BCL-only
tests/Nexus.Cli.Tests   — xUnit + NetArchTest + FluentAssertions
```

NetArchTest layer rules (in `LayerTests.cs`):

- `Core` depends on neither `Adapters` nor the Cli host.
- `Adapters` does not depend on the Cli host.
- The Cli host is the only assembly with an entry point.

## Consequences

- **+** Layer drift fails CI immediately; no architecture tests = aspirational
  pattern with no teeth.
- **+** Adapters are isolated enough to gain a second consumer (e.g., a
  future Aspire AppHost or a unit-test fake) without rewiring.
- **+** Core stays AOT-clean by construction — it never references
  `HttpClient`, `Spectre.Console`, or any reflection-using API.
- **−** Three csprojs cost ~200ms more `dotnet restore` time. Acceptable.
- **−** Need to remember to add namespace strings to `LayerTests.cs` when
  introducing new top-level folders. Mitigated by colocating them in a
  static array at the top of the file.

## Verification

- `dotnet test` runs four NetArchTest assertions; all green.
- Adding a `using Spectre.Console.Cli;` to any file under
  `src/Nexus.Cli.Core` immediately fails the build (compile error: package
  not referenced) and the test (NetArchTest layer rule).
