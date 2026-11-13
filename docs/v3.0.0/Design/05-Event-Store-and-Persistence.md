# 05 — Event Store and Persistence

Design drivers: [ADR-002](../ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md), [ADR-006](../ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md), [ADR-008](../ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md), [ADR-009](../ADRs/ADR-009-event-store-on-adonet-postgresql-first.md), [ADR-010](../ADRs/ADR-010-commit-ordered-global-position-for-gap-free.md) and [ADR-016](../ADRs/ADR-016-projections-inline-or-async-with-partitioned-fenced.md). Fixes: GAP-04, GAP-05, GAP-15, GAP-17, CMD-05, EF-06, EF-21 and NFR-PERF-03.

## 1. Ports

The core defines small role interfaces (ISP). A storage adapter implements all of them for its database, and the storage conformance kit verifies them ([doc 11](11-Testing-and-Quality.md)).

```csharp
public interface IEventStore                         // streams
{
    ValueTask<StreamSlice> ReadStreamAsync(StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken ct);
    IAsyncEnumerable<StoredEvent> ReadStreamBackwardsAsync(StreamId stream, int maxCount, CancellationToken ct);
    ValueTask<StreamInfo?> GetStreamInfoAsync(StreamId stream, CancellationToken ct);
}

public interface IEventLogReader                     // global, commit-ordered (ADR-010)
{
    ValueTask<LogBatch> ReadAsync(GlobalPosition after, BucketRange buckets, int maxCount, CancellationToken ct);
    ValueTask<GlobalPosition> GetSafeHorizonAsync(CancellationToken ct);
}

public interface IStorageSessionFactory { ValueTask<IStorageSession> BeginAsync(CancellationToken ct); }

public interface IStorageSession : IAsyncDisposable  // one DB transaction = one unit of work
{
    void Append(StreamId stream, string streamType, ExpectedVersion expected, ReadOnlySpan<EventToAppend> events);
    void EnqueueOutbox(ReadOnlySpan<OutboxMessage> messages);
    ValueTask<InboxEntry?> InboxSeenAsync(string consumer, MessageId id, CancellationToken ct);
    void RecordInbox(string consumer, MessageId id, InboxResult result);
    void SaveCheckpoint(string subscription, int partition, GlobalPosition position, FencingToken token);
    void SaveProcessState(ProcessKey key, ProcessVersion expected, ReadOnlyMemory<byte> state, bool completed);
    IDocumentSession Documents { get; }               // projection writes in this transaction
    DbConnection Connection { get; }                  // for EF Core / custom projections (same tx)
    DbTransaction Transaction { get; }
    ValueTask FlushAsync(CancellationToken ct);                  // sends queued writes now (append first), no commit
    ValueTask<CommitResult> CommitAsync(CancellationToken ct);   // flushes all queued writes in ONE round trip
}

public interface ISnapshotStore { /* Get / Save (outside the command transaction) */ }
public interface ILeaseManager  { /* TryAcquire / Renew / Release → FencingToken */ }
public interface IDeadLetterStore { /* Park / Query / Get / MarkReplayed / Discard / Purge */ }
```

**Write-behind session.** `Append`, `EnqueueOutbox`, `RecordInbox`, `SaveCheckpoint` and the document writes are *queued* in memory and sent as **one batch** in `CommitAsync` (`NpgsqlBatch` or one SQL Server command batch). Only operations that need an answer before deciding (`InboxSeenAsync`, loads) go to the server during the session. That gives exactly one round trip for the write side of a typical command.

### 1.1 Value types

| Type | Shape | Notes |
|------|-------|-------|
| `StreamId` | `string`, ≤ 200 chars, `{aggregate}-{id}` | Validated at construction |
| `StreamVersion` | `long`. `None = 0`; the first event is 1 | |
| `ExpectedVersion` | `Any`, `NoStream`, `StreamExists`, or `Exact(v)` | |
| `GlobalPosition` | `(ulong Commit, ulong Sequence)`, compared lexicographically | PostgreSQL: `(xid8, seq)`. SQL Server: `(rowversion, 0)` |
| `Bucket` | `ushort` 0–1023 = `xxHash32(tenant_id + '/' + stream_id) & 1023` | Computed by the client at append; fixes the stream→partition mapping and spreads tenants ([ADR-022](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)) |

## 2. PostgreSQL schema (reference implementation, PostgreSQL 15+)

