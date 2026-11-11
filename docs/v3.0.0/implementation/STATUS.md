# v3 implementation status

Updated in the PR that completes a work package (CONTRIBUTING.md). Status values: `todo`, `in progress`, `done`.

## Progress summary

Updated 2026-11-11. **28 of 63 work packages are done (44%)**, 2 in progress and 33 not started. By estimated effort, 56 of 138.5 engineer-weeks are done (40%).

| Milestone | Done | In progress | Todo | Effort done (ew) |
|-----------|------|-------------|------|------------------|
| M0 Foundations | 5 | 0 | 0 | 4 of 4 |
| M1 Core | 14 | 0 | 1 | 36.5 of 37 |
| M2 PostgreSQL | 8 | 2 | 1 | 13 of 19 |
| M3 Cloud transports | 0 | 0 | 7 | 0 of 19 |
| M4 Tooling and operations | 1 | 0 | 6 | 2.5 of 16.5 |
| M5 Security and migration | 0 | 0 | 5 | 0 of 11 |
| Track S: Workflow DSL and Studio | 0 | 0 | 13 | 0 of 32 |

Completed in this round:

- WP-1.2: analyzers SF0005 and SF0014, the SF0010 code fix, and text `CorrelateBy`.
- WP-1.9: `CommandRejected<T>` replies (E2E-15) and the node registry with lease fair-share.
- WP-1.12: the schema catalog (ADR-029). This covers JSON Schema per type, fingerprints, startup registration that fails on a changed published version, compatibility rules SFC01–SFC10, and AsyncAPI export.
- WP-1.13: named domain queries (ADR-030), the typed builder, and analyzers SF0015, SF0016 and SF0018.
- WP-1.14: the Bank.Api host, the Studio demo console, the docker-compose demo, and images on ghcr.io.
- WP-4.2: the production query API and domain queries over HTTP.

Still in progress:

- WP-2.7 (`SourceFlow.Storage.EntityFrameworkCore` read models): rebuilding a projection into a new schema, which waits for the blue/green rebuild tooling.
- WP-2.8 (SFQL on PostgreSQL): index sync (`db index sync`).

Not started: the v2 retirements (1.R, 2.R, 3.R), every cloud transport (M3), the CLI, health and dashboards, SQL Server, event partitioning, chaos and load testing, payload protection, crypto-shredding, the v2 migration tool, and the whole Studio track.

