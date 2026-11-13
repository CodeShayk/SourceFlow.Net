# SourceFlow.Storage.SqlServer

The SQL Server storage adapter for SourceFlow.Net v3. `SqlServerStore` implements the same persistence SPI as the PostgreSQL reference adapter: the event store and a gap-free, commit-ordered global log, storage sessions with write-behind batching, the transactional outbox and inbox, fenced checkpoints and leases, dead letters, process-manager state, snapshots, read-model documents, blue/green read-model aliases, the schema registry, node liveness and crypto-shredding keys. It is written in T-SQL over `Microsoft.Data.SqlClient`, passes the storage conformance kit, and runs the Bank end-to-end scenarios. The package also contains an SFQL compiler that turns queries into one parameterised T-SQL statement.

This page concentrates on what is specific to SQL Server and on how it differs from [SourceFlow.Storage.PostgreSQL](SourceFlow.Storage.PostgreSQL-README.md). The persistence model itself is described in [Architecture/05 Event Store and Persistence](Architecture/05-Event-Store-and-Persistence.md).

## Contents

- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Differences from PostgreSQL](#differences-from-postgresql)
- [Schema management](#schema-management)
- [Tables](#tables)
- [How the adapter works](#how-the-adapter-works)
- [SFQL on SQL Server](#sfql-on-sql-server)
- [Operations](#operations)
- [Troubleshooting](#troubleshooting)
- [Related documents](#related-documents)

## Requirements

| Item | Requirement |
|------|-------------|
| .NET | `net10.0` |
| Engine | SQL Server 2022 or later, or Azure SQL Database. The SFQL compiler uses `DATE_BUCKET`, which needs 2022. CI runs SQL Server 2022 on pull requests and 2022 and 2025 nightly |
| Driver | `Microsoft.Data.SqlClient` (a package dependency) |
| Rights | DDL for the migration login; DML for the runtime login. `VIEW SERVER STATE` (or `VIEW DATABASE PERFORMANCE STATE` on Azure SQL Database) for the horizon-age gauge |

## Installation

```bash
dotnet add package SourceFlow.Net
dotnet add package SourceFlow.Storage.SqlServer
```

## Quick start

```csharp
using SourceFlow.Hosting;
using SourceFlow.Storage.SqlServer;

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "accounts";
    sf.AddModule<ContractsModule>();
    sf.AddModule<AccountsModule>();
    sf.UseSqlServer(builder.Configuration.GetConnectionString("SourceFlow")!, o =>
    {
        o.Schema = "accounts";
        o.QueryConnectionString = builder.Configuration.GetConnectionString("SourceFlowReadOnly");   // optional
    });
    sf.UseInMemoryTransport();
});

builder.Services.AddHealthChecks().AddSourceFlow();
```

The Bank end-to-end tests run the same modules on SQL Server with `sf.UseSqlServer(connectionString, o => o.Schema = schema)` ([`tests/Storage/SourceFlow.Storage.Tests/SqlServer`](../../../tests/Storage/SourceFlow.Storage.Tests/SqlServer)).

As with PostgreSQL, the schema is migrated (`AutoMigrate`) or verified when the host starts, before any SourceFlow component runs. `UseSqlServer` registers `SqlServerStore` as a singleton and the SQL Server query engine.

## Configuration

`SqlServerStorageOptions` ([source](../../../src/Storage/SourceFlow.Storage.SqlServer/SqlServerStorageOptions.cs)) is deliberately small:

| Option | Default | Description |
|--------|---------|-------------|
| `ConnectionString` | none (required) | The connection string. Connections are pooled by SqlClient. |
| `Schema` | `"sourceflow"` | The schema that holds the SourceFlow tables. 1–63 characters of `[a-z0-9_]`, starting with a letter or underscore. |
| `AutoMigrate` | `null` | `true` migrates at startup, `false` verifies. `null` means on in `Development`, off elsewhere. |
| `QueryTimeout` | 5 s | The command timeout for SFQL queries. Positive, at most 30 s. |
| `QueryConnectionString` | `ConnectionString` | A read-only connection for SFQL queries, for example with `ApplicationIntent=ReadOnly` to an availability-group secondary. |

The runtime options on `SourceFlowOptions` (`PollInterval`, `LeaseTtl`, `OutboxPartitions`, `SubscriptionPartitions`, `InboxRetention`, `RetentionInterval` and so on) apply unchanged; see the [PostgreSQL README](SourceFlow.Storage.PostgreSQL-README.md#configuration) for the table. `PollInterval` matters more on SQL Server, because it is how other nodes notice new events.

## Differences from PostgreSQL

| Area | PostgreSQL | SQL Server |
|------|------------|------------|
| Global position | `(tx_id xid8, seq)` read below `pg_snapshot_xmin` | `(CAST(rv AS bigint), seq)`, where `rv` is a `rowversion`, read below `MIN_ACTIVE_ROWVERSION()` |
| Commit | One `NpgsqlBatch` that ends with `COMMIT` (one round trip) | One command batch, then `COMMIT` (two round trips). A batch is split at 2,000 parameters, because SQL Server allows at most 2,100 |
| Multi-row writes | `unnest` of array parameters | One JSON parameter read with `OPENJSON` (no table-valued parameters, so no user-defined table types to manage) |
| Failed checks | `sf_assert` raises SQLSTATE `SF001` | `THROW 50001` (append) and `THROW 50002` with a check code (fenced writes, process state) |
| Upserts | `INSERT … ON CONFLICT` | `MERGE … WITH (HOLDLOCK)` and `UPDLOCK, HOLDLOCK` existence checks |
| Payload columns | `json` | `nvarchar(max)` JSON text, kept as written |
| Key comparison | Ordinal (`text`, `COLLATE "C"` for ordering) | Key columns are `nvarchar(100/200/400) COLLATE Latin1_General_100_BIN2`, ordinal whatever the database collation |
| Cross-node wake-ups | `LISTEN`/`NOTIFY` | None. In-process notifications only; other nodes poll at `PollInterval` |
| Tenancy | `TenantIsolation.Runtime` or `RowLevelSecurity` | Runtime isolation only: every query filters on `tenant_id`. Row-level security is not provided |
| Partitioned events | `PartitionEvents`, maintenance job, detach | Not provided |
| `[Queryable]` index sync | `db index sync` | Not built yet |
| Correlation and causation indexes | Expression indexes on `metadata` | Not created |
| Migrations from the CLI | `dotnet sourceflow db migrate` and `db script` | Not in the CLI: use `AutoMigrate` or `SqlServerSchemaMigrator` from code (below) |
| Least-privilege grants script | `db script --grants` | Not provided; grant DML on the schema yourself |
| Conformance kit | Every SCK case | Every case except the three that need a controllable store clock (leases use the database clock) |

Everything else behaves the same: ES-R1 and ES-R2, the exceptions (`ConcurrencyException`, `StreamClosedException`, `LeaseLostException`, `DuplicateMessageException`), the inclusive safe horizon, leases on the database clock, outbox due times on the host's `TimeProvider`, archived streams, blue/green aliases and batched retention.

## Schema management

The schema is one embedded script, `V001__initial` ([`Schema/V001__initial.sql`](../../../src/Storage/SourceFlow.Storage.SqlServer/Schema/V001__initial.sql)). It already contains what PostgreSQL added in later migrations (`collection_aliases.previous` and `building`, `outbox.first_due_at`).

`SqlServerSchemaMigrator` ([source](../../../src/Storage/SourceFlow.Storage.SqlServer/SqlServerSchemaMigrator.cs)):

- takes an exclusive, transaction-owned application lock (`sp_getapplock` on `sourceflow.migrate:{schema}`), so concurrent migrators are serialised;
- creates the schema and `{schema}.schema_history` when missing;
- runs every pending script in one transaction (a failure applies nothing) and records each with a SHA-256 checksum;
- fails with `SqlServerSchemaException` when an applied script was modified, or, in `VerifyAsync`, when the schema is missing or behind. A newer schema is accepted, for rolling upgrades.

The `dotnet sourceflow db` commands drive the PostgreSQL migrator only. On SQL Server, migrate in one of these ways:

```csharp
// 1. At startup (development, or a deployment that allows DDL at start).
sf.UseSqlServer(connectionString, o => o.AutoMigrate = true);

// 2. From a migration job or deployment step, with an owner login.
var migrator = new SqlServerSchemaMigrator(ownerConnectionString, schema: "accounts");
IReadOnlyList<int> applied = await migrator.MigrateAsync(cancellationToken);

// 3. For DBA review: T-SQL in one transaction, batches separated by GO (run with sqlcmd or SSMS).
File.WriteAllText("sourceflow-accounts.sql", migrator.Script(fromVersion: 0));
```

`GetAppliedVersionsAsync()` returns the applied versions, and `LatestVersion` the package's latest. A resolved `SqlServerStore` also offers `CreateMigrator()`.

With `AutoMigrate` off, the runtime login needs only `SELECT, INSERT, UPDATE, DELETE` on the schema's tables. To keep history append-only, deny `UPDATE` and `DELETE` on `events` to that login yourself; there is no generated grants script for SQL Server.

## Tables

The tables match the PostgreSQL adapter's, with SQL Server types:

| Table | Notes |
|-------|-------|
| `streams` | Clustered key `(tenant_id, stream_id)`. |
| `events` | Clustered on `seq` (identity). `rv rowversion` gives the commit order; `ix_events_commit_order` is a unique index on `rv` that includes `bucket`. `ix_events_type_time (tenant_id, event_type, created_at)` serves SFQL. `tags`, `data` and `metadata` are `nvarchar(max)` JSON. `events` is the only table with a `rowversion`, so only in-progress appends hold the horizon. |
| `outbox` | Clustered on `seq`; `ix_outbox_ready` and the filtered `ix_outbox_key` support the head-of-key claim. `first_due_at` keeps the original due time across retries. |
| `inbox` | Clustered key `(consumer, message_id)`; `ix_inbox_processed_at` for retention. |
| `checkpoints`, `leases`, `dead_letters`, `nodes`, `collection_aliases`, `schemas`, `data_keys` | As on PostgreSQL. |
| `process_instances`, `documents`, `subject_streams` | Their primary keys are `NONCLUSTERED`, because the key can exceed the 900-byte clustered key limit. |
| `snapshots` | Clustered key `(tenant_id, stream_id)`. |
| `schema_history` | Applied migrations and checksums (created by the migrator). |

All timestamps are `datetimeoffset(7)`.

## How the adapter works

### Sessions and commits

A session is one Read Committed transaction on one pooled connection. Reads go to the server at once; writes are queued and sent by `CommitAsync` in one command batch, the stream append first (ES-R1), then `COMMIT`. Each conditional statement checks its outcome in T-SQL:

| Check | Raised | Mapped to |
|-------|--------|-----------|
| Append compare-and-set (`@@ROWCOUNT = 0`, or the stream exists for `NoStream`) | `THROW 50001` | `ConcurrencyException`, or `StreamClosedException` for a deleted or archived stream |
| Fenced checkpoint, process-state version | `THROW 50002, @check` | `LeaseLostException` or `ConcurrencyException` |
| Inbox unique key | duplicate key | `DuplicateMessageException` |

The first failure rolls the whole transaction back, including outbox, inbox, document and checkpoint writes. The design recommends `READ_COMMITTED_SNAPSHOT ON` for read concurrency ([doc 05 §5.5](../Design/05-Event-Store-and-Persistence.md#55-sql-server-solution)).

### Global order and the safe horizon

A `rowversion` is assigned when a row is written and is unique in the database. `MIN_ACTIVE_ROWVERSION()` is the lowest rowversion that is still uncommitted, so every row below it is final. Subscriptions read `rv > @after AND rv < MIN_ACTIVE_ROWVERSION() AND bucket BETWEEN @lo AND @hi ORDER BY rv`. The horizon is reported inclusively as `(MIN_ACTIVE_ROWVERSION() − 1, long.MaxValue)`. Because the append writes the `streams` row first, per-stream order equals global order, exactly as on PostgreSQL.

`GetHorizonAgeAsync` reads the oldest active writing transaction from dynamic management views. Without `VIEW SERVER STATE` (or `VIEW DATABASE PERFORMANCE STATE` on Azure SQL Database) it returns zero, so the `sourceflow.subscription.horizon_age` gauge reads zero and cannot warn you about a stuck transaction.

### Notifications and polling

SQL Server has no `LISTEN`. Subscribers in the same process are signalled after each local commit. Subscribers and relays in other processes find new events and outbox rows on their next poll, at `PollInterval` (1 s by default). Lower it, for example to 200 ms, when cross-node latency matters, at the cost of more idle queries.

### Clocks

Leases use the database clock (`SYSDATETIMEOFFSET()`); outbox due times use the host's `TimeProvider`.

## SFQL on SQL Server

`SqlServerQueryEngine` compiles a query into one parameterised T-SQL statement on `QueryConnectionString`, with `QueryTimeout` as the command timeout:

- Text compares with `COLLATE Latin1_General_BIN2`, ordinal like the interpreter.
- `NULLS FIRST` and `NULLS LAST` become a `CASE` sort key.
- A boolean value is a `bit` built from a predicate.
- `bin()` uses `DATE_BUCKET` (SQL Server 2022).
- Keyset continuation pages work as on PostgreSQL.
- A construct SQL Server cannot express the same way (for example the text of a decimal, or arithmetic between two timestamps) falls back to the in-memory interpreter, which returns the same rows.
- `from events` leaves archived streams out unless `options (include_archived = true)`.

There is no `[Queryable]` index sync for SQL Server yet. Create indexes for hot payload predicates yourself (for example a persisted computed column over `JSON_VALUE(data, '$.status')` with a filtered index). See [Architecture/08 Query Side and SFQL](Architecture/08-Query-Side-and-SFQL.md).

## Operations

- **Health and gauges.** `SqlServerStore` implements `IStoreMonitor`: the store probe, outbox backlog per partition, parked dead letters per consumer, events after a checkpoint, horizon age, active process managers and active workflows. See [Observability and Performance](Observability-and-Performance.md).
- **Retention.** Inbox rows older than `InboxRetention` and resolved dead letters older than `DeadLetterRetention` are deleted by the relay role in batches of `RetentionBatchSize`.
- **Store commands.** `dotnet sourceflow dlq …` and `projection …` connect to PostgreSQL when run as the global tool. Run them **inside the service** (`dotnet run --project <service> -- sourceflow dlq list`, with `SourceFlowCli.TryRunAsync` in `Program.cs`); they then use the service's SQL Server storage.
- **EF Core read models.** `SourceFlow.Storage.EntityFrameworkCore` works on the SQL Server session connection with `(o, c) => o.UseSqlServer(c)`. The schema-per-generation defaults (`CREATE SCHEMA IF NOT EXISTS`, `DROP SCHEMA … CASCADE`) are PostgreSQL syntax; override `EfGenerationOptions.CreateSchemaSql` and `DropSchemaSql` for SQL Server. See [SourceFlow.Storage.EntityFrameworkCore](SourceFlow.Storage.EntityFrameworkCore-README.md).

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Startup fails: `The SourceFlow schema 's' is not installed` | `AutoMigrate` is off outside `Development`. | Run `SqlServerSchemaMigrator.MigrateAsync`, apply `Script()` output, or enable `AutoMigrate`. |
| `SqlServerSchemaException`: migration modified | An applied script's checksum changed. | Restore the original package version; never edit applied scripts. |
| Async projections on another node lag by about a second | No cross-process notifications on SQL Server. | Lower `PollInterval`. |
| `sourceflow.subscription.horizon_age` is always zero | The login cannot read the DMVs. | Grant `VIEW SERVER STATE`, or `VIEW DATABASE PERFORMANCE STATE` on Azure SQL Database. |
| Subscriptions stall while the database is busy | A long writing transaction holds `MIN_ACTIVE_ROWVERSION()` back. | Find and end it; keep long batch writes out of the SourceFlow database. |
| SFQL `bin()` fails | The engine is older than SQL Server 2022. | Upgrade; the adapter requires 2022. |
| Ids that differ only by case collide or sort unexpectedly in your own SQL | Your columns use a case-insensitive collation. | SourceFlow's key columns are binary-collated; compare with `COLLATE Latin1_General_100_BIN2` in custom SQL. |
| `dotnet sourceflow db migrate` cannot connect | The global tool's store commands target PostgreSQL. | Use the migrator from code or `AutoMigrate`. |

## Related documents

- [SourceFlow.Storage.PostgreSQL](SourceFlow.Storage.PostgreSQL-README.md), the reference adapter
- [Architecture/05 Event Store and Persistence](Architecture/05-Event-Store-and-Persistence.md), [Architecture/06 Projections and Read Models](Architecture/06-Projections-and-Read-Models.md)
- [Message Idempotency Guide](Message-Idempotency-Guide.md)
- Design: [doc 05 §5.5, as built (WP-4.4)](../Design/05-Event-Store-and-Persistence.md#55-sql-server-solution)
- ADRs: [ADR-009](../ADRs/ADR-009-event-store-on-adonet-postgresql-first.md), [ADR-010](../ADRs/ADR-010-commit-ordered-global-position-for-gap-free.md)