```sql
CREATE SCHEMA IF NOT EXISTS sourceflow;

-- Streams: one row per aggregate instance; the concurrency anchor
CREATE TABLE sourceflow.streams (
    tenant_id    text        NOT NULL DEFAULT '-',          -- ADR-022; '-' = single-tenant default
    stream_id    text        NOT NULL,
    stream_type  text        NOT NULL,
    version      bigint      NOT NULL,
    is_deleted   boolean     NOT NULL DEFAULT false,
    is_archived  boolean     NOT NULL DEFAULT false,        -- excluded from loads/rebuilds unless requested (§9)
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, stream_id)
);

-- Events: append-only
CREATE TABLE sourceflow.events (
    seq            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tx_id          xid8        NOT NULL DEFAULT pg_current_xact_id(),
    tenant_id      text        NOT NULL DEFAULT '-',
    bucket         smallint    NOT NULL,                     -- xxHash32(tenant_id, stream_id) & 1023
    stream_id      text        NOT NULL,
    stream_version bigint      NOT NULL,
    event_id       uuid        NOT NULL,
    event_type     text        NOT NULL,
    type_version   int         NOT NULL,
    tags           text[]      NOT NULL DEFAULT '{}',        -- DCB-ready (ADR-021)
    data           json        NOT NULL,      -- exact payload text (ADR-028)
    metadata       jsonb       NOT NULL,      -- envelope attributes: correlation, causation, trace, source, actor
    created_at     timestamptz NOT NULL,      -- decision time (ctx.Now), written explicitly; SFQL 'time'
    CONSTRAINT uq_events_stream_version UNIQUE (tenant_id, stream_id, stream_version),
    CONSTRAINT uq_events_event_id UNIQUE (event_id)
);
CREATE INDEX ix_events_commit_order ON sourceflow.events (tx_id, seq) INCLUDE (bucket);
-- Read-side indexes used by SFQL (doc 14 §7.2)
CREATE INDEX ix_events_type_time   ON sourceflow.events (tenant_id, event_type, created_at);
CREATE INDEX ix_events_correlation ON sourceflow.events ((metadata->>'correlationId'));
CREATE INDEX ix_events_causation   ON sourceflow.events ((metadata->>'causationId'));
CREATE INDEX ix_events_tags        ON sourceflow.events USING gin (tags);   -- created when EnableTags = true
-- + generated partial expression indexes for [Queryable] fields (`dotnet sourceflow db index sync`)
```

With `TenantIsolation.RowLevelSecurity` ([ADR-022](../ADRs/ADR-022-conjoined-multi-tenancy-with-row-level-security.md)):

- Migration V002 creates a `tenant_isolation` policy on `streams`, `events`, `snapshots`, `documents`, `process_instances` and `subject_streams`. Each policy is `USING` and `WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true))`.
- The migrator **enables and forces** row-level security on those tables. Forcing makes the policies apply to the table owner too, so only superusers and `BYPASSRLS` roles skip them.
- Startup verification fails when the database's state differs from the configured isolation.
- Every session runs `set_config('sourceflow.tenant', tenant, true)` at `BeginAsync`, so custom SQL on `session.Connection` is scoped too. `set_config` writes nothing, so ES-R1 still holds.
- Tenant-scoped reads outside a session send the setting in the same batch, which is one implicit transaction.
- With no setting, the application role sees no rows.
- The global log (subscriptions) is read through `SystemConnectionString`, a role with `BYPASSRLS`, because it spans tenants.
- `inbox`, `outbox`, `checkpoints`, `leases` and `dead_letters` hold no tenant payload and are not policy-protected.

`metadata` is `jsonb` so operators can query by correlation or causation (`metadata->>'correlationId'`) using an optional expression index. `data` is `json`, not `jsonb` ([ADR-028](../ADRs/ADR-028-postgresql-payload-columns-and-adapter-refinements.md)): it keeps the serializer's exact bytes and accepts every valid JSON text (including `\u0000`), while still supporting `->>` and expression indexes for debugging, ad-hoc analysis and SFQL. Fields protected by crypto-shredding are ciphertext strings inside it ([doc 10 §4](10-Security-and-Compliance.md#4-personal-data-in-an-immutable-log-crypto-shredding)).

## 3. Append

### 3.1 SQL (exact expected version)

```sql
WITH s AS (
    UPDATE sourceflow.streams
       SET version = version + @count, updated_at = now()
     WHERE tenant_id = @tenant AND stream_id = @stream_id AND version = @expected
       AND NOT is_deleted AND NOT is_archived
 RETURNING version - @count AS base)
INSERT INTO sourceflow.events (tenant_id, bucket, stream_id, stream_version, event_id, event_type, type_version,
                               tags, data, metadata, created_at)
SELECT @tenant, @bucket, @stream_id, s.base + e.ord, e.id, e.type, e.ver,
       ARRAY(SELECT jsonb_array_elements_text(e.tags)),   -- per-event tag lists are jagged, so passed as jsonb arrays
       e.data, e.meta, @decided_at
  FROM s, unnest(@ids::uuid[], @types::text[], @vers::int[], @tags::jsonb[], @datas::jsonb[], @metas::jsonb[])
       WITH ORDINALITY AS e(id, type, ver, tags, data, meta, ord);
```

