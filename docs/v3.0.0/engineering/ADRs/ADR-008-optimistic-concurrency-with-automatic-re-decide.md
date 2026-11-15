# ADR-008: Optimistic concurrency with automatic re-decide

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 computes `max(seq)+1` by loading the full history (O(n), racy) and detects conflicts only after side effects (GAP-05).

## Decision

- The load returns the stream version `v`. The append requires `expected = v`; the storage adapter enforces it atomically (a conditional update of the stream row, with a unique `(stream_id, version)` constraint as backstop).
- On conflict, the command processor discards the decision and re-runs load→decide, up to `MaxConcurrencyRetries` times (default 3), with full-jitter backoff (5–50 ms).
- Business invariants are re-checked against fresh state automatically.
- Callers may also pass `ExpectedVersion` explicitly to get *their own* optimistic check (for example, from an HTTP `If-Match` header).

## Alternatives

(a) Pessimistic locking (`SELECT … FOR UPDATE` on the stream): serialises writers and holds locks during `Decide`. Rejected for the default; it may become a per-aggregate option for very hot streams later. (b) Single-writer actors per stream: needs cluster membership, which is out of scope.

## Consequences

(+) O(1) sequencing. No partial side effects, because everything is in one transaction. (−) Hot streams can churn under heavy contention. The `eventstore.concurrency_conflicts` metric makes this visible, and partition-key routing reduces contention.
