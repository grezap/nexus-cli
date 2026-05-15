# ADR-0008 — Phase 0.F v0.5: `kafka failover` as a demo-grade DR verb via SSH + the kafka CLI scripts

- **Status**: Accepted
- **Date**: 2026-05-15
- **Deciders**: Greg Zapantis
- **Related**: ADR-0002 (AOT from day one), ADR-0007 (SSH.NET managed client), MASTER-PLAN line 258 (Kafka DR east→west < 60 s gate), `nexus-platform-plan/docs/adr/ADR-0023` (MirrorMaker 2 dedicated mode)

## Context

The last stubbed master-plan verb is `nexus kafka failover` (v0.5). With Phase 0.H closed (`nexus-infra-kafka` `v0.1.0`, 2026-05-15), the infrastructure is live and the verb is unblocked. The master-plan gate (line 258) is **"Kafka DR east→west < 60 s via `nexus-cli kafka failover`"**.

There are three real design forks:

### Fork 1 — What should the verb DO?

Two interpretations of "Kafka DR east→west":

- **A — demo-grade.** Simulate region loss by suspending every broker in the source cluster (host-level outage via `vmrun suspend` × 3); prove the target cluster keeps serving by running an RF=3 produce/consume round-trip on a fresh probe topic; measure the RTO; auto-recover. This is what `failover-test swarm-manager` does for the Swarm raft leader, scaled up to a whole cluster.
- **B — production-grade.** A, plus: read MM2's `<src>.checkpoints.internal` topic on the target, parse each `CheckpointRecord` (binary Kafka record format: source topic-partition + source offset → target offset + metadata), and apply them via `AdminClient.alterConsumerGroupOffsets` on the target. This is the true "consumers can resume on the target" semantic — the thing MM2 was designed to enable.

### Fork 2 — How does the verb talk to Kafka?

- **X — shell out via SSH.** Every existing verb (`cluster-status`, `failover-test consul-leader`, etc.) reaches the running fleet via SSH (`SshNetClient`, ADR-0007) and runs lightweight CLI scripts (`docker node ls`, `consul members`, etc.) on the target VM. For Kafka the equivalent is `sudo /opt/kafka/bin/kafka-topics.sh ...` over SSH against `SSL://localhost:9092` on a broker, using the broker's on-disk `client-ssl.properties` for mTLS.
- **Y — Confluent.Kafka NuGet.** Add the `Confluent.Kafka` client library. Cleaner C# code (typed AdminClient + producer + consumer). But: `Confluent.Kafka` is a managed wrapper over native `librdkafka` — a 4-6 MB native blob with non-trivial AOT-compilation behaviour. The repo's 22.65 MB AOT binary has only 2.35 MB headroom against the 25 MB master-plan exit gate (`ADR-0002`), so this likely blows the gate; and librdkafka's AOT compatibility on Windows is not a story I want to debug as part of a v0.5 ship.
- **Z — managed (non-AOT) build for kafka verb only.** Drop AOT for this one verb, ship as a separate JIT executable. Breaks the "single binary controls the entire fleet" promise of `ADR-0002` and adds operational complexity.

### Fork 3 — Scope split between v0.5.0 and v0.5.x

- **Single ship (v0.5.0 = B+Y or B+X).** Take ~2 weeks; ship the production-grade verb in one go.
- **Phased (v0.5.0 = A+X, v0.5.1 = +B).** Ship the demo-grade verb in ~3-4 days; defer real offset translation to v0.5.1 once a real consumer app exists (`streamcore` in Phase 12) to translate offsets FOR.

## Decision

**A + X + phased: ship a demo-grade kafka-failover verb as v0.5.0, implemented as SSH + `vmrun` + the on-broker `kafka-*` CLI scripts. Defer real consumer-group offset translation to v0.5.1.**

### Verb shape

Two subcommands matching the established `failover-test` pattern (the closest analog):

```
nexus kafka failover east-to-west [--json] [--no-color] [--yes]
nexus kafka failover west-to-east [--json] [--no-color] [--yes]
```

Both inherit the `FailoverTestSettingsBase` flags. No `--node` (the DR unit is a whole cluster, not a single broker — the subcommand name encodes the direction).

### What the verb does (apply-flow)

