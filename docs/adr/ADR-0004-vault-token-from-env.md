# ADR-0004 — Auth model: consume operator's `VAULT_TOKEN` from env; no embedded creds

- **Status:** Accepted
- **Date:** 2026-05-07
- **Phase:** 0.F

## Context

`nexus-cli` needs short-lived management tokens for Consul (HTTPS:8501) and
Nomad (HTTPS:4646). The lab's authoritative source for those tokens is Vault
KV at `nexus/swarm/{consul,nomad}-bootstrap-token` (per Phase 0.E.2.3 +
0.E.3.2 close-out canon).

To reach those KV paths, the CLI itself needs a Vault token. Three patterns:

1. **CLI does its own login** — embed AppRole role-id/secret-id, or prompt
   for AD username/password, exchange for a token. Implies persistent
   storage, secret-handling code, plus another reflection-y dependency for
   any LDAP/HTTP form parser.
2. **OAuth-style device flow** — opens a browser, waits for callback. Wrong
   ergonomic for a TTY-first operator tool.
3. **Inherit `VAULT_TOKEN` from the operator's env** — operator runs
   `vault login` once per session (their existing flow), nexus-cli reads
   `$env:VAULT_TOKEN`, `$env:VAULT_ADDR`, `$env:VAULT_CACERT` (or
   `$env:NEXUS_CA_BUNDLE`).

The operator already has an authenticated `vault` CLI session running on
the build host; option 3 is zero-friction.

## Decision

Adopt option 3. `Nexus.Cli.Adapters.Vault.VaultTokenResolver` reads:

| Variable | Required | Use |
|---|---|---|
| `VAULT_TOKEN` | yes | `X-Vault-Token` header on every Vault call |
| `VAULT_ADDR` | yes | API base URL, e.g. `https://192.168.70.121:8200` |
| `NEXUS_CA_BUNDLE` | optional | Override; same shape as `VAULT_CACERT` |
| `VAULT_CACERT` | yes (unless `NEXUS_CA_BUNDLE` set) | Path to PEM bundle of the lab root CA |

Failure modes (each tested in `VaultTokenResolverTests.cs`):

- Missing `VAULT_TOKEN` → `Result.Fail("VAULT_TOKEN is not set. Run vault login first.")` + exit 2.
- Missing `VAULT_ADDR` → similarly.
- Neither CA-bundle var set → similarly.
- CA bundle path doesn't exist → similarly.

The `NEXUS_CA_BUNDLE` override exists so the operator can point at a
non-standard bundle (e.g., a pre-rotation copy during 0.D.5 leaf renewals)
without touching `VAULT_CACERT`.

## Consequences

- **+** No embedded creds; nothing to leak in tarballs / GH Releases.
- **+** Operator's existing token lifecycle (LDAP/AppRole, periodic renewal
  via `vault token renew`) governs the CLI's auth surface — no parallel TTL
  to track.
- **+** Trivial to use under CI / scripted runs: pass `VAULT_TOKEN` as a
  secret env var.
- **−** Operator must `vault login` first; the CLI prints a clear actionable
  error if they forget.
- **−** No persistent SSO at v0.1; deferred to v0.2 if a `nexus login` verb
  becomes valuable.

## Verification

- `nexus.exe cluster-status` with no env vars set exits 2 with
  `error: VAULT_TOKEN is not set. Run vault login first.`
- 6 unit tests in `VaultTokenResolverTests.cs` cover each failure mode +
  the success path + the `NEXUS_CA_BUNDLE` precedence rule.
