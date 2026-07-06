# ADR-0027 — `scale-up` / `VmrunVmResizer` + the day-2 destructive-op safety gates

- **Status:** Accepted
- **Phase / version:** Completion backlog batches 2-3 · nexus-cli v0.8.7
- **Date:** 2026-07-06
- **Supersedes / relates:** ADR-0009 (IClusterAdapter SPI + the `CanResizeVm` hook), ADR-0006 (the hand-rolled `vms.yaml` reader that resolves a VM → cluster), ADR-0007 (SSH.NET for the in-guest FS extend), ADR-0010 (apply-on-demand IaC for net-new-VM growth — the boundary this verb stops at). Closes GAP #13 (`scale-up`), GAP #11 (swarm guarded restore), GAP #4 (kafka resize-gate) of the completion backlog.

## Context

Three day-2 operator verbs were the last "big" gaps in the completion backlog. They share one property: **they are the operations that are easy to do dangerously.** Resizing a VM means powering it off; restoring a snapshot means overwriting live state. The backlog directive ("everything inside `nexus-cli`, nothing bypassed") required implementing them, but safely and honestly.

- **`scale-up`** was a `VmrunVmResizer` skeleton that returned `Fail` ("lands in 0.G.1.x"). It is a **generic** verb — one implementation over every VM, not per-adapter — so it needed to be cluster-aware without being coupled to any single adapter.
- **swarm `backup restore`** was a hard refusal (the `consul`/`nomad snapshot restore` overwrite the live orchestration tier in place).
- **kafka `scale-up`** needed a gate so a resize can't power-cycle the KRaft controller-leader.

## Decision

### 1. `VmrunVmResizer` (generic vertical resize)

- **CPU / RAM:** `vmrun stop` (soft→hard) → an **atomic `.vmx` edit** (`numvcpus` / `memsize`, write-temp-then-`File.Move`) → **cold** start. A suspend would not apply the edits, so the resize is a genuine power-cycle. VMs powered off stay off (the edit applies on next boot).
- **Disk:** `vmware-vdiskmanager -x` grows the backing `.vmdk` **offline** (grow-only; shrink refused), then a **SAFE** in-guest filesystem extend over SSH. The extend is gated by `growpart --dry-run` and handles plain-partition ext4 (`growpart`+`resize2fs`), LVM-on-partition (`growpart`+`pvresize`+`lvextend -r`), and Windows (`Resize-Partition` to `SizeMax`). **It never repartitions a live boot disk.**
- **Honest non-extend:** when root is **not** the last partition (the pre-2026-07-06 deb13 swap-after-root layout), `growpart --dry-run` fails → the vmdk grows but the guest FS is left untouched and the result says so plainly (`Outcome = ok` with a warning; exit codes 3 = layout-can't-grow, 4 = tool-unavailable are treated as safe non-extends, not failures). No false success. (The deb13 packer preseed was subsequently changed to a growable-root layout — no swap partition + a 2 GB `/swapfile` — so new clones auto-extend.)
- **New surface:** `IVmrunClient.StopAsync` / `StartAsync` / `GrowVirtualDiskAsync`; `VmrunPaths.ResolveVdiskManager()` locates `vmware-vdiskmanager` alongside `vmrun`; `ClusterBootstrapper.BuildVmResizer` wires an `ISshClient` for the guest-side grow.

### 2. Cluster-safety gate (refuse the write-primary)

`VmrunVmResizer` resolves the VM's **owning cluster adapter** and consults `IClusterAdapter.CanResizeVm` before powering anything off:

- `ResolveOwningAdapterId(catalogCluster, vmName)` maps a vms.yaml cluster to the adapter that owns the VM. Most tiers are 1:1; the documented splits are `sqlserver` → `sqlserver-ag` (by the `sql-ag` node-name prefix), `foundation` → `vault` / `foundation-ad` (by `vault` / `dc-nexus` prefix), `platform-tools` → `registry`. Edge/workstation VMs have no owning data-cluster adapter (no write-window to protect) → the gate is skipped.
- The resolved adapter's status is **warmed** (`GetStatusAsync`) first, because `CanResizeVm` reads a cache that status populates.
- If the VM is the current write-primary/leader — **or the cluster can't be reached to prove otherwise** — the resize is **refused** with exit 2 unless `--force-primary` is passed. Refusing-on-unreachable is deliberate: a resize is destructive, so absence of proof is treated as "might be the primary".

### 3. kafka resize-gate (GAP #4)

`KafkaClusterAdapter.CanResizeVm` returns `false` when the target member's live role is `controller-leader` (a resize would power-cycle the KRaft controller / write window). The meta `KafkaAdapter.CanResizeVm` **delegates** to the region owning the VM (`kafka-east-*` → the east adapter, `kafka-west-*` → the west). The resolution is locked with a unit test.

### 4. swarm guarded `backup restore` (GAP #11)

`SwarmAdapter.BackupRestoreAsync` requires an explicit **`--confirm-destructive`** (on top of the command's `--yes`) via `RestoreRequest.ConfirmDestructive`. Without it, it refuses (exit 2) and points at the DR runbook. With it, it uploads the take's `consul.snap` / `nomad.snap` to a manager and runs `consul snapshot restore` + `nomad operator snapshot restore` **online against the raft leader**, counting the restored Consul KV keys + Nomad jobs.

## Consequences

- Vertical resize is now a real, bidirectional, generic verb that composes with the per-adapter `CanResizeVm` gate through a small resolver — not N per-tier resizers.
- The dangerous day-2 ops carry explicit, reviewable, unit-tested guardrails (`--force-primary`, `--confirm-destructive`) and honest reporting (the deb13 root-not-last warning beats a green check that lies).
- Net-new-VM growth (a 4th vault voter, a grown swarm/kafka fleet, an added DC) remains **out of scope** — that is terraform/Packer apply-on-demand per ADR-0010, not a runtime CLI op.
- **Live-verified** 2026-07-05/06: redis-1 (cpu 2→4→2, ram 2048→3072→2048, disk 40→42 GB vmdk grow with the honest deb13 root-not-last warning), kafka-east (controller-leader refused, follower eligible), swarm (guard refuses without `--confirm-destructive`; GREEN restore with it). AOT win-x64 **28.25 MB / 30**; **310/310 tests** (+29). Playbooks: `docs/handbook.md` §3.5; demos `docs/demos/DEMO-17`, `DEMO-160`, `DEMO-161`, `DEMO-162`.
