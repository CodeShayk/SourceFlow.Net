# 12 — Migration, Delivery Plan and Traceability

## 1. Compatibility statement

v3 is a **new major version**. It is neither source- nor binary-compatible with v2, and its storage model differs (events, not commands). Migration is supported by:

1. a concept and API map (§2);
2. a data migration tool, `dotnet sourceflow migrate v2` (§3);
3. a temporary wire bridge so v2 and v3 services can coexist (§4, doc 07 §8).

**v2 support policy.** Security and critical fixes for 12 months after 3.0 GA, on the last 2.x minor. No new features.

## 2. Concept and API map

| v2 | v3 | Notes |
|----|----|-------|
| `services.UseSourceFlow(assemblies)` | `services.AddSourceFlow(sf => sf.AddModule<TModule>())` | Compile-time module, no scanning |
| `IEntity { int Id }` | Aggregate `record` + `[StronglyTypedId]` | Ids are assigned by the caller |
| `Aggregate<T>.Send(cmd)` | `ICommandSender.SendAsync(cmd)` | Returns `CommandResult` |
| `Saga<T>` + `IHandles<C>.Handle(entity, cmd)` | Aggregate `Decide(C, ctx)`, or `ICommandHandler<C, TAgg>` | Pure decision; the runtime persists |
| `IHandlesWithEvent<C, E>` | `Decision.Accept(new E(...))` | |
| Saga publishing follow-up commands | Process manager `Reaction.Send` | Through the outbox, durable |
| `View<TVM>` + `IProjectOn<E>` | `IDocumentProjection<TDoc, TKey>` / `IEfProjection<TDb>` | Inline or async, rebuildable |
| `ICommandStore` / `IEntityStore` / `IViewModelStore` | `IEventStore` (+ snapshots) / document store / EF Core read models | |
| `AddSourceFlowEfStores(...)` | `sf.UsePostgreSql(...)` / `sf.UseSqlServer(...)` (+ `SourceFlow.Storage.EntityFrameworkCore` for read models) | |
| `ReplayCommands(id)` | Not needed for state (loading *is* replaying events). `dotnet sourceflow projection rebuild` for read models. | Side-effect free |
| `ICommandDispatchMiddleware`, `ICommandSubscribeMiddleware`, `IEventDispatchMiddleware`, `IEventSubscribeMiddleware` | `IMessageMiddleware` with `PipelineStage` | |
| `UseSourceFlowAws(o, bus, idem)` / `UseSourceFlowGcp(...)` | `sf.UseAmazonSqs(...)` / `sf.UseGooglePubSub(...)` / `sf.UseAzureServiceBus(...)` | Options are honoured |
| `bus.Send.Command<T>(q => q.Queue("x"))` | `r.Command<T>().To("x")` | Remote only ([ADR-007](ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md)) |
| `bus.Raise.Event<T>(t => t.Topic("x"))` | `r.Publish<T>().As(map).To("x")` | Integration contract |
| `bus.Listen.To.CommandQueue("x")` | `r.Endpoint("x")` (default: service name) | |
| `bus.Subscribe.To.Topic("x")` | `r.Subscribe("x")` | One subscription per service |
| `IdempotencyConfigurationBuilder` / `IIdempotencyService` | Built-in inbox (always on) | |
| `IDeadLetterStore` / `AwsDeadLetterMonitor` | Built-in parking + `dotnet sourceflow dlq …` | |
| `ICircuitBreaker` / `CircuitBreakerOptions` | Polly `ResiliencePipeline` (`sf.Resilience(...)`) | |
| `IMessageEncryption` / `AwsKmsMessageEncryption` | `sf.Security(s => s.UseAwsKms(...))` | |
| `[SensitiveData]` / `SensitiveDataMasker` | `[PersonalData]` + M.E.Compliance.Redaction + crypto-shredding | |
| `AddSourceFlowTelemetry(...)` / `DomainObservabilityOptions` | Always on. `AddSource(SourceFlowTelemetry.ActivitySourceName)` / `AddMeter(SourceFlowTelemetry.MeterName)` on the OTel builders | |

## 3. Data migration (`dotnet sourceflow migrate v2`)

