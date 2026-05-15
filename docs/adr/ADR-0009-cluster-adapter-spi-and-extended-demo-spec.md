# ADR-0009 — Phase 0.G v0.6: `IClusterAdapter` SPI for the data-tier verb expansion; System B JSON demo spec extended

- **Status**: Accepted
- **Date**: 2026-05-15
- **Deciders**: Greg Zapantis
- **Related**: [ADR-0002](./ADR-0002-aot-from-day-one.md) (AOT from day one), [ADR-0007](./ADR-0007-ssh-net-managed-client.md) (SSH.NET), [ADR-0008](./ADR-0008-kafka-failover-demo-grade-via-ssh.md) (kafka failover via SSH + on-broker CLI), [`nexus-platform-plan` ADR-0024](https://github.com/grezap/nexus-platform-plan/blob/main/docs/adr/ADR-0024-aot-gate-amendment-and-cluster-adapter-framework.md) (AOT gate raised to ≤30 MB; cluster-adapter framework rationale), `feedback_cli_verb_terminology.md`, `feedback_demo_playbook_canon.md`, `feedback_dry_single_source_of_truth.md`

## Context

Phase 0.G introduces 7 new clustered workloads (Redis Cluster · MongoDB RS · Percona PXC · Patroni · ClickHouse · StarRocks · SQL FCI/AG) and pairs each with a `nexus-cli` `v0.6.x` release. Each cluster gets **13 verb groups**: `cluster-status` · `failover-test` · `scale-out` (cluster-membership change — add/remove a node) · `scale-up` (VM CPU/RAM/disk resize) · `backup` take/restore · `health` · `topology --watch` · `cert-rotate` · `chaos` · `acl` · plus the existing `demo`. See `feedback_cli_verb_terminology.md` for the `scale-out` vs `scale-up` distinction.

The implementation pattern for cross-cluster orchestration was set by [ADR-0008](./ADR-0008-kafka-failover-demo-grade-via-ssh.md) — SSH to a target node and shell out to the on-node native CLI. The kafka case is a one-off concrete service (`KafkaFailoverService`). Repeating that shape 7 times produces 7 parallel services that don't share structure and 7 separate command-binding sites that don't share verb-level test scaffolding.

The parent canon ([`nexus-platform-plan/docs/adr/ADR-0024`](https://github.com/grezap/nexus-platform-plan/blob/main/docs/adr/ADR-0024-aot-gate-amendment-and-cluster-adapter-framework.md)) raises the AOT exit gate from ≤25 MB to ≤30 MB for the data-tier verb expansion, and declares an `IClusterAdapter` framework SPI as the implementation strategy. **This ADR is the nexus-cli-side implementation record** for that decision: SPI shape, adapter discipline, the extended System B JSON spec shape, and the runtime-enforcement of step expectations.

## Decision

### `IClusterAdapter` SPI

One adapter per cluster. Each lives in `Nexus.Cli.Core/Adapters/<Cluster>Adapter.cs` (one file per cluster), is DI-registered in `Nexus.Cli/Composition`, and implements:

```csharp
public interface IClusterAdapter
{
    string ClusterId { get; }       // "redis", "mongo", "percona", ...
    string DisplayName { get; }     // "Redis Cluster", "MongoDB RS", ...

    Task<ClusterStatus>       GetStatusAsync(CancellationToken ct);
    Task<FailoverResult>      FailoverAsync(FailoverRequest req, CancellationToken ct);
    Task<ScaleOutResult>      ScaleOutAddAsync(ScaleOutAddRequest req, CancellationToken ct);
    Task<ScaleOutResult>      ScaleOutRemoveAsync(ScaleOutRemoveRequest req, CancellationToken ct);
    Task<HealthReport>        HealthAsync(CancellationToken ct);
    Task<TopologySnapshot>    TopologyAsync(CancellationToken ct);
    Task<BackupResult>        BackupTakeAsync(BackupRequest req, CancellationToken ct);
    Task<RestoreResult>       BackupRestoreAsync(RestoreRequest req, CancellationToken ct);
    Task<CertRotationResult>  RotateCertAsync(CancellationToken ct);
    Task<ChaosOutcome>        ApplyChaosAsync(ChaosScenario scenario, CancellationToken ct);
    Task<AclSnapshot>         AclAsync(AclOperation op, CancellationToken ct);
    bool                      CanResizeVm(string vmName, string role); // consulted by IVmResizer
}
```

`scale-up` is **generic** — a single `IVmResizer` service operates on any VM (vmrun stop → `.vmx` edit → start → guest-side `lvextend`/`resize2fs` for disks), consulting each registered adapter's `CanResizeVm` to refuse mid-write-window primary resize without `--force-primary`. Concrete adapters land per Phase 0.G.N (one cluster per release).

### SSH-shell-out invariant (mirrors ADR-0008)

All adapter operations dispatch via `SshNetClient` (ADR-0007) to on-node native CLIs:

| Cluster | On-node CLI |
|---|---|
| Redis | `redis-cli --tls --cacert /etc/nexus-redis/ca.pem -a $auth` |
| MongoDB | `mongosh --tls --tlsCAFile /etc/nexus-mongo/ca.pem` |
| Percona PXC | `mysql --ssl-mode=VERIFY_CA` + Galera `wsrep_*` SHOW queries |
| Patroni | `patronictl` + `psql` over `sslmode=verify-full` |
| ClickHouse | `clickhouse-client --secure` |
| StarRocks | `mysql --ssl-mode=VERIFY_CA` (FE speaks MySQL wire) |
| SQL FCI | `Get-Cluster` / `Move-ClusterGroup` over SSH-to-Windows |
| SQL AG | `sqlcmd -E` + `ALTER AVAILABILITY GROUP ... FAILOVER` |
| Kafka (retrofit) | `kafka-topics.sh` / `kafka-metadata-quorum.sh` (per ADR-0008) |

**No managed DB drivers linked.** Explicitly absent from the package graph: `StackExchange.Redis`, `MongoDB.Driver`, `Npgsql`, `MySqlConnector`, `Microsoft.Data.SqlClient`, `ClickHouse.Client`. Each would add 4-7 MB AOT-reachable.

### Extended System B JSON demo spec shape

The existing `nexus-cli/docs/demos/<id>.json` shape `{ id, title, description, steps[]: { command, waitAfterSeconds } }` is extended with **5 optional** fields:

- **`prerequisites: { vmsAlive: string[], envVars: string[] }`** (top-level) — state required before the demo can run.
- Per step: **`expectedExitCode: int`** — if set and the step's actual exit code differs, the step is marked failed.
- Per step: **`expectedOutputContains: string[]`** — each token must appear in the step's stdout+stderr (ordinal substring); missing token → step failed.
- Per step: **`observe: [{ where, what }, ...]`** — operator-visible observation points (UI URL · log query · dashboard panel + what to look for).
- **`whatProves: string`** (top-level) — one-sentence "what this demo proves".

All five are **opt-in**. Existing v0.4.0 readers ignore unknown JSON properties — `DEMO-01-cluster-status.json` and `DEMO-02-infrastructure.json` continue to parse without modification.

The reader (`JsonDemoCatalog`) and runner (`DemoRunner`) are extended:

- `JsonDemoCatalog.Load()` maps the new fields onto extended `DemoSpec` + `DemoStep` records (optional parameters with `null` defaults — backwards compatible at the type level).
- `DemoRunner.RunAsync()` enforces `expectedExitCode` + `expectedOutputContains` per step: when either expectation is set and not met, the step is marked failed and the demo aborts (`Status=StepFailed`) regardless of the actual exit code. When *no* expectation is set, behaviour matches v0.4.0 (`exit==0` ⇒ step OK).
- `prerequisites`, `observe`, `whatProves` are loaded into the model but **not yet runtime-enforced** — they surface in `nexus demo run` output reporting in a future v0.6.x release (not blocking 0.G.0d).

### AOT gate ≤30 MB

Per parent [ADR-0024](https://github.com/grezap/nexus-platform-plan/blob/main/docs/adr/ADR-0024-aot-gate-amendment-and-cluster-adapter-framework.md):

- The 25 MB gate stays **sealed** against the Phase 0.F `v0.5.0` ship (22.75 MB achieved).
- Phase 0.G `v0.6.0` → `v0.7.0` ships under **≤30 MB**.
- CI gate validates `dotnet publish -c Release -r {linux-x64,win-x64} -p:PublishAot=true` produces a binary ≤30 MB on every release.
- Trimmer warnings reviewed on every new-adapter PR.

## Consequences

### Positive

- **One pattern, 8 cluster implementations.** Each new cluster is ~250-400 LOC of adapter + ~50 LOC of command binding + ~150 LOC of unit tests. No per-cluster framework reinvention.
- **AOT footprint stays predictable.** No managed DB drivers; each adapter adds ~150-300 KB. Estimated v0.7.0 size: ~26-27 MB (3-4 MB headroom under the new gate).
- **Demos become self-verifying.** `expectedExitCode` + `expectedOutputContains` turn `nexus demo run <id>` into a regression-catching smoke harness for the 91+ verb invocations the Phase 0.G expansion adds.
- **Backwards compatible end-to-end.** Existing v0.4.0 JSON specs, the existing reader, the existing runner — all keep working unchanged.

### Negative

- **NetArchTest gains a new constraint set.** Architecture tests verify (a) every `*Adapter` implements `IClusterAdapter`; (b) no `*Adapter` references a managed-DB-driver type. Maintenance cost: keep the negative-list current as the .NET ecosystem evolves.
- **SPI evolves over Phase 0.G.** As new cluster operations land (point-in-time recovery · per-cluster online-DDL · etc.), the SPI grows. Each new method is a breaking change for adapter implementers; mitigated by `IClusterAdapter` being internal-only — no third-party consumers.
- **Step expectation enforcement is best-effort.** `expectedOutputContains` does ordinal substring matching on the (tailed) stdout+stderr concatenation. Demos requiring rich regex / structural assertions still need bespoke verification scripts; the spec extension covers the common 90 % case.

### Neutral

- **`scale-up` lives outside the SPI.** VM resize is generic; only the per-cluster constraint ("don't resize a primary mid-write-window") is adapter-aware via `CanResizeVm`. Keeps the SPI focused on cluster-membership concerns.
- **`KafkaAdapter` retrofit.** v0.6.0 absorbs v0.5.0's `KafkaFailoverService` into a `KafkaAdapter` that implements `IClusterAdapter`. The existing `nexus kafka failover {east-to-west,west-to-east}` verb shape stays — the implementation moves under the adapter pattern. Verified by the existing v0.5.0 verification doc + a fresh `0.G.0-aot-baseline.md` reporting the post-retrofit AOT size.
- **Per-adapter ADR discipline.** Each cluster's per-cluster decisions (e.g., MongoDB X.509 auth path · ClickHouse Keeper-vs-ZooKeeper · SQL FCI shared-storage strategy) live in a per-cluster ADR in this repo's `docs/adr/` (ADR-0010 onward).

## Verification

- **AOT gate (CI):** every release tag's `release.yml` validates `dotnet publish -c Release -r {linux-x64,win-x64} -p:PublishAot=true` produces a binary ≤30 MB. Size recorded in `docs/verification/0.G.N-<cluster>.md`.
- **SPI architecture (NetArchTest):** new tests in `tests/Nexus.Cli.Tests/Architecture/ClusterAdapterTests.cs` (added when the first concrete adapter lands in 0.G.1) — every concrete `*Adapter` in `Nexus.Cli.Core.Adapters` implements `IClusterAdapter`; no `*Adapter` references `StackExchange.Redis|MongoDB.Driver|Npgsql|MySqlConnector|Microsoft.Data.SqlClient|ClickHouse.Client`.
- **JSON shape (unit, 0.G.0d):** `tests/Nexus.Cli.Tests/Demos/JsonDemoCatalogTests.cs` covers — minimal spec parses identically to v0.4.0 · extended spec with all 5 new fields parses correctly · subset-extended spec parses with the rest defaulted to `null`/empty.
- **Runtime expectation (unit, 0.G.0d):** `tests/Nexus.Cli.Tests/Demos/DemoRunnerTests.cs` covers — step without expectations behaves like v0.4.0 · expectedExitCode met → success · expectedExitCode mismatched → step failed · expectedOutputContains met → success · expectedOutputContains missing → step failed.
- **Per-cluster live verification:** each Phase 0.G.N close-out records measured RTOs/throughputs in `docs/verification/0.G.N-<cluster>.md` against the master-plan §5.3 budget table.
