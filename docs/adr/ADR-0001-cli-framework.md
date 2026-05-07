# ADR-0001 — CLI framework: Spectre.Console.Cli over System.CommandLine + Cocona

- **Status:** Accepted
- **Date:** 2026-05-07
- **Phase:** 0.F (Master plan §4 line 156)

## Context

`nexus-cli` is the operator surface for the NexusPlatform 66-VM lab (per
master plan E29). It needs: rich verb routing, Spectre-style table rendering
for `cluster-status`, dependency-injection hooks for HTTP / Vault / SSH
clients, and a Native AOT publish path that meets the ≤25 MB exit gate on
both `linux-x64` and `win-x64`.

Three candidates were evaluated:

1. **Spectre.Console.Cli** — mature attribute-driven verb tree, first-class
   ANSI table/markup rendering (already required for the human view of
   `cluster-status`), DI bridge via `ITypeRegistrar`. AOT support is
   "experimental" — the runtime walks the type hierarchy via reflection to
   bind `TSettings`, which conflicts with full trim.
2. **System.CommandLine** — first-party Microsoft, beta as of late 2025; AOT
   story is solid but the rendering surface is bare (no native table). Means
   adding Spectre.Console (sans `.Cli`) anyway for tables, plus a separate
   verb-routing library.
3. **Cocona** — ASP.NET-style minimalist; less ceremony but smaller community,
   less polish on help formatting, no native table renderer.

## Decision

Use **Spectre.Console.Cli 0.50** as the verb router and renderer. Mitigate the
AOT/reflection gap with `<TrimMode>partial</TrimMode>` (see ADR-0002) plus
explicit `[DynamicDependency]` rooting in
`src/Nexus.Cli/Infrastructure/AotRoots.cs`. The IL2026/IL3050 cascade emitted
by Spectre's internal use of `TypeDescriptor.GetConverter` and
`Activator.CreateInstance` is suppressed via `<NoWarn>` on the publish project
only — Core + Adapters keep the strict trim analyzer.

## Consequences

- **+** Polished operator UX from day one; tables, rules, status banners, and
  consistent help output. Worth the trim friction.
- **+** Single dependency surface; nothing else needed for rendering.
- **−** AOT trim cascade is real and noisy. Centralised in
  `AotRoots.KeepAlive()`; new commands must add their `Settings` type there.
- **−** Tied to Spectre's release cadence. If Spectre ever drops AOT support
  entirely, we revisit this ADR — not before.

## Verification

- `pwsh -File scripts\cli.ps1 publish -Rid win-x64` produces a 9.7 MB binary
  (master plan exit gate is ≤25 MB).
- `nexus.exe --help` renders the verb tree with no runtime exceptions.
- `nexus.exe cluster-status` reaches the Vault token resolver, confirming
  Spectre's command + settings binding survives partial trim.