| Expected | Stream statement in the CTE |
|----------|-----------------------------|
| `Exact(v)` | `UPDATE … WHERE version = v` (above) |
| `NoStream` | `INSERT INTO streams … ON CONFLICT (stream_id) DO NOTHING RETURNING 0 AS base` |
| `StreamExists` | `UPDATE … WHERE NOT is_deleted AND NOT is_archived` (no version predicate) |
| `Any` | `INSERT INTO streams … ON CONFLICT DO UPDATE SET version = streams.version + @count WHERE NOT is_deleted AND NOT is_archived` |

Both take the stream row lock and apply to the committed version they then observe, so every append that reaches the database is still a compare-and-set on an observed version, which §5.3 relies on ([ADR-028](../ADRs/ADR-028-postgresql-payload-columns-and-adapter-refinements.md)).

**ES-R2: one stream append per session.** A command changes one aggregate (the DDD consistency boundary). The session rejects a second `Append` to a different stream. Bulk operations such as the v2 import use one session per stream.

**Conflict detection.** The statement affects 0 rows exactly when the stream predicate fails. The session checks the affected-row count and raises `ConcurrencyException`, then rolls back. That also discards every other queued write in the same batch (outbox, inbox, projections). A unique violation on `uq_events_stream_version` is mapped to `ConcurrencyException` as a backstop. **No retries happen in the storage layer.** Re-deciding is the command processor's job ([ADR-008](../ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md)), and the resilience pipeline excludes `ConcurrencyException` (fixes EF-21).

### 3.2 One round trip

`CommitAsync` sends one `NpgsqlBatch`. In order, it contains:

1. `BEGIN` (implicit).
2. The append CTE.
3. The outbox `INSERT … SELECT unnest(…)`.
4. The inbox insert.
5. The document upserts: one multi-row `unnest` upsert when each document is written once.
6. The process-state and checkpoint writes.
7. `SELECT pg_notify(…)`.
8. `COMMIT`.

The conditional statements assert in SQL (`sf_assert`, SQLSTATE `SF001`), so a failed check aborts the batch before its `COMMIT` ([ADR-028](../ADRs/ADR-028-postgresql-payload-columns-and-adapter-refinements.md)). `Max Auto Prepare` keeps these statements prepared on the server.

### 3.3 Cost

The append is O(events appended): one indexed update and n inserts. Unlike v2 (NFR-PERF-03), it never reads the stream history to find the next version.

## 4. Reading a stream and loading an aggregate

```text
Load(stream):
  if cache has (state, v):                       start = (state, v)          // optional, §8.2
  elif snapshot store has (state, v, hash==current): start = (state, v)
  else:                                          start = (Initial, 0)
  tail = SELECT … FROM events WHERE stream_id = @s AND stream_version > @v ORDER BY stream_version
  state = fold(Evolve, start.state, upcast(deserialize(tail)))
  return (state, v + |tail|)
```

The tail read uses `uq_events_stream_version` (an index range scan). Deserialisation streams rows from `NpgsqlDataReader` with `GetFieldValue<ReadOnlyMemory<byte>>`-style access into the source-generated JSON reader, with no intermediate strings.

## 5. Global ordering and the safe horizon

### 5.1 The problem

An identity value (`seq`) is assigned at **insert**, but becomes visible at **commit**. Consider two transactions:

| Time | Tx A | Tx B | Reader with checkpoint = 10 |
|------|------|------|-----------------------------|
| t1 | inserts seq 11 | | |
| t2 | | inserts seq 12, commits | reads `seq > 10` → sees 12, checkpoint = 12 |
| t3 | commits | | reads `seq > 12` → **11 is lost forever** |

v2 had no global reads. Naive v3 designs, and several event stores, have this bug.

### 5.2 PostgreSQL solution: order by transaction id, read below `xmin`

- Every event row records `tx_id = pg_current_xact_id()`. The value is the same for every row in a transaction, and is assigned when the transaction first writes.
- `pg_snapshot_xmin(pg_current_snapshot())` is the smallest transaction id still **in progress**. Every transaction with `tx_id < xmin` has finished (committed or aborted), so no new row with such a `tx_id` can ever appear.
- Readers therefore read:

```sql
SELECT seq, tx_id, bucket, stream_id, stream_version, event_id, event_type, type_version, data, metadata, created_at
  FROM sourceflow.events
 WHERE (tx_id, seq) > (@after_tx, @after_seq)
   AND tx_id < pg_snapshot_xmin(pg_current_snapshot())
   AND bucket BETWEEN @bucket_lo AND @bucket_hi
 ORDER BY tx_id, seq
 LIMIT @max;
```

