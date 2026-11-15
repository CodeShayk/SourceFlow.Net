# ADR-026: Per-service query API with a SQL syntax and injection-proof compilation

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

The product now requires **every service** to expose a query API for retrieving its data. The API must validate syntax, guard against SQL and script injection, and support the basic SQL constructs: joins, filtering, grouping and ordering. SFQL ([ADR-023](ADR-023-sfql-query-language-with-cesql-predicates.md)) was pipeline-only, allowed key lookups but no general joins, and its HTTP endpoint was opt-in with ad-hoc queries disabled by default ([doc 14](../Design/14-Query-Side-and-SFQL.md)). Queries must still stay inside the owning service's boundary (product Q2, rules Q-B1…B6).

## Decision

1. **A query API in every service.** `MapSourceFlowQueryApi()` provides `POST /query`, `POST /query/validate`, `POST /query/explain` and `GET /query/schema`, next to the named domain queries. Mapping without an authorization policy is a startup error (fail closed). Ad-hoc queries need `sourceflow.query.adhoc`; named domain queries remain the stable contract (Q-B4).
2. **A SQL syntax for SFQL**: a read-only `SELECT` subset with `DISTINCT`, `INNER`/`LEFT` joins, `WHERE`, `GROUP BY`/`HAVING`, `ORDER BY … NULLS FIRST|LAST`, `LIMIT`/`OFFSET`, `CASE`/`CAST`/`COALESCE`, and `IN`/`EXISTS` subqueries (depth 1). It parses into the **same AST** as the pipeline syntax, which gains a `join` stage. Binder, policy, compilers and security are shared.
3. **Bounded joins.** Equality joins with an indexed key on at least one side; at most 3 joins (cap 5); tenant equality added automatically; no personal-data join keys; event-to-event joins only when both sides are bounded.
4. **Injection-proof by construction, with defence in depth**:
   - the API is never a pass-through, and the grammar is an allow-list;
   - generated SQL contains only templates, catalog-derived identifiers and placeholders, and **every** value from the request is a bound parameter;
   - name resolution is catalog-only, and functions are allow-listed;
   - no dynamic code evaluation;
   - safe response encoding and headers, and errors never echo input;
   - read-only role and transaction, row-level security, limits, cost ceiling, and rate limiting.

## Alternatives

| Option | Why not chosen |
|--------|----------------|
| Pass SQL through with a "blocklist" of dangerous keywords | Blocklists are bypassable (encodings, comments, dialect tricks). We parse into an AST and generate SQL ourselves, which is an allow-list by construction |
| Pass SQL through on a read-only database role only | Prevents writes, but not data exfiltration across tenants or services, schema discovery, or DoS. It also exposes the physical schema (breaks Q-R4) |
| GraphQL | Joins via resolvers multiply round trips. Weak for grouping and analytical queries. A second language next to SFQL |
| OData `$filter/$expand/$apply` | Supports grouping and expansion but is entity-centric and verbose. Customers asked for SQL constructs |
| Full SQL (unions, CTEs, window functions) in 3.0 | More surface to secure and test. Candidates for `sfql/1.1` once usage justifies them |

## Consequences

- (+) A familiar SQL syntax for analysts and BI tools, without giving up the logical model, service boundary, tenancy or personal-data rules.
- (+) A single security argument for both syntaxes.
- (−) A larger grammar and compiler, plus join planning per provider. This is mitigated by cross-provider equivalence tests, the injection corpus, fuzzing, the no-literal property test and DAST.
- (−) Ad-hoc joins can be expensive. This is mitigated by the indexed-key rule, the cost ceiling, limits and rate limiting.

## Traces

Product request (2026-11-10): query API per service with injection protection and basic SQL constructs. Doc 14 §4, §7.3, §9. Amends [ADR-023](ADR-023-sfql-query-language-with-cesql-predicates.md). Q2 boundary rules unchanged.