1. **Pre-flight (target healthy?)** — SSH to a target broker, run `kafka-metadata-quorum.sh ... describe --status`, confirm `CurrentLeader` is reported. Refuse to inject failure if the target is unhealthy.
2. **Inject failure** — sequential `vmrun suspend` of every source broker. Sequential not parallel: the `0.H.6` cold-rebuild proof surfaced a VMware-under-load "Unknown error" concurrency flake on parallel `vmrun start` (and by extension `vmrun suspend`). A 2-second inter-suspend gap is enough.
3. **Verify target keeps serving** — single SSH command on a target broker: create a fresh probe topic with RF=3, produce a unique token, consume it back, delete the topic. The probe runs `sudo` because `/etc/nexus-kafka/` is `0750 root:kafka` and the CLI tools need to traverse it (the `feedback_sudo_required_for_consul_etc_traverse.md` lesson, Kafka edition). Retry with a poll interval (~3 s) until the round-trip succeeds, up to a 90 s deadline.
4. **RTO = T_targetHealthy − T_failureInjected** — measured from a single monotonic `Stopwatch` so all timeline offsets are consistent.
5. **Auto-recovery** — sequential `vmrun start nogui` for each suspended broker.
6. **Wait for source healthy again** — same metadata-quorum probe on a source broker, up to a 4-minute deadline (cold VM boot + KRaft quorum re-form).

### Why this combination

- **A satisfies the master-plan gate.** "Kafka DR east→west < 60 s via the verb" means the verb runs and returns a recorded RTO under 60 s on a healthy cluster. The probe round-trip is the explicit "target keeps serving" proof; the demo-grade scope makes this measurable without the multi-week B detour.
- **X keeps `ADR-0002` honest.** Adding ~150 lines of C# that drives existing SSH + on-broker scripts costs ~0.1 MB to the AOT binary (measured: 22.65 → 22.75 MB after v0.5.0 build). The 25 MB exit gate stays comfortably met with 2.25 MB headroom. The "single AOT binary controls the fleet" promise stays intact.
- **X is consistent.** Every other verb in the CLI follows the SSH-shells-out pattern. Adding a managed Kafka client just for this one verb would be a one-off that future contributors would have to learn separately.
- **The deferred B is honest.** Real consumer-group offset translation only matters once a real consumer app exists. `streamcore` (Phase 12) is the first one. Building B now would be future-coupled work that ships under-exercised; building it when `streamcore` lands means we can write the test against the actual consumer behaviour.

## Consequences

### Positive

- **Ships in days, not weeks.** v0.5.0 = ~3-4 days of work, closes the last stubbed master-plan verb, tags `nexus-cli` at v0.5.0 with all five verbs live.
- **AOT binary stays under the gate.** No new dependencies; +0.1 MB. The "managed-vs-native-CLI decision" flagged in the memory is resolved in favour of native by sidestepping the Kafka client library entirely.
- **The verb is demonstrable.** Pairs cleanly with DEMO-08 ("Survive a Kafka region failure") — the demo's CLI step IS `nexus kafka failover east-to-west`. The RTO output is the headline.
- **The choice is reversible.** When B is wanted, it slots in alongside as `nexus kafka failover east-to-west --translate-offsets --group <name>` (or similar) without breaking the v0.5.0 verb shape.

### Negative

- **Doesn't translate per-group offsets.** A real consumer app pointed at `kafka-east` would NOT seamlessly resume on `kafka-west` from where it left off; it would resume from its `auto.offset.reset` policy (typically `latest` → potential lost messages, or `earliest` → potential duplicates). This is a real DR limitation, and the verb's output is clear about it. Mitigated by `streamcore` + v0.5.1 being on the roadmap.
- **The "RTO" measured is target-health-RTO, not consumer-resume-RTO.** Under B, the "consumers can resume" time would be `target healthy` + `offset translation`. Under A, only the first half is measured. Acceptable for a lab DR demo; production would care about the full number.
- **Sequential `vmrun suspend` of 3 brokers** adds ~6-10 s before the RTO clock even starts. The lab-host concurrency flake prevents going parallel. Mitigated by measuring RTO from "all source brokers suspended", not from "verb invoked".

### Neutral

- **Re-uses the failover-test family's settings + render + Stopwatch patterns** (`FailoverTestSettingsBase`, `EmitHuman`/`EmitJson`, monotonic timeline). The Render is a near-direct port; the Service is a structural port of `FailoverTestService.RunSwarmManagerAsync`.
- **No new Vault tokens consumed.** Unlike `FailoverTestBootstrapper`, the kafka-failover bootstrapper reads no Vault KV — it only needs `vms.yaml` + the SSH key + `vmrun.exe`. The kafka CLI tools on the broker authenticate via the broker's own on-disk PEM keystore (rendered by Vault Agent at the OS layer, not the CLI's concern).

## Verification

`docs/verification/0.5.0-kafka-failover.md` records:

- The AOT binary size after the v0.5.0 build vs the v0.4.0 baseline (22.65 → 22.75 MB).
- Both directions exercised against the live tier — east→west + west→east — with the actual RTOs.
- The CLI surface (`nexus kafka failover --help`, `--json` output, exit-code semantics).
- The cross-tier doc sweep (CHANGELOG, README, portfolio-index, `grezap/grezap` profile, plan README) — per `feedback_handbook_standard.md` invariant 1.