The set of rows below the horizon is **final**. The adapter reports the horizon inclusively as `(xmin − 1, max)`: every position at or below `LogBatch.Horizon` is final ([ADR-028](../ADRs/ADR-028-postgresql-payload-columns-and-adapter-refinements.md)). Ordering by `(tx_id, seq)` is total and stable, so a checkpoint `(tx_id, seq)` can never skip a row. The `bucket` filter is answered from the `INCLUDE` column of `ix_events_commit_order`, so partitioned readers scan only the index range and fetch heap rows only for their own buckets.

### 5.3 Per-stream order equals global order

**Claim.** For any stream, if append B commits version `k+1` after append A committed version `k`, then `tx_id(A) < tx_id(B)`. Subscriptions therefore see each stream's events in version order.

**Argument.**
1. Every append is a compare-and-set on an exact version (§3.1). B succeeded with `expected = k`, so B must have *observed* version `k`. That is only possible after A committed.
2. A transaction id is assigned when a transaction first writes (`pg_current_xact_id()` assigns it on first call). **ES-R1: the stream write is the first write in every session.** Inbox and outbox writes are queued after it, and the inbox *check* is a read, which assigns no id.
3. So B's id was assigned after B observed A's commit, and A's id was assigned before A committed. Transaction ids are assigned monotonically, so `tx_id(A) < tx_id(B)`. ∎

Without ES-R1, B could take an id early (by writing something else first), wait on A's row lock, and then commit with a *lower* `tx_id` than A, delivering events out of order. The session enforces ES-R1 by construction (queued writes, append flushed first), and a conformance test asserts it by injecting a prior write and expecting `InvalidOperationException`.

### 5.4 Operational implication

A long-running *writing* transaction anywhere in the database holds `xmin` back and delays (never corrupts) subscriptions. Read-only transactions don't, because they have no transaction id. `sourceflow.subscription.horizon_age` measures how old the oldest in-progress transaction is, and the health check turns `Degraded` above a threshold (default 30 s). Guidance: keep the event store database for SourceFlow workloads, or at least avoid long write transactions in it.

### 5.5 SQL Server solution

```sql
CREATE TABLE sourceflow.Events (
    Seq           bigint IDENTITY NOT NULL,
    Rv            rowversion      NOT NULL,
    Bucket        smallint        NOT NULL,
    StreamId      nvarchar(200)   NOT NULL,
    StreamVersion bigint          NOT NULL,
    EventId       uniqueidentifier NOT NULL,
    EventType     varchar(200)    NOT NULL,
    TypeVersion   int             NOT NULL,
    Data          nvarchar(max)   NOT NULL,     -- 'json' type on SQL Server 2025+ (opt-in)
    Metadata      nvarchar(max)   NOT NULL,
    CreatedAt     datetime2(7)    NOT NULL DEFAULT sysutcdatetime(),
    CONSTRAINT PK_Events PRIMARY KEY CLUSTERED (Seq),
    CONSTRAINT UQ_Events_Stream UNIQUE (StreamId, StreamVersion),
    CONSTRAINT UQ_Events_EventId UNIQUE (EventId));
CREATE UNIQUE INDEX IX_Events_Rv ON sourceflow.Events (Rv) INCLUDE (Bucket);

-- Safe read
SELECT TOP (@max) … FROM sourceflow.Events
 WHERE Rv > @after AND Rv < MIN_ACTIVE_ROWVERSION() AND Bucket BETWEEN @lo AND @hi
 ORDER BY Rv;
```

`MIN_ACTIVE_ROWVERSION()` is the lowest rowversion that is still uncommitted, so every row below it is final. The append uses `UPDATE … WITH (ROWLOCK)` on `Streams`, then `INSERT … SELECT` from a table-valued parameter. A zero `@@ROWCOUNT` raises error 50001, which maps to `ConcurrencyException`. ES-R1 applies here too. `READ_COMMITTED_SNAPSHOT ON` is recommended for read concurrency.

**As built (WP-4.4).** `SourceFlow.Storage.SqlServer` follows this design with these differences:

