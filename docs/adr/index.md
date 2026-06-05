# Architecture Decision Records — `nexus-cli`

> Numbering is local to this repo (independent of the parent `nexus-platform-plan` ADRs). Format follows
> [Michael Nygard's classic template](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

| ID | Status | Title |
|---:|:---:|---|
| [ADR-0001](ADR-0001-cli-framework.md) | Accepted | CLI framework: Spectre.Console.Cli over System.CommandLine + Cocona |
| [ADR-0002](ADR-0002-aot-from-day-one.md) | Accepted | Native AOT from day one; partial trim mode |
| [ADR-0003](ADR-0003-three-project-layout.md) | Accepted | 3-project layered solution enforced by NetArchTest |
| [ADR-0004](ADR-0004-vault-token-from-env.md) | Accepted | Auth model: consume operator's `VAULT_TOKEN` from env; no embedded creds |
| [ADR-0005](ADR-0005-dapper-on-aot.md) | Accepted | Dapper + FluentMigrator on AOT paths (future DB I/O) |
| [ADR-0006](ADR-0006-handrolled-vms-yaml-reader.md) | Accepted | Hand-rolled vms.yaml flow-mapping reader (no YamlDotNet) |
| [ADR-0007](ADR-0007-ssh-net-managed-client.md) | Accepted | SSH client: SSH.NET (managed) over ssh.exe shell-out or native libssh |
| [ADR-0008](ADR-0008-kafka-failover-demo-grade-via-ssh.md) | Accepted | Phase 0.F v0.5: `kafka failover` as a demo-grade DR verb via SSH + the kafka CLI scripts (defers per-consumer-group offset translation to v0.5.1; keeps AOT under the 25 MB gate by not adding `Confluent.Kafka`) |
| [ADR-0009](ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) | Accepted | Phase 0.G v0.6: `IClusterAdapter` SPI for the data-tier verb expansion (one adapter per cluster, SSH-shell-out, no managed DB drivers); System B JSON demo spec extended with `prerequisites` / `expectedExitCode` / `expectedOutputContains` / `observe[]` / `whatProves` (backwards compatible); AOT exit gate raised to ≤30 MB per `nexus-platform-plan` ADR-0024 |
| [ADR-0010](ADR-0010-cluster-adapter-patterns-and-redis-adapter.md) | Accepted | Phase 0.G.1 v0.6.0: cross-adapter patterns + Redis exemplar — scale-out = apply-on-demand against the proven Terraform/Packer graph (`--count N`, unbounded; reserve an IP/MAC range, no idle VMs) + role-aware SSH join (Greg 2026-06-05: "most stable; live add an arbitrary number of nodes with their role"); chaos = on-node `nexus-chaos.sh` helper with idempotent time-boxed auto-heal; backup = engine-native dump → gateway NFS + restore round-trip verify; cli.ps1 + CI size gate 25→30 MB for the 0.G line |
| [ADR-0011](ADR-0011-mongo-adapter-and-operator-credential-model.md) | Accepted | Phase 0.G.2 v0.6.1: `MongoAdapter` + the Vault-KV operator-credential model for ALL password-auth clusters (Mongo/Percona/Patroni/SQL) — a dedicated least-privilege `nexus-cluster-admin` operator user (clusterMonitor+clusterManager+backup+restore+userAdminAnyDatabase) whose password lives ONLY in Vault KV (`nexus/oltp/mongo/operator-password`) and is fetched at runtime via an optional `INexusVaultClient` plumbed through `ClusterBootstrapper` (mTLS-only Redis/Kafka unaffected; createUser bootstrap reads it via the node's own Vault Agent token — never on disk). Engine specifics: double-quoted `--eval` JS, URI-database-path scopes `mongodump`, `--nsInclude` required for `mongorestore` ns-remap |
