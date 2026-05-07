# ADR-0005 — Dapper + FluentMigrator on AOT paths (future DB I/O)

- **Status:** Accepted
- **Date:** 2026-05-07
- **Phase:** 0.F

## Context

The master plan §3 row E4 mandates: *"Dapper + FluentMigrator on AOT paths
(nexus-cli, PulseNLP ingestion, LocalMind API, DataFlow Studio Kafka
workers). EF Core permitted on non-AOT paths. Per-project ADR."*

`nexus-cli` v0.1 ships `cluster-status` with zero database I/O — every state
reach is HTTPS to Consul / Nomad / Portainer. Per the master plan policy, we
still record the data-access decision **now**, so future commands
(`infrastructure list`, `failover-test history`, `demo run` job state) can
land without an emergency design discussion.

## Decision

When `nexus-cli` first needs persistence:

- **Reads** — `Dapper` 2.x against the chosen store via `IDbConnection`. No
  reflection-driven object materialisation is acceptable on the AOT path;
  Dapper's source-gen extensions (`Dapper.AOT`) will be evaluated and
  preferred over hand-rolled `IDataReader` parsing where they fit.
- **Migrations** — `FluentMigrator` (SQL Server, PostgreSQL, MySQL/Percona)
  with explicit `Up()` + `Down()`. CI gate is `up → down → up` on a fresh
  container, mirroring the master plan §6 acceptance gate for application
  projects.
- **EF Core is forbidden** on this AOT path. Even with the EF Core 9 AOT
  preview, the trim cascade pulls reflection that violates ADR-0002.
- **No ORM-style abstraction** layer over Dapper — direct SQL strings,
  parameterised, lifted into a `Repository` class per bounded context.
  When the same query shape repeats 3+ times, extract a typed mapper.

## Consequences

- **+** The decision is locked in before code exists, so no future PR can
  drift to EF Core "for convenience" and break the size budget.
- **+** Pairs cleanly with master plan E1 (FluentMigrator) and §6 acceptance
  gate (FluentMigrator up→down→up CI test).
- **−** Hand-written SQL takes more keystrokes than EF's LINQ. Acceptable for
  an operator CLI where the query surface is deliberately narrow.
- **−** Mock-friendly testing requires `Microsoft.Data.Sqlite` in-memory or
  test-container patterns. Will be added under `tests/Nexus.Cli.Tests/Db/`
  when the first DB-touching command lands.

## Verification

- v0.1.0 has no DB code, so no test exists yet.
- This ADR is referenced from any future PR introducing DB I/O; the diff
  must show `Dapper`/`FluentMigrator` package additions, not `Microsoft.EntityFrameworkCore.*`.
- Master plan §3 E4 cross-link verified at MASTER-PLAN.md:86.