- **Engine.** SQL Server 2022 or later, or Azure SQL Database. The SFQL compiler uses `DATE_BUCKET` for `bin()`, which needs 2022.
- **Position.** `GlobalPosition` is `(CAST(rv AS bigint), seq)`. `seq` is the identity key, so rows that share a commit still have a total order. The horizon is `(MIN_ACTIVE_ROWVERSION() - 1, ulong.MaxValue)`.
- **Append.** The rows travel as one JSON parameter read with `OPENJSON`, not as a table-valued parameter. A TVP needs a user-defined table type per schema, and that type cannot be changed while it is in use. A failed version check raises `THROW 50001`. Fenced writes raise `THROW 50002` with a check code.
- **Batches.** A session's writes go as one command batch, split at 2,000 parameters. SQL Server allows at most 2,100.
- **Upserts.** Upserts use `MERGE … WITH (HOLDLOCK)`. Leases use the database clock.
- **Keys.** Key columns are `nvarchar(100/200/400)` with `COLLATE Latin1_General_100_BIN2`, so ids compare ordinally, as on PostgreSQL, whatever the database collation. A primary key over 900 bytes is `NONCLUSTERED`.
- **Notifications.** Notifications are in-process only. Other nodes find new events by polling at `PollInterval`, because SQL Server has no `LISTEN`.
- **Tenancy.** Tenancy is `TenantIsolation.Runtime`: every query filters on `tenant_id`. Row-level security, as on PostgreSQL in WP-2.6, is not provided.
- **Horizon age.** `GetHorizonAgeAsync` reads the oldest active transaction from DMVs, so the login needs `VIEW SERVER STATE`, or `VIEW DATABASE STATE` on Azure SQL. Without it the gauge reports zero.
- **Migrations.** The migrator takes `sp_getapplock` and records checksums in `schema_history`. `Script()` emits the same migration as one transaction, for review or DBA-run deployments.
- **SFQL.** Queries are compiled to one parameterised T-SQL statement on `QueryConnectionString`; for example, use `ApplicationIntent=ReadOnly` for a secondary. `QueryTimeout` is the command timeout.
  - **Text.** Text compares with `COLLATE Latin1_General_BIN2`, ordinal like the interpreter.
  - **Nulls.** `NULLS FIRST`/`LAST` becomes a `CASE` sort key.
  - **Booleans.** A boolean value is a `bit` built from a predicate.
  - **Fallback.** A construct SQL Server cannot express the same way falls back to the in-memory interpreter, which returns the same rows. Examples are the text of a decimal and arithmetic between two timestamps.
  - **Not yet.** Index sync for `[Queryable]` fields (persisted computed columns plus filtered indexes) is not built yet.

## 6. Isolation and transactions

- PostgreSQL uses **Read Committed**, the default. The conditional update provides the compare-and-set, so Serializable isn't needed.
- Every session is one transaction on one connection from `NpgsqlDataSource` (pooled). EF Core projections join it with `DbContext.Database.UseTransaction(session.Transaction)` on the same connection, with no distributed transaction.
- **Inline projections and read models in another database** are rejected at startup: an inline projection needs the same database as the events. Async projections may target any database (doc 06 §5).

## 7. Supporting tables (PostgreSQL; SQL Server equivalents are analogous)

### 7.1 Outbox

```sql
CREATE TABLE sourceflow.outbox (
    seq           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    message_id    uuid        NOT NULL,
    partition     smallint    NOT NULL,           -- hash(partition_key) % OutboxPartitions
    partition_key text,                           -- NULL = unordered (e.g. timers)
    destination   text        NOT NULL,           -- logical endpoint or topic
    kind          smallint    NOT NULL,           -- 1 send, 2 publish, 3 scheduled
    attributes    jsonb       NOT NULL,           -- envelope attributes (CloudEvents)
    body          bytea       NOT NULL,
    deliver_at    timestamptz NOT NULL DEFAULT now(),
    attempts      int         NOT NULL DEFAULT 0,
    last_error    text,
    created_at    timestamptz NOT NULL DEFAULT now());
CREATE INDEX ix_outbox_ready ON sourceflow.outbox (partition, deliver_at, seq);
CREATE INDEX ix_outbox_key   ON sourceflow.outbox (partition, partition_key, seq) WHERE partition_key IS NOT NULL;

-- Relay claim: only the HEAD row per key is eligible (strict per-key order, even across failures).
-- @now is the host's TimeProvider (business time, like timers); leases use the database clock (ADR-028).
SELECT o.* FROM sourceflow.outbox o
 WHERE o.partition = @p AND o.deliver_at <= @now
   AND (o.partition_key IS NULL OR NOT EXISTS (
        SELECT 1 FROM sourceflow.outbox h
         WHERE h.partition = o.partition AND h.partition_key = o.partition_key AND h.seq < o.seq))
 ORDER BY o.seq LIMIT @batch;
```

There is no `FOR UPDATE SKIP LOCKED`, because a partition is owned by exactly one relay through a fenced lease (§7.4). That keeps the query simple and preserves order.

### 7.2 Inbox

```sql
CREATE TABLE sourceflow.inbox (
    consumer       text        NOT NULL,
    message_id     uuid        NOT NULL,
    processed_at   timestamptz NOT NULL DEFAULT now(),
    result_status  smallint,
    result_version bigint,
    result_error   text,
    PRIMARY KEY (consumer, message_id));
CREATE INDEX ix_inbox_processed_at ON sourceflow.inbox (processed_at);
-- Cleanup: DELETE … WHERE ctid IN (SELECT ctid … WHERE processed_at < now() - @retention LIMIT 5000), looped
```

