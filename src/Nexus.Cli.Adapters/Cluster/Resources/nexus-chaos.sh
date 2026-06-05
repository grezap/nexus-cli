#!/usr/bin/env bash
# =============================================================================
# nexus-chaos.sh -- on-node chaos-injection helper for nexus-cli's `chaos` verb.
#
# Shipped + pushed to a target node over SSH by the cluster adapters
# (IClusterAdapter.ApplyChaosAsync); see nexus-cli ADR-0010. It is the one
# adapter operation that is NOT a thin shell-out to an existing engine CLI --
# there is no engine-native "inject a fault" command -- so this small,
# dependency-light helper provides one with a strict ZERO-TOUCH, idempotent,
# SELF-HEALING contract:
#
#   * Every fault is TIME-BOXED. `inject` schedules its own revert via a
#     transient `systemd-run --on-active` timer, so even if the operator's SSH
#     session dies the node un-breaks itself at <duration>. The adapter ALSO
#     issues an explicit `lift` at the end -- belt and braces.
#   * Faults live in DEDICATED namespaces (a separate nft table at higher
#     priority than the main ruleset; a root netem qdisc; a cgroup-less bounded
#     hog process; a unit stop/cont) so `lift` is a clean, total removal and we
#     never touch / append-after the canonical firewall drop
#     (feedback_nftables_runtime_add_after_drop.md).
#   * Re-running `inject` for the same scenario first lifts the old one
#     (idempotent).
#
# Usage:
#   nexus-chaos.sh inject  <scenario> <duration_s> [intensity] [target]
#   nexus-chaos.sh lift    <scenario>
#   nexus-chaos.sh status
#
# Scenarios (match Core ChaosScenario.ScenarioType):
#   network-partition   target = peer CIDR to cut (default 192.168.10.0/24, the
#                                VMnet10 backplane). nft drop table.
#   packet-loss         intensity = loss %% (default 20). tc netem on backplane NIC.
#   slow-disk           intensity = added latency ms (default 200). tc netem delay
#                                (models slow I/O round-trips on the wire path).
#   cpu-starve          intensity = worker count (default = nproc). stress-ng if
#                                present, else a bounded shell busy-loop fallback.
#   memory-pressure     intensity = MB to pin (default 512). stress-ng --vm if
#                                present, else a tmpfs balloon fallback.
#   process-kill        target = systemd unit to SIGSTOP (required, e.g.
#                                redis-server / nexus-mongo / nexus-patroni).
#                                lift = SIGCONT (start if it had exited).
#
# Exit codes: 0 ok; 2 usage/arg error; 3 unsupported scenario; 4 missing tool.
# Run as root (the adapter invokes it under sudo). Safe to `set -euo pipefail`.
# =============================================================================
set -euo pipefail

STATE_DIR=/run/nexus-chaos
NFT_TABLE="inet nexus_chaos"
DEFAULT_BACKPLANE_CIDR="192.168.10.0/24"

log() { printf 'nexus-chaos: %s\n' "$*" >&2; }
die() { log "ERROR: $*"; exit "${2:-2}"; }
have() { command -v "$1" >/dev/null 2>&1; }

mkdir -p "$STATE_DIR"

# Backplane NIC = the interface carrying the VMnet10 192.168.10.0/24 address.
# Falls back to the default route's device. Used by the tc scenarios.
backplane_nic() {
    local dev
    dev=$(ip -o -4 addr show 2>/dev/null | awk '$4 ~ /^192\.168\.10\./ {print $2; exit}')
    [ -n "$dev" ] || dev=$(ip -o -4 route show default 2>/dev/null | awk '{print $5; exit}')
    printf '%s' "${dev:-eth0}"
}

# Schedule the matching `lift` to run at <duration> via a transient timer, so a
# dropped SSH session can never leave the node broken. Idempotent (replaces any
# existing timer for this scenario).
schedule_revert() {
    local scenario="$1" duration="$2" unit="nexus-chaos-revert-${scenario}"
    systemctl stop "${unit}.timer" >/dev/null 2>&1 || true
    if have systemd-run; then
        systemd-run --quiet --collect \
            --unit="$unit" --on-active="${duration}s" \
            /usr/local/bin/nexus-chaos.sh lift "$scenario" >/dev/null 2>&1 \
            || nohup bash -c "sleep $duration; /usr/local/bin/nexus-chaos.sh lift '$scenario'" >/dev/null 2>&1 &
    else
        # No systemd-run -- detached sleep is the portable fallback.
        nohup bash -c "sleep $duration; /usr/local/bin/nexus-chaos.sh lift '$scenario'" >/dev/null 2>&1 &
    fi
}

