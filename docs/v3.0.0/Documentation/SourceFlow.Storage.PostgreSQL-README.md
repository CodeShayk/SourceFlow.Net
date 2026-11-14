# SourceFlow.Storage.PostgreSQL

The PostgreSQL storage adapter for SourceFlow.Net v3, and the reference implementation of the persistence SPI. One `PostgreSqlStore` implements every storage port of the core: the event store and the commit-ordered global log, storage sessions (one transaction per command or subscription batch), the transactional outbox and inbox, fenced checkpoints and leases, dead letters, process-manager state, snapshots, read-model documents, blue/green read-model aliases, the schema registry, node liveness and crypto-shredding keys. It is written with hand-tuned SQL over `NpgsqlDataSource`, not an ORM ([ADR-009](../ADRs/ADR-009-event-store-on-adonet-postgresql-first.md)). A command commits in one database round trip, the global log is provably gap-free ([ADR-010](../ADRs/ADR-010-commit-ordered-global-position-for-gap-free.md)), and payloads round-trip byte for byte ([ADR-028](../ADRs/ADR-028-postgresql-payload-columns-and-adapter-refinements.md)). The package also contains the SFQL compiler for PostgreSQL and the `[Queryable]` index sync.

How the persistence model works in general (streams, sessions, the safe horizon, leases) is described in [Architecture/05 Event Store and Persistence](Architecture/05-Event-Store-and-Persistence.md). This page is about using the PostgreSQL adapter.

## Contents

