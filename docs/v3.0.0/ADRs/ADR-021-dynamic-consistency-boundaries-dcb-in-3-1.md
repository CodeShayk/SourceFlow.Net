# ADR-021: Dynamic consistency boundaries (DCB) — schema-ready in 3.0, API in 3.1

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

Some business rules span more than one aggregate: "a username is unique", "a course can't exceed capacity *and* a student can't exceed their course limit". With stream-per-aggregate consistency ([ADR-008](ADR-008-optimistic-concurrency-with-automatic-re-decide.md), ES-R2), teams handle these with oversized aggregates, reservation process managers, or eventual compensation. The industry has started adopting **Dynamic Consistency Boundaries**. Axon Framework 5 and Axon Server 2025.x support them, KurrentDB 25.1 added multi-stream appends, and Cratis Chronicle and the dcb.events community specify them ([competitive analysis](../13-Competitive-Analysis.md)). In DCB, a decision reads the events matching a *query* (event types + tags), and the append succeeds only if no new matching event has been committed since the read.

## Decision

**3.0 (schema-ready, no new API):**
- `events.tags text[] NOT NULL DEFAULT '{}'`, with a `GIN (tags)` index created when the opt-in `EnableTags` is set.
- Event members can be marked `[Tag("account")]`. The generator emits `account:{value}` tags at append time. Stream events get the implicit tag `stream:{stream_id}`.
- Events written by 3.0 are therefore DCB-queryable in 3.1 **without data migration**.

**3.1 (API):**
```csharp
var query  = EventQuery.Types<CourseCreated, StudentSubscribed>().Tagged($"course:{c}", $"student:{s}");
var model  = await store.ReadAsync(query, ct);                    // returns events + position P
var events = SubscriptionDecider.Decide(model.Fold(), command);  // pure, as ADR-003
await store.AppendAsync(events, AppendCondition.NoneMatching(query, after: model.Position), ct);
```
- **PostgreSQL implementation.**
  1. Take `pg_advisory_xact_lock(hash(tag))` for each tag in the condition, in sorted order (deadlock-free). Advisory locks don't assign a transaction id, so ES-R1 still holds.
  2. Check `NOT EXISTS (… WHERE tags && @tags AND event_type = ANY(@types) AND (tx_id, seq) > @after)`.
  3. Insert.

  Every append that carries tags takes the same locks, so stream-based and DCB writers serialise on shared tags. **SQL Server** uses `sp_getapplock` per tag in the same way.
- Streams remain the default model. DCB is opt-in per decision (an `[DcbDecider]` attribute and a generated runner).

## Alternatives

- (a) Multi-stream appends with expected versions per stream (KurrentDB's approach). Simpler, but it still requires choosing streams up front and doesn't express "no new matching events".
- (b) Ship DCB in 3.0. Rejected for schedule risk: it needs its own conformance tests and ordering proof.
- (c) Ignore DCB. Rejected, because it is becoming a market expectation.

## Consequences

- (+) Cross-entity invariants without oversized aggregates. The 3.0 data is forward-compatible.
- (−) Lock and GIN-index overhead for tagged appends, measured in the 3.1 benchmarks. Tag cardinality guidance is needed (avoid hot tags).

## Traces

Competitive analysis C2. Product Q4 (id strategy) and Q7 (consistency) context.
