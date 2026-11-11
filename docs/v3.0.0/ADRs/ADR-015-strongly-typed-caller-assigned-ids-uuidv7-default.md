# ADR-015: Strongly typed, caller-assigned ids (UUIDv7 default)

| | |
|---|---|
| **Status** | Accepted (product sign-off 2026-11-10) |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 uses `int Id` everywhere and logs new-entity commands under id 0 (GAP-20, Q4).

## Decision

- Aggregate ids are generated value types: `[StronglyTypedId] public readonly partial record struct AccountId(Guid Value);`. String and long variants are supported.
- The default factory is `Guid.CreateVersion7(timeProvider.GetUtcNow())`: time-ordered, which suits B-tree indexes.
- Ids are assigned **by the caller** before the command is sent. A default or empty id is rejected with `InvalidCommand`.
- Stream id = `{aggregateName}-{id}`, for example `bank-account-0193a…`.

## Consequences

(+) Commands are idempotent and routable before they are handled; no database-generated keys. (−) Users must generate ids (one call).
