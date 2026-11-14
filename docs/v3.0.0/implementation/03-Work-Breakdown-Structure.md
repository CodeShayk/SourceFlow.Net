# 03 — Work Breakdown Structure

Each work package (WP) lists:

- **Design**: the design docs and ADRs it implements;
- **v2 input**: files on `master` to read for behaviour, scenarios or pitfalls. They are never copied;
- **Deliverables**: projects and files to create;
- **Tasks**;
- **Tests**: IDs defined in plans 05 and 06;
- **Acceptance**;
- **Depends on**;
- **Size** in engineer-weeks (ew).

Every WP also meets the Definition of Done in [plan 09 §1](09-Definition-of-Done-Risks-and-Tracking.md#1-definition-of-done-per-work-package).

## 1. Design corrections found while planning (already applied to the design docs)

| ID | Correction | Affected | Built in |
|----|------------|----------|----------|
| DC-1 | Roslyn generators can't feed the System.Text.Json source generator. Each module **declares** its own `JsonSerializerContext`; SF0010 verifies completeness and a code fix adds missing types. | ADR-004, doc 03 §8, doc 04 §2.2 | WP-1.2, WP-1.3 |
| DC-2 | The projection idempotency guard must be checked **on read** (skip events the document already reflects), not only on write. Document reads return `(data, version)`. | doc 06 §5 | WP-1.4, WP-1.9 |
| DC-3 | `Reaction` fluent API: static `Send/Become/Complete/Ignore`, instance `AndSend/Schedule/AndBecome/AndComplete`. | doc 03 §6.1 | WP-1.1 |
| DC-4 | GitVersion has no rule for a `v3.0.0` branch, and a `v3.0.0` branch clashes with the `v3.0.0` tag. | plan 02 §2 | WP-0.1, WP-6.3 |

---

## M0 — Foundations (4 ew)

### WP-0.1 Branch and versioning · 0.5 ew · depends: —
- **Design**: plan 02 §1–2.
- **Tasks**: cut `v3.0.0` from `master` @ the agreed commit; set up branch protection (PR-only, required checks, linear history, no force-push); apply the `GitVersion.yml` changes from plan 02 §2; document the work-branch naming rule in `CONTRIBUTING.md`.
- **Acceptance**: GitVersion yields `3.0.0-alpha.1` on `v3.0.0` and `3.0.0-v3-wp-…` on a sample work branch (verified in CI logs). `master` versioning is unchanged.

### WP-0.2 Repository scaffold · 1 ew · depends: WP-0.1
- **Deliverables**: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `SourceFlow.slnx`. Empty projects for every v3 package in plan 02 §3 (categorised `src/<Category>/`), with correct references and the dependency rules from design doc 01 §5. One test project per category (plan 04 §3). `tests/Fixtures/Bank/*` and `benchmarks/SourceFlow.Benchmarks` skeletons.
- **Tasks**: delete `SourceFlow.Net.sln` and `tests/SourceFlow.Net.EntityFramework.Tests`; remove v2 projects from the build (folders kept, plan 02 §4); verify package majors against nuget.org and pin them; enable SourceLink and deterministic builds.
- **Tests**: ARCH-01..05 (stubs that already pass on empty projects).
- **Acceptance**: `dotnet build SourceFlow.slnx -c Release` and `dotnet test` succeed on Windows and Linux with zero warnings. The v2 folders are not part of the build.

### WP-0.3 CI pipelines (skeleton) · 1 ew · depends: WP-0.2
- **Deliverables**: `.github/workflows/v3-pr.yml`, `v3-nightly.yml`, `v3-codeql.yml` (plan 08); the emulator compose files merged into `.github/emulators/` (LocalStack 3 pinned, the Pub/Sub emulator from `gcp_cloud`, the Service Bus emulator + SQL Edge from `azure-cloud`).
- **Acceptance**: a PR into `v3.0.0` runs build + unit + architecture + API-approval jobs. Nightly runs on schedule. Emulators start healthy in a smoke job.

### WP-0.4 Quality harness · 1 ew · depends: WP-0.2
- **Deliverables**: `tests/Architecture/SourceFlow.Architecture.Tests` (NetArchTest rules ARCH-01..08); a public-API approval helper (PublicApiGenerator + approved-file snapshots) used by each category test project; coverage collection (coverlet) + ReportGenerator merge + `tools/coverage-gate` script (plan 04 §5); BenchmarkDotNet harness with a baseline-comparison script (plan 08 §4).
- **Acceptance**: the gate fails a PR that lowers coverage below the thresholds or changes public API without approval files.

### WP-0.5 Design baseline · 0.5 ew · depends: —
- **Tasks**: record the ADR acceptance state; confirm DC-1..DC-4 with the architects; open tracking issues for every WP in this plan (plan 09 §3).
- **Acceptance**: the board shows all 54 WPs with owners for M0–M1.

---

## M1 — Core runtime and programming model (37 ew)

### WP-1.1 Core contracts and SPIs (`SourceFlow.Net`) · 1.5 ew · depends: WP-0.2
- **Design**: doc 03 §2–4, §6; ADR-003, 005, 015, 018, 020, 022, 023.
- **v2 input**: `src/SourceFlow/Messaging/*` (Command, Event, Metadata), `Saga/*`, `Projections/*`, `Aggregate/*`. These show what v3 replaces: payload wrappers, mutable metadata, int ids, `IHandlesWithEvent`.
- **Deliverables**:
  - `Messages.cs`: `IMessage`, `ICommand`, `ICommand<TAgg>`, `IEvent`, `IIntegrationEvent`, `MessageKind`, `IStronglyTypedId`, `Error`, `UnhandledMessageException`.
  - `Attributes.cs`: `MessageType`, `Aggregate`, `Projection`, `ProcessManager`, `SourceFlowModule(jsonContext)`, `AggregateId`, `PersonalData`, `Queryable`, `Tag`, `RenamedFrom`, `StronglyTypedId`, `DomainQuery`.
  - `Decisions.cs`: `Decision`, `DecisionKind`, `DecisionContext`, `CommandStatus`, `CommandResult`, `ConsistencyToken`.
  - `Aggregates.cs`: `IAggregate<TSelf>`, `IAggregate<TSelf,TId>`, `IAggregateRuntime<TSelf>`, `ICommandHandler<TCmd,TAgg>`.
  - `Reactions.cs`: `Reaction` (DC-3), `ReactionContext`, `ScheduledMessage`, `IProcessManager*`, `EventContext`, `IDocumentProjection*`, `IEventHandler<T>`.
  - `Modules.cs`: `ISourceFlowModuleBuilder`, `ISourceFlowModule`.
  - `Queries.cs`: `IDomainQuery<TRow>`.
- **Tests**: UT-ABS-01..18.
  - SPI namespaces (interfaces only in this WP; implementations follow): `SourceFlow.Storage`, `SourceFlow.Transport`, `SourceFlow.Security`, `SourceFlow.Query`, `SourceFlow.Pipeline`, `SourceFlow.Diagnostics`, and `SourceFlow.Hosting.SourceFlowBuilder` with one `Use*` slot per SPI ([ADR-027](../ADRs/ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md)).
- **Acceptance**: `SourceFlow.Net` references only the BCL and `Microsoft.Extensions.*` (ARCH-01). Domain types live in the `SourceFlow` namespace, which ARCH-04 checks for domain code. Public API approved.

### WP-1.2 `SourceFlow.Generators` (generators + analyzers) · 4 ew · depends: WP-1.1
- **Design**: ADR-004, doc 03 §4.2, §9; DC-1.
- **v2 input**: `src/SourceFlow/IocExtensions.cs` (the reflection scanning this replaces; GAP-16).
- **Deliverables**:
  - an incremental generator with pipelines for aggregates, projections, process managers, message types, handlers (`ICommandHandler`, `IEventHandler`) and modules;
  - emitters for the aggregate runtime (`AggregateName`, `HandledCommands`, `TargetOf`, `Decide`, `Evolve`), projection runtime (`Apply`), process-manager runtime (`CorrelateBy`, `React`) and module `Register`;
  - a strongly typed id generator (`New`, `Parse`, `TryFormat`, JSON converter, `ToStreamSegment`);
  - analyzers SF0001, 0002, 0003, 0004, 0005, 0006, 0007, 0008, 0009, 0010 (with code fix), 0014 in M1. SF0015/0016/0018 come in WP-1.13, and SF0011/0013/0017 in WP-5.2;
  - value-equatable models for incremental caching.
- **Tasks**: pack into `SourceFlow.Net` (`analyzers/dotnet/cs`; the project stays `netstandard2.0`, the only non-`net10.0` project); write generated files to `obj/generated` for review; add a doc page "reading generated code".
- **Tests**: GEN-01..40.
- **Acceptance**: the sample compiles with zero reflection in dispatch. Generator pipelines are cached (a no-change edit produces no new outputs; GEN-38). Every diagnostic has positive and negative tests.

### WP-1.3 Messaging core · 2 ew · depends: WP-1.1
- **Design**: doc 04 §1–2; ADR-005, 011.
- **v2 input**: `Cloud/Serialization/PolymorphicJsonConverter.cs`, AWS `Messaging/Serialization/*` (assembly-qualified-name typing that v3 removes; GAP-04, GAP-10).
- **Deliverables**: `Messaging/MessageEnvelope.cs` (CloudEvents mapping), `MessageIds` (UUIDv7, RFC 9562 UUIDv5), `MessageTypeRegistry` (frozen, allow-list, duplicate detection), `MessageSerializer` (`JsonTypeInfo` from module contexts; `IBufferWriter`/`ReadOnlySequence` overloads), `IUpcaster<TFrom,TTo>` + upcaster chain, `CloudEventsMapper` (binary/structured, `sf_ext` overflow), and size/header limit checks.
- **Tests**: UT-MSG-01..30.
- **Acceptance**: an unknown `(type, version)` is never deserialized. A UUIDv5 golden vector matches RFC 9562. The envelope round-trips through both CloudEvents modes.

### WP-1.4 Storage SPI + in-memory store · 2.5 ew · depends: WP-1.3
- **Design**: doc 05 §1, §3, §5, §7; ADR-008, 010; DC-2.
- **v2 input**: `ICommandStore`, `IEntityStore`, `IViewModelStore`, `E2E/Impl/InMemory*Store.cs` (the shapes being replaced).
- **Deliverables**: `Storage/Ports.cs` (`IEventStore`, `IEventLogReader`, `IStorageSessionFactory`/`IStorageSession`, `ICheckpointReader`, `ILeaseManager`, `IOutboxStore`, `IDocumentReader`, `IDeadLetterStore`, `IStoreNotifications`, `IAggregateSnapshotStore`, plus records and exceptions); `Storage/Buckets.cs`; `Storage/InMemory/InMemoryStore.cs`, which enforces compare-and-set, ES-R2, fencing, inbox uniqueness, the head-of-key outbox rule and versioned documents, and provides fault-injection hooks.
- **Tests**: UT-STO-01..35, and the SCK suite run against the in-memory store (SCK-01..50, plan 06).
- **Acceptance**: the in-memory store passes the **entire** storage conformance kit. It is the reference implementation the database adapters are compared against.

### WP-1.5 Runtime model and routing · 1.5 ew · depends: WP-1.3
- **Design**: doc 04 §3; ADR-007; design doc 01 §9 (options).
- **v2 input**: `Cloud/Configuration/BusConfiguration.cs` (builder validation rules worth keeping: no URLs or ARNs, clear errors), `ICommandRoutingConfiguration`.
- **Deliverables**: `Runtime/Descriptors.cs` (aggregate, command handler, projection, process manager, event handler; AOT-safe generic factories); `Runtime/RuntimeModel.cs`; `Runtime/Routing.cs` (`RoutingBuilder`, `RoutingTable`; owned XOR routed, RT-R1…R6); `SourceFlowOptions` with `[OptionsValidator]` + `ValidateOnStart`.
- **Tests**: UT-RT-01..25.
- **Acceptance**: invalid routing fails at startup with actionable messages. Every option is honoured (no inert options).

### WP-1.6 Command processing + temporal reads · 3 ew · depends: WP-1.4, WP-1.5, WP-1.7
- **Design**: doc 03 §5; ADR-006, 008, 018; doc 14 §6 (tokens).
- **v2 input**: `Messaging/Bus/Impl/CommandBus.cs`, `Impl/CommandStoreAdapter.cs`, `Saga/Saga.cs`. They show the O(n) sequencing, append-after-side-effects and swallowed event failures that v3 eliminates (GAP-05, 07, 20).
- **Deliverables**: `Runtime/EnvelopeFactory.cs`, `AggregateStore.cs` (fold, fail-loud on unknown types), `AggregateReader.cs` (`AsOf`), `CommandProcessor.cs` (one session; inbox, append, publications to outbox, inline projections, commit; re-decide with jittered backoff), `CommandSender.cs` (`ICommandSender`, `SendOptions`, local XOR remote, idempotency key → UUIDv5), `ProjectionApplier.cs` (shared inline/async, read-side guard).
- **Tests**: UT-CMD-01..32; E2E-01..08.
- **Acceptance**: N parallel deposits on one stream all succeed with the correct balance (E2E-04). A rejected command appends nothing. A duplicate idempotency key returns the original result.

### WP-1.7 Middleware and diagnostics · 1.5 ew · depends: WP-1.3
- **Design**: ADR-012, 019; doc 08.
- **v2 input**: the four `Messaging/*Middleware` interfaces and their 39 tests (the nesting and short-circuit semantics are kept), `Observability/DomainTelemetryService.cs` (the metric bugs in GAP-14).
- **Deliverables**: `Pipeline/Middleware.cs` (`PipelineStage`, `MessageContext`, `IMessageMiddleware`, cached `MiddlewarePipeline`), `TelemetryMiddleware`, `LoggingScopeMiddleware`, `ErrorClassificationMiddleware`; `Diagnostics/SourceFlowDiagnostics.cs` (ActivitySource, Meter, every instrument in doc 08 §3 with `InstrumentAdvice`); `Diagnostics/Log.cs` (`[LoggerMessage]`, event-id ranges).
- **Tests**: UT-MW-01..20, UT-DIAG-01..15.
- **Acceptance**: each metric is recorded exactly once per operation (MeterListener tests). Spans follow messaging semconv names.

### WP-1.8 Outbox, scheduler, transport SPI, in-memory broker, receive pipeline · 3.5 ew · depends: WP-1.6
- **Design**: doc 04 §5–9; doc 07 §2; ADR-006, 014.
- **v2 input**: AWS/GCP listener loops (`AwsSqsCommandListener*.cs`, `PubSubCommandListener.cs`): backoff loops, poison handling and the GAP-02 shared-queue defect; `Cloud/DeadLetter/*`.
- **Deliverables**: `Transport/Transport.cs` (SPI, `TransportTopology`, `TransportCapabilities`); `Transport/InMemory/InMemoryBroker.cs` + `InMemoryTransport.cs` (fan-out per service, competing consumers, delayed redelivery, fault injection: duplicate, drop-ack, raw inject; idle counters); `Outbox/OutboxRelay.cs` (partition leases, head-of-key, backoff, park after `OutboxMaxAttempts`); `Outbox/Scheduler` (`deliver_at` semantics); `Receiving/ReceiveEndpoint.cs` (partitioned dispatcher RX-R1, bounded channels RX-R2, settle-after-commit RX-R3, classification, delayed retry, park); `DeadLetters/IDeadLetterManager` (list, replay, discard).
- **Tests**: UT-OUT-01..18, UT-RX-01..22, UT-DLQ-01..10; TCK run against the in-memory transport (TCK-01..45).
- **Acceptance**: the in-memory transport passes the TCK. Unknown types are parked unread. Duplicate deliveries are absorbed by the inbox.

### WP-1.9 Subscriptions, projections, process managers, event handlers · 4 ew · depends: WP-1.8
- **Design**: doc 06; ADR-016, 020; doc 03 §6.
- **v2 input**: `Aggregate/EventSubscriber.cs`, `Projections/EventSubscriber.cs`, `Saga/CommandSubscriber.cs` (in-memory fan-out, which v3 replaces with durable subscriptions).
- **Deliverables**: `Subscriptions/SubscriptionEngine.cs` (workers per (subscription, partition), lease + fencing, wake-ups, adaptive batch, Stop/Park policies, single-event isolation on failure, stalled metric); `Projections/ProjectionRunner`; `ProcessManagers/ProcessManagerRunner.cs` (correlate, load/save state with optimistic version, commands and timers to outbox, completion); event-handler dispatch for local (subscription) and remote (receive) sources with per-consumer inbox; `nodes` heartbeat + fair-share rebalancer.
- **Tests**: UT-SUB-01..28, UT-PRJ-01..12, UT-PM-01..18; E2E-09..16.
- **Acceptance**: with 4 partitions, every event is projected exactly once (E2E-11). A process-manager timeout fires under `FakeTimeProvider` (E2E-14). A stale lease can't commit (UT-SUB-12).

### WP-1.10 Hosting, roles, tenancy context · 1.5 ew · depends: WP-1.9
- **Design**: doc 01 §9; ADR-022.
- **Deliverables**: `Hosting/SourceFlowBuilder.cs` (`AddModule<T>`, `Routing`, `Projections`, `ProcessManagers`, `UseMiddleware`, `UseInMemory*`, `UseReflectionJsonFallback` annotated RUC/RDC), `ServiceCollectionExtensions.AddSourceFlow`, `IHostedLifecycleService` orchestration (validate → provision → start → ready → drain), `SourceFlowRoles`, `ITenantResolver` + ambient tenant, `IActorAccessor`, and the readiness health check.
- **Tests**: UT-HOST-01..16.
- **Acceptance**: startup fails fast on invalid configuration. Shutdown drains within `ShutdownTimeout` (UT-HOST-09).

### WP-1.11 `SourceFlow.Testing` · 2.5 ew · depends: WP-1.9, WP-1.10
- **Design**: doc 11 §2–3.
- **v2 input**: `tests/SourceFlow.Cloud.AWS.Tests/TestHelpers/*` (environment abstractions worth simplifying), the E2E in-memory stores.
- **Deliverables**: `AggregateSpec<T>` (Given/When/Then/ThenRejected/ThenNoOp; serializer round-trip and determinism checks), `ProcessManagerSpec<T>` (ThenSends/ThenSchedules/ThenCompleted, `WhenTimeAdvances`), `ProjectionSpec<T>`; `SourceFlowTestHost` (`StartAsync`, `Send`, `WaitForIdleAsync`, `Documents<T>`, `Outbox`, `Broker`, `Time`); abstract bases `StorageConformanceTests` and `TransportConformanceTests`; `IFaultInjector`.
- **Tests**: UT-TST-01..20 (specs testing the spec library), plus every M1 E2E uses the host.
- **Acceptance**: no `Task.Delay`-based waits in any SourceFlow test (enforced by an analyzer rule in the test projects).

### WP-1.12 Schema catalog · 2 ew · depends: WP-1.2, WP-1.3
- **Design**: doc 14 §2.
- **Deliverables**: a generator extension that emits a JSON Schema 2020-12 document per message, document and process-state type (with `x-sourceflow-*` extensions); `Catalog/SchemaCatalog` (startup registration, fingerprints, immutability check); a `Catalog/Compatibility` checker (additive vs breaking rules) reused by the CLI; an AsyncAPI 3.0 exporter (library part).
- **Tests**: UT-CAT-01..18, GEN-41..46.
- **Acceptance**: changing a published `(type, version)` fails startup. The compatibility checker classifies the ten rule cases in doc 14 §2.2 correctly.

### WP-1.13 SFQL front end (pipeline + SQL syntax) + in-memory interpreter + domain queries · 5.5 ew · depends: WP-1.12
- **Design**: doc 14 (grammar `sfql/1` in both syntaxes, semantics, Q-B1…B6, G-1…G-12, §9.2 request guard, §9.3 I-1…I-8); ADR-023, ADR-026.
- **Deliverables**: `Query/RequestGuard` (size, UTF-8/NFC, forbidden characters, token and depth limits; SFQ0103/0104), `Query/Lexer`, `PipelineParser` and **`SqlParser`** (read-only `SELECT` subset, allow-list grammar; SFQ0101/0102 with positions; both produce one `Ast`), join, grouping, `DISTINCT`, `CASE`, subquery (depth 1) and `OFFSET` nodes, `Binder` (catalog, version rewrites, typing, Q-B1 ownership → SFQ0204), `Policy` (tenant, PII, permissions, cost guard incl. join rules SFQ0404–0409, limits), `Planner`, `InMemoryInterpreter` (hash joins, grouping, distinct, subqueries, linear-time `LIKE`), `IQueryService`, typed builder, `[DomainQuery]` runtime + generator binding (SF0018), consistency tokens (`at_least`), continuation tokens, `IAggregateReader` source (`from aggregate … as of`); analyzers SF0015/SF0016.
- **Tests**: UT-QL-01..95, GEN-47..50, QRY-01..20 and QRY-46..52 (in-memory part), QRY-57..60 (injection corpus, no-literal property), plus CESQL TCK cases.
- **Acceptance**: every example in doc 14 §10 (A–I, both syntaxes) parses, binds and runs in memory with the expected rows. Every SFQ diagnostic has a test. The whole injection corpus is rejected or neutralised. Equivalent pipeline and SQL queries produce identical ASTs.

### WP-1.14 Bank sample (ported v2 E2E domain) · 1.5 ew · depends: WP-1.11
- **v2 input**: `tests/SourceFlow.Core.Tests/E2E/**` (BankAccount, AccountAggregate, AccountSaga, AccountView, the commands and events).
- **Deliverables**: `tests/Fixtures/Bank/Bank.Accounts` (BankAccount aggregate, AccountSummary projection, `AccountOpenedV1` integration event, JSON context), `tests/Fixtures/Bank/Bank.Transfers` (Transfer aggregate + MoneyTransfer process manager with timeout), `tests/Fixtures/Bank/Bank.Notifications` (a second service: owns a `Statement` aggregate, subscribes to `accounts.events`), and `samples/Bank/Bank.Api` (minimal API host).
- **Tests**: the sample's own specs; it is the domain for E2E-01..16.
- **Acceptance**: the v2 account lifecycle (open, activate, deposit, withdraw, close) is expressible in v3, with the differences documented in a migration note.

### WP-1.R Retire v2 core · 0.5 ew · depends: WP-1.6…1.14
- **Tasks**: confirm that every `SourceFlow.Core.Tests` row in [plan 07](07-V2-Test-Disposition-Matrix.md) is satisfied; delete `src/SourceFlow` and `tests/SourceFlow.Core.Tests`; update docs links.
- **Acceptance**: the M1 exit gate (plan 09 §2) is green.

---

## M2 — PostgreSQL, projections, query on PostgreSQL (19 ew)

### WP-2.1 PostgreSQL schema and migrator · 1.5 ew · depends: WP-1.4
- **Design**: doc 05 §2, §7, §11; ADR-021 (tags), ADR-022 (tenant columns), doc 14 §2.2 (`schemas`).
- **v2 input**: `src/SourceFlow.Stores.EntityFramework/Migrations/DbContextMigrationHelper.cs` (avoid its `CREATE TABLE IF NOT EXISTS` and error swallowing), `CommandRecord` indexes.
- **Deliverables**: `SourceFlow.Storage.PostgreSQL/Schema/V001__initial.sql` (every table in doc 05 + `schemas`, `nodes`, `collection_aliases`, `subject_streams`), `V002__rls.sql` (optional policies), `V003__partition_events.sql` (opt-in variant); `SchemaMigrator` (advisory lock, `schema_history`); `NpgsqlDataSource` registration (`UsePostgreSql`).
- **Tests**: SCK-M01..06 (fresh install, upgrade path, concurrent migrators, `AutoMigrate` off in Production).

### WP-2.2 PostgreSQL append and stream read · 2 ew · depends: WP-2.1
- **Design**: doc 05 §3–4 (CTE append, Exact/NoStream only on the wire, ES-R1, ES-R2, unnest `jsonb[]` tags).
- **Deliverables**: `PostgreSqlStorageSession` (write-behind, one `NpgsqlBatch` per commit, affected-row check → `ConcurrencyException`, unique-violation mapping), `PostgreSqlEventStore.ReadStreamAsync` (streaming reader, no intermediate strings).
- **Tests**: SCK-01..20 against PostgreSQL; PERF-03.

### WP-2.3 Global log reader and notifications · 1.5 ew · depends: WP-2.2
- **Design**: doc 05 §5 (xid8 horizon, bucket filter), doc 06 §2 (LISTEN/NOTIFY).
- **Deliverables**: `PostgreSqlEventLogReader` (`ReadLogAsync`, `GetSafeHorizonAsync`, `horizon_age`), `PostgreSqlNotifications` (dedicated LISTEN connection, reconnect, fallback to polling, `ListenConnectionString` for PgBouncer).
- **Tests**: SCK-21..28, including **SCK-24 gap-freedom under 32 concurrent writers with delayed commits** (property-based).

### WP-2.4 Outbox, inbox, checkpoints, leases, dead letters, process state, documents · 2.5 ew · depends: WP-2.2
- **Deliverables**: SQL and C# for each port per doc 05 §7 (head-of-key claim query, fenced checkpoint `UPDATE … FROM leases`, lease acquire/renew with a database clock, inbox insert with stored result and batched cleanup, dead-letter park/query/replay, process state with version, documents with version and the alias table).
- **Tests**: SCK-29..50 against PostgreSQL.

### WP-2.5 Snapshots and aggregate cache · 1.5 ew · depends: WP-2.2
- **Design**: doc 05 §8.
- **Deliverables**: `ISnapshotStore` (PostgreSQL), a background snapshotter (bounded channel, drop-oldest), `schema_hash` invalidation, the `MemoryCache`-based state cache (`ReadTail` / `Optimistic`).
- **Tests**: UT-SNAP-01..10, SCK-51..55, PERF-04.

### WP-2.6 Tenancy with row-level security · 1 ew · depends: WP-2.4
- **Design**: ADR-022.
- **Deliverables**: `SET LOCAL sourceflow.tenant` per session, subscription role with `BYPASSRLS`, tenant-aware bucket hash, tenant tags on metrics (bounded top-N).
- **Tests**: SCK-56..60 (cross-tenant read impossible with RLS on; isolation with RLS off is enforced by the runtime).

### WP-2.7 `SourceFlow.Storage.EntityFrameworkCore` read models · 2 ew · depends: WP-2.4
- **Design**: doc 03 §6.3, doc 05 §10.
- **v2 input**: `SourceFlow.Stores.EntityFramework` naming conventions and multi-provider setup (reuse ideas, not code).
- **Deliverables**: `IEfProjection<TDbContext>`, a `DbContext` bound to the session connection and transaction, an `IdempotentUpsert` helper, and rebuild support with schema-per-generation.
- **Tests**: UT-EF-01..10, SCK-61..64 (projection + checkpoint atomic in one transaction).

### WP-2.8 SFQL on PostgreSQL (incl. joins and grouping) + index sync + domain queries · 3.5 ew · depends: WP-1.13, WP-2.4
- **Design**: doc 14 §7, §10.
- **Deliverables**: `PostgreSqlSfqlCompiler` (parameterised SQL where **every request value is a parameter** (I-3); catalog-only identifiers quoted by Npgsql; horizon predicate; version rewrites; joins with tenant equality; `GROUP BY`/`HAVING`; `DISTINCT`; subqueries; `NULLS FIRST/LAST`; keyset and read-model-only `OFFSET` pagination), read-only transaction + `statement_timeout`, optional `EXPLAIN` cost ceiling (SFQ0407), `[Queryable]` partial-expression-index generator (`db index sync` library part), read-only `QueryDataSource`, `ExplainAsync`.
- **Tests**: QRY-21..52 (golden SQL incl. joins and grouping, cross-provider equivalence with the in-memory interpreter), QRY-57..60 on PostgreSQL.

### WP-2.9 Storage conformance kit complete · 2 ew · depends: WP-2.2…2.6
- **Deliverables**: the full SCK in `SourceFlow.Testing` (plan 06 §2), wired for in-memory and PostgreSQL; CsCheck linearisability tests for append and outbox.
- **Acceptance**: green on PostgreSQL 15, 16 and 17 (nightly matrix).

### WP-2.10 Performance baseline · 1 ew · depends: WP-2.9
- **Deliverables**: component benchmarks PERF-01..10 with recorded baselines.
- **Acceptance**: the doc 09 component targets are met, or deviations are logged with an owner.

### WP-2.R Retire v2 EF stores · 0.5 ew · depends: WP-2.9
- **Tasks**: satisfy the plan 07 rows for `SourceFlow.Stores.EntityFramework.Tests`; delete `src/SourceFlow.Stores.EntityFramework` and its tests.

---

## M3 — Cloud transports: AWS, Google Cloud and Azure all GA in 3.0.0 (19 ew)

Three transport workstreams run **in parallel** after WP-3.1 (product sign-off for Q5/Q6).

### WP-3.1 Transport SPI final + Transport Conformance Kit · 3 ew · depends: WP-1.8
- **Design**: doc 07 §2, §7.
- **v2 input**: the scenario lists of every AWS/GCP integration test (plan 07 maps them to TCK IDs) and the Azure suites on `azure-cloud`.
- **Deliverables**: `TransportCapabilities` finalized; `TransportConformanceTests` with TCK-01..45 (envelope, send/publish/fan-out, ordering, settlement, DLQ backstop, filters with CESQL, provisioning, resilience, shutdown, E2E with core); a fixture contract for emulator and real-cloud runs.
- **Acceptance**: the in-memory transport passes all 45.

### WP-3.2 `SourceFlow.Cloud.Aws` · 4 ew · depends: WP-3.1
- **Design**: doc 07 §4 (AWS column), §4.1, §5, §6.
- **v2 input**: `src/SourceFlow.Cloud.AWS/**` (bootstrapper, dispatchers, listeners, health check); AWS tests (`Sqs*`, `Sns*`, `AwsBusBootstrapperTests`, `BusConfigurationTests`, `AwsRetryPolicyTests`, `AwsServiceThrottlingAndFailureTests`); `AwsOptions` (GAP-03).
- **Deliverables**: `AwsTransportOptions` → `AWSOptions` and client config (honoured options); sender (`SendMessageBatch`, `PublishBatch`, FIFO group and dedup ids); receiver (long poll, visibility heartbeat, `ChangeMessageVisibility` delay); CloudEvents binary mapping with `sf_ext` overflow; provisioner (queues, FIFO validation, SNS topics, raw-delivery subscriptions with filter policy, queue policy, redrive DLQ; Create/Validate/None); health check; DLQ ingestor.
- **Tests**: UT-AWS-01..30; TCK-01..45 on LocalStack (PR) and real AWS (nightly).
- **Acceptance**: TCK green. GAP-02 and GAP-03 regression tests (UT-AWS-05, UT-AWS-12) pass.

### WP-3.3 `SourceFlow.Cloud.Gcp` · 3.5 ew · depends: WP-3.1
- **v2 input**: `src/SourceFlow.Cloud.GCP/**`, `tests/SourceFlow.Cloud.GCP.Tests/**`, `.github/gcp-emulator`.
- **Deliverables**: options (ProjectId, ADC, emulator detection); sender with ordering keys and publisher ordering; receiver (streaming pull, ack-deadline extension, nack-based delay); provisioner (topics, `{svc}` and `{svc}.{topic}` subscriptions, `enable_message_ordering`, dead-letter policy, filters); health check.
- **Tests**: UT-GCP-01..25; TCK on the Pub/Sub emulator (PR) and real GCP (nightly).
- **Acceptance**: TCK green, including fan-out per service (the GAP-13 regression, TCK-12).

### WP-3.4 `SourceFlow.Cloud.Azure` · 4 ew · depends: WP-3.1
- **v2 input**: `origin/azure-cloud` `src/SourceFlow.Cloud.Azure/**`, `tests/SourceFlow.Cloud.Azure.Tests/**` (session handling, subscription filtering, emulator CI gate; note the two emulator-divergent session tests excluded there), `.github/azure-emulator`.
- **Deliverables**: options (`FullyQualifiedNamespace` + `TokenCredential`; connection string in Development only); sender with batching, sessions and duplicate detection; receiver (processor or session processor, lock auto-renew, scheduled re-send for delay); provisioner (queues, topics, `{svc}` subscriptions with `ForwardTo`, correlation filters, `MaxDeliveryCount`); health check.
- **Tests**: UT-ASB-01..25; TCK on the Service Bus emulator (PR) and real Azure (nightly). Emulator-divergent cases are marked `RealCloudOnly` with a reason.
- **Acceptance**: TCK green (emulator subset on PR, all cases nightly).

### WP-3.5 Provisioning, topology export, DLQ ingestor, IaC modules · 2.5 ew · depends: WP-3.2…3.4
- **Deliverables**: the `Topology` model and naming conventions; `ProvisioningMode` behaviour shared across transports; JSON export (library); a `DeadLetterIngestor` background service; `deploy/topology/{terraform,bicep}` modules consuming the export, tested against TCK topology fixtures.
- **Tests**: UT-TOP-01..15, TCK-35..40.

### WP-3.6 Cross-service E2E per transport · 1.5 ew · depends: WP-3.5, WP-1.14
- **Deliverables**: E2E-17..24 parameterised over in-memory, AWS, Google Cloud and Azure (Bank Accounts ↔ Notifications ↔ Transfers).
- **Acceptance**: green on all four transports (emulators on PR; real cloud nightly).

### WP-3.R Retire v2 cloud providers · 0.5 ew · depends: WP-3.6
- **Tasks**: satisfy the plan 07 rows for AWS and GCP tests; delete `src/SourceFlow.Cloud.AWS`, `src/SourceFlow.Cloud.GCP` and their tests; archive `azure-cloud` as `archive/azure-cloud`.

---

## M4 — Operations, SQL Server, scale (16.5 ew)

### WP-4.1 `SourceFlow.Tool` (`dotnet sourceflow`) · 3 ew · depends: WP-2.4, WP-3.5, WP-2.8
- **Commands**: `db migrate|script|index sync`, `dlq list|show|replay|discard|purge`, `projection rebuild|repartition|skip|drop`, `topology export|validate`, `query "<sfql>" [--explain] [--format]`, `schema check|export --format asyncapi|jsonschema`.
- **Tests**: UT-CLI-01..30 (System.CommandLine parsing + handlers against in-memory), E2E-25..28 against PostgreSQL.

### WP-4.2 `SourceFlow.AspNetCore` (incl. the per-service query API) · 2.5 ew · depends: WP-1.13, WP-2.8
- **Deliverables**: `MapCommand<T>` (Idempotency-Key, If-Match → ExpectedVersion, ETag, `SourceFlow-Consistency`, ProblemDetails 422/409/412), `MapDomainQueries()` (named, versioned queries only; Q-B4), **`MapSourceFlowQueryApi()`**: `POST /query`, `/query/validate`, `/query/explain`, `GET /query/schema` (doc 14 §9.1). It fails closed without an authorization policy (SFQ0504), and adds the transport guard (64 KB, JSON-only, depth), rate and concurrency limiting (ASP.NET Core rate limiter, SFQ0502/0503), problem+json errors without input echo (S-3), security headers (S-2), NDJSON and CSV with formula neutralisation (S-4), and OpenAPI 3.1 description.
- **Tests**: UT-ASP-01..40 (`WebApplicationFactory`), QRY-53..56 (API end to end on PostgreSQL), DAST in nightly (plan 08).

### WP-4.3 Health checks, dashboards and alerts · 2 ew · depends: WP-1.9, WP-3.5
- **Deliverables**: every check in doc 08 §5; a dashboard and alert generator from the instrument catalogue → `deploy/observability/*.json|yaml`; an Aspire dashboard sample.
- **Tests**: UT-HC-01..14; a CI drift check (generated files must match).

### WP-4.4 `SourceFlow.Storage.SqlServer` + SFQL SQL Server compiler · 4 ew · depends: WP-2.9, WP-2.8
- **Design**: doc 05 §5.5 (rowversion, `MIN_ACTIVE_ROWVERSION`, TVP append, `sp_getapplock` for the migrator), doc 14 §10 examples B and G (joins, grouping, parameterisation via `SqlParameter`, `ApplicationIntent=ReadOnly`).
- **Tests**: the full SCK on SQL Server 2022/2025 (Testcontainers MsSql); QRY cross-provider equivalence.

### WP-4.5 `PartitionEvents` + stream archiving · 1.5 ew · depends: WP-2.2
- **Deliverables**: the partitioned-schema variant and a partition-maintenance job (PostgreSQL); `ArchiveStreamAsync`, with exclusion rules in loads, rebuilds and SFQL (`include_archived`).
- **Tests**: SCK-65..72 (the full SCK also runs on the partitioned variant nightly).

### WP-4.6 OpenTelemetry integration (no package) · 1 ew · depends: WP-1.7
- **Design**: [ADR-027 §5](../ADRs/ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md#5-opentelemetry-without-an-extra-package) (the former `SourceFlow.OpenTelemetry` package is folded into the core).
- **Deliverables**: `SourceFlowTelemetry.ActivitySourceName` / `MeterName` constants; histogram bucket boundaries declared with `InstrumentAdvice<T>` (no SDK views); the pinned semconv version and `OTEL_SEMCONV_STABILITY_OPT_IN` handling in `SourceFlow.Diagnostics`; an OpenTelemetry wiring guide and the Bank sample wiring.
- **Tests**: UT-OTEL-01..08 in `SourceFlow.Core.Tests` (OpenTelemetry SDK in-memory exporter as a test-only dependency).

### WP-4.7 Chaos suite + tier-M load test · 2.5 ew · depends: WP-3.6, WP-4.4
- **Deliverables**: CH-01..10 (Toxiproxy, container kill, SIGSTOP, long transaction), and an NBomber load scenario against the Bank API at tier M.
- **Acceptance**: the SLOs in doc 08 §6 are met in the M4 load test report.

---

## M5 — Security and migration (11 ew)

### WP-5.1 Payload protection + KMS key providers · 3 ew · depends: WP-3.6
- **v2 input**: `AwsKmsMessageEncryption.cs`, `GcpKmsMessageEncryption.cs`, Azure `AzureKeyVaultMessageEncryption.cs`, and their tests (KMS round-trip, caching, rotation property tests).
- **Deliverables**: `IKeyProvider` and `PayloadProtectionMiddleware` in `SourceFlow.Security` (AES-256-GCM, associated data `id|type|source`, DEK reuse limits, zeroing); the KMS key providers in the cloud packages: `SourceFlow.Cloud.Aws` (AWS KMS, `UseAwsKms()`), `SourceFlow.Cloud.Azure` (Key Vault, `UseAzureKeyVault()`), `SourceFlow.Cloud.Gcp` (Cloud KMS, `UseGoogleKms()`).
- **Tests**: UT-SEC-01..20; TCK-41..45 with protection on; real-KMS nightly.

### WP-5.2 Crypto-shredding · 3 ew · depends: WP-5.1, WP-2.4
- **Deliverables**: generated `[PersonalData]` field encryption in the serializer path, `data_keys` and `subject_streams` maintenance, `IPersonalDataEraser`, the `$personal-data.erased` system event, automatic document scrubbing, the snapshot rule, and analyzers SF0011, SF0013, SF0017.
- **Tests**: UT-PII-01..18, E2E-29..31 (erase → reads show `[erased]`, projections scrubbed, SFQL rejects PII filters).

### WP-5.3 Redaction wiring · 0.5 ew · depends: WP-1.7
- **Deliverables**: SourceFlow taxonomy, `[PersonalData]` → classification, the default redactors, and `LogPayloads` gating.
- **Tests**: UT-RED-01..06.

### WP-5.4 v2 migration tool + v2 wire bridge · 3 ew · depends: WP-2.4, WP-3.2, WP-4.1
- **Design**: doc 12 §3–4; doc 07 §8.
- **v2 input**: the EF `CommandRecord` schema and entity tables (read with SQL, not by referencing v2 code); the AWS v2 attribute shape (`CommandType`, `EntityId`, `SequenceNo`, `EventType`, `EventName`).
- **Deliverables**: `migrate v2 plan|scaffold|import|archive|verify`, `IV2Import<TV2Entity,TAggregate>`, a deterministic id map (UUIDv5 of the int id), and the `AcceptV2Messages` allow-list translator (AWS).
- **Tests**: MIG-01..12 against a seeded v2 database built from the v2 E2E scenario (created by a fixture script that uses the v2 `CommandRecord` DDL).

### WP-5.5 Fuzzing + security review · 1.5 ew · depends: WP-5.2
- **Deliverables**: SharpFuzz harnesses (CloudEvents parsers, registry lookup, protected-payload decoder, SFQL lexer and parser), a threat-model review against doc 10 §1, and SBOM + signing in the release pipeline.

---

## M6 — Hardening and GA (6.5 ew)

| WP | Scope | Size | Depends |
|----|-------|------|---------|
| **WP-6.1** Docs, samples, book alignment | Package READMEs; a docs site generated from `docs/`; migration guide; samples per transport | 2 | M5 |
| **WP-6.2** Performance tuning | Meet doc 09 §7.1 targets or record formal waivers; allocation audits | 2 | WP-4.7 |
| **WP-6.3** RC, release, cutover | GitVersion label `rc`; release workflow (sign, SBOM, GitHub Packages + NuGet.org); RC programme (two adopters for four weeks); create `v2.x`; merge `v3.0.0` → `master`; delete branch `v3.0.0`; tag `v3.0.0` (plan 02 §2); final 2.x deprecation notes | 1.5 | WP-6.1, WP-6.2 |
| **WP-6.4** Test hardening | Stryker.NET mutation score ≥ 70 % on core; close coverage gaps; flaky-test burn-down (zero quarantined at GA) | 1 | M5 |

---

## 10. Schedule, critical path and staffing

**Totals**: M0 4 · M1 37 · M2 19 · M3 19 · M4 16.5 · M5 11 · M6 6.5 = **113 ew** across **54 WPs** (+3 ew for the per-service query API, SQL syntax and joins; ADR-026), plus **track S** (workflow DSL and Studio, [plan 10](10-Studio-Workflow-Track-Plan.md)): **32 ew** across 13 WPs in a sixth lane (F, 2 engineers), sprints 6–13, not on the 3.0.0 critical path.

**Critical path** (longest dependency chain):
WP-0.1 → 0.2 → 1.1 → 1.3 → 1.4 → 1.6 → 1.8 → 1.9 → 1.10 → 1.11 → 2.2 → 2.4 → 2.9 → 3.1 → {3.2 ∥ 3.3 ∥ 3.4} → 3.5 → 3.6 → 4.7 → 6.2 → 6.3.

**Staffing**: five engineers in fixed lanes, plus a part-time architect reviewer.

| Lane | Owner profile | M0–M1 | M2 | M3 | M4 | M5–M6 |
|------|---------------|-------|----|----|----|-------|
| A Core runtime | Senior .NET | 1.3–1.6, 1.8–1.10 | 2.5 | 3.1 | 4.6, 4.3 | 6.2 |
| B Generators and query | Roslyn | 1.1, 1.2, 1.12, 1.13 | 2.8 | 3.5 | 4.1, 4.2 | 5.2 |
| C Storage | Databases | 1.4 (with A), 1.11 | 2.1–2.4, 2.6, 2.9 | 3.6 | 4.4, 4.5 | 5.4 |
| D Transports | Cloud | 0.3, 1.7, 1.14 | 2.7, 2.10 | **3.2** | 4.7 | 5.1 |
| F Workflows and Studio (plan 10) | .NET/Roslyn + React/TS | — | WP-S.1–S.4 (from sprint 6) | WP-S.5–S.8 | WP-S.9–S.12 | WP-S.13, Studio 1.0 RC |
| E Quality and ops | SDET/DevOps | 0.1, 0.2, 0.4, 0.5 | SCK on PostgreSQL | **3.3**, then **3.4** (with D) | 4.7 | 5.3, 5.5, 6.1, 6.3, 6.4 |

**Calendar** (two-week sprints, 5 engineers ≈ 10 ew per sprint at ~85 % focus):

| Sprint | 1 | 2–4 | 5–6 | 7–8 | 9 | 10–11 | 12 | 13–14 |
|--------|---|-----|-----|-----|---|-------|----|-------|
| Milestone | M0 | M1 | M1 finish + M2 start | M2 | M3 SPI/TCK | M3 transports (parallel) | M4 | M4 finish, M5, M6 |

That gives roughly **28 weeks to GA**, plus a 15 % buffer (≈ 4 weeks) held at M3 and M6. Scope is fixed (all three transports in 3.0.0, plan 09 §5), so schedule risk is absorbed by the buffer and, if needed, by moving the date.