| WP | Title | Estimate (ew) | Status | Notes |
|----|-------|---------------|--------|-------|
| WP-0.1 | Branch and versioning | 0.5 | done | |
| WP-0.2 | Repository scaffold | 1 | done | |
| WP-0.3 | CI pipelines (skeleton) | 1 | done | |
| WP-0.4 | Quality harness | 1 | done | |
| WP-0.5 | Design baseline | 0.5 | done | |
| WP-1.1 | Core contracts and SPIs (`SourceFlow.Net`) | 1.5 | done | Contracts in namespace SourceFlow; UT-ABS-01..18 |
| WP-1.2 | `SourceFlow.Generators` (generators + analyzers) | 4 | done | Generator (ids, aggregates, PMs incl. text `CorrelateBy`, projections, session projections, module); analyzers SF0001–SF0010 and SF0014 (routing `Command<T>().To(…)` for a locally owned command), SF0010 code fix (adds `[JsonSerializable]`); GEN-01..42 (GEN-13, 28, 35, 36 added). Guide: `docs/v3.0.0/guides/reading-generated-code.md`. SF0015/16/18 come with WP-1.13 |
| WP-1.3 | Messaging core | 2 | done | Envelope, CloudEvents, UUIDv5/v7, registry, serializer, upcasters; UT-MSG-01..28 |
| WP-1.4 | Storage SPI + in-memory store | 2.5 | done | Storage SPI, InMemoryStore, SCK (49 cases) green |
| WP-1.5 | Runtime model and routing | 1.5 | done | RoutingBuilder/RoutingTable (RT-R1, RT-R5), options + validators; UT-RT |
| WP-1.6 | Command processing + temporal reads | 3 | done | CommandProcessor, CommandSender, AggregateReader (AsOf), snapshots (interval, schema hash); the aggregate cache landed with WP-2.5 |
| WP-1.7 | Middleware and diagnostics | 1.5 | done | MessagePipeline, built-in middleware, ErrorClassifier, SourceFlowMetrics with InstrumentAdvice; UT-MW |
| WP-1.8 | Outbox, scheduler, transport SPI, in-memory broker, receive pipeline | 3.5 | done | Transport SPI, InMemoryBroker, OutboxRelay, ReceiveEndpoint, dead letters; TCK-01..12 green on the in-memory broker |
| WP-1.9 | Subscriptions, projections, process managers, event handlers | 4 | done | SubscriptionEngine (fenced, partitioned, adaptive batches, park policy with per-event isolation), projections, process managers (incl. `CommandRejected<T>` replies: `sf-reply-to` header for local owners and `ReplyOnReject()` routes, reply committed with the rejection, ADR-018), event handlers; `INodeRegistry` heartbeat + fair-share rebalancer (one lease released per pass, graceful hand-back on stop). UT-SUB-01/05/06/08/09/10/12/17, UT-PRJ-01, UT-PM-01/10..13, E2E-09..15. Repartition and blue/green rebuild (UT-SUB-21..28) and the `projection skip` Stop policy come with the CLI (WP-4.1) |
| WP-1.10 | Hosting, roles, tenancy context | 1.5 | done | AddSourceFlow, SourceFlowBuilder Use* slots, roles, SourceFlowHost lifecycle, health check |
| WP-1.11 | `SourceFlow.Testing` | 2.5 | done | SourceFlowTestHost, AggregateSpec, ProcessManagerSpec, ProjectionSpec, approvals; UT-TST |
| WP-1.12 | Schema catalog | 2 | done | JSON Schema 2020-12 per message type and version, read-model document and process state, derived from the serializer's JsonTypeInfo with `x-sourceflow-*` extensions (ADR-029); canonical text + SHA-256 fingerprints; `ISchemaStore` (in-memory, PostgreSQL `schemas`) registered at startup, a changed published `(type, version)` fails startup (`SchemaRegistration` = Enforce/Warn/Off); lock file; `SchemaCompatibility` rules SFC01–SFC10; AsyncAPI 3.0 exporter. UT-CAT-01..18 |
| WP-1.13 | SFQL front end (pipeline + SQL syntax) + in-memory interpreter + domain queries | 5.5 | done | Request guard, lexer, both parsers into one AST; catalog, binder, policy (SFQ0101–0602); in-memory interpreter and `IQueryService` (typed parameters, PII redaction, tenancy, keyset continuation, `at_least` waits, plan cache, `IQueryEngine` SPI); named domain queries (`[DomainQuery]`, generated runtime, `ExecuteAsync`, startup binding, ADR-030); typed builder (`Events<T>()`, `ReadModel<T>()`); analyzers SF0015/SF0016/SF0018. UT-QL-01..95, GEN-47..50; PostgreSQL compiler in WP-2.8 |
| WP-1.14 | Bank sample (ported v2 E2E domain) | 1.5 | done | Bank.Contracts/Accounts/Transfers/Notifications + E2E-01..07, 11, 13, 14, 17..20 green; Bank.Api host (commands, demo query endpoints), Studio demo console, docker-compose demo and ghcr.io images (`v3-images` workflow) |
| WP-1.R | Retire v2 core | 0.5 | todo | |
| WP-2.1 | PostgreSQL schema and migrator | 1.5 | done | V001 schema (ADR-028: json payload columns, tenant columns), PostgreSqlSchemaMigrator (advisory lock, checksummed schema_history, db script), UsePostgreSql with AutoMigrate (Development only by default); SCK-M01..M06. V002 (RLS) and V003 (partitioned events) come with WP-2.6 and WP-4.5 |
| WP-2.2 | PostgreSQL append and stream read | 2 | done | Write-behind session (one NpgsqlBatch, append first; ES-R1/ES-R2), CTE append for all ExpectedVersion kinds, conflict and closed-stream diagnosis, single-statement stream read; SCK-01..19 green. Commit is batch + COMMIT (two round trips; folding COMMIT in is WP-2.10) |
| WP-2.3 | Global log reader and notifications | 1.5 | done | (tx_id, seq) read below pg_snapshot_xmin with bucket filter, inclusive horizon, GetHorizonAgeAsync; LISTEN/NOTIFY with origin filtering, reconnect, ListenConnectionString; SCK-21..28 incl. SCK-24 (32 writers) and the held-horizon test. Wiring horizon_age into metrics/health is WP-4.3 |
| WP-2.4 | Outbox, inbox, checkpoints, leases, dead letters, process state, documents | 2.5 | done | Head-of-key claim, fenced complete/retry, database-clock leases, fenced checkpoint upsert, inbox with stored result, process state CAS, versioned documents, dead letters with the full envelope; SCK-29..58 green. Batched inbox/outbox retention cleanup pending (with WP-4.3 operations) |
| WP-2.5 | Snapshots and aggregate cache | 1.5 | done | Background snapshot writer (bounded, drop-oldest, drained on shutdown), MemoryCache aggregate cache (ReadTail, Optimistic with rejection confirmation and no shortcut for If-Match), cache and snapshot metrics; UT-SNAP-01..10, SCK-51..53/55 on in-memory and PostgreSQL (with and without RLS), parallel deposits on PostgreSQL in both cache modes. PERF-04 moves to the WP-2.10 baseline |
| WP-2.6 | Tenancy with row-level security | 1 | done | V002 tenant_isolation policies; TenantIsolation.RowLevelSecurity enables and forces RLS (migrator applies and verifies the state); session-scoped set_config; system (BYPASSRLS) data source for the global log; tenant-aware buckets. The whole SCK passes under RLS through a DML-only role; SCK-59/60/M07, Bank transfer E2E under RLS. Per-tenant metric tags (bounded top-N) move to WP-4.3 |
| WP-2.7 | `SourceFlow.Storage.EntityFrameworkCore` read models | 2 | in progress | Core session-projection SPI (ISessionProjection, IProjectionUnitOfWork, IStorageSession.FlushAsync for ES-R1), generator support (GEN-41/42), IEfProjection, EfSessionContextFactory on the session connection and transaction, UseEntityFrameworkProjections, IdempotentUpsert; UT-EF-01..04, SCK-61..64 inline and async on PostgreSQL. Rebuild with schema-per-generation waits for the blue/green rebuild tooling (doc 06 §6) |
| WP-2.8 | SFQL on PostgreSQL (incl. joins and grouping) + index sync + domain queries | 3.5 | in progress | PostgreSqlQueryEngine (`IQueryEngine`): one parameterised statement per page (every request value a typed `$n` parameter, I-3), keyset predicate and ORDER BY outside a derived table, `COLLATE "C"` text ordering, horizon predicate, per-version rename CASE, tenant predicates (G-9), `NULLIF` division, read-only transaction with `statement_timeout` (`QueryTimeout`, default 5 s) and the RLS tenant setting; aggregate and schema sources stay in the interpreter. Cross-provider equivalence with the interpreter on the same data (QRY-01..20, incl. joins, grouping/having, distinct, subqueries, keyset pages interchangeable between engines), golden SQL (QRY-42/43), injection corpus (QRY-44), no-literal CsCheck property (QRY-60). Catalog now types enums by their JSON (number or string). Domain queries run through the engine (ExecuteAsync and `/queries`). `/query/explain` shows the parameterised SQL. Next: index sync (`db index sync`) |
| WP-2.9 | Storage conformance kit complete | 2 | done | SCK-01..58 in SourceFlow.Testing, run on in-memory, PostgreSQL and PostgreSQL with RLS. New in this package: SCK-23 (in-flight writes invisible), SCK-37 (batched retention via IStoreRetention, plus the host retention loop), SCK-54 (CsCheck property: snapshot + tail = full fold), PostgreSQL SCK-28 (LISTEN loss and reconnect), and SCK-L01 CsCheck linearisability of appends and the outbox. SCK-36 is covered by SCK-34. SCK-20 (one round trip) moves to WP-2.10, and SCK-65..72 (partitioned events) to WP-4.5 |
| WP-2.10 | Performance baseline | 1 | done | benchmarks/SourceFlow.Benchmarks (BenchmarkDotNet PERF-01..04, 06; measurement harness PERF-05, 08, 10); baselines in benchmarks/baselines and the report in implementation/perf-baseline.md. One-round-trip PostgreSQL commit (SCK-20), batch-aware document projections and adaptive subscription batches. Deviations logged with owners (PERF-01/02 allocations, PERF-10 borderline). PERF-07 waits for M3 and PERF-09 for WP-4.7 |
| WP-2.R | Retire v2 EF stores | 0.5 | todo | |
| WP-3.1 | Transport SPI final + Transport Conformance Kit | 3 | todo | |
| WP-3.2 | `SourceFlow.Cloud.Aws` | 4 | todo | |
| WP-3.3 | `SourceFlow.Cloud.Gcp` | 3.5 | todo | |
| WP-3.4 | `SourceFlow.Cloud.Azure` | 4 | todo | |
| WP-3.5 | Provisioning, topology export, DLQ ingestor, IaC modules | 2.5 | todo | |
| WP-3.6 | Cross-service E2E per transport | 1.5 | todo | |
| WP-3.R | Retire v2 cloud providers | 0.5 | todo | |
| WP-4.1 | `SourceFlow.Tool` (`dotnet sourceflow`) | 3 | todo | |
| WP-4.2 | `SourceFlow.AspNetCore` (incl. the per-service query API) | 2.5 | done | MapCommand (Idempotency-Key, If-Match, ETag, consistency token, ProblemDetails); `AddSourceFlowQueryApi` + `MapSourceFlowQueryApi` (`/query`, `/validate`, `/explain`, `/schema`) and `MapDomainQueries` (`/queries`, `/queries/{name}/v{n}`): fail-closed without an authorization decision (SFQ0504, checked when the pipeline is built), transport guard (64 KB, JSON only, depth 16 → 413/415/400), per-principal rate and concurrency limits and a service limit (SFQ0502/0503, Retry-After), permissions and tenant from claims, problem+json without input echo, security headers, NDJSON with trailer, CSV with formula neutralisation, OpenAPI metadata. UT-ASP-01..06, 21..36. The Bank demo now uses it (anonymous only by explicit opt-in). QRY-53..56 (API end to end on PostgreSQL) and the nightly DAST scan remain with WP-4.7 |
| WP-4.3 | Health checks, dashboards and alerts | 2 | todo | |
| WP-4.4 | `SourceFlow.Storage.SqlServer` + SFQL SQL Server compiler | 4 | todo | |
| WP-4.5 | `PartitionEvents` + stream archiving | 1.5 | todo | |
| WP-4.6 | OpenTelemetry integration (no package) | 1 | todo | |
| WP-4.7 | Chaos suite + tier-M load test | 2.5 | todo | |
| WP-5.1 | Payload protection + KMS key providers | 3 | todo | |
| WP-5.2 | Crypto-shredding | 3 | todo | |
| WP-5.3 | Redaction wiring | 0.5 | todo | |
| WP-5.4 | v2 migration tool + v2 wire bridge | 3 | todo | |
| WP-5.5 | Fuzzing + security review | 1.5 | todo | |
| WP-S.1 | Static manifest emission | 2 | todo | |
| WP-S.2 | Design-time manifest export + workspace | 1.5 | todo | |
| WP-S.3 | DSL core: model, reader, schema, validator | 3.5 | todo | |
| WP-S.4 | Simulator, path enumerator, spec generator, exporters | 3 | todo | |
| WP-S.5 | Workflow code generation + runtime abstractions | 4 | todo | |
| WP-S.6 | Runtime support: metrics, CLI, version guard | 1.5 | todo | |
| WP-S.7 | WASM core + TypeScript facade | 1.5 | todo | |
| WP-S.8 | Studio shell + local host | 2.5 | todo | |
| WP-S.9 | Views: domain map, statechart, swimlane | 3.5 | todo | |
| WP-S.10 | Editing and round trip | 3 | todo | |
| WP-S.11 | Simulation, path explorer, spec generation UI | 2 | todo | |
| WP-S.12 | Exports, static build, PR diagram action | 2 | todo | |
| WP-S.13 | OrderFulfilment sample, docs, hardening | 2 | todo | |

## Notes

- WP-0.4: the benchmark harness lands with the first measurable code (WP-1.3 serializer benchmarks); the coverage gate is `tools/CoverageGate` with `thresholds.json`.
- WP-0.5: GitHub issues per WP are opened by the maintainers; this table is the in-repo tracker.
- Snapshot approvals use `SourceFlow.Testing.Approvals` (approved files), not Verify (licence decision pending).
