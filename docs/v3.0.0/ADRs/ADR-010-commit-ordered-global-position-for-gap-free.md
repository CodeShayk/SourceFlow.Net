# ADR-010: Commit-ordered global position for gap-free subscriptions

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

An identity or sequence column is allocated at insert time, not at commit time. A reader can see position 12 committed while 11 is still in flight, move its checkpoint past 11, and lose it forever. v2 had no global reads at all.

## Decision

A subscription reads only events whose transaction is known to be complete, in an order that later commits can't change:
- **PostgreSQL**: each event stores `tx_id xid8 DEFAULT pg_current_xact_id()`. The position is `(tx_id, seq)`. Reads ask for `tx_id < pg_snapshot_xmin(pg_current_snapshot())` ordered by `(tx_id, seq)`. Every transaction below `xmin` has finished, so that prefix is final.
- **SQL Server**: each event has a `rowversion`. Reads ask for `rv < MIN_ACTIVE_ROWVERSION()` ordered by `rv`.
- An append always writes the stream row first, so the transaction id is acquired under the stream lock and per-stream order matches global order ([doc 05 §5](../05-Event-Store-and-Persistence.md#5-global-ordering-and-the-safe-horizon)).

## Alternatives

(a) Gap detection with a timeout (wait N ms for missing numbers): heuristic, and loses events under long transactions. (b) A single-writer sequencer that assigns positions after commit: an extra hop and a single point of failure.

## Consequences

(+) Provably gap-free, with no timeouts. (−) A long-running transaction on the event database delays subscription progress (not correctness). The `subscription.horizon_age` metric exposes it. Position is a composite value type (`GlobalPosition`).