v2 stores *commands* and *current entity state*. Re-running v2 commands to derive events would repeat the non-determinism that [ADR-002](ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md) removes. The migration therefore imports **current state as genesis events** and **archives** the v2 history.

```csharp
// User-supplied, one per aggregate type (generated scaffold via `dotnet sourceflow migrate v2 scaffold`)
public sealed class BankAccountImport : IV2Import<V2BankAccount, BankAccount>
{
    public AccountId MapId(V2BankAccount e) => AccountIdMap.FromLegacy(e.Id);       // deterministic (UUIDv5 of the int id)
    public IEnumerable<IEvent> ToEvents(V2BankAccount e, V2ImportContext ctx) =>
    [
        new AccountImported(MapId(e), e.AccountName, e.Balance,
                            e.IsClosed ? AccountStatus.Closed : AccountStatus.Open, ctx.ImportedAt)
    ];
}
```

| Step | Command | What happens |
|------|---------|--------------|
| 1. Plan | `migrate v2 plan` | Counts entities and commands per type, estimates duration, checks every v2 entity type has an `IV2Import` |
| 2. Freeze or dual-run | — | Stop writes to the bounded context, or run the wire bridge (§4) and import in waves |
| 3. Import | `migrate v2 import --batch 1000` | One session per stream (ES-R2), `ExpectedVersion.NoStream`, so it is idempotent and resumable. Metadata `{migratedFrom: "v2", v2CommandCount, v2LastSequenceNo}` |
| 4. Archive | `migrate v2 archive` | Copies the v2 `CommandRecord` table to `sourceflow_v2_archive.commands` (read-only audit) |
| 5. Rebuild | automatic | Projections build from events. Each needs a `When(…Imported)` handler (generated stub) |
| 6. Verify | `migrate v2 verify` | Compares the rebuilt v3 state with the v2 entity per id using `IV2Import.Compare`, and produces a mismatch report. The exit code gates cut-over. |
| 7. Cut over | — | Switch traffic. The bridge stays until v2 producers are retired. |

Known limitations: v2 commands lost by GAP-04 or GAP-20 (logged under id 0, or not rebuildable) can't be recovered as history, but the current state *is* migrated. The plan step reports them.

**As built (WP-5.4).** See also the guide [guides/migrating-from-v2.md](guides/migrating-from-v2.md).

- **API.** `SourceFlow.Migration` in the core:
  - **Registration.** `sf.MigrateFromV2(v2 => v2.From(new V2Source(dataSource, V2Dialect.PostgreSql|SqlServer)).Import(…).Ignore(…).NotRebuildable(…))`.
  - **Import contract.** `IV2Import<TV2Entity, TAggregate, TId>` has a third type parameter for the aggregate id, which C# can't infer from the aggregate. Its members: `MapId`, `ToEvents`, `Compare`, and an optional `OwnsCommand` that attributes commands when several v2 entity types share the command table.
  - **Row types.** v2 rows are `IV2Entity<TSelf>` records with a static `TableName` and `Read(V2Row)`. `V2Row` converts the column types v2 wrote on each provider.
  - **Ids.** `V2Ids.FromLegacy<TId>(id, scope?)` is UUIDv5 in a fixed namespace.
- **Reading v2.** The v2 database is read with plain SQL through a `DbDataSource`, on PostgreSQL or SQL Server. No v2 code is referenced.
- **CLI.** `dotnet sourceflow migrate v2 plan|scaffold|import|archive|verify`.
  - **Where it runs.** Inside the service, with the registered migration. `plan`, `scaffold` and `archive` also run from the global tool with `--v2-connection`, `--v2-provider`, `--v2-schema`, `--v2-command-table` and `--v2-archive-schema`.
  - **Exit codes.** `plan` exits 1 while a v2 table has neither an import nor `Ignore`; `verify` exits 1 on any mismatch.
- **Plan.**
  - **Counts.** Commands per type, entity rows, and the commands each import owns.
  - **GAP-20.** Commands logged under `EntityId = 0`.
  - **GAP-04.** Commands with an empty payload, plus the types declared with `NotRebuildable`. The tool can't load v2 types, so it can't detect a missing parameterless constructor itself.
  - **Estimate.** At `EstimatedStreamsPerSecond` (200 by default).
