# ADR-0006 — Hand-rolled `vms.yaml` flow-mapping reader (no YamlDotNet)

- **Status:** Accepted
- **Date:** 2026-05-08
- **Phase:** 0.F (Master plan §4 line 156, §5.3 line 245)

## Context

The v0.2 `infrastructure` verb (`list`, `status`, `suspend`, `resume`)
needs to read the canonical fleet inventory from
[`nexus-platform-plan/docs/infra/vms.yaml`](https://github.com/grezap/nexus-platform-plan/blob/main/docs/infra/vms.yaml)
to drive vmrun. The file is ~250 lines, schema-stable, and uses a single
canonical shape for VM entries: a one-line YAML *flow mapping* such as

```yaml
- { name: dc-nexus, os: ws2025-desktop, vcpu: 2, ram_gb: 4, …, role: "AD DC + DNS" }
```

`nexus-cli` is published as Native AOT (ADR-0002). Three reader strategies
were considered:

1. **YamlDotNet** — the standard .NET YAML library. Heavy reflection-emit;
   not AOT-clean without source-generation flavours that don't (yet)
   exist for the YAML 1.1 path we need. Importing it would either break
   the AOT publish or require carving custom trim suppressions, both of
   which violate ADR-0002's "trim warnings = errors on Core + Adapters".
2. **Build-time YAML→JSON conversion** — translate `vms.yaml` to JSON at
   `nexus-cli` build time and read it via the existing source-gen
   `JsonSerializer`. Trivial parser, zero AOT risk, but couples
   `nexus-cli`'s build to the plan repo's file shape; cold-rebuild
   workflows (clone, `cli.ps1 cycle`) silently fail when the sibling
   repo is absent. Hidden coupling fails the "selective build" canon.
3. **Hand-rolled flow-mapping reader** — a line-based lexer that
   recognises the canonical shape: top-level `clusters:` blocks, cluster
   names at indent 2, `purpose`/`phase`/`nodes` fields at indent 4, and
   single-line flow-mapping node entries at indent 6. BCL-only, ~150 LOC,
   AOT-clean by construction (no reflection, no source-gen attribute
   plumbing).

## Decision

Implement strategy **3** as `Nexus.Cli.Adapters.Inventory.VmsYamlCatalog`.

- Recognises the canonical shape *only* — anything past flow-mapping
  nodes (block-style sequences, anchors, aliases, directives, multi-line
  scalars) raises a fixture-test failure rather than silently
  mis-parsing.
- Tolerates the existing canon's quirks: two top-level `clusters:`
  blocks (edge first, foundation+ second) merged in file order; quoted
  strings with embedded commas (split is quote-aware); `virtual_ips:`
  and similar irrelevant cluster fields are skipped along with their
  indented sub-blocks.
- Path discovery: explicit ctor argument → `NEXUS_VMS_YAML` env var →
  sibling-repo fallback (`../nexus-platform-plan/docs/infra/vms.yaml`)
  → `Result.Fail` with the env-var name in the diagnostic.

## Consequences

- **+** AOT-clean. No reflection, no source-gen, no extra package.
  Stays inside ADR-0002's trim-warnings-as-errors guarantee on Core +
  Adapters.
- **+** Single, BCL-only, ~150-LOC class. Trivial to read, debug, and
  port if `nexus-cli` ever grows a non-.NET sibling.
- **+** Eight unit tests cover the structural quirks of the canonical
  file (multi-clusters root, virtual_ips skip, quoted-comma split,
  cache, missing path, unknown-cluster diagnostic) so regressions in
  the parser surface immediately.
- **−** Schema-evolution risk: if `vms.yaml` ever adopts block-style
  mappings, anchors, or multi-line scalars, the reader will need
  extending. Mitigation: a fixture-pinned test that fails fast in CI
  when canon drifts; the operator and the parser owner are the same
  person at v0.2.0.
- **−** Not a general YAML parser; can't be repurposed for arbitrary
  workloads. Acceptable scope for v0.2.

## Verification

- `tests/Nexus.Cli.Tests/Inventory/VmsYamlCatalogTests.cs` — 8 fixtures
  covering multi-clusters merge, metadata capture, flow-mapping parse,
  sub-block skip, quoted-comma split, missing-path diagnostic, on-disk
  round-trip with cache, unknown-cluster error path. All green.
- Live read against the canonical 250-line `vms.yaml` from a Native AOT
  publish: 81 nodes across 12 clusters parsed in <50 ms; `infrastructure
  list` table renders correctly; `infrastructure status foundation`
  filters to the 8 declared foundation nodes.
- Trim analyzer green on `Nexus.Cli.Adapters` after this addition; no
  new IL2026/IL3050 warnings introduced.
