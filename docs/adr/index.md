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
