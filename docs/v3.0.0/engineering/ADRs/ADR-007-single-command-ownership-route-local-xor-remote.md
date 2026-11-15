# ADR-007: Single command ownership: route local XOR remote

| | |
|---|---|
| **Status** | Accepted (product sign-off 2026-11-10) |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 runs every dispatcher, so a routed command also runs locally ("hybrid", Q3). Remote receivers bypass the bus, so the owner never logs the command (GAP-19).

## Decision

- Each command type has **exactly one** handler, owned by exactly one service.
- A service either *owns* a command type (it has a local handler, and remote senders route to this service's endpoint) or *routes* it (no local handler is allowed).
- Startup validation and an analyzer (where both are in one compilation) reject a command type that is both handled and routed, or neither.
- A received command goes through the **same** command processor as a local one, so the owner appends, sequences and can replay its streams.

## Alternatives

Keep hybrid behind a flag. Rejected: it needs idempotency to stay correct, it surprises users, and there is no known use case.

## Consequences

(+) Clear ownership, and the history lives with the owner. (−) Moving a command between services means changing its route, which is a deliberate act.

## Product sign-off (2026-11-10)

Q2 and Q3 were accepted, with one added requirement for Q2: **querying events is modelled within the owning service's domain and business boundary.** Ownership therefore covers reads as well as writes. Only the owning service queries its streams. It does so through query models expressed in its own domain vocabulary ([ADR-023](ADR-023-sfql-query-language-with-cesql-predicates.md), [doc 14 §1.1](../Design/14-Query-Side-and-SFQL.md#11-query-ownership-the-service-boundary-q2)).

## Traces

Q2, Q3, GAP-19, CMD-13.