### 7.3 Checkpoints

```sql
CREATE TABLE sourceflow.checkpoints (
    subscription text     NOT NULL,
    partition    int      NOT NULL,
    commit_pos   xid8     NOT NULL DEFAULT '0',
    seq_pos      bigint   NOT NULL DEFAULT 0,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (subscription, partition));

-- Fenced save: succeeds only while this instance still holds the lease with this token
UPDATE sourceflow.checkpoints c
   SET commit_pos = @tx, seq_pos = @seq, updated_at = now()
  FROM sourceflow.leases l
 WHERE c.subscription = @sub AND c.partition = @p
   AND l.name = @lease AND l.owner = @me AND l.token = @token AND l.expires_at > now();
-- 0 rows ⇒ LeaseLostException ⇒ whole transaction (incl. read-model writes) rolls back
```

### 7.4 Leases

```sql
CREATE TABLE sourceflow.leases (
    name       text PRIMARY KEY,               -- e.g. 'sub:account-summary:3', 'outbox:7'
    owner      text,
    token      bigint NOT NULL DEFAULT 0,      -- fencing token, strictly increasing
    expires_at timestamptz);

-- Acquire or renew (database clock only; no reliance on instance clocks)
UPDATE sourceflow.leases
   SET owner = @me, expires_at = now() + @ttl,
       token = CASE WHEN owner = @me AND expires_at > now() THEN token ELSE token + 1 END
 WHERE name = @name AND (owner IS NULL OR owner = @me OR expires_at <= now())
RETURNING token;
```

The TTL defaults to 30 s and is renewed every 10 s. A lost renewal stops work on that partition immediately. Fencing (§7.3) guarantees that a paused former owner cannot commit after a new owner has taken over.

### 7.5 Dead letters, process state, snapshots, documents, keys

```sql
CREATE TABLE sourceflow.dead_letters (
    id uuid PRIMARY KEY, consumer text NOT NULL, source text NOT NULL,       -- endpoint / subscription / outbox
    message_id uuid NOT NULL, message_type text, attributes jsonb NOT NULL, body bytea NOT NULL,
    reason text NOT NULL, error_type text, error_message text, error_detail text,
    attempts int NOT NULL, first_failed_at timestamptz NOT NULL, parked_at timestamptz NOT NULL DEFAULT now(),
    status smallint NOT NULL DEFAULT 0,                                       -- 0 parked, 1 replayed, 2 discarded
    status_changed_at timestamptz, status_note text);
CREATE INDEX ix_dead_letters_status ON sourceflow.dead_letters (status, parked_at);

CREATE TABLE sourceflow.process_instances (                               -- payloads are json (ADR-028)
    tenant_id text NOT NULL DEFAULT '-', process_type text, process_id text, version bigint NOT NULL, state json NOT NULL,
    completed boolean NOT NULL DEFAULT false, updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, process_type, process_id));

CREATE TABLE sourceflow.snapshots (
    tenant_id text NOT NULL DEFAULT '-', stream_id text, version bigint NOT NULL, state_type text NOT NULL,
    schema_hash text NOT NULL, data json NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, stream_id));

CREATE TABLE sourceflow.documents (
    tenant_id text NOT NULL DEFAULT '-', collection text, id text, version bigint NOT NULL, data json NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, collection, id));

CREATE TABLE sourceflow.nodes (                                             -- liveness for lease fair-share (doc 06 §4)
    node_id text PRIMARY KEY, roles int NOT NULL, heartbeat_at timestamptz NOT NULL);

CREATE TABLE sourceflow.collection_aliases (                                -- blue/green projection switch (doc 06 §6, ADR-032)
    alias text PRIMARY KEY, target text NOT NULL, switched_at timestamptz NOT NULL DEFAULT now(),
    previous text NULL,                                                     -- retired by the last switch, dropped after RetainPreviousFor
    building text NULL);                                                    -- requested by `projection rebuild`, cleared by its switch

CREATE TABLE sourceflow.subject_streams (                                   -- which streams hold a subject's PII (doc 10 §4)
    tenant_id text NOT NULL, subject_id text NOT NULL, stream_id text NOT NULL,
    PRIMARY KEY (tenant_id, subject_id, stream_id));

-- sourceflow.schemas (event schema catalog) is specified in doc 14 §2.2

CREATE TABLE sourceflow.data_keys (                                         -- crypto-shredding, doc 10
    subject_id text PRIMARY KEY, key_ref text NOT NULL, wrapped_key bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(), shredded_at timestamptz);
```

## 8. Snapshots and caching

