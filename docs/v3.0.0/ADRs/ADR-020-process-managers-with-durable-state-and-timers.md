# ADR-020: Process managers with durable state and timers replace "Saga"

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2's "Saga" was really a command handler. Long-running coordination across aggregates or services had no first-class support, timeouts included.

## Decision

- A **process manager** is a record with `Reaction React(TEvent, ReactionContext)` methods. A `Reaction` carries the new state, commands to send, timers to schedule, and completion.
- Events are correlated to an instance through generated `CorrelateBy` functions.
- The instance state is persisted as a document with a version (optimistic concurrency) in the same transaction as its outbox commands, timers, inbox entry and checkpoint.
- Timers are scheduled outbox messages (`deliver_at`) delivered back to the process manager as `TimeoutElapsed<T>`.

## Alternatives

(a) Event-sourced process state: possible, but adds storage and replay complexity without a clear benefit. (b) An external workflow engine (Temporal, Dapr Workflows): far heavier. Users can still integrate one.

## Consequences

(+) Coordination is durable and testable (Given events → Then commands). (−) A new concept for v2 users. The migration guide maps v2 Saga → v3 aggregate `Decide` + process manager.

## Amendment (2026-11-10)

Process managers may also be **generated from the workflow DSL** ([ADR-024](ADR-024-workflow-dsl-compiled-to-process-managers.md)). The runtime contract defined here is unchanged; the DSL adds declarative retries, compensation, a pivot and manual intervention on top of it.
