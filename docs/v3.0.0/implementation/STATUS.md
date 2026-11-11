# v3 implementation status

Updated in the PR that completes a work package (CONTRIBUTING.md). Status values: `todo`, `in progress`, `done`.

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
| WP-1.12 | Schema catalog | 2 | todo | |
| WP-1.13 | SFQL front end (pipeline + SQL syntax) + in-memory interpreter + domain queries | 5.5 | in progress | Done: request guard, lexer, both parsers into one AST (UT-QL-01..13, 61..65, 69..77); catalog from the runtime model (Q-B1), binder with per-version renames, typing, output naming, G-6 tie-break, G-10/G-11/G-12 (SFQ0201–0206; UT-QL-21..33, 79, 88); policy: permissions, PII keys, cost guard, join and offset rules, limits (SFQ0301, 0401, 0402, 0405, 0406, 0409, 0501; UT-QL-34..39, 80, 89); in-memory interpreter (SQL three-valued logic, hash joins, grouping, subqueries, aggregate as-of folds, schema rows) and `IQueryService` (typed parameters SFQ0202/0203, PII redaction, tenancy, keyset continuation tokens SFQ0601, `at_least` consistency waits SFQ0602, plan cache, `IQueryEngine` SPI for provider compilers; UT-QL-41..60). PostgreSQL compiler: WP-2.8. Next: the typed builder and `[DomainQuery]` |
| WP-1.14 | Bank sample (ported v2 E2E domain) | 1.5 | done | Bank.Contracts/Accounts/Transfers/Notifications + E2E-01..07, 11, 13, 14, 17..20 green; Bank.Api host (commands, demo query endpoints), Studio demo console, docker-compose demo and ghcr.io images (`v3-images` workflow) |
| WP-1.R | Retire v2 core | 0.5 | todo | |
| WP-2.1 | PostgreSQL schema and migrator | 1.5 | done | V001 schema (ADR-028: json payload columns, tenant columns), PostgreSqlSchemaMigrator (advisory lock, checksummed schema_history, db script), UsePostgreSql with AutoMigrate (Development only by default); SCK-M01..M06. V002 (RLS) and V003 (partitioned events) come with WP-2.6 and WP-4.5 |
| WP-2.2 | PostgreSQL append and stream read | 2 | done | Write-behind session (one NpgsqlBatch, append first; ES-R1/ES-R2), CTE append for all ExpectedVersion kinds, conflict and closed-stream diagnosis, single-statement stream read; SCK-01..19 green. Commit is batch + COMMIT (two round trips; folding COMMIT in is WP-2.10) |
| WP-2.3 | Global log reader and notifications | 1.5 | done | (tx_id, seq) read below pg_snapshot_xmin with bucket filter, inclusive horizon, GetHorizonAgeAsync; LISTEN/NOTIFY with origin filtering, reconnect, ListenConnectionString; SCK-21..28 incl. SCK-24 (32 writers) and the held-horizon test. Wiring horizon_age into metrics/health is WP-4.3 |
| WP-2.4 | Outbox, inbox, checkpoints, leases, dead letters, process state, documents | 2.5 | done | Head-of-key claim, fenced complete/retry, database-clock leases, fenced checkpoint upsert, inbox with stored result, process state CAS, versioned documents, dead letters with the full envelope; SCK-29..58 green. Batched inbox/outbox retention cleanup pending (with WP-4.3 operations) |
| WP-2.5 | Snapshots and aggregate cache | 1.5 | done | Background snapshot writer (bounded, drop-oldest, drained on shutdown), MemoryCache aggregate cache (ReadTail, Optimistic with rejection confirmation and no shortcut for If-Match), cache and snapshot metrics; UT-SNAP-01..10, SCK-51..53/55 on in-memory and PostgreSQL (with and without RLS), parallel deposits on PostgreSQL in both cache modes. PERF-04 moves to the WP-2.10 baseline |
| WP-2.6 | Tenancy with row-level security | 1 | done | V002 tenant_isolation policies; TenantIsolation.RowLevelSecurity enables and forces RLS (migrator applies and verifies the state); session-scoped set_config; system (BYPASSRLS) data source for the global log; tenant-aware buckets. The whole SCK passes under RLS through a DML-only role; SCK-59/60/M07, Bank transfer E2E under RLS. Per-tenant metric tags (bounded top-N) move to WP-4.3 |
| WP-2.7 | `SourceFlow.Storage.EntityFrameworkCore` read models | 2 | in progress | Core session-projection SPI (ISessionProjection, IProjectionUnitOfWork, IStorageSession.FlushAsync for ES-R1), generator support (GEN-41/42), IEfProjection, EfSessionContextFactory on the session connection and transaction, UseEntityFrameworkProjections, IdempotentUpsert; UT-EF-01..04, SCK-61..64 inline and async on PostgreSQL. Rebuild with schema-per-generation waits for the blue/green rebuild tooling (doc 06 §6) |
| WP-2.8 | SFQL on PostgreSQL (incl. joins and grouping) + index sync + domain queries | 3.5 | in progress | PostgreSqlQueryEngine (`IQueryEngine`): one parameterised statement per page (every request value a typed `$n` parameter, I-3), keyset predicate and ORDER BY outside a derived table, `COLLATE "C"` text ordering, horizon predicate, per-version rename CASE, tenant predicates (G-9), `NULLIF` division, read-only transaction with `statement_timeout` (`QueryTimeout`, default 5 s) and the RLS tenant setting; aggregate and schema sources stay in the interpreter. Cross-provider equivalence with the interpreter on the same data (QRY-01..20, incl. joins, grouping/having, distinct, subqueries, keyset pages interchangeable between engines), golden SQL (QRY-42/43), injection corpus (QRY-44), no-literal CsCheck property (QRY-60). Catalog now types enums by their JSON (number or string). Next: index sync (`db index sync`) and `[DomainQuery]` |
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
| WP-4.2 | `SourceFlow.AspNetCore` (incl. the per-service query API) | 2.5 | in progress | MapCommand (Idempotency-Key, If-Match, ETag, consistency token, ProblemDetails) done, UT-ASP-01..06; query endpoints wait for WP-1.13 |
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