### 8.1 Snapshots

- Each aggregate opts in with `[Aggregate("…", SnapshotEvery = 100, SnapshotVersion = 1)]`.
- After a successful commit that crosses a multiple of `SnapshotEvery`, the state is handed to a bounded background channel and written **outside** the command transaction. A lost snapshot is harmless; it is just a cache.
  - The channel holds `SnapshotQueueCapacity` entries (default 1,024). When it is full, the oldest entry is dropped, so commands never wait on snapshot I/O.
  - A failed write is dropped too. Both cases count in `sourceflow.snapshot.dropped`.
  - The host drains the queue on shutdown, within `ShutdownTimeout`. Without a running host, snapshots are written inline.
- `schema_hash = hash(SnapshotVersion, generated state-shape fingerprint)`. A shape change or an explicit `SnapshotVersion` bump invalidates old snapshots automatically. Bump `SnapshotVersion` when `When` logic changes without the shape changing; the analyzer can't detect that case.
- Guidance: turn snapshots on only when `sourceflow.aggregate.load.events` p95 exceeds about 200 for that aggregate type.

### 8.2 Aggregate state cache (opt-in)

- An in-process `MemoryCache` (size-limited) holds `(state, version)` per stream and is set after each commit.
- `CacheMode.ReadTail` (the default when enabled): start from the cached state and read the tail. Usually that is zero rows, from a single index probe.
- `CacheMode.Optimistic`: skip the tail read and rely on the append's version check. A stale entry causes a conflict, an automatic reload, and then success. This is best when partition-keyed routing gives stream affinity to one instance.
  - **A rejection or no-op decided on a cached state is confirmed against the tail before it is returned.** It appends nothing, so no version check would otherwise catch a stale state. If newer events exist, the runtime re-decides on fresh state.
  - A command with an expected version (`If-Match`) never takes the optimistic shortcut.
  - Enable the cache with `SourceFlowOptions.AggregateCache` and size it with `AggregateCacheSize`. Lookups count in `sourceflow.aggregate.cache.hits` with `result` set to hit, miss or stale.
- It is correct by construction, because optimistic concurrency never lets a stale decision commit.

## 9. Retention, archiving and growth

