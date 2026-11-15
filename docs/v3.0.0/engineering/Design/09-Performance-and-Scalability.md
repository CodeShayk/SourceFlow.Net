# 09 — Performance and Scalability

Every number in this document is a **target** or an **estimate**, to be validated by the benchmark and load-test programme in §7. Design drivers: [ADR-001](../ADRs/ADR-001-target-dotnet-10-and-csharp-14.md), [ADR-004](../ADRs/ADR-004-source-generation-instead-of-reflection.md), [ADR-008](../ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md), [ADR-009](../ADRs/ADR-009-event-store-on-adonet-postgresql-first.md), [ADR-010](../ADRs/ADR-010-commit-ordered-global-position-for-gap-free.md) and [ADR-016](../ADRs/ADR-016-projections-inline-or-async-with-partitioned-fenced.md). Fixes: NFR-PERF-03, NFR-PERF-04, NFR-PERF-07.

## 1. Hot-path rules

| # | Rule |
|---|------|
| HP-1 | No reflection, `Activator`, `MakeGenericMethod` or `Type.GetType` after startup. Every dispatch goes through generated `switch` statements or `FrozenDictionary` lookups ([ADR-004](../ADRs/ADR-004-source-generation-instead-of-reflection.md)). |
| HP-2 | `ValueTask` on every hot async API. No `async void`, no sync-over-async, `ConfigureAwait(false)` in library code. |
| HP-3 | No LINQ, closures or `params object[]` on the hot path. Logging uses `[LoggerMessage]`. |
| HP-4 | Serialisation writes to pooled `IBufferWriter<byte>` and reads from `ReadOnlySequence<byte>`, with no intermediate `string` for bodies. |
| HP-5 | One database round trip per command write (doc 05 §3.2). At most one more for the load (zero with the optimistic cache). |
| HP-6 | Bounded queues everywhere (`Channel.CreateBounded`). Backpressure, never unbounded buffering. |
| HP-7 | No lock on the hot path. Registries are immutable, and caches are lock-free (`MemoryCache`) or sharded. `System.Threading.Lock` is used only on cold paths. |
| HP-8 | Every allocation of a user-visible object (events, commands) is the user's. The framework aims for a fixed per-message overhead. |

## 2. Local command: cost budget

| Step | Allocation target | Notes |
|------|-------------------|-------|
| `SendAsync` + route lookup | 0 B | `FrozenDictionary<Type, CommandRoute>` |
| Envelope + context | ≤ 400 B | One `MessageEnvelope` and one `MessageContext` |
| Middleware chain | 0 B steady state | Delegates composed once per type and cached |
| Load (in-memory store or cache hit) | ≤ 200 B | Generated `Evolve` switch over the tail |
| `Decide` | User's events only | `Decision` is a struct |
| Serialise events and outbox | Pooled | Source-generated `JsonTypeInfo` into rented buffers |
| Commit (in-memory) | ≤ 300 B | |
| **Total framework overhead** | **≤ 2 KB per command; p50 ≤ 20 µs** (in-memory store, 1 event) | Benchmark `CommandPipelineBench` |

With PostgreSQL, the dominant cost is two round trips (load and commit), about 1–3 ms within an AZ. Target: append (commit) p99 ≤ 10 ms, end-to-end command p99 ≤ 25 ms at 70 % database CPU.

## 3. Concurrency model

```text
                 ┌──────────── API requests (Kestrel) ─────────────┐
                 ▼                                                 ▼
          CommandProcessor  (async, per request; no shared mutable state)
                 │
 Receivers ──► Partitioned dispatcher ──► W workers (bounded channels, per-key order)
 Subscription engine ──► one worker per owned (subscription, partition) lease
 Outbox relay ──► one worker per owned outbox partition lease
 Scheduler ──► part of the outbox relay
 Snapshotter ──► single background consumer of a bounded channel (drop-oldest)
```

- **Parallelism knobs**: `Receive.MaxConcurrency` (default 2 × cores), `Subscription.Partitions` (per subscription), `Outbox.Partitions` (default 16), and the database connection pool.
- **Hot streams**: optimistic concurrency causes retries when many writers hit one stream. Mitigations, in order:
  1. Route by partition key so that one instance processes a stream's commands serially (RX-R1).
  2. Use `CacheMode.Optimistic` with that affinity, so the load costs nothing.
  3. Redesign the aggregate boundary (the usual root cause).

  `concurrency_conflicts` shows which aggregate types are affected.

## 4. Database efficiency

| Operation | Round trips | Statements |
|-----------|-------------|------------|
| Load aggregate (no cache) | 1 | Snapshot `SELECT` + tail `SELECT`, in one batch |
| Commit command | 1 | Append CTE, outbox `INSERT … unnest`, inbox `INSERT`, inline projection upserts, `pg_notify` |
| Subscription batch (N events) | 2 (+1 for document `GetMany`) | Read batch, then one write batch (upserts + fenced checkpoint) |
| Outbox relay batch (N messages) | 2 | Claim `SELECT`, then a `DELETE … WHERE seq = ANY(@ids)` after the broker acknowledges |

- **Prepared statements**: Npgsql `Max Auto Prepare=20` and stable SQL text per operation.
- **Connection pool sizing**: `Max Pool Size ≥ API concurrency + Receive.MaxConcurrency + owned subscription leases + owned outbox leases + 4`. A startup warning fires if the configured pool is smaller.
- **PgBouncer**: transaction pooling is supported, which needs protocol-level prepared statement support (PgBouncer 1.21+). The `LISTEN` wake-up connection must bypass PgBouncer (`ListenConnectionString`), and falls back to polling if it isn't set.

