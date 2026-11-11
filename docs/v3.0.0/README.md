# SourceFlow.Net v3.0.0 — Technical Design

| | |
|---|---|
| **Status** | Draft for architecture review |
| **Target** | .NET 10 (`net10.0`) and C# 14 only |
| **Input** | [Product requirements](../product/README.md), especially the [gaps and open questions](../product/06-Gaps-Risks-and-Open-Questions.md) |
| **Nature** | Ground-up rewrite. Not source- or binary-compatible with v2. A migration path is provided ([doc 12](12-Migration-Delivery-and-Traceability.md)) |

## 1. Executive summary

v2 proved there is demand for a .NET library that covers commands, state changes, events, read models and cross-service messaging in one package. Its weak points are structural, not cosmetic:

- It is **command-sourced**. Replaying commands re-runs business logic under today's code, so replay can't be trusted and causes side effects.
- Logging the command, persisting the entity and dispatching the event are **not atomic**.
- The production-grade messaging features **exist but are not wired**.
- Message types are identified by **CLR assembly-qualified names**.
- Discovery uses **runtime reflection**, and DI lifetimes don't match.

Patching these one at a time would leave the core model unsound.

v3 rebuilds the library on a small number of well-proven patterns:

| Pillar | v3 design | What it fixes |
|---|---|---|
| **Correct model** | **Event sourcing** with aggregates written as pure decide/evolve functions ("deciders"). Events are the source of truth; commands are transient intents. | Unsafe replay, implicit rebuild contract, entity ids logged as 0 |
| **Durability** | **One transaction per command**: event append, outbox, inbox and inline projections commit together. Optimistic concurrency on the stream version, with an automatic re-decide on conflict. | Non-atomic writes, sequence-number races, swallowed failures |
| **Correct distribution** | Every command has **one owning service**. Routing is local *or* remote. Integration events leave through a **transactional outbox**, and inbound messages are deduplicated by an **inbox** keyed on the logical message id. | Hybrid double handling, owner can't replay, per-delivery idempotency |
| **Gap-free streaming** | The global event position **follows commit order** (PostgreSQL `xid8`, SQL Server `rowversion`). Subscriptions are partitioned, protected by fenced leases, and checkpointed in the same transaction as their writes. | Projections that can silently skip or double-apply events |
| **Observability by default** | OpenTelemetry traces and metrics are always emitted (close to free when nobody listens), use the standard messaging semantic conventions, and include lag, outbox-age and conflict metrics as first-class signals. | Telemetry that was off by default and partly wrong |
| **Performance** | Source-generated handler maps and JSON contracts, no reflection on the hot path, pooled buffers, a single database round trip per command, batched relays, and per-partition parallelism. | O(n) sequence lookup, reflection-heavy dispatch |
| **Parity across clouds** | One transport SPI and a **Transport Conformance Kit** that every provider must pass: AWS SNS/SQS, Azure Service Bus, Google Pub/Sub, and in-memory. | AWS options ignored, broken GCP fan-out, providers that differ in what they support |
| **Queryable read side** | A generated event schema catalog (JSON Schema, AsyncAPI export) and **SFQL**: a read-only, schema-checked, tenant- and PII-safe query language over the event log, read models and temporal aggregate state. Its predicates use CloudEvents SQL syntax. Consistency tokens give read-your-writes. | No supported way to query events. Raw SQL coupled to the physical schema. |
| **Visual workflows (separate utility)** | A YAML saga DSL compiled into ordinary process managers, plus **SourceFlow Studio** (React): a domain map across services, state-machine views of success, failure, retry and compensation paths, simulation, generated tests and PR diagrams. One semantic core, shared with the browser through WebAssembly. | Workflows were invisible, and failure paths were reviewed only by reading code. |
| **Multi-tenant and DCB-ready** | Conjoined tenancy with PostgreSQL row-level security in 3.0. Event tags make stored events ready for dynamic consistency boundaries (3.1). | Tenancy was only a header. Cross-aggregate rules need oversized aggregates. |
| **Secure by default** | Message types must be registered before they can be received. KMS envelope encryption. Crypto-shredding for personal data in the immutable log. Redaction through the platform. Least-privilege provisioning modes. | Arbitrary type instantiation, unmasked logs, mandatory create permissions |

## 2. Document map

