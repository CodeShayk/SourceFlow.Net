# ADR-018: Business rejections are values, not exceptions

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 sagas signal business rule violations by throwing `ArgumentException` or `InvalidOperationException`. A rule violation is an expected outcome, not a defect. Using exceptions for it costs a stack capture on a common path. It also makes a rejection indistinguishable from an infrastructure failure, so cloud listeners retry an invalid command until it is dead-lettered.

## Decision

`Decide` returns `Decision.Reject(Error)` for expected rule violations. The command processor returns `CommandResult.Rejected(error)` and appends nothing. Exceptions are reserved for defects and infrastructure failures. For received commands, a rejection is a *handled* outcome: the inbox is recorded, the message is acknowledged, an optional `CommandRejected` reply or event is sent, and nothing is retried.

## Consequences

(+) No exception cost on a common path. Rejections are explicit in tests and telemetry (`outcome=rejected`). Poison retries of invalid commands are avoided. (−) Callers must handle the result. `CommandResult.ThrowIfNotAccepted()` exists for convenience.