## 5. Capacity model

The estimates assume 1.5 events per command, 0.5 integration events per command, about 600 B event payloads, and about 1.1 KB per event row including indexes and overhead. Average load is taken as 15 % of peak.

| Tier | Peak commands/s | Event rows/day | Event storage/year | Outbox inserts/s (peak) | Guidance |
|------|-----------------|----------------|--------------------|-------------------------|----------|
| **S** | 100 | ~2 M | ~0.8 TB | 50 | One table, defaults |
| **M** | 1,000 | ~19 M | ~7.7 TB | 500 | Turn on `PartitionEvents` from day 1. Snapshots for long streams. `Outbox.Partitions` 16–32 |
| **L** | 10,000 | ~194 M | ~77 TB | 5,000 | Partitioned events. Database per service (already the rule). Consider an archive tier. Several relay instances. A dedicated subscription node pool |

**Consequence for the design.** Tier M exceeds the 1 TB comfort zone within weeks. So `PartitionEvents` (monthly range partitions on `created_at`) is an **opt-in schema variant in 3.0**, not a later feature. This updates the YAGNI register in [doc 01](01-Architecture-Overview.md#4-deliberately-not-built-yagni-register) and the growth row in [doc 05 §9](05-Event-Store-and-Persistence.md#9-retention-archiving-and-growth). It is safe because:

- version uniqueness is enforced by the `streams` compare-and-set, so the event-level unique constraints can become per-partition backstops;
- the safe-horizon query probes the `(tx_id, seq)` index of each partition, and with monthly partitions there are only a few dozen probes;
- old partitions can be detached and archived without touching hot data.

**Throughput ceiling per database.** A well-provisioned PostgreSQL instance (16 vCPU, NVMe) sustains roughly 10–20k small transactional commits per second with these statement shapes. That is the expected ceiling for tier L, and the M3 load test must confirm it. Beyond it, split services across databases (the bounded-context rule) rather than sharding one service's event store (not in 3.0).

## 6. Scalability model

| Scale unit | How it scales | Limit |
|------------|---------------|-------|
| Command API instances | Stateless, behind a load balancer | Database write throughput; contention on hot streams |
| Message receivers | Competing consumers per endpoint, with per-key order | Broker partition, group or session concurrency; database |
| Async projections | Up to `P` partition workers per subscription, spread by leases | `P` (≤ 1024). Read-model database |
| Process managers | Same as projections (subscription partitions) and receivers | As above |
| Outbox relay | Up to `Outbox.Partitions` workers across instances | Broker publish rate |
| Database | Vertical scaling, read replicas for *read models only*, one database per service | Single-primary write throughput |

The roles feature ([doc 01 §9](01-Architecture-Overview.md#9-hosting-model)) lets API pods (`CommandApi`) and worker pods (`Workers`) scale on different signals: request rate versus `subscription.lag` and `outbox.age`. KEDA can scale workers on the exported lag metric.

## 7. Benchmark and load-test programme

| Level | Tool | What it measures | Gate |
|-------|------|------------------|------|
| Micro | BenchmarkDotNet (`[MemoryDiagnoser]`, `[EventPipeProfiler]`) | Envelope mapping, serialiser, `Decide`/`Evolve` dispatch, middleware overhead, type-registry lookup, partitioned dispatcher | CI fails on > 10 % time or any allocation regression against the baseline on `main` |
| Component | BenchmarkDotNet + Testcontainers PostgreSQL | Append (1, 3, 10 events), load (0, 100, 1,000 events, with and without snapshot), subscription batch, outbox relay batch | Nightly. Trend dashboard. 15 % regression = issue |
| System | NBomber (or k6 driving a sample API) + Testcontainers or real cloud | Mixed workload at tiers S and M; soak test 4 h; recovery after broker and database restarts | Before each milestone exit and before RC |
| Startup | `dotnet-counters`, a stopwatch harness, Native AOT publish | Time to `ready` (excluding provisioning) | ≤ 150 ms JIT, ≤ 50 ms AOT for the core |

### 7.1 Target summary (validated in M2–M4)

| Scenario | Target |
|----------|--------|
| In-process command overhead | p50 ≤ 20 µs, ≤ 2 KB allocations |
| PostgreSQL commit (1–3 events + outbox) | p99 ≤ 10 ms |
| Commands per instance (4 vCPU, PostgreSQL, distinct streams) | ≥ 5,000/s |
| Event → async projection latency | p95 ≤ 100 ms (PostgreSQL with LISTEN/NOTIFY) |
| Outbox: commit → broker acknowledgement | p95 ≤ 250 ms |
| Projection rebuild, document projection, one partition | ≥ 20,000 events/s |
| Global log read throughput | ≥ 50,000 events/s per reader |

## 8. Revisit triggers

| Observation | Action |
|-------------|--------|
| Outbox inserts dominate commit time (> 30 %) at tier L | Prototype a log-tailing relay for integration events only, keeping the outbox for commands and timers |
| `aggregate.load.events` p95 > 200 | Turn on snapshots for that type. Consider the optimistic cache with affinity |
| A projection needs more than 1024-way parallelism | Shard the read model by tenant, or use a dedicated pipeline (beyond 3.0) |
| The database ceiling is reached for one bounded context | Re-examine the bounded context. A dedicated event database adapter (KurrentDB) is a 3.x candidate |