| Concern | 3.0 design | Revisit |
|---------|-----------|---------|
| Deleting a stream | Soft delete: `streams.is_deleted` plus a `$deleted` event; further appends are rejected. Hard deletion of event rows is **not** offered, because it breaks projections and audit. | — |
| Right to erasure (GDPR) | Crypto-shredding ([doc 10 §4](10-Security-and-Compliance.md#4-personal-data-in-an-immutable-log-crypto-shredding)) | — |
| Table growth | Default: one table, with autovacuum tuning guidance. **Opt-in `PartitionEvents`** (in 3.0): monthly `RANGE (created_at)` partitions created ahead of time by a maintenance job. Version uniqueness stays enforced by the `streams` compare-and-set, so `uq_events_stream_version` and `uq_events_event_id` become per-partition backstops (they must include the partition key). Each partition has its own `(tx_id, seq)` index, and the safe-horizon read probes them all. Recommended from capacity tier M ([doc 09 §5](09-Performance-and-Scalability.md#5-capacity-model)). | More than about 100 partitions per table: switch to quarterly ranges, or detach and archive |
| Cold streams | **3.0**: `IEventStore.ArchiveStreamAsync(stream)` sets `streams.is_archived`. Archived streams reject appends, are excluded from rebuilds and SFQL by default (`options (include_archived = true)` includes them), and stay loadable. With `PartitionEvents`, old partitions holding only archived streams can be detached. **3.1**: compaction appends a genesis "snapshot event", then archives the prefix (competitive analysis C3). | 3.x: transparent reads from object storage |
| Inbox, outbox, dead letters | Batched background cleanup with configurable retention. Adapters implement `IStoreRetention`, and the relay role runs it every `RetentionInterval` (1 h). Processed inbox rows older than `InboxRetention` (7 days) are deleted; redeliveries older than that are no longer detected. Replayed or discarded dead letters older than `DeadLetterRetention` (30 days) are deleted; parked ones are never deleted. Deletes run in batches of `RetentionBatchSize` (5,000) rows per statement. Outbox rows are deleted on delivery. | — |

**As built (WP-4.5).** The table above is implemented with these specifics.

- **Archived streams.** Archived streams are left out of read models by every projection subscription, not only by rebuilds. That way a rebuild produces the same documents as the live projection.
  - **Lookup.** The subscription asks `IEventStore.GetArchivedStreamsAsync` once per batch and tenant. It is one query, and only for events that some projection handles.
  - **SFQL.** `from events` applies the same rule, in the interpreter and in both SQL compilers (a `NOT EXISTS` on `streams`). `options (include_archived = true)` lifts it. `from stream` and `from aggregate` always read archived streams, as loads do.
  - **Permanence.** Archiving is permanent.
- **Converting the events table.** `PostgreSqlStorageOptions.PartitionEvents` converts the events table when the migrator runs, inside its transaction. The conversion is not a numbered script, and running it again does nothing.
  - **No copy.** No rows are copied. The old table becomes the partition `events_initial`, from MINVALUE to the start of the month after both its newest row and now. Monthly partitions `events_yyyy_MM` follow.
  - **Keys.** The primary key is `(seq, created_at)`. `seq` comes from the sequence `events_seq`, because PostgreSQL 15 and 16 reject identity columns on partitioned tables. An application role therefore needs `USAGE` on the sequence.
  - **Bookkeeping.** `event_partitions` records each range and when it was detached.
- **Maintenance.** `sf_ensure_event_partitions` creates partitions ahead of time under an advisory lock.
  - The migrator creates 3 months ahead.
  - A hosted job does the rest: `PartitionMonthsAhead` (default 3) every `PartitionMaintenanceInterval` (default 1 hour), on every node.
  - `created_at` comes from the runtime clock. A row past the last partition fails to insert, so the job logs an error (event 2302) when it cannot create partitions.
- **Detaching.** `PostgreSqlStore.DetachEventPartitionAsync`, or `dotnet sourceflow db partitions detach`, detaches a partition that meets both conditions:
  - its range has ended;
  - every stream with events in it is archived or deleted.

  The detached table keeps its rows for archiving, for example with `pg_dump -t`, and the operator drops it afterwards. Its events then leave stream reads, the global log and queries.
- **Query indexes.** Query-index sync builds the indexes of a partitioned table without `CONCURRENTLY`, because PostgreSQL does not support that on partitioned tables.
- **Commands.** `dotnet sourceflow db migrate --partition-events` converts an existing table. `db partitions list | ensure | detach` manages the partitions.
- **Tests.** SCK-65..68 and SCK-72 cover the partitioned variant, and the whole PostgreSQL suite also runs on it when `SOURCEFLOW_TEST_DATABASE` ends in `-partitioned` (nightly). SCK-69..71 cover the archive rules on every store.

## 10. Read-model stores

- **Document store** (`IDocumentSession` / `IDocumentReader<T>`): `jsonb` documents keyed by `(collection, id)`, with `Get`, `GetMany`, and a raw-SQL query escape hatch. Rebuilt collections are versioned (`account-summary@2`) for blue/green switches (doc 06 §6).
- **EF Core** (`SourceFlow.Storage.EntityFrameworkCore`): `IEfProjection<TDbContext>` handlers receive a `DbContext` bound to the session's connection and transaction. `SaveChanges` happens before the checkpoint in the same commit. EF Core is **not** used for the event store ([ADR-009](../ADRs/ADR-009-event-store-on-adonet-postgresql-first.md)).
  - EF projections are one kind of **session projection** (`ISessionProjection<TUnitOfWork>` in the core). The generator emits their `When` dispatch, and their module registers them. A storage package supplies an `IProjectionUnitOfWork<T>` that creates the unit of work on the session and completes it before the commit. For EF Core that registration is `UseEntityFrameworkProjections<TDbContext>((o, c) => o.UseNpgsql(c), o => new ReportingDbContext(o))`. A session projection without its unit of work fails at startup.
  - **ES-R1 still holds.** Before creating the first unit of work, the runtime calls `IStorageSession.FlushAsync`, which sends the queued append first. EF writes therefore always follow the append, and a conflict surfaces before any EF write. Code that writes through `session.Connection` directly must call `FlushAsync` first.
  - `IdempotentUpsert.UpsertAsync` applies a change only when the source version is newer than the row's `IVersionedReadModel.Version`, so replays and rebuilds are harmless.
  - Tracked changes are saved once, at the end. Set-based statements (`ExecuteUpdateAsync`) run immediately, so a projection should not mix the two on the same rows.
- **Custom**: `IProjection` with `IStorageSession.Connection` for any SQL.
- **No process-global state.** The model and type configuration live in DI-registered options (fixes GAP-17).

## 11. Schema management

- Versioned SQL migration scripts are embedded in each storage package and tracked in `sourceflow.schema_history`. A PostgreSQL advisory lock (or `sp_getapplock` on SQL Server) serialises concurrent migrators.
- `AutoMigrate` defaults to `true` in Development and `false` otherwise. Production runs `dotnet sourceflow db migrate` or `db script` (emits SQL for DBAs).
- Least privilege: the runtime role needs `SELECT/INSERT/UPDATE/DELETE` on the SourceFlow tables only. No DDL is needed unless `AutoMigrate` is on.