# ---- inject -----------------------------------------------------------------
inject() {
    local scenario="${1:-}" duration="${2:-30}" intensity="${3:-}" target="${4:-}"
    [ -n "$scenario" ] || die "inject needs a scenario"
    case "$duration" in (*[!0-9]*|'') die "duration must be integer seconds";; esac

    # Idempotent: clear any prior instance of this scenario first.
    lift "$scenario" >/dev/null 2>&1 || true

    case "$scenario" in
      network-partition)
        have nft || die "nft not found" 4
        local cidr="${target:-$DEFAULT_BACKPLANE_CIDR}"
        # Dedicated table, higher priority (-300) than the baseline filter, so the
        # drop is reached BEFORE the canonical accept/drop and lifts as one unit.
        nft add table $NFT_TABLE
        nft "add chain $NFT_TABLE in  { type filter hook input  priority -300 ; policy accept ; }"
        nft "add chain $NFT_TABLE out { type filter hook output priority -300 ; policy accept ; }"
        nft "add rule  $NFT_TABLE in  ip saddr $cidr drop"
        nft "add rule  $NFT_TABLE out ip daddr $cidr drop"
        printf '%s\n' "$cidr" >"$STATE_DIR/network-partition"
        log "partitioned from $cidr (table $NFT_TABLE)"
        ;;
      packet-loss|slow-disk)
        have tc || die "tc (iproute2) not found" 4
        local nic; nic=$(backplane_nic)
        local spec
        if [ "$scenario" = packet-loss ]; then spec="loss ${intensity:-20}%"
        else spec="delay ${intensity:-200}ms"; fi
        tc qdisc replace dev "$nic" root netem $spec
        printf '%s\n' "$nic" >"$STATE_DIR/$scenario"
        log "$scenario on $nic: netem $spec"
        ;;
      cpu-starve)
        local n="${intensity:-$(nproc 2>/dev/null || echo 2)}"
        if have stress-ng; then
            stress-ng --cpu "$n" --timeout "${duration}s" >/dev/null 2>&1 &
            echo $! >"$STATE_DIR/cpu-starve"
        else
            # Fallback: n bounded busy-loops that self-exit at <duration>.
            : >"$STATE_DIR/cpu-starve"
            local i
            for ((i=0;i<n;i++)); do
                nohup bash -c "end=\$((SECONDS+$duration)); while [ \$SECONDS -lt \$end ]; do :; done" >/dev/null 2>&1 &
                echo $! >>"$STATE_DIR/cpu-starve"
            done
        fi
        log "cpu-starve: $n workers for ${duration}s"
        ;;
      memory-pressure)
        local mb="${intensity:-512}"
        if have stress-ng; then
            stress-ng --vm 1 --vm-bytes "${mb}m" --vm-keep --timeout "${duration}s" >/dev/null 2>&1 &
            echo $! >"$STATE_DIR/memory-pressure"
        else
            # Fallback: a tmpfs balloon, auto-removed at <duration>.
            local mnt="$STATE_DIR/mem"
            mkdir -p "$mnt"
            mount -t tmpfs -o size="${mb}m" tmpfs "$mnt" 2>/dev/null || true
            dd if=/dev/zero of="$mnt/balloon" bs=1M count="$mb" >/dev/null 2>&1 || true
            printf '%s\n' "$mnt" >"$STATE_DIR/memory-pressure"
        fi
        log "memory-pressure: ${mb} MB for ${duration}s"
        ;;
      process-kill)
        [ -n "$target" ] || die "process-kill needs a target unit"
        systemctl kill -s STOP "$target"
        printf '%s\n' "$target" >"$STATE_DIR/process-kill"
        log "process-kill: SIGSTOP $target"
        ;;
      *) die "unknown scenario '$scenario'" 3 ;;
    esac

    schedule_revert "$scenario" "$duration"
    log "injected '$scenario'; auto-revert scheduled at ${duration}s"
}

# ---- lift -------------------------------------------------------------------
lift() {
    local scenario="${1:-}"
    [ -n "$scenario" ] || die "lift needs a scenario"
    systemctl stop "nexus-chaos-revert-${scenario}.timer" >/dev/null 2>&1 || true

    case "$scenario" in
      network-partition)
        nft delete table $NFT_TABLE >/dev/null 2>&1 || true
        ;;
      packet-loss|slow-disk)
        local nic; nic=$(cat "$STATE_DIR/$scenario" 2>/dev/null || backplane_nic)
        tc qdisc del dev "$nic" root >/dev/null 2>&1 || true
        ;;
      cpu-starve)
        if [ -f "$STATE_DIR/cpu-starve" ]; then
            while read -r pid; do [ -n "$pid" ] && kill "$pid" >/dev/null 2>&1 || true; done <"$STATE_DIR/cpu-starve"
        fi
        ;;
      memory-pressure)
        local mnt; mnt=$(cat "$STATE_DIR/memory-pressure" 2>/dev/null || true)
        if [ -n "$mnt" ] && [ -d "$mnt" ]; then
            umount "$mnt" >/dev/null 2>&1 || true
        else
            pkill -f 'stress-ng --vm' >/dev/null 2>&1 || true
        fi
        ;;
      process-kill)
        local unit; unit=$(cat "$STATE_DIR/process-kill" 2>/dev/null || echo "$target")
        if [ -n "$unit" ]; then
            systemctl kill -s CONT "$unit" >/dev/null 2>&1 || true
            # If it had actually exited rather than just stopped, bring it back.
            systemctl is-active --quiet "$unit" || systemctl start "$unit" >/dev/null 2>&1 || true
        fi
        ;;
      *) die "unknown scenario '$scenario'" 3 ;;
    esac
    rm -f "$STATE_DIR/$scenario"
    log "lifted '$scenario'"
}

# ---- status -----------------------------------------------------------------
status() {
    local found=0 f
    for f in "$STATE_DIR"/*; do
        [ -e "$f" ] || continue
        [ "$(basename "$f")" = mem ] && continue
        printf '%s active: %s\n' "$(basename "$f")" "$(cat "$f" 2>/dev/null || true)"
        found=1
    done
    [ "$found" = 1 ] || echo "no active chaos scenarios"
}

cmd="${1:-}"; shift || true
case "$cmd" in
  inject) inject "$@" ;;
  lift)   lift "$@" ;;
  status) status ;;
  *) die "usage: nexus-chaos.sh {inject <scenario> <duration> [intensity] [target] | lift <scenario> | status}" ;;
esac
