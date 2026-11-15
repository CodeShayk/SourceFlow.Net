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
  *As built (WP-C.8, CR-16):* so is any id that could not be read back from its stream id. `IStronglyTypedId.IsValid` is false for:
  - a default id;
  - an Int64 id that is zero or negative;
  - a string id that is empty, too long, or contains a dash, whitespace or a control character.

  These checks apply to such an id:
  - a send returns `Rejected` with `sourceflow.invalid_command`;
  - `ToStreamSegment()` throws;
  - the generated JSON converter refuses to write it;
  - `StreamId.For` refuses a segment with a dash.

  So such an id is never stored (GEN-66, GEN-67).
- Stream id = `{aggregateName}-{id}`, for example `bank-account-0193a…`.

## Consequences

(+) Commands are idempotent and routable before they are handled; no database-generated keys. (−) Users must generate ids (one call).