| # | Document | Read if you are… |
|---|----------|------------------|
| 01 | [Architecture Overview](01-Architecture-Overview.md) — quality goals, principles, package layout, runtime flows | everyone (start here) |
| ADRs | [Architecture Decision Records](ADRs/README.md) — 27 ADRs, one file each, with alternatives and consequences | reviewers and approvers |
| 03 | [Domain Programming Model](03-Domain-Programming-Model.md) — messages, ids, aggregates, handlers, process managers, projections, generators | domain developers |
| 04 | [Messaging Pipeline and Delivery Semantics](04-Messaging-Pipeline.md) — envelope, type registry, routing, middleware, inbox and outbox, retries, dead letters | framework and platform engineers |
| 05 | [Event Store and Persistence](05-Event-Store-and-Persistence.md) — contracts, schemas, concurrency, global ordering, snapshots | storage implementers and DBAs |
| 06 | [Projections and Subscriptions](06-Projections-and-Subscriptions.md) — subscription engine, partitions, leases, rebuilds | framework engineers |
| 07 | [Cloud Transports](07-Cloud-Transports.md) — SPI, topology, AWS, Azure and GCP mappings, provisioning, conformance kit | transport implementers and platform engineers |
| 08 | [Observability](08-Observability.md) — traces, metrics, logs, health checks, SLOs | SRE and operators |
| 09 | [Performance and Scalability](09-Performance-and-Scalability.md) — hot-path design, capacity model, targets, benchmarks | performance reviewers |
| 10 | [Security and Compliance](10-Security-and-Compliance.md) — threat model, encryption, crypto-shredding, supply chain | security reviewers |
| 11 | [Testing and Quality Engineering](11-Testing-and-Quality.md) — spec testing, conformance kits, chaos, benchmarks in CI | everyone |
| 12 | [Migration, Delivery Plan and Traceability](12-Migration-Delivery-and-Traceability.md) — v2→v3 migration, milestones, gap→fix matrix, risks | product and engineering leads |
| 13 | [Competitive Analysis](13-Competitive-Analysis.md) — Marten/Wolverine, Eventuous, KurrentDB, Axon 5, NServiceBus/MassTransit, Akka.NET; feature matrix, adopted practices, v3 advantages | product, leadership, architects |
| 14 | [Query Side: Event Schema Catalog and SFQL](14-Query-Side-and-SFQL.md) — CQRS read-side semantics, JSON Schema catalog, AsyncAPI export, the SFQL language (pipeline + SQL syntax with joins, grouping and ordering), and the injection-safe **query API on every service** | domain developers, operators, analysts |
| 15 | [Workflow DSL and SourceFlow Studio](15-Workflow-DSL-and-Studio.md) — saga DSL compiled to process managers; React visual designer over a business domain's services (success, failure, retry, rollback); simulation and spec generation | domain developers, architects, domain experts |

## 3. Answers to the open product questions

**Product sign-off received on 2026-11-10.** Every answer below is **Accepted**. Q2 was accepted with an added requirement, and Q5 and Q6 were fixed to release 3.0.0.

| Question ([doc 06](../product/06-Gaps-Risks-and-Open-Questions.md#open-questions-for-product-decisions)) | Decision (accepted) | ADR |
|---|---|---|
| Q1 — Command sourcing or event sourcing? | **Event sourcing.** Commands are not stored as the source of truth. | [ADR-002](ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md) |
| Q2 — Where does the command log live? | In the **owning service's** event store. Its streams are the only authoritative history. **Added requirement:** querying events is modelled *inside* the owning service's domain and business boundary. Each service exposes queries only over its own events and read models, using its own domain vocabulary. Other services never query its event log directly; they consume its integration events or call its query API. See [doc 14 §1.1](14-Query-Side-and-SFQL.md#11-query-ownership-the-service-boundary-q2). | [ADR-007](ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md), [ADR-023](ADRs/ADR-023-sfql-query-language-with-cesql-predicates.md) |
| Q3 — Hybrid dispatch by default? | **No.** A command type is handled locally *or* routed to exactly one remote owner, never both. | [ADR-007](ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md) |
| Q4 — Id strategy? | **Strongly typed ids**, assigned by the caller. UUIDv7 by default; string ids also supported. | [ADR-015](ADRs/ADR-015-strongly-typed-caller-assigned-ids-uuidv7-default.md) |
| Q5 — GA bar for GCP? | **Google Pub/Sub ships GA in 3.0.0**, passing the full conformance kit. | [ADR-014](ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md) |
| Q6 — Azure provider? | **Azure Service Bus ships GA in 3.0.0**, with no slip to 3.1. AWS, Azure and GCP are all release-blocking. | [ADR-014](ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md) |
| Q7 — Consistency model? | **Transactional write side** (append, outbox and inbox in one transaction). Read side is configurable per projection: inline (strong) or async (eventual). | [ADR-006](ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md), [ADR-016](ADRs/ADR-016-projections-inline-or-async-with-partitioned-fenced.md) |

## 4. Conventions in these documents

- **MUST, SHOULD, MAY** follow RFC 2119.
- Product requirement IDs (`CMD-05`, `GAP-19`, …) refer to [docs/product](../product/README.md).
- v3 design IDs: ADRs are `ADR-nnn`, and each document numbers its own design rules (for example `ES-R3` = event-store rule 3).
- Code samples are **illustrative API shapes**, not final signatures. The public API is frozen at the end of M1 by API approval tests ([doc 11](11-Testing-and-Quality.md)).
- Performance figures are **targets to validate**, not measurements ([doc 09](09-Performance-and-Scalability.md)).