- **Scaffold.** Generates the row type from `INFORMATION_SCHEMA.COLUMNS` and an import stub. A v3 aggregate named like the table is filled in; otherwise the stub has placeholders and a `#warning`.
- **Import.**
  - **Ordering.** Keyset pages by v2 id (`--batch`). `--limit` caps a wave.
  - **Writes.** One storage session per stream, `ExpectedVersion.NoStream`. An existing stream is skipped, so re-runs resume and repeat nothing.
  - **Events.** Deterministic ids (UUIDv5 of tenant, stream and version) and actor `sourceflow-migrate-v2`.
  - **Headers.** `migratedFrom`, `v2CommandCount`, `v2LastSequenceNo` and `v2Id` are event headers.
  - **Like any append.** Personal data is encrypted, and inline projections apply the events.
  - **Not published.** Imported events are not published to integration topics.
- **Archive.** Copies the command table into `sourceflow_v2_archive.commands` in the v2 database, with the same columns and its own primary key.
  - **Read-only.** PostgreSQL uses a `BEFORE UPDATE OR DELETE OR TRUNCATE` trigger; SQL Server uses `INSTEAD OF UPDATE, DELETE`.
  - **Re-runs** copy only new rows.
- **Verify.** Loads each aggregate through `IAggregateReader` and calls `Compare`. A stream with no events is a mismatch.
- **Tests.**
  - **MIG-01..09** run on PostgreSQL and on SQL Server (`tests/Storage/.../Migration`). Each test seeds a v2 database from the v2 `CommandRecord` DDL and the v2 E2E account rows, then imports into the Bank sample's account aggregate.
  - **MIG-10..12** are the AWS bridge on LocalStack (`tests/EndToEnd/V2BridgeScenarios.cs`).
  - **Unit tests:** UT-MIG-01..06 (core) and UT-AWS-31..33 (the AWS receiver).

## 4. Coexistence

- **Strangler by bounded context.** Migrate one service at a time. The v3 service accepts v2 wire messages through the explicit allow-list bridge (doc 07 §8).
- **v3 → v2 direction**: v3 publishes CloudEvents. A v2 consumer can't read them, so publish a v2-shaped integration event (`.As(v2Shape)`, routed to the legacy topic) until the consumer is migrated.
- **As built (WP-5.4).** The v2 → v3 bridge exists for AWS (doc 07 §8), the only v2 cloud provider on `master`. The v2 Azure provider lives on the `azure-cloud` branch (WP-3.R) and has no bridge.

## 5. Delivery plan