- [Requirements](#requirements)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Schema management](#schema-management)
- [Tables](#tables)
- [How the adapter works](#how-the-adapter-works)
- [Multi-tenancy and row-level security](#multi-tenancy-and-row-level-security)
- [Partitioned events and archiving](#partitioned-events-and-archiving)
- [Blue/green read-model generations](#bluegreen-read-model-generations)
- [SFQL on PostgreSQL](#sfql-on-postgresql)
- [Connections, pooling and PgBouncer](#connections-pooling-and-pgbouncer)
- [Performance](#performance)
- [Operations](#operations)
- [Troubleshooting](#troubleshooting)
- [Related documents](#related-documents)

## Requirements

| Item | Requirement |
|------|-------------|
| .NET | `net10.0` (every SourceFlow v3 package targets .NET 10 only) |
| PostgreSQL | 15 or later. The nightly matrix runs PostgreSQL 15, 16 and 17, plus 17 with partitioned events |
| Driver | `Npgsql` (a package dependency) |
| Database rights | DDL rights for the migration role; DML only for the runtime role (see [Least-privilege grants](#least-privilege-grants)) |

## Installation

```bash
dotnet add package SourceFlow.Net
dotnet add package SourceFlow.Storage.PostgreSQL
```

The `dotnet sourceflow` tool runs migrations and other store commands; see [SourceFlow.Tool](SourceFlow.Tool-README.md).

## Quick start

Register the adapter on the SourceFlow builder. This is the shape of the TravelBooking sample's hosts, which share a database
and keep each service in its own schema ([`samples/TravelBooking/Shared/ServiceHost.cs`](../../../samples/TravelBooking/Shared/ServiceHost.cs)):

```csharp
using Bookings;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("SourceFlow")
    ?? throw new InvalidOperationException("Set ConnectionStrings__SourceFlow to a PostgreSQL connection string.");

builder.Services.AddSourceFlow(sf =>
{
    sf.AddBookingsService();                                   // service name "bookings", its module, projections and routes
    sf.UsePostgreSql(connectionString, o =>
    {
        o.Schema = "bookings";                                // one schema per service; the default is "sourceflow"
        o.AutoMigrate = builder.Environment.IsDevelopment();   // the default when left null
    });
    sf.UseInMemoryTransport();                                 // the sample uses UseAwsTransport, UseAzureTransport or UseGcpTransport
});

builder.Services.AddHealthChecks().AddSourceFlow();
```

At host start, before any SourceFlow component starts, the adapter either applies pending migrations (`AutoMigrate`) or verifies that the schema is installed and current. A missing or outdated schema stops the host with a `SchemaMigrationException` that says which command to run.

The second overload takes only an options callback, which is how you hand the adapter an `NpgsqlDataSource` you already own:

```csharp
NpgsqlDataSource dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "accounts";
    sf.AddModule<AccountsModule>();
    sf.UsePostgreSql(o =>
    {
        o.DataSource = dataSource;   // the application owns and disposes it
        o.Schema = "accounts";
    });
});
```

`UsePostgreSql` registers `PostgreSqlStore` as a singleton, so application code and tools can resolve it (for example to call `DetachEventPartitionAsync`).

## Configuration

All options are on `PostgreSqlStorageOptions` ([source](../../../src/Storage/SourceFlow.Storage.PostgreSQL/PostgreSqlStorageOptions.cs)). They are validated when `UsePostgreSql` runs; an invalid value throws `InvalidOperationException` at registration.

| Option | Default | Description |
|--------|---------|-------------|
| `ConnectionString` | none | The connection string. Required unless `DataSource` is set. |
| `DataSource` | none | An existing pooled `NpgsqlDataSource`. When set, `ConnectionString` is ignored and the application owns the data source; otherwise the adapter builds and disposes one. |
| `Schema` | `"sourceflow"` | The schema that holds the SourceFlow tables. 1–63 characters of `[a-z0-9_]`, starting with a letter or underscore. Use one schema per service. |
| `AutoMigrate` | `null` | `true` applies pending migrations at startup; `false` only verifies. `null` means on in the `Development` environment and off elsewhere. |
| `ListenConnectionString` | `ConnectionString` | A separate connection string for the `LISTEN` connection, for deployments behind a transaction-pooling PgBouncer that cannot hold `LISTEN`. |
| `ListenReconnectDelay` | 2 s | The delay before the `LISTEN` connection reconnects after a failure. Must be positive. Subscribers keep polling meanwhile. |
| `NotificationChannel` | the schema name | The `LISTEN`/`NOTIFY` channel. Same character rules as `Schema`. |
| `TenantIsolation` | `TenantIsolation.Runtime` | `Runtime`: every query filters by tenant. `RowLevelSecurity`: PostgreSQL row-level security enforces it too (see [Multi-tenancy](#multi-tenancy-and-row-level-security)). |
| `SystemConnectionString` | the main connection | The connection for cross-tenant readers (the global log). Must be a `BYPASSRLS` role, and is required with `RowLevelSecurity`. |
| `SystemDataSource` | none | An existing data source for cross-tenant readers; takes precedence over `SystemConnectionString`. |
| `QueryTimeout` | 5 s | The statement timeout for SFQL queries. Positive, at most 30 s. |
| `PartitionEvents` | `false` | Partitions `events` by month of `created_at` (see [Partitioned events](#partitioned-events-and-archiving)). |
| `PartitionMonthsAhead` | 3 | How many months after the current one the maintenance job keeps partitions for. 1 to 24. |
| `PartitionMaintenanceInterval` | 1 hour | How often the maintenance job checks the partitions ahead. Must be positive. |

The runtime options that drive the adapter's work (poll interval, lease TTL, outbox partitions, retention) are on `SourceFlowOptions`, set with `sf.Configure(o => …)`. The ones that matter most for storage:

| `SourceFlowOptions` | Default | Effect on the store |
|---------------------|---------|---------------------|
| `PollInterval` | 1 s | How often subscriptions and the relay poll when no notification arrives (10 ms to 10 min). |
| `LeaseTtl` | 30 s | Lease time-to-live for outbox and subscription partitions; must exceed `PollInterval`. |
| `OutboxPartitions` | 16 | Outbox partitions, each relayed under its own lease. |
| `SubscriptionPartitions` | 1 | Partitions per async subscription (a power of two). |
| `SubscriptionBatchSize` | 1000 | The largest subscription batch (batches start at 100 and grow while catching up). |
| `InboxRetention` | 14 days | How long processed inbox rows are kept for deduplication. |
| `DeadLetterRetention` | 30 days | How long replayed or discarded dead letters are kept. |
| `RetentionInterval` | 1 hour | How often the relay role runs retention cleanup. |
| `RetentionBatchSize` | 5000 | Rows deleted per cleanup statement. |
| `SwitchLagThreshold` | 100 | Events a new read-model generation may lag before it takes over reads. |
| `RetainPreviousFor` | 7 days | How long a retired read-model generation is kept. |
| `MonitorInterval` | 15 s | How often the monitor samples outbox backlog, lag and horizon age. |

## Schema management

The schema is defined by versioned SQL scripts embedded in the package ([`Schema/`](../../../src/Storage/SourceFlow.Storage.PostgreSQL/Schema)). `{schema}` in each script is replaced by the configured schema name.

| Script | Contents |
|--------|----------|
| `V001__initial` | Every table and index (see [Tables](#tables)). Payload columns are `json`, envelope metadata `jsonb`. |
| `V002__tenant_policies` | The `tenant_isolation` row-level security policies. Always created; they take effect only where the migrator enables row-level security. |
| `V003__commit_assertions` | The `sf_assert(ok, code)` function used by the one-round-trip commit. A failed check raises SQLSTATE `SF001`. |
| `V004__read_model_generations` | `previous` and `building` columns on `collection_aliases` (blue/green rebuilds, ADR-032). |
| `V005__outbox_first_due` | `outbox.first_due_at`, so the outbox age keeps growing while a broker is down. |
| `V006__data_keys_per_tenant` | `data_keys.tenant_id`, keyed by `(tenant_id, subject_id)`: erasing a subject in one tenant leaves the same subject id in another tenant intact. Existing keys move to the default tenant `-`. |
| `V007__data_keys_policy` | The `tenant_isolation` policy on `data_keys`, which becomes a tenant table under row-level security. |

`PostgreSqlSchemaMigrator` ([source](../../../src/Storage/SourceFlow.Storage.PostgreSQL/PostgreSqlSchemaMigrator.cs)) applies them:

- It takes a transaction-scoped advisory lock per schema, so concurrent migrators (several nodes starting at once) are serialised.
- Every pending script runs in **one transaction**. A failure applies nothing and raises `SchemaMigrationException` naming the script.
- Each applied script is recorded in `{schema}.schema_history` with a SHA-256 checksum. An applied script that was later modified fails both migration and verification: applied migrations are immutable.
- `VerifyAsync` accepts a schema that is *newer* than the package (migrations are additive), so a rolling upgrade can run old and new nodes side by side.
- When `TenantIsolation` is set, it also applies or verifies the row-level security state, altering only tables whose state differs.

### Running migrations

| Situation | How |
|-----------|-----|
| Local development | Leave `AutoMigrate` null (on in `Development`), or set it to `true`. |
| Production, migration job | `dotnet sourceflow db migrate --connection "<owner connection>" [--schema s] [--rls] [--partition-events]` |
| Production, DBA review | `dotnet sourceflow db script [--schema s] [--from N] [--rls] [--partition-events] > migrate.sql`, reviewed and applied by a DBA. The script takes the same lock and writes the same history rows. |
| Inside the service | `dotnet run --project <service> -- sourceflow db migrate` when the host calls `SourceFlowCli.TryRunAsync` (the TravelBooking services do). It uses the service's data source and schema. |
| From code | `await store.CreateMigrator().MigrateAsync()` on a resolved `PostgreSqlStore`, or `new PostgreSqlSchemaMigrator(dataSource, schema, isolation, partitionEvents)`. |

The global tool reads the connection from `--connection` or the `SOURCEFLOW_CONNECTION` environment variable. With the global tool, pass the same `--rls` and `--partition-events` choices that the service configures: the migrator applies the isolation you ask for, and the service's startup verification fails if they differ. Inside the service, `db migrate` uses the service's own `TenantIsolation` and `PartitionEvents`, so `--rls` is not needed there; passing `--rls` to a service configured for `Runtime` isolation is refused.

### Least-privilege grants

Run migrations as the schema owner and the service as a separate role with DML only. `db script --grants <role>` prints the grants (`PostgreSqlSchemaMigrator.GrantsScript(role)`):

```bash
dotnet sourceflow db script --schema sourceflow --grants sourceflow_app > grants.sql
```

The script grants `USAGE` on the schema, `SELECT, INSERT, UPDATE, DELETE` on its tables, sequence and function rights, and then **revokes `UPDATE`, `DELETE` and `TRUNCATE` on `events`** and every events partition, so the runtime role cannot rewrite history. Default privileges make partitions created later append-only for the role too. Re-run it after every migration that adds tables. With `AutoMigrate` off, the runtime role needs no DDL at all.

## Tables

Every table lives in the configured schema. `tenant_id` defaults to `'-'`, the single-tenant default.

| Table | Purpose | Key |
|-------|---------|-----|
| `streams` | One row per aggregate instance: type, current version, `is_deleted`, `is_archived`. The concurrency anchor of every append. | `(tenant_id, stream_id)` |
| `events` | The append-only event log. `seq` identity, `tx_id xid8` (the writing transaction), `bucket` (0–1023), stream id and version, `event_id`, type and type version, `tags text[]`, `data json`, `metadata jsonb`, `created_at` (the decision time). | `seq`; unique `(tenant_id, stream_id, stream_version)` and `event_id` |
| `outbox` | Messages waiting for the relay: partition, partition key, destination, kind (1 send, 2 publish, 3 scheduled), CloudEvents attributes (`jsonb`), body (`bytea`), `deliver_at`, attempts, last error, `first_due_at`. | `seq` |
| `inbox` | Processed messages per consumer, with the stored result (status, version, error code, global position). | `(consumer, message_id)` |
| `checkpoints` | Subscription positions per partition (`commit_pos xid8`, `seq_pos`). | `(subscription, partition)` |
| `leases` | Fenced leases: owner, fencing `token`, `expires_at` (database clock). | `name` |
| `dead_letters` | Parked messages with the full envelope, error details, status (0 parked, 1 replayed, 2 discarded) and audit note. | `id` |
| `process_instances` | Process-manager state (`json`), version and completed flag. | `(tenant_id, process_type, process_id)` |
| `snapshots` | The latest aggregate snapshot per stream: version, state type, schema hash, `data json`. | `(tenant_id, stream_id)` |
| `documents` | Read-model documents (`data json`) with their version, per collection. | `(tenant_id, collection, id)` |
| `nodes` | Node heartbeats for lease fair-share. | `node_id` |
| `collection_aliases` | Which read-model generation serves reads (`target`), the retired one (`previous`) and the one being built (`building`). | `alias` |
| `subject_streams` | Which streams hold a data subject's personal data (crypto-shredding). | `(tenant_id, subject_id, stream_id)` |
| `schemas` | The schema registry: JSON Schema and fingerprint per message type and version. | `(tenant_id, message_type, type_version)` |
| `data_keys` | Wrapped data keys per tenant and subject, and their shredding time. | `(tenant_id, subject_id)` |
| `schema_history` | Applied migrations and their checksums (created by the migrator). | `version` |

Indexes on `events`: `ix_events_commit_order (tx_id, seq) INCLUDE (bucket)` for the global log, `ix_events_type_time (tenant_id, event_type, created_at)` for SFQL, and expression indexes on `metadata->>'correlationId'` and `metadata->>'causationId'` for tracing a conversation. `db index sync` adds partial expression indexes named `sfq_…` for `[Queryable]` fields.

### Payload columns are `json`, not `jsonb`

`events.data`, `documents.data`, `process_instances.state` and `snapshots.data` are `json` ([ADR-028](../ADRs/ADR-028-postgresql-payload-columns-and-adapter-refinements.md)):

- `json` keeps the serializer's exact text. `jsonb` reorders keys, drops whitespace and duplicate keys, so a payload read back would not be the payload written. Exact bytes matter for signatures, hashes, upcasters and downstream consumers, and the conformance kit asserts the round trip.
- `json` accepts the `\u0000` escape, which `jsonb` rejects and which `System.Text.Json` emits for a string containing U+0000.
- The `->`, `->>` and `#>>` operators and expression indexes still work, so SFQL and `[Queryable]` indexes are unaffected.
- What you lose: GIN containment indexes and the `@>` and `?` operators on payload columns, and `json = json` comparisons.

Envelope `metadata` and outbox and dead-letter `attributes` stay `jsonb`; the adapter writes them itself. A U+0000 character in an envelope header or actor is not supported.

## How the adapter works

This section summarises the PostgreSQL specifics; [Architecture/05](Architecture/05-Event-Store-and-Persistence.md) explains the model behind them.

### One transaction, one round trip

A storage session is one Read Committed transaction on one pooled connection. Reads that are needed before deciding (loading a stream, the inbox check, documents) go to the server at once. Writes are queued in memory and `CommitAsync` sends them as one `NpgsqlBatch` that ends with `COMMIT`:

1. the stream append (a CTE that updates or inserts the `streams` row and inserts the events);
2. the outbox rows (`INSERT … SELECT unnest(…)`);
3. the inbox rows;
4. read-model document upserts and deletes (a multi-row `unnest` upsert);
5. process-state writes and fenced checkpoint saves;
6. `COMMIT`.

The conditional statements (the append compare-and-set, the process-state version check and the fenced checkpoint) call `sf_assert`. A failed check raises SQLSTATE `SF001`, PostgreSQL skips the rest of the batch including its `COMMIT`, and the adapter rolls back and maps the failure:

| Failed check | Exception |
|--------------|-----------|
| Stream not at the expected version, or a duplicate `event_id` | `ConcurrencyException` (the command processor re-decides) |
| Stream deleted or archived | `StreamClosedException` (diagnosed after the rollback) |
| Process state not at the expected version | `ConcurrencyException` |
| Checkpoint lease lost or expired | `LeaseLostException` |
| Inbox row already present | `DuplicateMessageException` |

The storage layer never retries. Re-deciding after a conflict is the command processor's job ([ADR-008](../ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md)).

### Appends

Every `ExpectedVersion` kind is a single atomic statement:

| Expected | Statement on `streams` |
|----------|------------------------|
| `Exact(v)` | `UPDATE … WHERE version = v AND NOT is_deleted AND NOT is_archived` |
| `NoStream` | `INSERT … ON CONFLICT (tenant_id, stream_id) DO NOTHING` |
| `StreamExists` | `UPDATE … WHERE NOT is_deleted AND NOT is_archived` |
| `Any` | `INSERT … ON CONFLICT DO UPDATE SET version = version + n WHERE NOT is_deleted AND NOT is_archived` |

The append is O(events appended); it never reads the stream history to find the next version.

### Global order and the safe horizon

Each event records `tx_id = pg_current_xact_id()`. The global position is `(tx_id, seq)`. Subscriptions read:

```sql
WHERE (tx_id, seq) > (@after_tx, @after_seq)
  AND tx_id < pg_snapshot_xmin(pg_current_snapshot())
  AND bucket BETWEEN @lo AND @hi
ORDER BY tx_id, seq
```

Every transaction below `xmin` has finished, so the rows below it can never change, and a checkpoint can never skip an event that commits late ([ADR-010](../ADRs/ADR-010-commit-ordered-global-position-for-gap-free.md)). The horizon is reported inclusively as `(xmin − 1, long.MaxValue)`.

The operational consequence: a long-running **writing** transaction anywhere in the database holds `xmin` back and delays (never corrupts) subscriptions. Read-only transactions have no transaction id and do not. `sourceflow.subscription.horizon_age` reports the age of the oldest in-progress writing transaction (from `pg_stat_activity`), and the `sourceflow.subscriptions` health check turns `Degraded` above `Health.HorizonAgeDegraded` (30 s). Keep the event store database for SourceFlow, or at least avoid long write transactions in it.

### Notifications

After a commit, the store signals in-process subscribers immediately and sends one `NOTIFY` on the notification channel **after** the commit, outside the transaction. One `NOTIFY` covers every commit made while the previous one was being sent. (A `NOTIFY` inside the commit serialised every commit on PostgreSQL's notification lock and capped throughput at about 270 commits/s; the load test found and fixed this.) A dedicated `LISTEN` connection relays other processes' commits and reconnects after `ListenReconnectDelay` on failure. A lost notification only delays other nodes until their next poll (`PollInterval`).

### Clocks

Leases and fencing use the database clock (`now()`), so instance clock skew cannot break fencing. Outbox due times (`deliver_at`: timers and delayed retries) use the host's `TimeProvider`, like `ctx.Now`, so a test host with a fake clock fires timers when the fake clock moves.

## Multi-tenancy and row-level security

Every tenant-scoped table (`streams`, `events`, `snapshots`, `documents`, `process_instances`, `subject_streams`, `data_keys`) has a `tenant_id` column, and every query filters on it. With `TenantIsolation.RowLevelSecurity` the database enforces it as well ([ADR-022](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)):

```csharp
sf.UsePostgreSql(o =>
{
    o.ConnectionString = appConnection;            // the application role, DML only
    o.TenantIsolation = TenantIsolation.RowLevelSecurity;
    o.SystemConnectionString = systemConnection;   // a BYPASSRLS role, for the cross-tenant global log
});
```

- The migrator **enables and forces** row-level security on the tenant tables. Forcing makes the `tenant_isolation` policies apply to the table owner too; only superusers and `BYPASSRLS` roles skip them.
- With `PartitionEvents`, every events partition gets the `tenant_isolation` policy and the parent's row-level security state, including `events_initial` and the partitions the maintenance job creates later. PostgreSQL applies a policy from the table a query names, so without this a query on `events_2026_11` would read every tenant's events. Each migration repairs a partition that lost its policy.
- Each session runs `set_config('sourceflow.tenant', tenant, true)` at `BeginAsync`, so custom SQL and EF Core writes on `session.Connection` are scoped too. This adds one round trip per session.
- Tenant-scoped reads outside a session send the setting in the same batch.
- Without the setting, the application role sees no rows.
- The global log is read through `SystemConnectionString` or `SystemDataSource`, because it spans tenants.
- `inbox`, `outbox`, `checkpoints`, `leases` and `dead_letters` hold no tenant payload and have no policy.
- Startup verification fails if the database's row-level security state differs from the configured isolation, on any tenant table or events partition, or if one lacks the policy. Migrate with `db migrate --rls` to match.

Tenant resolution, the security model and the threat model are in [Architecture/10 Security and Multi-Tenancy](Architecture/10-Security-and-Multi-Tenancy.md) and the [Security and Compliance Guide](Security-and-Compliance-Guide.md).

## Partitioned events and archiving

From capacity tier M (about 1,000 commands/s peak) the design recommends partitioning the events table by month ([doc 09 §5](../Design/09-Performance-and-Scalability.md#5-capacity-model)). Set `PartitionEvents = true`:

```csharp
sf.UsePostgreSql(connectionString, o =>
{
    o.PartitionEvents = true;
    o.PartitionMonthsAhead = 3;                                // default
    o.PartitionMaintenanceInterval = TimeSpan.FromHours(1);    // default
});
```

- **Conversion.** The migrator converts an existing `events` table once, inside its transaction. No rows are copied: the old table becomes the partition `events_initial`, covering everything up to the start of the month after both its newest row and now. Monthly partitions `events_yyyy_MM` follow. Running the conversion again does nothing. To convert from the command line: `dotnet sourceflow db migrate --partition-events`.
- **Keys.** The primary key becomes `(seq, created_at)` and `seq` comes from the sequence `events_seq` (PostgreSQL 15 and 16 reject identity columns on partitioned tables). The runtime role needs `USAGE` on that sequence; the grants script includes it. Stream-version and event-id uniqueness stay enforced by the `streams` compare-and-set; the unique constraints become per-partition backstops.
- **Maintenance.** `sf_ensure_event_partitions` creates partitions ahead under an advisory lock. The migrator creates 3 months ahead; a hosted job on every node keeps `PartitionMonthsAhead` months ahead every `PartitionMaintenanceInterval`. `created_at` comes from the runtime clock, so a row past the last partition fails to insert: the job logs event 2302 (`Creating events partitions ahead … failed`) when it cannot create partitions. If the runtime role has no DDL rights, run `dotnet sourceflow db partitions ensure` as the owner on a schedule.
- **Bookkeeping.** `event_partitions` records each partition's range and when it was detached.

| Command | `PostgreSqlStore` method | Purpose |
|---------|--------------------------|---------|
| `db partitions list` | `ListEventPartitionsAsync()` | Every partition, oldest first, including detached ones |
| `db partitions ensure` | `EnsureEventPartitionsAsync()` | Create the partitions ahead now |
| `db partitions detach <name>` | `DetachEventPartitionAsync(name)` | Detach an old partition (owner rights) |

**Archiving and detaching.** `IEventStore.ArchiveStreamAsync` marks a stream archived: it rejects appends, stays loadable, and is left out of projection subscriptions and of SFQL `from events` unless `options (include_archived = true)`. Archiving is permanent. A partition can be detached only when its range has ended and every stream with events in it is archived or deleted; otherwise `EventPartitionException` explains why. The open-stream check reads every tenant's rows through the system data source, with `row_security` off: under row-level security a role that can't bypass the policies gets an error instead of a count of zero. The detached table keeps its rows for archiving (for example `pg_dump -t`), and the operator drops it afterwards. Its events then leave stream reads, the global log and queries.

## Blue/green read-model generations

The adapter implements `ICollectionAliases` (migration V004) and `IDocumentAdmin`, so document projections, and EF Core projections that keep a schema per generation, are rebuilt blue/green ([ADR-032](../ADRs/ADR-032-blue-green-read-model-generations-behind-forward-only-aliases.md)):

- A projection version bump, or `dotnet sourceflow projection rebuild account-summary@1`, builds a new generation (`account-summary@2` or `account-summary@1.2`) into its own collection while the old one keeps serving reads.
- Once every partition of the new generation is within `SwitchLagThreshold` events of the log, a node switches the alias with a compare-and-set on `collection_aliases`.
- The retired generation's documents are deleted after `RetainPreviousFor` (`DELETE FROM documents WHERE collection = …`, all tenants).

The process is described in [Architecture/06 Projections and Read Models](Architecture/06-Projections-and-Read-Models.md#bluegreen-rebuilds).

## SFQL on PostgreSQL

`UsePostgreSql` also registers `PostgreSqlQueryEngine`, which compiles SFQL and SQL-syntax queries to one parameterised PostgreSQL statement per page. Every request value is a typed parameter, never literal SQL. Each query runs in a `READ ONLY` transaction with `statement_timeout` set to `QueryTimeout`, with the tenant setting under row-level security. Text orders with `COLLATE "C"`, ordinal like the in-memory interpreter. Aggregate and schema sources stay in the interpreter.

For fast predicates on payload fields, mark them `[Queryable]` and sync the expression indexes from inside the service:

```bash
dotnet run --project samples/TravelBooking/Bookings.Api -- sourceflow db index sync          # show the plan
dotnet run --project samples/TravelBooking/Bookings.Api -- sourceflow db index sync --apply  # create missing, drop stale
```

Indexes are created `CONCURRENTLY` (without it on a partitioned events table, which PostgreSQL requires). The query language, the compiler and the query API are described in [Architecture/08 Query Side and SFQL](Architecture/08-Query-Side-and-SFQL.md).

## Connections, pooling and PgBouncer

- **One data source.** The adapter opens connections from one pooled `NpgsqlDataSource` (yours, or one it builds). A session holds one connection for the duration of a command or a subscription batch.
- **Extra connections.** The store holds one connection for `LISTEN` and briefly uses one to send `NOTIFY`. With row-level security, the system data source is a second pool.
- **Pool size.** `Maximum Pool Size` must leave room under the server's `max_connections` (100 by default) for every instance of every service. A pool larger than the server allows fails with `53300: too many clients`, and the reconnect churn can exhaust the client's ephemeral ports. The tier-M reference run used `Maximum Pool Size=60` against `max_connections=100` ([`loadtest/README.md`](../../../loadtest/README.md)).
- **Pool budget.** As a rule of thumb from [doc 09 §4](../Design/09-Performance-and-Scalability.md#4-database-efficiency): API concurrency + receive concurrency (`ReceiveConcurrency`) + owned subscription leases + owned outbox leases + a few for the store.
- **Prepared statements.** The adapter's SQL text is stable per operation, so Npgsql automatic preparation (`Max Auto Prepare` in the connection string, for example `20`) keeps it prepared on the server.
- **PgBouncer.** Transaction pooling works for sessions, but it cannot hold `LISTEN`. Point `ListenConnectionString` at the database directly (bypassing PgBouncer). Without it, cross-node wake-ups fall back to polling at `PollInterval`.

## Performance

Indicative numbers from the WP-2.10 baseline ([`implementation/perf-baseline.md`](../implementation/perf-baseline.md)) and the WP-4.7 load test ([`implementation/load-test-report.md`](../implementation/load-test-report.md)), on a developer workstation with PostgreSQL 17 in Docker. They are a floor, not a ceiling.

| Measurement | Result |
|-------------|--------|
| Commit of 1 / 3 / 10 events plus outbox (PERF-03) | mean 2.2 / 2.5 / 2.7 ms, p95 2.7 / 3.0 / 2.8 ms, one round trip |
| Aggregate load with 0 / 100 / 1,000 events (PERF-04) | 2.1 → 3.4 → 9.5 ms without snapshots; flat at about 3.1–3.4 ms with snapshots |
| Document projection rebuild, one partition (PERF-05) | 32,654 events/s with adaptive batches |
| Global log read (PERF-06) | about 148,000 events/s |
| Event to async projection, `LISTEN`/`NOTIFY` (PERF-08) | p95 14.8 ms, p50 9.3 ms (including the command) |
| Tier-M load test, 1,000 commands/s for 3 minutes | p99 37.7 ms, 0 failures, read-model freshness p99 247 ms |

Beyond 1,000 commands/s on the shared machine, PostgreSQL with default settings saturated first (WAL flushes and lock waits) at about 2,700 commands/s. Size the database per [doc 09 §5](../Design/09-Performance-and-Scalability.md#5-capacity-model) for higher rates.

## Operations

- **Health.** `AddHealthChecks().AddSourceFlow()` adds `sourceflow.store`, which probes the store and checks that the schema is the version the adapter expects. Gauges such as `sourceflow.outbox.age`, `sourceflow.subscription.lag` and `sourceflow.subscription.horizon_age` are sampled every `MonitorInterval`. See [Observability and Performance](Observability-and-Performance.md).
- **Retention.** The relay role deletes processed inbox rows older than `InboxRetention` and resolved dead letters older than `DeadLetterRetention`, in batches of `RetentionBatchSize` rows per statement. Outbox rows are deleted on delivery. Parked dead letters are never deleted automatically.
- **Dead letters.** `dotnet sourceflow dlq list|show|replay|discard|purge`.
- **Projections.** `dotnet sourceflow projection skip|repartition|rebuild|drop|generations|switch`.
- **Vacuum.** `outbox`, `inbox` and `leases` are high-churn tables. Default autovacuum is usually enough at tier S; watch bloat and tune per table at higher rates.
- **Backups.** Everything SourceFlow needs (events, outbox, checkpoints, read models) is in one schema, so a consistent backup of the schema is a consistent backup of the service.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Startup fails: `The SourceFlow schema 's' is not installed` | `AutoMigrate` is off outside `Development` and no migration ran. | Run `dotnet sourceflow db migrate`, apply `db script` output, or set `AutoMigrate = true`. |
| Startup fails: `missing migrations V004, V005` | The package was upgraded but the schema was not. | Run `db migrate` with the new tool or package. |
| `Migration V00n__… was modified after it was applied` | The checksum of an applied script differs. | Never edit applied scripts; restore the original package version. |
| `Row-level security must be enabled and forced for TenantIsolation.RowLevelSecurity, but is not on …` | The database and the configuration disagree. | Run `db migrate --rls` (or without `--rls` for `Runtime`). |
| `TenantIsolation.RowLevelSecurity needs SystemConnectionString or SystemDataSource` | No `BYPASSRLS` connection for the global log. | Set `SystemConnectionString` to a `BYPASSRLS` role. |
| Queries or loads return nothing under row-level security | The role sees no rows without `sourceflow.tenant`; custom SQL ran outside a session. | Use `IStorageSession.Connection`, or set the tenant yourself in the same transaction. |
| Subscriptions stop advancing; `sourceflow.subscription.horizon_age` grows | A long writing transaction holds `pg_snapshot_xmin` back. | Find it in `pg_stat_activity` (`backend_xid IS NOT NULL`) and end it; keep long batch jobs out of this database. |
| Async projections lag by about `PollInterval` on other nodes | `LISTEN` cannot connect (PgBouncer in transaction mode, network). | Set `ListenConnectionString` to a direct connection. |
| `53300: too many clients` | Pools across instances exceed `max_connections`. | Lower `Maximum Pool Size` or raise `max_connections`. |
| Inserts fail with `no partition of relation "events" found for row` | `PartitionEvents` is on and no partition covers the current month (log event 2302). | Run `db partitions ensure` as the owner; check that the maintenance job runs. |
| `db partitions detach` refuses a partition | Its range has not ended, or it holds events of open streams. | Archive or delete the streams first (`ArchiveStreamAsync`), then detach. |
| `ConcurrencyException` in custom code using `session.Connection` | Writing through the connection before the queued append breaks ES-R1. | Call `await session.FlushAsync(ct)` before writing through `Connection`. |

## Related documents

- [Architecture/05 Event Store and Persistence](Architecture/05-Event-Store-and-Persistence.md) and [Architecture/06 Projections and Read Models](Architecture/06-Projections-and-Read-Models.md)
- [SourceFlow.Storage.SqlServer](SourceFlow.Storage.SqlServer-README.md) and [SourceFlow.Storage.EntityFrameworkCore](SourceFlow.Storage.EntityFrameworkCore-README.md)
- [Message Idempotency Guide](Message-Idempotency-Guide.md)
- [SourceFlow.Net core](SourceFlow.Net-README.md), [SourceFlow.Tool](SourceFlow.Tool-README.md), [Observability and Performance](Observability-and-Performance.md)
- Design: [doc 05 Event Store and Persistence](../Design/05-Event-Store-and-Persistence.md), [doc 09 Performance and Scalability](../Design/09-Performance-and-Scalability.md)
- ADRs: [ADR-009](../ADRs/ADR-009-event-store-on-adonet-postgresql-first.md), [ADR-010](../ADRs/ADR-010-commit-ordered-global-position-for-gap-free.md), [ADR-022](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md), [ADR-028](../ADRs/ADR-028-postgresql-payload-columns-and-adapter-refinements.md), [ADR-032](../ADRs/ADR-032-blue-green-read-model-generations-behind-forward-only-aliases.md)
