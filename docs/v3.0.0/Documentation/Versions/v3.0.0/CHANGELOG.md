# SourceFlow.Net v3.0.0 - Changelog

**Release date**: Unreleased
**Status**: All 63 work packages of the v3 plan are complete. The release candidate is pending (see
[Release readiness](#release-readiness)).
**Target framework**: `net10.0` only, C# 14

## Release summary

SourceFlow.Net v3 is a redesign, not an upgrade. v2 stored commands and replayed them to rebuild state; v3 stores
**events**. Aggregates are pure deciders, everything that crosses a boundary goes through a transactional outbox and an
inbox, and the event log is commit-ordered and gap-free. Registration is generated at compile time instead of discovered
by reflection.

Around that core, 3.0 adds:

- PostgreSQL and SQL Server event stores, and EF Core read models in the same transaction;
- AWS SNS/SQS, Azure Service Bus and Google Pub/Sub transports, all passing the same 45-case conformance kit;
- a query side: a schema catalog, the SFQL query language with a SQL syntax, named domain queries and a per-service HTTP
  query API;
- conjoined multi-tenancy with PostgreSQL row-level security;
- payload encryption and crypto-shredding of personal data with AWS KMS, Azure Key Vault and Google Cloud KMS;
- always-on OpenTelemetry traces and metrics, health checks, a generated Grafana dashboard and Prometheus alerts;
- the `dotnet sourceflow` CLI;
- a testing toolkit with given/when/then specs and conformance kits;
- a workflow DSL compiled into process managers, and SourceFlow Studio, a visual designer for it;
- a v2 data migration tool and a v2 wire bridge for AWS.

Where to go next: the [documentation index](../../README.md), the [wiki](../../wiki.md), and
[Migrating from v2](../../../guides/migrating-from-v2.md).

## Breaking changes from v2

v3 is neither source- nor binary-compatible with v2, and its storage model is different. Nothing in v2 survives as code.
Plan the move with the [migration guide](../../../guides/migrating-from-v2.md) and
[design doc 12](../../../Design/12-Migration-Delivery-and-Traceability.md).

### Platform

| | v2 | v3 |
|-|----|----|
| Target frameworks | `netstandard2.0`, `netstandard2.1`, `net8.0` to `net10.0` | `net10.0` only ([ADR-001](../../../ADRs/ADR-001-target-dotnet-10-and-csharp-14.md)) |
| Language | — | C# 14 |
| Native AOT | — | Runtime packages are AOT-compatible (EF Core read models and the CLI excepted) |
| Registration | Assembly scanning | Source-generated modules ([ADR-004](../../../ADRs/ADR-004-source-generation-instead-of-reflection.md)) |
| Databases | EF Core 9 on any provider | PostgreSQL 15 or later, SQL Server 2022 or later / Azure SQL |

.NET Framework and older runtimes stay on v2. v2 gets security and critical fixes for 12 months after 3.0 GA.

### Storage model

- **Event sourcing replaces command sourcing** ([ADR-002](../../../ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md)).
  Loading an aggregate is a pure fold over its events; there is no command replay.
- **v2 data is imported, not replayed.** `dotnet sourceflow migrate v2` imports each entity's current state as genesis
  events and archives the v2 command table. History lost to v2 gaps GAP-04 and GAP-20 can't be recovered; current state
  can.
- **New schemas.** The v2 EF Core tables (`CommandRecord`, entity and view-model tables) are not used by v3.

### Wire format

- Messages are **CloudEvents 1.0** with W3C trace context and logical type names (`{name}.v{version}`), never .NET type
  names ([ADR-005](../../../ADRs/ADR-005-logical-message-type-names-and-registry.md),
  [ADR-011](../../../ADRs/ADR-011-cloudevents-envelope-and-w3c-trace-context.md)).
- v3 services can accept v2 messages on AWS through an explicit allow-list (`AwsTransportOptions.AcceptV2Messages`).
  There is no bridge for v2 Azure or Google Cloud providers.
- v2 consumers can't read v3 messages. Until they move, publish a v2-shaped integration event with `.As(…)` to the
  legacy topic.

### API map

| v2 | v3 |
|----|----|
| `services.UseSourceFlow(assemblies)` | `services.AddSourceFlow(sf => sf.AddModule<TModule>())` |
| `IEntity { int Id }` | Aggregate `record` implementing `IAggregate<TSelf, TId>` with a strongly typed, caller-assigned id |
| `Aggregate<T>.Send(cmd)` | `ICommandSender.SendAsync(cmd)`, returning `CommandResult` |
| `Saga<T>` + `IHandles<C>` | `Decide(command, context)` on the aggregate returning a `Decision`, or `ICommandHandler<TCommand, TAggregate>` |
| `IHandlesWithEvent<C, E>` | `Decision.Accept(new E(…))` |
| Saga publishing follow-up commands | Process manager reactions (`Reaction`), through the outbox |
| `View<TVM>` + `IProjectOn<E>` | `IDocumentProjection<TDocument, TKey>`, `ISessionProjection<…>` or `IEfProjection<…>`; inline or async |
| `ICommandStore` / `IEntityStore` / `IViewModelStore` | `IEventStore` with snapshots; the document store; EF Core read models |
| `AddSourceFlowEfStores(…)` | `sf.UsePostgreSql(…)` / `sf.UseSqlServer(…)` (+ `sf.UseEntityFrameworkProjections<TDbContext>(…)`) |
| `ReplayCommands(id)` | Not needed for state; `dotnet sourceflow projection rebuild` for read models |
| Four middleware interfaces (command/event, dispatch/subscribe) | `IMessageMiddleware` with `PipelineStage` (`Send`, `Handle`, `Receive`, `Publish`, `Project`) |
| `UseSourceFlowAws(…)` / `UseSourceFlowGcp(…)` | `sf.UseAwsTransport(…)` / `sf.UseGcpTransport(…)` / `sf.UseAzureTransport(…)` |
| `bus.Send.Command<T>(q => q.Queue("x"))` | `r.Command<T>().To("x")`, remote commands only ([ADR-007](../../../ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md)) |
| `bus.Raise.Event<T>(t => t.Topic("x"))` | `r.Publish<T>().As(map).To("x")` |
| `bus.Listen.To.CommandQueue("x")` | `r.Endpoint("x")` (default: the service name) |
| `bus.Subscribe.To.Topic("x")` | `r.Subscribe("x")` |
| `IIdempotencyService`, `IdempotencyConfigurationBuilder` | The built-in transactional inbox, always on |
| `IDeadLetterStore`, `AwsDeadLetterMonitor` | Built-in parking, `IDeadLetterManager` and `dotnet sourceflow dlq …` |
| `ICircuitBreaker`, `CircuitBreakerOptions` | Removed; SDK retries, relay back-off, retry budgets and parking |
| `IMessageEncryption`, `AwsKmsMessageEncryption` | `sf.Security(s => s.UseAwsKms(…).Protect(…))` |
| `[SensitiveData]`, `SensitiveDataMasker` | `[PersonalData]` / `[Secret]` with Microsoft.Extensions.Compliance.Redaction, and crypto-shredding |
| `AddSourceFlowTelemetry(…)`, `DomainObservabilityOptions` | Always on: `AddSource("SourceFlow")` and `AddMeter("SourceFlow")` on your OpenTelemetry builders |
| `PolymorphicJsonConverter` | Removed: no type discriminators on the wire |

## New features

### Core programming model (`SourceFlow.Net`)

- **Aggregates as pure deciders** ([ADR-003](../../../ADRs/ADR-003-aggregates-as-pure-deciders-decide-evolve.md)):
  `[Aggregate]` records with `Decide` and `When`; `Decision.Accept`, `Reject` and `NoOp`. Business rejections are values
  (`Error`), not exceptions ([ADR-018](../../../ADRs/ADR-018-business-rejections-are-values-not-exceptions.md)).
- **Strongly typed ids** (`[StronglyTypedId]`), caller-assigned, UUIDv7 by default; default ids are rejected
  ([ADR-015](../../../ADRs/ADR-015-strongly-typed-caller-assigned-ids-uuidv7-default.md)).
- **Optimistic concurrency with automatic re-decide** (`MaxConcurrencyRetries`,
  [ADR-008](../../../ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md)).
- **Message contracts:** `ICommand<TAggregate>`, `IEvent`, `IIntegrationEvent`, `ITimeout`, `[MessageType(name, version)]`,
  upcasters (`IUpcaster<TFrom, TTo>`), event tags (`[Tag]`, ready for dynamic consistency boundaries in 3.1).
- **Command results** carry the new version, the rejection and a `ConsistencyToken` for read-your-writes queries.
- **Temporal reads**: `IAggregateReader.LoadAsync(id, AsOf, tenantId)`.
- **Snapshots and an aggregate cache** (`AggregateCacheMode.ReadTail` or `Optimistic`), with snapshot schema hashing.
- **Source generator and analyzers** in the `SourceFlow.Net` package: generated ids, aggregates, process managers,
  projections, domain queries, workflows, modules and a static service manifest. Analyzers SF0001 to SF0011 and SF0013
  to SF0021, with a code fix for SF0010. See [Reading generated code](../../../guides/reading-generated-code.md).

### Messaging and routing

- **Transactional outbox and inbox** for effectively-once processing
  ([ADR-006](../../../ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md)); see the
  [idempotency guide](../../Message-Idempotency-Guide.md).
- **Routing** with single command ownership: `Command<T>().To(…)`, `ReplyOnReject()`, `Publish<T>().As(…)/.AsIs().To(…)`,
  `Endpoint`, `Subscribe` with `Only<T>()` and CloudEvents SQL filters, `OrderedEndpoint()` and `OrderedTopic(…)`.
- **`CommandRejected<T>` replies** to the sender, committed with the rejection.
- **One middleware abstraction** for all stages ([ADR-012](../../../ADRs/ADR-012-one-middleware-abstraction-for-all-stages.md)),
  an `ErrorClassifier`, immediate and delayed retries, and parking with reasons such as `DeserializationFailed`,
  `UnknownMessageType`, `Exhausted` and `DecryptionFailed`.
- **Dead letters**: `IDeadLetterManager` (list, show, replay, discard, purge) with the operator recorded; the broker's
  dead-letter queues are ingested on `DeadLetterIngestInterval`.
- **Message type registry** as an allow-list, frozen at startup.

### Persistence

- **Storage SPI** with role-segregated ports (`IEventStore`, `IEventLogReader`, `IStorageSession`, `IOutboxStore`,
  `ICheckpointStore`, `ILeaseManager`, `ISnapshotStore`, `IDeadLetterStore`, documents, monitoring, retention) and an
  in-memory implementation.
- **`SourceFlow.Storage.PostgreSQL`** ([ADR-009](../../../ADRs/ADR-009-event-store-on-adonet-postgresql-first.md),
  [ADR-028](../../../ADRs/ADR-028-postgresql-payload-columns-and-adapter-refinements.md)): ADO.NET, one round-trip commit,
  a commit-ordered, gap-free global log on `(tx_id, seq)` ([ADR-010](../../../ADRs/ADR-010-commit-ordered-global-position-for-gap-free.md)),
  LISTEN/NOTIFY, an embedded checksummed migrator, `PartitionEvents` (monthly partitions, converted in place), stream
  archiving, and `[Queryable]` index sync.
- **`SourceFlow.Storage.SqlServer`**: the same ports on SQL Server 2022+ and Azure SQL (rowversion-ordered log, one
  command batch per commit, `sp_getapplock` migrator).
- **Storage conformance kit** (SCK) passed by in-memory, PostgreSQL (plain, partitioned and with row-level security) and
  SQL Server (54 of 57 cases; 3 need clock control).

See [Event store and persistence](../../Architecture/05-Event-Store-and-Persistence.md).

### Projections and read models

- **Projections inline or async** with partitioned, fenced, checkpointed subscriptions
  ([ADR-016](../../../ADRs/ADR-016-projections-inline-or-async-with-partitioned-fenced.md)), adaptive batches, a node
  registry with fair-share lease balancing, and Stop or Park failure policies with an audited skip.
- **Document projections** with versioned documents, and **session projections** that write in the same transaction.
- **`SourceFlow.Storage.EntityFrameworkCore`**: EF Core read models on the session's connection and transaction
  (`IEfProjection`, `UseEntityFrameworkProjections`, `IdempotentUpsert`).
- **Blue/green read-model generations** behind forward-only aliases
  ([ADR-032](../../../ADRs/ADR-032-blue-green-read-model-generations-behind-forward-only-aliases.md)), including an EF
  Core schema per generation.

See [Projections and read models](../../Architecture/06-Projections-and-Read-Models.md).

### Process managers

- `[ProcessManager]` records with durable state, correlation (`CorrelateBy`), timers and reactions committed with the
  state ([ADR-020](../../../ADRs/ADR-020-process-managers-with-durable-state-and-timers.md)).

### Transports

- **Final pull-based transport SPI** ([ADR-033](../../../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)):
  lock renewal, explicit dead-lettering, capabilities, typed errors, and an in-memory reference broker.
- **Transport conformance kit**: 49 cases (TCK-01..49), passed by every transport
  ([ADR-014](../../../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md)).
- **`SourceFlow.Cloud.Aws`**: SQS and SNS, FIFO ordering and deduplication, SNS filter policies, redrive queues,
  `UseAwsTransport`.
- **`SourceFlow.Cloud.Azure`**: Service Bus with sessions for ordered endpoints, forwarding subscriptions,
  `DefaultAzureCredential` by default, `UseAzureTransport`.
- **`SourceFlow.Cloud.Gcp`**: Pub/Sub with per-service subscriptions, enforced ordering keys, dead-letter policies,
  `UseGcpTransport`.
- **Provisioning modes and topology as data** ([ADR-017](../../../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md)):
  `CreateIfMissing` in Development, `Validate` elsewhere; `dotnet sourceflow topology export`; Terraform modules for all
  three clouds and a Bicep module in `deploy/topology`.

See [Cloud transports](../../Architecture/07-Cloud-Transports.md).

### Hosting (`SourceFlow.Net`, `SourceFlow.AspNetCore`)

- `AddSourceFlow` with `Use*` slots, options validated at startup, and node roles (`Api`, `Receiver`, `Relay`,
  `Subscriptions`).
- **Fluent configuration**: a service and its features in one chain: `WithServiceName`, `WithRoles`,
  `WithProvisioning`, `WithHealthChecks`, `WithQueryApi` (SourceFlow.AspNetCore) and `When(condition, …)` to enable a
  feature from configuration; `app.MapSourceFlowHealthChecks()` maps the liveness, readiness and full health endpoints.
- **`MapCommand<T>`**: command endpoints with `Idempotency-Key`, `If-Match`, `ETag`, a consistency token header and
  ProblemDetails.
- **Health checks** (`AddHealthChecks().AddSourceFlow()`): ready, store, transport, outbox, subscriptions, leases and keys.

### Query side

- **Schema catalog** ([ADR-029](../../../ADRs/ADR-029-schema-catalog-derived-from-serializer-metadata.md)): JSON Schema
  2020-12 per message type and version, fingerprints, startup registration that fails on a changed published version,
  compatibility rules SFC01 to SFC10, and AsyncAPI 3.0 export.
- **SFQL** ([ADR-023](../../../ADRs/ADR-023-sfql-query-language-with-cesql-predicates.md)) with a SQL syntax
  ([ADR-026](../../../ADRs/ADR-026-per-service-query-api-with-sql-syntax-and-injection-proof-compilation.md)): events,
  streams, aggregates and read models; joins, grouping, keyset paging, read-your-writes waits; an in-memory interpreter
  and compilers for PostgreSQL and SQL Server that return the same rows.
- **Named domain queries** (`[DomainQuery]`, bound at startup,
  [ADR-030](../../../ADRs/ADR-030-domain-queries-bind-at-startup.md)) and a typed builder.
- **Per-service query API**: `AddSourceFlowQueryApi`, `MapSourceFlowQueryApi` (`/query`, `/validate`, `/explain`,
  `/schema`) and `MapDomainQueries`; fails closed without authorization, per-principal limits, NDJSON and CSV output.
  A domain query can require its own authorization policy (`[DomainQuery(…, Policy = "…")]`), and subqueries are held
  to the same permissions, cost guard and personal-data rules as the query they sit in.

See [Query side and SFQL](../../Architecture/08-Query-Side-and-SFQL.md).

### Observability

- **Always-on telemetry** ([ADR-019](../../../ADRs/ADR-019-telemetry-always-on-with-standard-semantic-conventions.md)):
  one `ActivitySource` and one `Meter`, both named `SourceFlow`, with OpenTelemetry semantic conventions pinned to 1.27.0
  and `OTEL_SEMCONV_STABILITY_OPT_IN` support. No package dependency on the OpenTelemetry SDK.
- **Metrics** such as `sourceflow.command.duration`, `sourceflow.eventstore.append.duration`, `sourceflow.outbox.pending`,
  `sourceflow.outbox.age`, `sourceflow.subscription.lag`, `sourceflow.subscription.horizon_age`,
  `sourceflow.deadletter.count` and `sourceflow.workflow.transitions`, plus the standard `messaging.*` instruments.
- **Generated dashboard and alerts**: `dotnet sourceflow observability export` writes a Grafana dashboard and Prometheus
  alert rules (`deploy/observability`).

See [Observability and performance](../../Observability-and-Performance.md) and the
[OpenTelemetry guide](../../../guides/opentelemetry.md).

### Security and compliance

- **Payload protection**: AES-256-GCM envelope encryption in the outbox and on the wire, with `RequireOnReceive`.
- **Key providers**: AWS KMS (`UseAwsKms`), Azure Key Vault (`UseAzureKeyVault`), Google Cloud KMS (`UseGoogleKms`) and
  `LocalKeyProvider` for development.
- **Crypto-shredding**: `[PersonalData(Subject = …)]` members encrypted per subject in events and snapshots
  (`EncryptPersonalData()`), and `IPersonalDataEraser` for erasure.
- **Redaction**: `[PersonalData]` and `[Secret]` classifications, `UseRedaction`, `LogPayloads` off by default.
- **Hardening**: type allow-list, banned reflection APIs, strict JSON, fuzzing of every untrusted-input parser, an
  append-only runtime role script (`db script --grants`), operator identity on dead-letter audits.
- **Supply chain**: author-signed packages, CycloneDX SBOMs, provenance attestations, CodeQL, dependency review,
  Renovate and OpenSSF Scorecard.

See the [Security and Compliance Guide](../../Security-and-Compliance-Guide.md).

### Multi-tenancy

- **Conjoined tenancy** ([ADR-022](../../../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)):
  `tenant_id` on every tenant table, `ITenantResolver`, the tenant in the envelope (`sftenant`) and in every storage
  session.
- **PostgreSQL row-level security** (`TenantIsolation.RowLevelSecurity`) with a `BYPASSRLS` system role for cross-tenant
  readers.
- **Cross-tenant queries** only with `sourceflow.query.cross-tenant`.

See [Security and multi-tenancy](../../Architecture/10-Security-and-Multi-Tenancy.md).

### Tooling and CLI (`dotnet sourceflow`)

Store commands run from the global tool against the database; model commands run inside the service through
`SourceFlowCli.TryRunAsync` ([ADR-031](../../../ADRs/ADR-031-cli-runs-store-commands-globally-and-model-commands-in-the-service.md)).

| Area | Commands |
|------|----------|
| Database | `db migrate [--rls] [--partition-events]`, `db script [--grants <role>]`, `db index sync`, `db partitions list\|ensure\|detach` |
| Dead letters | `dlq list\|show\|replay\|discard\|purge` |
| Projections | `projection rebuild\|skip\|repartition\|generations\|switch\|drop` |
| Topology | `topology export\|validate` |
| Queries and schemas | `query [--sql] [--param] [--validate] [--explain] [--format table\|json\|csv]`, `schema export --format lock\|jsonschema\|asyncapi`, `schema check --baseline` |
| Observability | `observability export` |
| Migration | `migrate v2 plan\|scaffold\|import\|archive\|verify` |
| Workflows | `manifest export [--check]`, `workflow list\|show\|resume\|retry-step\|abort`, `studio [--read-only]` |

See the [SourceFlow.Tool README](../../SourceFlow.Tool-README.md).

### Testing (`SourceFlow.Testing`)

- `SourceFlowTestHost`, an in-memory host for whole services.
- Given/when/then specs: `AggregateSpec`, `ProcessManagerSpec`, `ProjectionSpec` and `WorkflowSpec`.
- Approved-file snapshots (`Approvals`).
- The conformance kits: `StorageConformance`, `TransportConformance` and `PersonalDataKeyStoreConformance`, for
  writing your own adapters.
- `ChaosInjector` for fault-point chaos tests.

See the [SourceFlow.Testing README](../../SourceFlow.Testing-README.md).

### Workflows and SourceFlow Studio

- **Workflow DSL** (`*.sfflow.yaml`, `sourceflow.workflow/1`, [ADR-024](../../../ADRs/ADR-024-workflow-dsl-compiled-to-process-managers.md)):
  a schema (`schemas/workflow-1.json`), a validator (WF-V01 to WF-V16), CESQL guards, a simulator, a path enumerator and a
  spec generator, compiled by the source generator into process managers (SF0020, SF0021).
- **Static service manifests** (`[assembly: SourceFlowManifest]`) with what each command emits and rejects, and a
  `sourceflow.domain.yaml` workspace.
- **Workflow operations**: transition metrics and spans, a manual-intervention alert, the `workflow-instances` read model,
  CLI commands, and a startup guard against removing a version that still has instances.
- **SourceFlow Studio** ([ADR-025](../../../ADRs/ADR-025-studio-react-spa-with-shared-wasm-core.md)): a React app over a
  WebAssembly build of the DSL core, served locally by `dotnet sourceflow studio`. Domain map, statechart and swimlane
  views; visual editing with a lossless YAML round trip; simulation and path exploration; spec generation; SVG, PNG,
  Mermaid and BPMN export; a static site build (`npx @sourceflow/studio build`); and the `workflow-diff` pull-request
  action. Studio is on its own release train (Studio 1.0).

See the [Studio README](../../SourceFlow.Studio-README.md), [Process managers and workflows](../../Architecture/09-Process-Managers-and-Workflows.md)
and the [workflows guide](../../../guides/workflows.md).

### Samples

- **[TravelBooking](../../../../../samples/TravelBooking/README.md)**: two services, Bookings and Hotels, on PostgreSQL
  (a schema per service) with Amazon SNS/SQS between them, and the trip-booking workflow: retries, timeouts,
  compensation, a point of no return and manual intervention. HTTP endpoints, domain queries, the query API, health
  checks, OTLP export and the in-service CLI; its Studio workspace and the specs Studio generated (27 paths, a minimal
  cover of 6). `docker compose up -d --build` at the repository root starts it with SourceFlow Studio (read-only) and the
  Aspire dashboard; images `ghcr.io/codeshayk/sourceflow-travel-bookings`, `-travel-hotels` and `-travel-studio`.
- The Bank domain (accounts, transfers and notifications; the v2 end-to-end domain ported) and the OrderFulfilment
  services (Orders, Inventory, Payments, Shipping, with the order-fulfilment workflow) are test fixtures in
  [`tests/Fixtures`](../../../../../tests/Fixtures), not samples.

### Migration from v2

- `sf.MigrateFromV2(…)` with `V2Source` (PostgreSQL or SQL Server, plain SQL), `IV2Import<TV2Entity, TAggregate, TId>`
  and `V2Ids.FromLegacy` (UUIDv5).
- `dotnet sourceflow migrate v2 plan|scaffold|import|archive|verify`.
- The AWS v2 wire bridge (`AcceptV2Messages`, off by default).

## Removed and replaced

### v2 packages

| v2 package | v2 targets | In v3 |
|------------|------------|-------|
| `SourceFlow.Net` (project `src/SourceFlow`) | `netstandard2.0`, `2.1`, `net8.0`–`net10.0` | Rewritten under the same package id as the v3 core (with the generator), plus `SourceFlow.Testing` |
| `SourceFlow.Stores.EntityFramework` | `net8.0`–`net10.0` | Replaced by `SourceFlow.Storage.PostgreSQL`, `SourceFlow.Storage.SqlServer` and `SourceFlow.Storage.EntityFrameworkCore` (read models only) |
| `SourceFlow.Cloud.AWS` | `netstandard2.1`, `net8.0`–`net10.0` | Rewritten as `SourceFlow.Cloud.Aws` |
| `SourceFlow.Cloud.GCP` (2.0.0-beta.1) | `net8.0`–`net10.0` | Rewritten as `SourceFlow.Cloud.Gcp` |
| `SourceFlow.Cloud.Azure` (never on `master`; branch `azure-cloud`) | — | Rewritten as `SourceFlow.Cloud.Azure`; the v2 branch is archived as `archive/azure-cloud` |
| `SourceFlow.Cloud.Core` (consolidated into the core in v2) | — | Gone |

### v2 concepts and APIs with no v3 equivalent

| Removed | Why |
|---------|-----|
| Command store, command sequencing and command replay | Event sourcing; loading is a pure fold |
| Entity store | State is a fold over events (or a snapshot) |
| `Aggregate<T>` façade, `AggregateFactory`, aggregates as DI services | Aggregates are pure records, not services (GAP-08) |
| In-memory `EventQueue` and in-process event fan-out | Events are appended and delivered by durable subscriptions |
| `SagaDispatcher` | Generated `Decide` dispatch and process managers |
| Custom circuit breaker | No circuit breakers in 3.0 (relay breaker is a 3.x candidate) |
| `PolymorphicJsonConverter`, AWS JSON converters | No .NET type names or discriminators on the wire |
| TTL idempotency services (in-memory and EF) | The transactional inbox |
| Bespoke telemetry (`CloudTelemetry`, `CloudActivitySource`, `CloudMetrics`) | Standard `ActivitySource` and `Meter` with semantic conventions |
| `SensitiveDataMasker` | Microsoft.Extensions.Compliance.Redaction ([ADR-013](../../../ADRs/ADR-013-use-platform-libraries-delete-bespoke-equivalents.md)) |
| "Enhanced" dispatchers and listeners, per-cloud bootstrappers | One core receive pipeline and one provisioner per transport |

How every v2 test was ported, rewritten, replaced by a conformance kit or retired is recorded in the
[v2 test disposition matrix](../../../implementation/07-V2-Test-Disposition-Matrix.md); the starting point is the
[v2 baseline inventory](../../../implementation/01-V2-Baseline-Inventory.md).

### Repository and tooling

- `SourceFlow.slnx` replaces `SourceFlow.Net.sln`; projects are grouped by category under `src/` (Core, Storage, Cloud,
  Hosting, Tooling, Workflows), with central package management.
- One test stack (xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, CsCheck and Testcontainers) replaces
  NUnit, xUnit 2, Moq and FsCheck.
- New v3 workflows (`v3-*.yml`, `studio-pr.yml`, `workflow-diff.yml`); see [GitHub Actions setup](../../GitHub-Actions-Setup.md).

## Packages

Every package targets `net10.0` and ships `lib/net10.0` only. The source generator (`netstandard2.0`, a compiler plugin)
ships inside `SourceFlow.Net` under `analyzers/dotnet/cs` and is not a package of its own
([ADR-027](../../../ADRs/ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md)).

| Package | Contents | README |
|---------|----------|--------|
| `SourceFlow.Net` | Core model, runtime, SPIs (storage, transport, security, query, pipeline, diagnostics), in-memory adapters, workflow runtime types, generator and analyzers | [README](../../SourceFlow.Net-README.md) |
| `SourceFlow.Storage.PostgreSQL` | PostgreSQL event store and ports, SFQL compiler, index sync | [README](../../SourceFlow.Storage.PostgreSQL-README.md) |
| `SourceFlow.Storage.SqlServer` | SQL Server event store and ports, SFQL compiler | [README](../../SourceFlow.Storage.SqlServer-README.md) |
| `SourceFlow.Storage.EntityFrameworkCore` | EF Core read models in the storage session | [README](../../SourceFlow.Storage.EntityFrameworkCore-README.md) |
| `SourceFlow.Cloud.Aws` | SQS/SNS transport, provisioner, AWS KMS key provider, v2 bridge | [README](../../SourceFlow.Cloud.Aws-README.md) |
| `SourceFlow.Cloud.Azure` | Service Bus transport, provisioner, Key Vault key provider | [README](../../SourceFlow.Cloud.Azure-README.md) |
| `SourceFlow.Cloud.Gcp` | Pub/Sub transport, provisioner, Cloud KMS key provider | [README](../../SourceFlow.Cloud.Gcp-README.md) |
| `SourceFlow.AspNetCore` | `MapCommand`, the query API and domain queries over HTTP | [README](../../SourceFlow.AspNetCore-README.md) |
| `SourceFlow.Testing` | Test host, specs, approvals, conformance kits, chaos injector | [README](../../SourceFlow.Testing-README.md) |
| `SourceFlow.Studio` | Manifests, workspace, workflow DSL model, validator, simulator, exporters | [README](../../SourceFlow.Studio-README.md) |
| `SourceFlow.Tool` (`dotnet sourceflow`) | The CLI and the Studio local host | [README](../../SourceFlow.Tool-README.md) |

Studio's browser packages are versioned separately: `@sourceflow/studio`, `@sourceflow/core-client` and the
`@sourceflow/workflow-diff` action (0.1.0 in the repository).

## Known limitations and deferred items

### Not in 3.0

| Item | Plan |
|------|------|
| Dynamic consistency boundaries API (tags are stored already) | 3.1 ([ADR-021](../../../ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md)) |
| Stream compaction | 3.1 |
| RabbitMQ and Kafka transports; an event gateway to Kafka | 3.1 |
| SFQL `sfql/1.1` (window functions) | 3.1, if demand is confirmed |
| Database-per-tenant; KurrentDB adapter; claim-check for large payloads; GraphQL over read models; SourceFlow Console | 3.x |
| Studio observe mode (live overlay) | Studio 1.1 |
| Envelope signatures (`sfsig`), a relay circuit breaker, per-type receive size limits, an event checksum job | 3.x candidates |
| SQL Server row-level security and `[Queryable]` index sync | Not in 3.0 |
| SQL Server append-only grants script | Open item (O-3) |
| Per-tenant metric tags | Not built |
| OpenTelemetry `settle` span and baggage propagation | Not built |
| Crypto-shredding of personal data in workflow state | Not built |
| SFQL `from events` decrypting personal data | Fields are returned as encrypted markers |
| Analyzer SF0012 (process manager event without correlation) | Designed, not built |

### Release readiness

These items from [STATUS.md](../../../implementation/STATUS.md) and the
[security review](../../../implementation/security-review.md) are open at the time of writing:

- the first weekly 2-hour libFuzzer run per target (O-1);
- the NuGet signing certificate and the nuget.org key (O-2);
- the maintainer's sign-off of the security review (O-6);
- five consecutive green nights per cloud for the real-cloud legs;
- the PERF-09 acceptance run (5,000 commands/s per 4 vCPU) on a database sized as design doc 09 assumes;
- for Studio 1.0: the workflow end-to-end scenarios on the three real clouds, and publishing `@sourceflow/studio` and the
  `workflow-diff` action;
- the M6 hardening and release-candidate programme (beyond the 63 work packages);
- Track C, the design-conformance remediation ([plan 11](../../../implementation/11-Design-Conformance-Remediation-Plan.md)):
  the P0 security packages before the release candidate, P1 before GA, P2 before Studio 1.0. Done so far: WP-C.1
  (subqueries are policy-checked; per-query authorization for domain queries), WP-C.2 (row-level security on every
  events partition and on `data_keys`; the partition detach check sees every tenant), WP-C.3 (`[PersonalData]` on
  positional parameters and inside collections; unsupported shapes fail startup; a bounded key cache for erasure across
  nodes; the erasure audit records the actor; `LocalKeyProvider` needs `AllowLocalKeyProvider()` outside Development),
  WP-C.4 (`MapCommand` refuses a body that contradicts the route; continuation tokens are validated; no request text in
  SQL; query API status codes as doc 14 §9.1: 413, 503 with `Retry-After`, 429; Studio host file checks), WP-C.5 (every
  outbound message goes through the outbox: a command sent inside a handler joins its transaction, a routed API send and
  a dead-letter replay are written in a session of their own, and only the relay sends to the transport), WP-C.6 (the
  receive path: a draining shutdown through `IHostedLifecycleService`; per-key order kept while a head waits for a
  delayed retry; `InboxRetention` defaults to 14 days and is checked against the broker's retention; delays longer than
  the broker's maximum go through the outbox with the new `sfattempt` extension; failed settlements and receives no
  longer hold a key or an Azure session; the relay renews its lease while draining and publishes in batches;
  TCK-46..49), WP-C.7 (fenced writes lock the lease row, so a paused former owner can't commit after a takeover; SQL
  Server's single-document upsert keeps the newer version under concurrency; process managers and handlers back off
  before parking, and park once across a crash; an append after a flush or raw connection access is refused), WP-C.8
  (ids that can't be read back from a stream id are refused; schema fingerprints cover structure only, so renaming a
  type or adding an upcast version keeps them, with a one-time migration of stored fingerprints; an id's backing has one
  source and SF0022 checks it; cross-stream documents record their position and skip events already applied; schema
  PostgreSQL V008 and SQL Server V003).

### Other notes

- `SourceFlow.Tool` ships as a .NET tool (`dotnet sourceflow`) with SourceFlow Studio embedded; services can also
  reference it to run the CLI in process.
- Personal-data keys are per tenant (PostgreSQL migration V006, SQL Server V002): erasing a subject in one tenant leaves
  the same subject id in other tenants untouched. Markers written before this change (`v1`) still read.
- `sourceflow db migrate` inside a service uses the service's own tenant isolation, so it cannot switch row-level security
  off by mistake.
- Playwright runs the Studio end-to-end tests on Chromium only.
- On a shared development machine, PostgreSQL saturates at about 2,700 commands/s; see the
  [load test report](../../../implementation/load-test-report.md).

## Work packages

All 63 work packages are done (138.5 of 138.5 engineer-weeks).

| Milestone | Work packages | Scope |
|-----------|---------------|-------|
| M0 Foundations | 5 | Branch and versioning, repository scaffold, CI, quality harness, design baseline |
| M1 Core | 15 | Contracts, generator and analyzers, messaging, storage SPI, routing, command processing, middleware, outbox and transport SPI, subscriptions and process managers, hosting, testing toolkit, schema catalog, SFQL front end, Bank domain (now a test fixture), v2 core retired |
| M2 PostgreSQL | 11 | Schema and migrator, append, global log, outbox and friends, snapshots and cache, row-level security, EF Core read models and blue/green generations, SFQL on PostgreSQL, storage kit, performance baseline, v2 EF stores retired |
| M3 Cloud transports | 7 | Final SPI and TCK, AWS, Google Cloud, Azure, provisioning and IaC, cross-service end-to-end, v2 cloud providers retired |
| M4 Tooling and operations | 7 | CLI, ASP.NET Core and the query API, health checks and dashboards, SQL Server, partitioning and archiving, OpenTelemetry, chaos and load tests |
| M5 Security and migration | 5 | Payload protection and KMS providers, crypto-shredding, redaction, v2 migration and bridge, fuzzing and security review |
| Track S: Workflow DSL and Studio | 13 | Manifests, design-time export, DSL core, simulator and exporters, code generation, workflow operations, WebAssembly core, Studio shell, views, editing, simulation UI, exports and PR diagrams, OrderFulfilment services (now a test fixture) |

The per-package detail, including deviations from the design, is in [STATUS.md](../../../implementation/STATUS.md).

## Related documentation

- [Documentation index](../../README.md) and [wiki](../../wiki.md)
- [Migrating from v2](../../../guides/migrating-from-v2.md)
- [Architecture](../../Architecture/README.md)
- [Design documents](../../../Design/README.md) and [ADRs](../../../ADRs/README.md)
- [Implementation plans](../../../implementation/README.md)
- [v2.0.0 changelog](../../../../legacy-version/Versions/v2.0.0/CHANGELOG.md)
