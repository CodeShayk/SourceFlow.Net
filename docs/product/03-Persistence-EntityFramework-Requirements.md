# 03 — Persistence Requirements (`SourceFlow.Stores.EntityFramework`)

Source root: [`src/SourceFlow.Stores.EntityFramework`](../../src/SourceFlow.Stores.EntityFramework). Targets net8.0, net9.0 and net10.0 on EF Core 9. The status legend is in the [README](README.md#status-values).

---

## 1. Store implementations

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| EF-01 | The package shall implement `ICommandStore`, `IEntityStore` and `IViewModelStore` on EF Core, each with its own `DbContext` (`CommandDbContext`, `EntityDbContext`, `ViewModelDbContext`). | Implemented | [Stores/](../../src/SourceFlow.Stores.EntityFramework/Stores) |
| EF-02 | `EfCommandStore.Append` shall insert one row per command. `Load(entityId)` shall return that entity's commands in ascending `SequenceNo` order, read without change tracking. | Implemented | [EfCommandStore.cs](../../src/SourceFlow.Stores.EntityFramework/Stores/EfCommandStore.cs) |
| EF-03 | The command table shall enforce a **unique (EntityId, SequenceNo)** index, so two writers cannot both append the same sequence number. It shall also index `EntityId` and `Timestamp`. | Implemented | [CommandRecord.cs](../../src/SourceFlow.Stores.EntityFramework/Models/CommandRecord.cs) |
| EF-04 | `EfEntityStore` and `EfViewModelStore` shall `Persist` as an upsert (update if the id exists, insert if not), reject ids ≤ 0 on `Get` and `Delete`, and throw `InvalidOperationException` when a requested row does not exist. | Implemented | [EfEntityStore.cs](../../src/SourceFlow.Stores.EntityFramework/Stores/EfEntityStore.cs), [EfStoreBase.cs](../../src/SourceFlow.Stores.EntityFramework/Stores/EfStoreBase.cs) |
| EF-05 | Entity and view-model types shall be mapped at runtime, without a hand-written `DbContext`. They are registered explicitly (`RegisterEntityType<T>()`, `RegisterViewModelType<T>()`) or by assembly scan (`RegisterAssembly`), keyed on `Id`. | Implemented — the registrations are **process-wide static** state ([GAP-17](06-Gaps-Risks-and-Open-Questions.md#gap-17)) | [EntityDbContext.cs](../../src/SourceFlow.Stores.EntityFramework/EntityDbContext.cs), [ViewModelDbContext.cs](../../src/SourceFlow.Stores.EntityFramework/ViewModelDbContext.cs) |
| EF-06 | A helper shall create tables for runtime-registered entity and view-model types. | Partial — `DbContextMigrationHelper` emits `CREATE TABLE IF NOT EXISTS "…"`, which works on SQLite and PostgreSQL but not SQL Server. It also ignores the table-naming convention and swallows errors | [DbContextMigrationHelper.cs](../../src/SourceFlow.Stores.EntityFramework/Migrations/DbContextMigrationHelper.cs) |

## 2. Configuration and database providers

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| EF-10 | `AddSourceFlowEfStores(connectionString)` shall register all three stores on one SQL Server connection string. | Implemented | [ServiceCollectionExtensions.cs](../../src/SourceFlow.Stores.EntityFramework/Extensions/ServiceCollectionExtensions.cs) |
| EF-11 | `AddSourceFlowEfStores(commandCs, entityCs, viewModelCs)` shall let each store use its own SQL Server database. | Implemented | same |
| EF-12 | `AddSourceFlowEfStores(IConfiguration)` and `AddSourceFlowEfStores(Action<SourceFlowEfOptions>)` shall bind connection strings, naming, resilience and observability from configuration or code. A per-store connection string overrides `DefaultConnectionString`, and a store with neither shall fail with a clear error. | Implemented | [SourceFlowEfOptions.cs](../../src/SourceFlow.Stores.EntityFramework/Options/SourceFlowEfOptions.cs) |
| EF-13 | `AddSourceFlowEfStoresWithCustomProvider(Action<DbContextOptionsBuilder>)` shall support any EF Core provider (PostgreSQL, SQLite, MySQL and so on) for all stores. | Implemented | same |
| EF-14 | `AddSourceFlowEfStoresWithCustomProviders(cmd, entity, viewModel)` shall allow a **different provider per store** (for example, commands on PostgreSQL and view models on SQL Server). | Implemented | same |
| EF-15 | Table names shall be configurable per store: casing (PascalCase, camelCase, snake_case, lowercase or UPPERCASE), pluralisation, prefix, suffix, and an optional schema. | Implemented | [TableNamingConvention.cs](../../src/SourceFlow.Stores.EntityFramework/Options/TableNamingConvention.cs) |

## 3. Resilience and observability

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| EF-20 | Every store operation shall run through a Polly pipeline with a configurable timeout (default 30 s), retry (default 3 attempts, exponential backoff with jitter, 1 s base, 30 s cap) and circuit breaker (5 failures, 30 s break, 2 successes to close). Each stage can be switched off. | Implemented | [DatabaseResiliencePolicy.cs](../../src/SourceFlow.Stores.EntityFramework/Services/DatabaseResiliencePolicy.cs), [ResilienceOptions.cs](../../src/SourceFlow.Stores.EntityFramework/Options/ResilienceOptions.cs) |
| EF-21 | Retries shall cover transient failures only. A constraint violation (such as a duplicate `(EntityId, SequenceNo)`) shall not be retried. | Partial — the retry predicate includes every `DbUpdateException`, constraint violations included ([GAP-05](06-Gaps-Risks-and-Open-Questions.md#gap-05)) | `DatabaseResiliencePolicy` |
| EF-22 | Store operations shall emit OpenTelemetry spans (`sourceflow.ef.*`) and metrics. SQL text shall be left out of traces by default (`IncludeSqlInTraces = false`). | Implemented | [DatabaseTelemetryService.cs](../../src/SourceFlow.Stores.EntityFramework/Services/DatabaseTelemetryService.cs), [ObservabilityOptions.cs](../../src/SourceFlow.Stores.EntityFramework/Options/ObservabilityOptions.cs) |

## 4. Idempotency store (cloud support)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| EF-30 | `AddSourceFlowIdempotency(connectionString, cleanupIntervalMinutes = 60)` shall register a SQL Server–backed `IIdempotencyService` that several service instances can share. | Implemented | [EfIdempotencyService.cs](../../src/SourceFlow.Stores.EntityFramework/Services/EfIdempotencyService.cs) |
| EF-31 | `AddSourceFlowIdempotencyWithCustomProvider(configure, interval)` shall support any EF provider for the idempotency table. | Implemented | `ServiceCollectionExtensions` |
| EF-32 | The idempotency table shall be keyed on `IdempotencyKey` and indexed on `ExpiresAt`. `HasProcessed` shall ignore expired records. | Implemented | [IdempotencyDbContext.cs](../../src/SourceFlow.Stores.EntityFramework/IdempotencyDbContext.cs) |
| EF-33 | `MarkAsProcessed` shall be an upsert. A concurrent insert of the same key by another instance shall be treated as success, not as an error. | Implemented | `EfIdempotencyService.MarkAsProcessedAsync` |
| EF-34 | A hosted cleanup service shall delete expired idempotency records on the configured interval, in batches of up to 1,000. | Implemented | [IdempotencyCleanupService.cs](../../src/SourceFlow.Stores.EntityFramework/Services/IdempotencyCleanupService.cs) |
| EF-35 | The core package shall be able to turn on EF idempotency without a compile-time reference (`IdempotencyConfigurationBuilder.UseEFIdempotency`), and shall fail with an install hint when the EF package is missing. | Implemented — through reflection | [IdempotencyConfigurationBuilder.cs](../../src/SourceFlow/Cloud/Configuration/IdempotencyConfigurationBuilder.cs) |

---

## Acceptance scenarios (persistence)

From [`tests/SourceFlow.Stores.EntityFramework.Tests`](../../tests/SourceFlow.Stores.EntityFramework.Tests), run on SQLite.

1. **Ordering.** When commands with sequence numbers 1..N are appended for entity X, `Load(X)` returns exactly those N, in order, and none for other entities.
2. **Uniqueness.** When a second command is appended with the same `(EntityId, SequenceNo)`, the append fails and no duplicate row exists.
3. **Upsert.** When `Persist` is called on an entity whose id already exists, the row is updated, not duplicated.
4. **End to end.** The bank-account scenario from [doc 02](02-Core-Framework-Requirements.md#acceptance-scenarios-core) passes with all three stores backed by EF, including replay.
5. **Idempotency.** After `MarkAsProcessed(k, ttl)`, `HasProcessed(k)` returns true until `ttl` passes and false afterwards. Cleanup removes the expired row.