The milestones run in sequence. Each has exit criteria, and none is complete until its Definition of Done ([doc 11 §8](11-Testing-and-Quality.md#8-definition-of-done-per-feature)) is met.

| Milestone | Scope | Exit criteria |
|-----------|-------|---------------|
| **M0 Foundations** | New repo layout and `Directory.Build.props` (net10.0, C# 14, AOT/trim analysis, warnings as errors); CI pipelines; benchmark harness; architecture tests; ADR review | ADRs accepted; empty packages build, sign and publish to the feed |
| **M1 Core and programming model** | `SourceFlow.Net` contracts and SPIs, generators and analyzers SF0001–SF0017, type registry, **schema catalog generation** (JSON Schema), STJ serialiser, middleware, command processor (with consistency tokens), tenant context, in-memory store and transport, **SFQL parser, binder and in-memory interpreter**, `SourceFlow.Testing` specs and test host | The bank-account sample runs end to end in memory, including SFQL queries; specs and CESQL TCK pass; public API frozen (approval files) |
| **M2 PostgreSQL and projections** | PostgreSQL adapter (every port, tenant columns, tags, archive flag), migrations, outbox relay, inbox, scheduler, subscription engine (partitions, leases, fencing), document store, EF Core projections, snapshots, cache, **row-level security**, **SFQL PostgreSQL compiler**, `[Queryable]` index sync, temporal reads | Storage conformance kit green (including SFQL cross-provider and tenant-isolation cases); gap-freedom property test green; doc 09 component targets met |
| **M3 Transports** | Transport SPI and TCK first; then AWS SNS/SQS, Google Pub/Sub and Azure Service Bus **in parallel** (three workstreams); provisioning modes; DLQ ingestor; topology export | TCK green on emulators for all three, and nightly against real cloud; cross-service sample (accounts ↔ payments) works on each |
| **M4 Operations** | `dotnet sourceflow` CLI (dlq, projection, topology, db, **query, schema check and export, AsyncAPI**), `SourceFlow.AspNetCore` (`MapCommand`, opt-in `MapSourceFlowQuery`), health checks, dashboards and alerts, SQL Server adapter (+ SFQL compiler), `PartitionEvents`, stream archiving | SQL Server passes the storage CK; chaos suite green; tier-M load test meets the SLOs in doc 08 §6 |
| **M5 Security and migration** | Payload protection (AWS, Azure, GCP KMS), crypto-shredding, redaction wiring, v2 migration tool and wire bridge, fuzzing | Erasure end-to-end test; migration verified on a copy of a v2 sample database; security review signed off |
| **M6 Hardening and GA** | Docs site, samples, companion-book alignment, RC programme with early adopters, performance tuning | RC run in production by at least 2 early adopters for 4 weeks with no Sev-1 issues; every target in doc 09 §7.1 met or formally waived |

**Release scope is fixed by product sign-off (2026-11-10).** AWS SNS/SQS, Azure Service Bus and Google Pub/Sub are all **GA in 3.0.0** and all release-blocking (Q5, Q6). No transport may slip to 3.1. M3 therefore builds the three transports **in parallel** on the shared SPI and TCK, not one after another, and schedule pressure moves the *date*, not the scope. The remaining scope valve: if M4 slips, the HTTP query endpoint moves to 3.1. The C# and CLI surfaces of SFQL stay in 3.0.

### 5.0 SourceFlow Studio release train (product doc 07, WQ1)

The workflow DSL and Studio are a **separate utility on their own release train**: **Studio 1.0**. Work runs in a dedicated lane (F) from M1 exit and is **targeted** to be ready at 3.0.0 GA, but **does not block** 3.0.0. The fixed 3.0.0 scope from the Q5/Q6 sign-off is unchanged.

| Studio milestone | Scope | Depends on | Exit criteria |
|------------------|-------|------------|---------------|
| **S1 Manifests + DSL core** | Service manifests (static + design-time export), `[Emits]`/`[Rejects]`, SF0019; `SourceFlow.Studio` parser, validator (WF-V01…V16), simulator, path enumerator | v3 M1 (generator, schema catalog, CESQL evaluator) | DSL golden suite green; manifests regenerate with no drift on the Bank sample |
| **S2 Code generation + runtime support** | `WorkflowGenerator`, workflow runtime types (namespace `SourceFlow.Workflows` in `SourceFlow.Net`), operator commands, workflow metrics, CLI `workflow …` | S1, v3 M2 (PostgreSQL process state) | **Differential tests**: simulator ≡ generated process manager on every enumerated path; order-fulfilment E2E green in memory |
| **S3 Studio designer** | WASM core, React SPA (domain map, statechart, swimlane, editor, simulation, path explorer, spec generation), local host `dotnet sourceflow studio` | S1 (S2 for spec generation) | Playwright suite green; accessibility (axe) clean; performance budgets met |
| **S4 Exports + PR diagrams + hardening** | SVG/PNG/Mermaid/BPMN export, static build, `workflow-diff` GitHub Action, docs and samples | S3, v3 M3 (cross-service E2E on cloud transports) | Order fulfilment E2E green on AWS, Azure and Google Cloud; Studio 1.0 RC |
| *Studio 1.1* | Observe mode (live overlay through domain queries) | v3 GA | — |

### 5.1 Roadmap after 3.0 (from the [competitive analysis](13-Competitive-Analysis.md#5-where-competitors-lead-and-the-response))

| Release | Items |
|---------|-------|
| **3.1** | Dynamic consistency boundaries API ([ADR-021](ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md)); stream compaction; RabbitMQ and Kafka transports (through the TCK); event gateway (subscription → Kafka sink); SFQL `sfql/1.1` (window functions over `events`, if demand is confirmed) |
| **3.x** | Database-per-tenant; KurrentDB storage adapter; open-source "SourceFlow Console" over the CLI and query APIs; claim-check for large payloads; GraphQL adapter over read models |

## 6. Traceability: v2 gaps → v3 resolution

| Gap ([doc 06](../product/06-Gaps-Risks-and-Open-Questions.md)) | Resolution | Where |
|-----|------------|-------|
| GAP-01 Production pipeline not wired | Features are core middleware, always on, identical on every transport | doc 04 §4, doc 07 §1, [ADR-014](ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md) |
| GAP-02 AWS shared queue, no raw delivery | One endpoint and one receiver per service; raw delivery; FIFO compatibility validated | doc 07 §4.1, RX-R5 |
| GAP-03 Ignored options (Region …) | Options map to SDK config; validation forbids unhonoured options | doc 07 §6 |
| GAP-04 Implicit rebuild contract, silent skips | Event sourcing; logical type names; upcasters; unknown types fail loudly | [ADR-002](ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md), [ADR-005](ADRs/ADR-005-logical-message-type-names-and-registry.md), doc 03 §2.1 |
| GAP-05 O(n), racy, non-atomic sequencing | Compare-and-set append; one transaction; re-decide on conflict | [ADR-008](ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md), doc 05 §3 |
| GAP-06 Replay side effects | Loading is a pure fold; rebuilds exclude handlers and process managers | [ADR-002](ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md), doc 06 §6 |
| GAP-07 Swallowed event failures | Events are appended with the decision; subscribers retry, stop or park, visibly | doc 06 §7 |
| GAP-08 Captive dependencies | Aggregates aren't DI services; handlers are resolved per message scope | [ADR-003](ADRs/ADR-003-aggregates-as-pure-deciders-decide-evolve.md), doc 03 §5.3 |
| GAP-09 Per-delivery idempotency key | Inbox keyed on the logical `MessageId` | [ADR-006](ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md), doc 04 §6 |
| GAP-10 No type allow-list | Registry allow-list; parked unread | [ADR-005](ADRs/ADR-005-logical-message-type-names-and-registry.md), doc 10 §2 |
| GAP-11 Incomplete dead-letter story | Parking store, native DLQ backstop and ingestor, CLI replay | doc 04 §9.3 |
| GAP-12 Provisioning can't be switched off | `ProvisioningMode` + topology export | [ADR-017](ADRs/ADR-017-provisioning-modes-and-topology-as-data.md), doc 07 §5 |
| GAP-13 GCP shared subscriptions, no ordering | `{svc}.{topic}` subscriptions; ordering enabled and validated | doc 07 §4.1 |
| GAP-14 Metric and log inaccuracies | Single-recording rule with tests; semconv names | doc 08 §3 |
| GAP-15 "Not found" semantics unspecified | Pinned in the port contract and the conformance kit | doc 05 §1, doc 11 §4 |
| GAP-16 Assembly-scan fallback rethrows | No assembly scanning (generators) | [ADR-004](ADRs/ADR-004-source-generation-instead-of-reflection.md) |
| GAP-17 Process-global EF registration | Options in DI; no static state | doc 05 §10 |
| GAP-18 Doc and metadata drift | Dashboards and docs generated from code; API approval; package metadata checked in CI | doc 08 §6, doc 11 §1 |
| GAP-19 Owner doesn't log received commands | Received commands go through the owner's command processor | [ADR-007](ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md), RT-R2 |
| GAP-20 New entities logged under id 0 | Caller-assigned strongly typed ids; defaults rejected | [ADR-015](ADRs/ADR-015-strongly-typed-caller-assigned-ids-uuidv7-default.md) |

## 7. Traceability: product requirement areas → v3

| Area | v3 coverage |
|------|-------------|
| DOM (aggregates, sagas, views) | Aggregates, process managers, projections — doc 03 |
| CMD (commands, bus, log, replay) | Command processor and event-sourced streams — doc 03 §5, doc 05 |
| EVT (events) | Domain events vs integration events; durable subscriptions — doc 03 §7, doc 06 |
| STO (store contracts) | Role-segregated ports with pinned semantics — doc 05 §1 |
| REG (registration) | Generated modules — [ADR-004](ADRs/ADR-004-source-generation-instead-of-reflection.md) |
| EXT (middleware) | One middleware abstraction — [ADR-012](ADRs/ADR-012-one-middleware-abstraction-for-all-stages.md) |
| OBS / CTL (telemetry, health) | doc 08 |
| PERF | doc 09 |
| EF (persistence) | PostgreSQL and SQL Server stores, EF Core read models — doc 05 |
| BUS (routing, bootstrapping) | Routing, topology, provisioning modes — doc 04 §3, doc 07 |
| IDM, DLQ, RES | Inbox, parking, Polly — doc 04 |
| SEC | doc 10 |
| AWS / GCP (+ Azure) | doc 07, with the TCK as the parity gate |
| NFR (all) | Quality goals in doc 01 §2, verified by doc 11 |
| Query side (new request) | Schema catalog, SFQL, consistency tokens, temporal reads — doc 14, [ADR-023](ADRs/ADR-023-sfql-query-language-with-cesql-predicates.md) |
| Workflow DSL + visual designer (new request) | Separate utility: DSL compiled to process managers, React Studio, manifests — doc 15, ADR-024, ADR-025, §5.0 |
| Competitive parity (new request) | Tenancy ([ADR-022](ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)), DCB readiness ([ADR-021](ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md)), archiving, `MapCommand`, roadmap — doc 13 |

## 8. Risk register

| # | Risk | Likelihood | Impact | Mitigation |
|---|------|------------|--------|------------|
| R1 | v2 users find the event-sourcing shift too large | Medium | High | Migration tool, concept map, companion-book chapter, v2 support window, coexistence bridge |
| R2 | The source-generator learning curve or generator bugs block users | Medium | Medium | Snapshot-tested generators; clear diagnostics; documented "view generated code"; small generated surface |
| R3 | `xid8` horizon stalls caused by long transactions in shared databases | Medium | Medium | `horizon_age` metric and health check; guidance to keep a dedicated database or schema; documented impact (latency only) |
| R4 | Semantic-convention churn in OTel messaging | High | Low | Pinned semconv version with opt-in upgrades ([ADR-019](ADRs/ADR-019-telemetry-always-on-with-standard-semantic-conventions.md)) |
| R5 | Three release-blocking transports overload M3 | Medium | High | The SPI and TCK land first, so each transport is mechanical. Three parallel workstreams. Emulators in CI from day one. Schedule pressure moves the date, not the scope (Q5/Q6 sign-off) |
| R6 | Outbox write amplification at tier L | Low | Medium | Measured in M2–M4; log-tailing alternative documented (doc 09 §8) |
| R7 | AOT and trimming constraints conflict with user libraries (validators, EF Core) | Medium | Low | AOT is a goal for the core and adapters; EF Core read models are documented as JIT-only where EF requires it |
| R8 | Crypto-shredding complexity (key loss = data loss) | Low | High | KMS-wrapped keys with KMS backup and retention policies; erasure is an explicit, audited API; chaos test for KMS outage (fail closed, retry) |
| R9 | SFQL becomes a large surface to maintain, or queries hurt write performance | Medium | Medium | Small `sfql/1` grammar (no joins), cost guard, read-only role with replica support, golden tests plus CESQL TCK, versioned language |
| R10 | Added scope (tenancy, query engine) stretches M1–M4 | Medium | Medium | The query HTTP endpoint and the GraphQL adapter are explicit slip candidates. Tenancy columns are cheap and must land in M2, because they change keys. |

## 9. Review checklist for approvers

- [x] Q1–Q7 answers ([README §3](README.md#3-answers-to-the-open-product-questions)) accepted by product on 2026-11-10 (Q2 with the query-boundary requirement; Q5/Q6 = 3.0.0).
- [ ] [ADR-001](ADRs/ADR-001-target-dotnet-10-and-csharp-14.md) to [ADR-023](ADRs/ADR-023-sfql-query-language-with-cesql-predicates.md) accepted or amended.
- [ ] SFQL `sfql/1` grammar and semantics (doc 14) accepted as a public, versioned contract.
- [ ] Workflow DSL `sourceflow.workflow/1` and manifest `manifestVersion: 1` accepted as public contracts (doc 15); Studio release-train decision (WQ1) confirmed.
- [ ] Competitive positioning and licence commitment (doc 13 §6, §8) accepted by leadership.
- [ ] Capacity tiers (doc 09 §5) checked against known v2 user workloads.
- [ ] PostgreSQL 15+ / SQL Server 2022+ minimums acceptable.
- [ ] v2 support window (12 months) accepted.
- [ ] Milestone exit criteria accepted as release gates.
