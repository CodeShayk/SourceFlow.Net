# 13 — Competitive Analysis

**Purpose.** To check that the v3 design follows the practices and standards the mature event-sourcing ecosystem has converged on, to find where competitors are ahead, and to define where SourceFlow.Net v3 can offer a **clear, defensible advantage**.

**As of** November 2026. Facts marked † were checked against public sources (listed in §9) during this study. Everything else reflects widely documented, long-standing behaviour. Licences and features change quickly, so re-check before using this document externally.

## 1. Landscape and selection

| Category | Products studied | Why included |
|----------|------------------|--------------|
| .NET event stores and frameworks | **Marten** (+ its SQL Server sibling **Polecat**†), **Eventuous**, **KurrentDB** (formerly EventStoreDB)† with its .NET client, **Akka.NET Persistence** | Direct competitors for .NET event sourcing |
| .NET messaging frameworks with saga and outbox support | **Wolverine** (pairs with Marten as the "Critter Stack"), **NServiceBus**, **MassTransit** | Competitors for the messaging half of SourceFlow (outbox, inbox, sagas, transports) |
| Cross-platform reference | **Axon Framework 5 + Axon Server** (Java)†, the **DCB** specification community (dcb.events)† | They set new standards that .NET users will ask for |

Also noted, not profiled: Orleans `JournaledGrain`, Equinox (F#), Cratis Chronicle (.NET, DCB-oriented)†, Emmett (TypeScript), EventSourcingDB.

## 2. Profiles

### 2.1 Marten (+ Polecat) — JasperFx, MIT

- **Model.** PostgreSQL as document database plus event store. Projections can be inline, async (through the *async daemon*) or live. There are single-stream, multi-stream and flat-table projections. `FetchForWriting` gives optimistic concurrency.
- **Strengths.** The most mature .NET option. Very rich projection model. Multi-tenancy (conjoined, and database-per-tenant)†. Stream archiving† and compacting in v8†. Event "skipping" for poison events†. Deep Wolverine integration. Large community.
- **Ordering approach.** The async daemon tracks a *high-water mark* and skips sequence gaps after a time-based staleness threshold†. A public discussion (#4953)† analyses cases where that heuristic can advance past events still being committed.
- **Gaps relative to v3.** PostgreSQL-only (Polecat† covers SQL Server 2025 and .NET 10 as a separate product). Runtime code generation (pre-generation is optional) rather than Roslyn generators. Messaging, outbox and cloud transports need Wolverine. No built-in crypto-shredding (v8 offers masking of protected data by rewriting stored events†, a different trade-off).

### 2.2 Wolverine — JasperFx, MIT

- **Model.** A mediator plus message bus with a durable inbox and outbox†. Its "aggregate handler workflow" over Marten writes events from handlers†, and `[WriteAggregate]` spans several streams in one transaction†. Event forwarding goes through the outbox†. Sagas, durable scheduling, HTTP endpoints, and a broad transport set.
- **Strengths.** Excellent developer ergonomics. Transactional outbox and inbox. Many transports. Tight Marten and Polecat integration.
- **Gaps relative to v3.** Handler code generation happens at runtime by default. Cross-transport behavioural parity isn't enforced by a public conformance kit. The full "Critter Stack" means two products to learn. Commercial monitoring (CritterWatch) is separate.

### 2.3 Eventuous — Apache 2.0

- **Model.** Event-sourcing library with aggregates and functional command services. Stores: KurrentDB, PostgreSQL, SQL Server†. Subscriptions with partitioned consumers. A **gateway** that turns private domain events into integration events through producers†. Producers for RabbitMQ, Google Pub/Sub, Kafka and others.
- **Strengths.** Clean design, explicit private-to-public event mapping, OpenTelemetry diagnostics.
- **Gaps relative to v3.** The project states its API isn't yet stable†, and it is pre-1.0 (0.16/0.17 in 2026†). The integration gateway works from subscriptions rather than a transactional outbox. Smaller community.

### 2.4 KurrentDB (formerly EventStoreDB) — Kurrent License v1†

- **Model.** A purpose-built event database. Streams, `$all`, catch-up and persistent subscriptions, server-side projections. **Multi-stream appends** with per-stream expected versions arrived in 25.1†. Tiered **archiving** to cloud object storage (licensed)†. **Connectors** (sinks to Kafka, MongoDB, RabbitMQ, Elasticsearch; licensed except HTTP)†.
- **Strengths.** Throughput, built-in global ordering, operational features, a polyglot client ecosystem.
- **Gaps relative to v3.** A separate database to operate. Licence-gated features†. It is not an application framework, so aggregates, outbox and process managers come from elsewhere (for example Eventuous).

### 2.5 Axon Framework 5 + Axon Server — Apache 2.0 framework, commercial server tiers

- **Model.** A full CQRS and event-sourcing framework for the JVM. Version 5 introduces **Dynamic Consistency Boundaries (DCB)**†: consistency is decided per operation by querying tagged events instead of fixed aggregate streams. Axon Server 2025.x supports DCB†.
- **Strengths.** It defines the frontier: DCB, mature event processors, upcasting, deadline management, and a distributed command bus.
- **Gaps relative to v3.** JVM only. The server's advanced features are commercial. Version 5 is a significant migration from Axon 4†.

### 2.6 NServiceBus (Particular, commercial) and MassTransit (v9 commercial†)

- **Model.** Messaging frameworks with sagas, outbox, recoverability (immediate and delayed retries, error queues) and many transports. Neither is an event store.
- **Strengths.** Battle-tested recoverability and operations tooling (ServicePulse and ServiceInsight for NServiceBus), and large installed bases.
- **Market signal.** MassTransit v9 moved to a commercial licence (announced April 2025, commercial release planned for Q1 2026; v8 stays Apache 2.0)†. Many .NET teams are now re-evaluating open-source alternatives, which is a **window of opportunity** for an open, integrated library.

### 2.7 Akka.NET Persistence — Apache 2.0

- **Model.** Event-sourced persistent actors. Cluster sharding guarantees a **single writer** per entity. `Akka.Persistence.Query` provides projections.
- **Strengths.** No optimistic-concurrency conflicts by design, and high throughput for hot entities.
- **Gaps relative to v3.** The actor model is a steep paradigm shift. Integration events, outbox and cloud transports are left to the user.

## 3. Feature comparison matrix

Legend: ● built-in · ◐ partial, through a companion product, or licence-gated · ○ not provided · **v3** = this design (3.0 unless marked).

| Capability | Marten (+Wolverine) | Eventuous | KurrentDB | Axon 5 | NServiceBus / MassTransit | Akka.NET | **SourceFlow v3** |
|------------|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| Event-sourced aggregates | ● | ● | ◐ (client only) | ● | ○ | ● (actors) | ● pure deciders |
| Functional decide/evolve as the primary model | ◐ | ● | ○ | ◐ | ○ | ○ | ● |
| Compile-time source generation, Native AOT | ◐ (runtime codegen, pre-gen option) | ○ | ◐ | ○ | ○ | ○ | ● |
| Analyzers enforcing ES discipline (purity, determinism, PII) | ○ | ○ | ○ | ○ | ○ | ○ | ● |
| Stable logical event type names + upcasting | ● | ● | ◐ | ● | ◐ | ● | ● |
| Optimistic concurrency + automatic re-decide | ● (via Wolverine) | ● | ● | ● | — | — (single writer) | ● |
| Multi-stream atomic append / DCB | ● (`[WriteAggregate]`) | ○ | ● (25.1)† | ● (DCB)† | — | ○ | ◐ tags in 3.0 schema; DCB in 3.1 ([ADR-021](../ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md)) |
| Gap-free global ordering, **no timeouts** | ◐ (high-water mark + stale threshold) | ◐ (store-dependent) | ● (native log) | ● | — | ◐ (journal-dependent) | ● commit-ordered (`xid8` / rowversion) |
| Inline + async projections, rebuilds | ● | ● | ◐ | ● | ○ | ◐ | ● blue/green |
| Partitioned projections with **fencing tokens** | ◐ (sharding; leadership by locks) | ◐ | ◐ (persistent subscriptions) | ● | — | ◐ | ● |
| Transactional outbox + inbox | ● (Wolverine) | ◐ | ○ | ● | ● | ○ | ● always on |
| Idempotency key → stored command result (HTTP semantics) | ○ | ○ | ○ | ○ | ○ | ○ | ● |
| Process managers with durable timers | ● (Wolverine sagas, scheduling) | ◐ | ○ | ● (deadlines) | ● | ◐ | ● |
| PostgreSQL **and** SQL Server event store | ◐ (Marten + Polecat, separate products) | ● | — (own database) | ◐ | — | ● | ● one API |
| Cloud transports AWS + Azure + GCP | ● (Wolverine) | ◐ | ◐ (connectors, licensed) | ◐ | ● | ○ | ● |
| Public **transport conformance kit** (behavioural parity) | ○ | ○ | — | — | ◐ (internal) | — | ● |
| CloudEvents-native envelope | ○ | ○ | ○ | ○ | ○ | ○ | ● |
| Provisioning modes (create / **validate-only**) + IaC topology export | ◐ (auto-provision) | ○ | — | ◐ | ◐ (installers) | — | ● |
| OpenTelemetry with **messaging semconv** + lag, outbox-age and horizon metrics | ◐ | ● | ◐ | ◐ | ◐ | ◐ | ● |
| Shipped SLO dashboards and alerts | ○ | ○ | ◐ (UI) | ◐ | ● (ServicePulse, commercial) | ○ | ● Grafana/Prometheus + Aspire |
| Crypto-shredding for GDPR | ○ (masking by rewrite) | ○ | ○ | ◐ (commercial add-on) | ○ | ○ | ● |
| Multi-tenancy | ● (conjoined + database-per-tenant) | ○ | ◐ | ● | ◐ | ○ | ● conjoined + Postgres RLS in 3.0 ([ADR-022](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)); database-per-tenant 3.x |
| Stream archiving / tiered storage | ● (archive, compacting) | ○ | ◐ (licensed)† | ◐ | — | ○ | ◐ partitions + archive flag in 3.0; compaction 3.1 |
| Temporal queries (state as of version or time) | ● | ◐ | ● | ● | — | ◐ | ● (added by this study) |
| Kafka / RabbitMQ transports | ● | ● | ◐ (connectors) | ◐ | ● | ◐ | ◐ 3.1 through the TCK |
| Saga/workflow DSL + **visual designer** across services (success, failure, retry, rollback), simulation, generated tests | ◐ (code-first sagas; commercial CritterWatch for monitoring) | ○ | ○ | ◐ (code-first; commercial tooling) | ◐ (MassTransit state-machine DSL in code; NServiceBus ServiceInsight visualises sagas, commercial) | ○ | ● **SourceFlow Studio** (separate utility; doc 15) |
| Management UI | ◐ (commercial) | ○ | ● | ● | ● (commercial) | ○ | ◐ CLI + dashboards; console in 3.x |
| Licence for **all** core production features | MIT | Apache 2.0 | KLv1 (some features licensed)† | Apache 2.0 + commercial server tiers | Commercial / v9 commercial† | Apache 2.0 | **MIT/Apache, nothing gated** |

## 4. Industry practices: does v3 conform?

| Converged practice | Seen in | v3 conformance |
|--------------------|---------|----------------|
| Events are the source of truth; state is a fold | All event-sourcing products | ● [ADR-002](../ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md) |
| Per-stream optimistic concurrency with expected version | Marten, KurrentDB, Eventuous, Axon | ● [ADR-008](../ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md) |
| Stable event type names, separate from CLR types; upcasters | Axon, Marten, Eventuous | ● [ADR-005](../ADRs/ADR-005-logical-message-type-names-and-registry.md) |
| Private domain events vs public integration events | Eventuous (gateway), Axon, DDD guidance | ● doc 03 §7 |
| Transactional outbox and idempotent receivers | Wolverine, NServiceBus, MassTransit | ● [ADR-006](../ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md) |
| Immediate + delayed retries, then an error queue | NServiceBus, MassTransit, Wolverine | ● doc 04 §9 |
| Checkpointed, rebuildable projections | Marten, Eventuous, Axon | ● doc 06 |
| Snapshots as an optimisation, not truth | All | ● doc 05 §8 |
| Correlation and causation metadata | All | ● doc 04 §1 |
| Multi-tenancy as a first-class concern | Marten, Wolverine, Axon | ● after this study ([ADR-022](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)) |
| Moving beyond fixed aggregates for cross-entity invariants (DCB) | Axon 5†, KurrentDB multi-stream†, Cratis†, EventSourcingDB community | ◐ schema-ready in 3.0, API in 3.1 ([ADR-021](../ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md)) |
| Archiving or tiering cold data | Marten†, KurrentDB† | ◐ partitions + archive flag in 3.0 |

**Conclusion.** v3 follows every converged practice. Two emerging standards, DCB and first-class tenancy, are added by this study.

## 5. Where competitors lead, and the response

| # | Competitor advantage | Response | When |
|---|----------------------|----------|------|
| C1 | Marten and Wolverine: mature multi-tenancy | **Adopt**: conjoined tenancy (tenant column, tenant-scoped streams, PostgreSQL row-level security) in 3.0. Database-per-tenant in 3.x. | 3.0 ([ADR-022](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)) |
| C2 | Axon 5 DCB, KurrentDB multi-stream appends: cross-entity consistency without inflated aggregates | **Adopt in 3.1**: tag-based append conditions. 3.0 adds `tags` to the event schema and a `[Tag]` attribute, so stored events are DCB-ready with no migration. | 3.1 ([ADR-021](../ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md)) |
| C3 | Marten and KurrentDB: archiving and compaction | 3.0: monthly partitions plus a stream `archived` flag (excluded from loads and rebuilds unless asked for). 3.1: compaction (genesis snapshot event + archive the prefix). | 3.0 / 3.1 |
| C4 | Temporal queries ("state as of …") | **Adopt**: `IAggregateReader.LoadAsync(id, AsOf.Version(v) / AsOf.Time(t))`. Cheap, given the store design. | 3.0 |
| C5 | Wolverine.Http: low-ceremony HTTP endpoints | **Adopt** a small `SourceFlow.AspNetCore` package: `app.MapCommand<TCommand>(route)` maps `Idempotency-Key`, `If-Match` → `ExpectedVersion`, `CommandResult` → ProblemDetails or 2xx with `ETag`. | 3.0 |
| C6 | Transport breadth (Wolverine, MassTransit, NServiceBus) | RabbitMQ and Kafka transports through the TCK in 3.1. An event gateway (subscription → Kafka sink) in 3.1. | 3.1 |
| C7 | Management UI (ServicePulse, KurrentDB UI, CritterWatch) | 3.0: CLI plus generated Grafana dashboards, and the Aspire dashboard through OTLP. 3.x: an optional open-source "SourceFlow Console" over the same APIs. | 3.x |
| C8 | Akka.NET: single-writer throughput on hot entities | Partition-key affinity plus the optimistic cache (doc 09 §3) give most of the benefit without actors. Pessimistic per-aggregate locking stays a documented option. | 3.0 (existing) |
| C9 | KurrentDB: purpose-built store throughput | Keep the port open. A KurrentDB storage adapter is a 3.x candidate, which would make SourceFlow the framework layer over KurrentDB as well. | 3.x |
| C10 | Ecosystem maturity and community (Marten, NServiceBus) | Not solvable by design. Answered through the companion book, samples, migration guides from Marten, Eventuous and MassTransit, and an open roadmap. | Ongoing |

## 6. SourceFlow v3 competitive advantages

Each claim is backed by a concrete design element, so it can be verified rather than just marketed.

| # | Advantage | Evidence | Why competitors don't match it today |
|---|-----------|----------|--------------------------------------|
| A1 | **Provably gap-free subscriptions with no timeouts** | Commit-ordered positions (`xid8` horizon on PostgreSQL, `MIN_ACTIVE_ROWVERSION` on SQL Server), with the ordering argument in doc 05 §5 | Sequence-based stores need gap heuristics such as Marten's stale threshold†. KurrentDB solves it with its own database. |
| A2 | **Compile-time and Native-AOT-ready** | Roslyn generators for dispatch, JSON, ids and registration; no reflection on the hot path | Runtime code generation (Wolverine/Marten) or reflection (most others) |
| A3 | **Guard-rails as analyzers** | SF0001–SF0014: purity, determinism, PII leaks, ownership conflicts | No other product found ships event-sourcing discipline analyzers |
| A4 | **One integrated, fully open stack** | Event store, projections, process managers, outbox/inbox and three cloud transports in one coherent API, under a permissive licence with nothing gated | The same scope elsewhere means two products (Marten + Wolverine), a database plus a framework (KurrentDB + Eventuous), or commercial licences (NServiceBus, MassTransit v9†, licensed KurrentDB features†) |
| A5 | **Verified cross-cloud parity** | A public Transport Conformance Kit, CloudEvents envelope, identical retry and dead-letter semantics | Parity elsewhere is best-effort and not publicly verified |
| A6 | **Privacy by design** | Crypto-shredding with per-subject keys, erased-value semantics, automatic read-model scrubbing, PII analyzers | Most rely on DIY approaches or rewriting stored events |
| A7 | **Operations-first** | Validate-only provisioning, topology export for IaC, SLO dashboards generated from the instrument list, lag, outbox-age and horizon metrics, fenced leases | Comparable operations tooling is usually commercial or separate |
| A8 | **HTTP-grade command semantics** | Idempotency keys returning stored results, `If-Match` → expected version, rejections as values → ProblemDetails | Not provided end to end by the products studied |
| A9 | **One API over PostgreSQL and SQL Server** | One storage SPI and one conformance kit | Marten and Polecat are separate products; others are single-database |
| A11 | **Visual, verifiable workflows** | A YAML DSL compiled into ordinary process managers; React designer over the whole business domain; build-time validation of failure, retry and compensation completeness; simulation identical to production (shared WASM core); generated tests per path; PR diagrams | Comparable designers are commercial, code-first, or tied to a separate workflow engine |
| A10 | **Teachable and correct** | Pure deciders, Given/When/Then specs, companion-book alignment | A reference implementation that is both production-grade and pedagogical |

**Positioning statement.** *SourceFlow.Net is the open, compile-time-verified event-sourcing and messaging framework for .NET 10. It gives you provably correct streams, outbox-backed integration across AWS, Azure and Google Cloud, and privacy and operability built in, in one library on the database you already run.*

**Primary target segments.**
1. .NET teams on SQL Server or PostgreSQL who want event sourcing without running a second database.
2. Teams leaving commercial messaging frameworks after licence changes.
3. Regulated domains that need crypto-shredding and audit.
4. Multi-cloud or cloud-portable products.

## 7. Changes applied to the v3 design as a result of this study

| Change | Documents updated |
|--------|-------------------|
| Conjoined multi-tenancy + row-level security in 3.0 | [ADR-022](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md), doc 01 §4, doc 05 §2, doc 12 §5 |
| DCB-ready schema in 3.0 (`tags`), DCB API in 3.1 | [ADR-021](../ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md), doc 05 §2 |
| Stream archive flag (3.0), compaction (3.1) | doc 05 §9 |
| Temporal aggregate reads | doc 03 §5.4 |
| `SourceFlow.AspNetCore` with `MapCommand<T>` | doc 01 §6, doc 03 §5.1, doc 12 §5 |
| 3.1 roadmap: RabbitMQ, Kafka, event gateway, DCB, compaction; 3.x: console, KurrentDB adapter, database-per-tenant | doc 12 §5.1 |

## 8. Risks from the competitive position

| Risk | Mitigation |
|------|------------|
| The Critter Stack's momentum and maturity | Differentiate on A1–A9, not on feature count. Publish a Marten → SourceFlow migration guide only after GA. |
| DCB becomes the expected default before 3.1 | The schema is DCB-ready in 3.0, and the 3.1 API is designed now ([ADR-021](../ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md)). |
| Licence shifts in the ecosystem (including ours) | Commit publicly to MIT or Apache for every package. Any future commercial offering covers *services* (support, console hosting), never core features. |

## 9. Sources (checked during this study)

- Marten — [Async daemon (high-water mark)](https://martendb.io/events/projections/async-daemon), [Archiving](https://martendb.io/events/archiving), [Releases](https://www.github.com/JasperFx/marten/releases), [v8 stream compacting](https://devbytes.co.in/news/marten-v80-introduces-stream-compacting), [Discussion #4953: daemon progress vs concurrent appends](https://github.com/JasperFx/marten/discussions/4953)
- Wolverine — [Marten outbox](https://wolverine.netlify.app/guide/durability/marten/outbox), [Event sourcing / aggregate handler workflow](https://wolverine.netlify.app/guide/durability/marten/event-sourcing), [Event forwarding](https://wolverine.netlify.app/guide/durability/marten/event-forwarding), [Transactional middleware](https://wolverine.netlify.app/guide/durability/marten/transactional-middleware)
- Polecat — [Introduction](https://polecat.netlify.app/introduction), [Whitepaper](https://jasperfx-polecat.pages.dev/whitepaper)
- Eventuous — [Introduction](https://eventuous-docs.pages.dev/introduction.html), [Eventuous.Postgresql on NuGet](https://packages.nuget.org/packages/Eventuous.Postgresql/), [Eventuous.Subscriptions on NuGet](https://packages.nuget.org/packages/Eventuous.Subscriptions/)
- KurrentDB — [25.0 What's New (rename, licence, archiving, connectors)](https://docs.kurrent.io/server/v25.0/quick-start/whatsnew), [25.1 multi-stream appends](https://kurrent.io/releases/kurrentdb/25-1), [26.0 What's New](https://docs.kurrent.io/server/v26.0/quick-start/whatsnew)
- Axon — [Axon Framework 5.0 release](https://www.axoniq.io/blog/release-of-axon-framework-5-0), [DCB in AF5](https://www.axoniq.io/blog/dcb-in-af-5), [Axon Server 2025.1 with DCB](https://www.axoniq.io/blog/axon-server-future-proof-event-store)
- DCB — [dcb.events](https://dcb.events/), [Python eventsourcing DCB](https://eventsourcing.readthedocs.io/en/stable/topics/dcb.html), [Cratis Chronicle DCB](https://www.cratis.io/chronicle/dynamic-consistency-boundary.md)
- MassTransit — [Announcing MassTransit v9](https://masstransit.io/introduction/v9-announcement), [Analysis by Milan Jovanović](https://milanjovanovic.tech/blog/mediatr-and-masstransit-going-commercial-what-this-means-for-you)
- Comparison context — [Cratis: comparing KurrentDB, Marten and Chronicle](https://www.cratis.io/compare-event-sourcing-dotnet/)
