# ADR-028: PostgreSQL payload columns are `json`, plus adapter refinements found while building WP-2.1 to WP-2.4

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-11 |
| **Deciders** | v3 architecture review; WP-2.1 to WP-2.4 implementation |
| **Amends** | [doc 05](../05-Event-Store-and-Persistence.md) §2, §3.1, §5.2, §7; [ADR-009](ADR-009-event-store-on-adonet-postgresql-first.md) |

## Context

Doc 05 specified `jsonb` for `events.data`, `documents.data`, `process_instances.state` and `snapshots.data`. Running the storage conformance kit against PostgreSQL 15 to 17 showed that `jsonb` breaks guarantees the kit asserts and the rest of the system relies on:

- **It does not keep bytes.** `jsonb` stores a parsed tree. It removes insignificant whitespace, reorders keys and keeps only the last of any duplicate keys. A payload read back is therefore not the payload that was written (SCK-18, SCK-19, SCK-46, SCK-58). That matters wherever bytes are signed or hashed, or compared as idempotency evidence. It also matters wherever an upcaster or a downstream consumer must see exactly what the producer wrote.
- **It rejects valid JSON.** `jsonb` refuses the `\u0000` escape (SQLSTATE 22P05). System.Text.Json emits that escape for any string containing U+0000, so `jsonb` would reject a legal domain value when the append is committed.

Building the adapter also settled four points that doc 05 left open or stated differently:

1. Doc 05 had the session send `Any` and `StreamExists` to the server only as `Exact`/`NoStream`, after reading the current version first.
2. Doc 05 did not say which clock decides when an outbox row is due.
3. Doc 05 did not define whether the safe horizon is inclusive or exclusive.
4. `process_instances`, `snapshots` and `documents` had no tenant column, although the core scopes all three by tenant (ADR-022).

## Decision

1. **Payload columns are `json`.** This applies to `events.data`, `documents.data`, `process_instances.state` and `snapshots.data`. `json` keeps the exact text and validates it on input. It accepts `\u0000`, and it supports the same `->`, `->>` and `#>>` operators and expression indexes that SFQL and `[Queryable]` indexes use (doc 14 §7.2).
   - Envelope `metadata` and outbox and dead-letter `attributes` stay `jsonb`. The adapter writes them itself and only containment and key lookups query them.
   - A U+0000 character in an envelope header or actor is not supported.
2. **`Any` and `StreamExists` are atomic statements, not read-then-write.**
   - `StreamExists` is an `UPDATE … WHERE NOT is_deleted AND NOT is_archived`.
   - `Any` is an `INSERT … ON CONFLICT DO UPDATE`.
   - Both take the stream row lock and apply to the committed version they then see. Each is still a compare-and-set on an observed version, so the ordering argument of doc 05 §5.3 holds, and it saves a round trip.
3. **Outbox due times use the configured `TimeProvider`.** `deliver_at` is business time (timers, delayed retries), like `ctx.Now`.
   - Leases and fencing keep using the database clock (`now()`), as doc 05 §7.4 requires.
   - So a PostgreSQL host under a fake clock fires timers when the fake clock moves. Lease expiry still follows real time.
4. **The safe horizon is inclusive.** Every position at or below `LogBatch.Horizon` is final. PostgreSQL reports `(pg_snapshot_xmin − 1, long.MaxValue)` and the in-memory store reports its last position.
   - Conformance cases that assert on the log read until the horizon passes their own last commit. A database may hold the horizon back briefly while any writing transaction runs (doc 05 §5.4).
5. **Tenant columns are added.** `process_instances`, `snapshots` and `documents` carry `tenant_id` in their primary keys (ADR-022).
6. **Commit is two round trips for now.**
   - The write batch, append first, is one `NpgsqlBatch` round trip.
   - `COMMIT` is the second. Conditional failures (append CAS, process state CAS, checkpoint fence) are detected from the batch results and rolled back, so `COMMIT` cannot be folded into the batch without moving those checks into SQL raises.
   - That optimisation belongs to the performance baseline (WP-2.10).

## Consequences

- Payloads round-trip byte for byte on every supported database, which the conformance kit asserts.
- `json` is cheaper to write than `jsonb`, because nothing is parsed into a binary tree. Each query that reads a payload field parses that field at query time instead. Read-side hot paths should use `[Queryable]` expression indexes, not ad-hoc scans of payload fields.
- `jsonb`-only features, namely GIN containment indexes and the `@>` and `?` operators, are not available on payload columns. SFQL compiles field predicates to `->>` and comparison operators, so the query surface is unaffected.
- `json` columns cannot be compared for equality (`json = json` does not exist). Nothing in the adapter compares payloads in SQL.
