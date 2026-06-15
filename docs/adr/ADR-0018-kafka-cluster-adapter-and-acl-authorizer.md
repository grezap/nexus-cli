# ADR-0018 — Per-cluster KafkaClusterAdapter + KafkaEcosystemAdapter + StandardAuthorizer enablement

- **Status:** Accepted
- **Phase / tag:** Phase 0.H.7 / `nexus-cli` v0.6.7
- **Date:** 2026-06-15
- **Context ADRs:** [ADR-0008](ADR-0008-kafka-failover-demo-grade-via-ssh.md) (the v0.5 demo-grade kafka failover), [ADR-0009](ADR-0009-cluster-adapter-spi-and-extended-demo-spec.md) (the `IClusterAdapter` SPI), [ADR-0010](ADR-0010-cluster-adapter-patterns-and-redis-adapter.md) (the mTLS-only / chaos / apply-on-demand patterns, reused verbatim). Cross-tier: `nexus-platform-plan` ADR-0020..0023 (the Kafka tier's KRaft + mTLS design).

## Context

The v0.5 `KafkaAdapter` (ClusterId `kafka`) was a deliberate thin retrofit: it implemented only `FailoverAsync` (the cross-region MirrorMaker-2 east↔west DR drill) and stubbed the other twelve verbs with "deferred — use the kafka.ps1 smoke gates" messages. Greg's standing goal — *"the CLI must be able to manage everything"* — required promoting Kafka to the full verb surface like every other data-tier cluster, **without** breaking the existing `nexus kafka failover` / `nexus failover-test cluster kafka` DR surface.

The Kafka tier (Phase 0.H, `nexus-infra-kafka` v0.1.0) is two independent KRaft clusters — `kafka-east` (primary, .70.21–.23) and `kafka-west` (DR, .70.24–.26), each 3 combined broker+controller nodes — plus a 9-node ecosystem (Schema Registry HA pair, REST Proxy, Kafka Connect + Debezium, ksqlDB, MirrorMaker 2 pair). It is **mTLS-only** (Kafka 3.8 native PEM keystores from Vault PKI, `ssl.client.auth=required`); there is no SASL and no operator password.

## Decision

### 1. Per-cluster `KafkaClusterAdapter`, registered twice

A single parameterized `KafkaClusterAdapter(clusterId, …)` is registered **twice** in `ClusterBootstrapper` — once as `kafka-east`, once as `kafka-west` — matching the vms.yaml cluster keys and the established *ClusterId == vms.yaml-name* convention (`redis`, `postgres`, `sqlserver`, …). The existing `KafkaAdapter` (ClusterId `kafka`) **stays unchanged** as the cross-region DR meta-cluster. So the verb namespace is:

- `nexus <verb> kafka-east|kafka-west …` — the full per-cluster surface (status/health/topology/failover/scale-out/backup/cert-rotate/acl/chaos) against that cluster's 3 brokers.
- `nexus failover-test cluster kafka --direction east-to-west` — the unchanged MM2 DR drill (the meta-cluster).

### 2. Auth = mTLS-only, no `INexusVaultClient` (like Redis)

No operator password, no Vault-KV credential (contrast Mongo/Percona/Patroni/SQL/ClickHouse/StarRocks). The operator identity **is** the broker's own Vault-PKI keystore: every Kafka CLI runs ON a broker over SSH as

```
sudo /opt/kafka/bin/kafka-*.sh --bootstrap-server SSL://<vmnet10>:9092 --command-config /etc/nexus-kafka/client-ssl.properties …
```

`sudo` is required (`/etc/nexus-kafka` is `0750 root:kafka`, nexusadmin is not in the group — the Consul-`/etc/`-0750 lesson). The bootstrap host is the broker's **VMnet10 backplane IP** because `ssl.endpoint.identification.algorithm=https` requires the bootstrap host to be a cert SAN (the VMnet10 IP is one). No managed `Confluent.Kafka` driver is linked — the SSH-shell-out invariant (ADR-0009) keeps AOT flat (**26.18 MB / 30**, NetArchTest-enforced).

### 3. Verb → tool map (live-verified against both clusters)

| Verb | Mechanism |
|---|---|
| `status` | `kafka-metadata-quorum describe --status/--replication` → leader + voters + per-voter lag; under-/offline-partition counts |
| `health` | per-broker `kafka.service` + quorum-has-leader + 3 voters + voter fetch-lag + 0 under-replicated + 0 offline |
| `topology` | `kafka-topics --describe` → one shard per topic (`Np RFm`, partition-0 leader, replica set) |
| `failover` | **controlled controller-leader move**: stop `kafka.service` on the quorum leader → poll a survivor for re-election → RTO → restart-rejoin (complements the cross-region MM2 DR). RTO ≈ 4.5 s |
| `scale-out remove` | drain a broker (`systemctl stop kafka`), guarded so the controller-quorum majority is never lost |
| `scale-out add` | rejoin a stopped broker (`--role broker`) and wait for caught-up (lag 0). The combined controller quorum is fixed at 3 at format time; a genuine 4th broker is an apply-on-demand IaC growth op |
| `backup take/restore` | capture a topic to a node-local `.jsonl` (`kafka-console-consumer … --consumer.config`), then replay it into a verify topic and count the produce→consume round-trip |
| `cert-rotate` | per-broker re-issue from the node's **own** Vault Agent token (`pki_int/issue/kafka-broker`) → write `bundle.pem` → `kafka-tls-split.sh` → restart; **rolling** (one at a time, wait rejoin) since KRaft tolerates exactly one down |
| `acl` | `kafka-acls --list/--add/--remove` (requires the authorizer — decision 4) |
| `chaos` | the embedded `nexus-chaos.sh` (ADR-0010); `process-kill` SIGSTOPs `kafka.service`, observes ISR/leader churn, lifts, confirms recovery |

> **CLI-flag trap (caught live before it shipped):** the admin tools take `--command-config`, but `kafka-console-producer`/`-consumer` take `--producer.config`/`--consumer.config`. Passing `--command-config` to a console tool silently prints usage and processes nothing.

### 4. Enable the KRaft `StandardAuthorizer` so `acl` enforces

Before v0.6.7 the brokers carried no `authorizer.class.name`, so `kafka-acls` returned `SecurityDisabledException` and every principal had implicit full access. A **new, cold-rebuild-proven overlay** `role-overlay-kafka-acl-authorizer.tf` (gated `var.enable_kafka_acl_authorizer`, default true) appends to each broker's `server.properties`:

```
authorizer.class.name=org.apache.kafka.metadata.authorizer.StandardAuthorizer
super.users=<15 platform principals>
allow.everyone.if.no.acl.found=false
```

followed by a **rolling** restart (one broker at a time, followers first, leader last — NOT a big-bang, because enabling an authorizer is not a wire-format change; the SSL listeners are unchanged, so the quorum keeps a leader throughout).

**`super.users` = all 15 tier principals (6 brokers + 9 ecosystem), on both clusters.** This is the load-bearing decision. StandardAuthorizer is deny-by-default once `allow.everyone.if.no.acl.found=false`, and every node that connects to a broker authenticates with its own Vault-PKI client cert mapped (DEFAULT rule, single-RDN DN) to `User:CN=<host>.kafka.nexus.lab`. The 6 broker principals carry inter-broker replication + the controller quorum; the 9 ecosystem principals (Schema Registry, REST, Connect, ksqlDB, MirrorMaker 2) are all Kafka **clients**. Omitting the ecosystem principals would have left them deny-by-default and broken the running platform. Listing all 15 on both clusters is harmless (a west broker principal is simply never seen on east) and keeps ordinary **application** principals (anything not in the list) deny-by-default — which is exactly what makes `acl grant` a meaningful, demonstrable operation.

> The principal format was validated on a **single follower canary** (east-3) before the full roll: it rejoined the mTLS quorum cleanly with the authorizer active, proving the DN→principal mapping was correct, with the other two voters untouched and the quorum intact — a wrong `super.users` would have isolated only that one node.

### 5. `KafkaEcosystemAdapter` — a lighter OBSERVE adapter (ClusterId `kafka-ecosystem`)

The 9 ecosystem services are stateless clients, not a leader/quorum store, so this adapter implements the **observe + maintenance** subset: `status`/`health`/`topology` (per-service `systemctl` + each service's HTTPS health endpoint — SR :8081 `/subjects`, REST :8082 `/v3/clusters`, **Connect :8083 `/`**, **ksqlDB :8088 `/healthcheck`** — plus MM2 liveness via the journal), `cert-rotate` (re-issue + `kafka-tls-split.sh` which rebuilds both the PEM and the PKCS#12 keystores Connect/ksqlDB need + restart the service), and `chaos`. `failover`/`scale-out`/`backup`/`acl` return a clear pointer to the right surface (the per-cluster adapters or the IaC overlays). *(The Connect/ksqlDB ports were corrected from the original scoping note 8088/8090 to the live-probed 8083/8088.)*

## Consequences

- The CLI now manages all 23 cluster-adapter ClusterIds end-to-end; `kafka-east`/`kafka-west`/`kafka-ecosystem` joined the matrix. **All verbs live-verified GREEN against both running clusters + the ecosystem**, with **zero live-caught bugs** this slice (the thorough up-front contract probe — the `--consumer.config` flag, the real Connect/ksqlDB ports, the ecosystem-principal `super.users` design, the backplane-IP bootstrap — pre-empted the usual 1–4). 86/86 tests (+15 parser tests); AOT 26.18 MB.
- Enabling the authorizer is a permanent change to the running tier's security posture (deny-by-default). It is fully reversible (`var.enable_kafka_acl_authorizer=false` strips the stanza + rolling-restarts) and cold-rebuild-proven.
- A genuine 4th broker remains an apply-on-demand IaC operation (combined-mode controller quorum is fixed at format time) — consistent with the Redis/StarRocks scale-out story.
