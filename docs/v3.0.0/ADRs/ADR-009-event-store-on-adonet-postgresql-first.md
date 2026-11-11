# ADR-009: Event store on raw ADO.NET; PostgreSQL first

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 used EF Core for every store. An event store needs exact control over locking, ordering, batching and transaction IDs, which an ORM hides.

## Decision

- The event store, outbox, inbox, checkpoints, leases and dead letters are written with hand-tuned SQL over `NpgsqlDataSource` and `Microsoft.Data.SqlClient`.
- Each command commits in **one round trip**, using `NpgsqlBatch` or a single SQL Server batch.
- PostgreSQL is the reference implementation: `jsonb`, `xid8` and LISTEN/NOTIFY suit event sourcing well. SQL Server ships in the same milestone set (M4).
- EF Core is supported for **read models** (`SourceFlow.Storage.EntityFrameworkCore`), which can share the transaction through a connection the storage session provides.

## Alternatives

(a) EF Core everywhere: simpler for contributors, but slower and gives no control. (b) KurrentDB (EventStoreDB) only: excellent, but a second operational system for most teams. It remains a viable future adapter behind the same ports.

## Consequences

(+) Performance and correctness under our control. (−) SQL per provider must be maintained. The storage conformance kit protects against drift between providers.
