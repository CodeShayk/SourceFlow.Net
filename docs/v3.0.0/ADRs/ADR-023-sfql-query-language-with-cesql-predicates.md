# ADR-023: SFQL — a read-only query language with CESQL-compatible predicates

| | |
|---|---|
| **Status** | Accepted (product sign-off 2026-11-10) |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

In CQRS, the read side is separate from the write side. Today users can query read models only with their own SQL or EF Core, and they have no supported way to query the **event log** itself: audit ("every deposit over £10k last week"), tracing ("everything in correlation X"), analytics, support and debugging. Raw SQL against `sourceflow.events` couples users to:

- the physical schema (`jsonb` paths, provider differences);
- versioning (renamed fields across event versions);
- encryption (crypto-shredded fields are ciphertext);
- tenancy (it is easy to forget the tenant predicate).

It also gives no cost or safety guard. The original design listed a query language as YAGNI. The product owner has now explicitly requested one, which supersedes that entry.

## Decision

Define **SFQL (SourceFlow Query Language)**, specified in [doc 14](../Design/14-Query-Side-and-SFQL.md):

1. **Read-only by grammar.** There is no DML or DDL production, so a statement can't write.
2. **Logical, not physical.** It addresses logical event types, schema fields, read-model collections and aggregates, never tables or JSON paths.
3. **Pipeline syntax.** `from <source> | where … | project … | summarize … | order by … | take …`. It is easy to read, compose and parse with a hand-written recursive-descent parser (no dependency).
4. **CESQL v1 for expressions.** Predicates use the CloudEvents SQL Expression Language syntax and built-in functions. Over envelope attributes they evaluate with CESQL semantics, so **the same filter string can drive subscription filters** (doc 07) and queries. SFQL adds parameters, durations, decimals, timestamps, `between`, `is null` and data-path access.
5. **Statically checked** against a generated **event schema catalog** (JSON Schema 2020-12 with `x-sourceflow` extensions). Field renames across versions are resolved by the binder.
6. **Compiled** to parameterised SQL per provider (PostgreSQL, SQL Server) and interpreted for the in-memory store. Policies (tenant, PII, cost, permissions) are applied before execution.
7. **Separate read path.** A dedicated read-only connection (replicas allowed), a SELECT-only database role, and analyzers stop query use inside aggregate or projection code.

## Alternatives

| Option | Strengths | Why not chosen (as the primary language) |
|--------|-----------|------------------------------------------|
| Raw SQL | Full power, no new language | Physical coupling, provider differences, no versioning, PII or tenant safety, injection risk. Still available as an escape hatch for read models. |
| GraphQL | Great for UI read APIs | Weak at log, temporal and aggregate queries. Needs a schema server. A 3.x adapter over read models remains possible. |
| OData | Standard REST querying | Entity-centric. Awkward over a heterogeneous event log. |
| KQL / PRQL | Excellent pipeline ergonomics (SFQL borrows the shape) | Not embeddable as a standard, or tied to vendors. No CloudEvents alignment. |
| EventQL (EventSourcingDB) | Purpose-built for events | Tied to one database product. |
| CESQL alone | CNCF standard, CloudEvents-native | Boolean filter expressions only: no projection, aggregation, ordering or paging. **Adopted as SFQL's expression sublanguage.** |
| LINQ `IQueryable` provider | Familiar to C# developers | Large provider surface, runtime-only, not usable from CLI or HTTP. Offered instead as a *typed builder* that emits SFQL ASTs. |

## Consequences

- (+) One safe, portable, schema-aware way to query events and read models from C#, the CLI, or an opt-in HTTP endpoint. Interoperable filter syntax with CloudEvents tooling.
- (−) A language to specify, test and evolve. This is mitigated by a small core grammar, golden-file conformance tests, the CloudEvents CESQL TCK, and a versioned spec (`sfql/1`).

## Product sign-off (2026-11-10): queries stay inside the service boundary

Q2 was accepted with the requirement that **event querying is modelled within the owning service's domain and business boundary**. SFQL therefore:
1. resolves only the **service's own** catalog: its event types, its read models, its aggregates;
2. never reads another service's event log. Foreign facts are queryable only after the service has turned received integration events into its **own** read models;
3. is published to other services as a **named domain query** (a contract in the service's vocabulary), not as free-form access to its events ([doc 14 §1.1](../Design/14-Query-Side-and-SFQL.md#11-query-ownership-the-service-boundary-q2)).

## Traces

Q2 (sign-off). Product request (query side for CQRS). Competitive analysis (temporal queries C4). Doc 01 YAGNI register updated.

## Amendment (2026-11-10)

Superseded in part by [ADR-026](ADR-026-per-service-query-api-with-sql-syntax-and-injection-proof-compilation.md). SFQL gains a **SQL syntax** (a read-only `SELECT` subset) and **bounded joins**. Every service exposes the query API, with layered injection defence. Raw SQL **pass-through** remains rejected, as decided above: the SQL syntax is parsed into SFQL's own AST.
