# ADR-016: Projections: inline or async, with partitioned, fenced subscriptions

| | |
|---|---|
| **Status** | Accepted (product sign-off 2026-11-10) |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 projects synchronously and in memory at publish time. If the process crashes between commit and projection, the event is lost; there is no checkpoint and no rebuild.

## Decision

- **Inline** projections run inside the command's transaction (same database), giving read-your-writes and strong consistency. Use them sparingly, because they add latency to the write.
- **Async** projections and process managers run on the subscription engine:
    - read the commit-ordered log;
    - P partitions per subscription (`hash(stream_id) mod P`), default 1;
    - one lease per partition, carrying a fencing token;
    - checkpoint written in the same transaction as the read-model changes when they share a database (exactly-once); otherwise idempotent writes keyed on the stream version.
- **Rebuilds** are blue/green: project into a new table version, catch up, then switch atomically.

## Consequences

(+) Durable, scalable, rebuildable read models. (−) Eventual consistency for async projections, which is explicit and measured (`subscription.lag`).
