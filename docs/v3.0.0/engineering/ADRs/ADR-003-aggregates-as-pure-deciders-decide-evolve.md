# ADR-003: Aggregates as pure deciders (decide/evolve)

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

In v2, behaviour was spread across an `Aggregate` façade (sends commands), a `Saga` (mutates the entity, persists it, raises events) and reflection-built events. That mixes responsibilities and needs DI for domain objects, which caused GAP-08.

## Decision

An aggregate is an immutable `record` that carries state and two kinds of pure method:
- `Decision Decide(TCommand, DecisionContext)`: validate the command and return events or a rejection.
- `TSelf When(TEvent)`: apply one event to produce the next state.

The generator writes the dispatch switches. For logic that needs dependencies (I/O), a user writes an `ICommandHandler<TCommand, TAggregate>` that gathers the inputs and then calls a pure method. `DecisionContext` exposes `Now` (from `TimeProvider`), `Version`, `Metadata` and `CommandId`, so decisions stay deterministic in tests.

## Alternatives

- (a) A mutable OO aggregate with `Apply` and an uncommitted-events list (Eventuous or Marten style). Familiar, but it hides mutation, makes concurrency retries need careful resets, and is harder to test.
- (b) A fully functional decider with static abstracts only. Pure, but unfamiliar to many C# developers.
- The chosen design uses records with instance methods plus generation: functional semantics with idiomatic C#.

## Consequences

- (+) Given/When/Then tests with no DI or mocks. Concurrency retry is trivially safe. Aggregates aren't DI services, so lifetime bugs go away (GAP-08).
- (−) Developers learn the `Decision`/`When` idiom. Analyzers guide them (for example SF0004, "When must be side-effect free": it flags I/O APIs).
