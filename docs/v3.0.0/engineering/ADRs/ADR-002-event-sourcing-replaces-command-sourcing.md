# ADR-002: Event sourcing replaces command sourcing

| | |
|---|---|
| **Status** | Accepted (product sign-off 2026-11-10) |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 logs commands and rebuilds state by re-running sagas. Replay therefore runs today's business logic, clock and dependencies against old intents, so it is non-deterministic. It also re-publishes to the cloud (GAP-06), silently drops commands that can't be rebuilt (GAP-04), and can't be atomic with the entity snapshot (GAP-05). Events, which are the facts, are not stored at all (EVT-05).

## Decision

Events are the system of record, in append-only streams, one stream per aggregate instance. State = `fold(Evolve, Initial, events)`. Commands are transient. Optionally they are recorded only as inbox entries (for idempotency) and in event metadata (`causationId`), which keeps the audit trail of *which command caused which events*.

## Alternatives

- (a) Keep command sourcing but make it deterministic. That isn't possible in general, because handlers call services and read the clock.
- (b) A state-stored aggregate with an outbox, with no event store. That is simpler but loses history, temporal queries and rebuildable projections, which are the product's reason to exist.
- (c) Offer both modes. That doubles the core surface (YAGNI). A state-stored mode could come later on top of the same storage session.

## Consequences

- (+) Replay becomes a pure fold, so projections rebuild with no side effects. There is a full audit trail. The correct model matches the companion book's narrative.
- (−) A breaking conceptual change for v2 users, handled by the migration tool, which imports genesis events. Event schema evolution becomes a first-class concern ([ADR-005](ADR-005-logical-message-type-names-and-registry.md)).

## Traces

Q1, GAP-04, GAP-05, GAP-06, GAP-07, GAP-20, EVT-05.
