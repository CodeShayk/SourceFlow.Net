# ADR-022: Conjoined multi-tenancy with row-level security in 3.0

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

The original design treated multi-tenancy as YAGNI and only passed a `tenant` header through ([doc 01 §4](../01-Architecture-Overview.md#4-deliberately-not-built-yagni-register)). The competitive analysis (C1) shows mature competitors (Marten, Wolverine, Axon) provide it, and SaaS teams treat it as a prerequisite. Bolting it on later would change primary keys in every table: a breaking schema migration.

## Decision

**Conjoined tenancy** (shared tables, tenant column) is built into the 3.0 schema and runtime:

- `tenant_id text NOT NULL DEFAULT '-'` on `streams`, `events`, `snapshots`, `documents`, `process_instances`, `inbox` and `outbox`. Keys become `(tenant_id, stream_id)`, `(tenant_id, collection, id)`, and so on. The single-tenant default `'-'` costs almost nothing.
- `ITenantResolver` (for example from claims or a header) sets the ambient tenant for `SendAsync`. The tenant travels in the envelope (`sftenant`) and is restored on receive. Stream ids are tenant-scoped.
- **PostgreSQL row-level security** (opt-in, `TenantIsolation.RowLevelSecurity`): policies `USING (tenant_id = current_setting('sourceflow.tenant'))` on every table. The session runs `SET LOCAL sourceflow.tenant` per transaction. Subscription workers run as a role with `BYPASSRLS` because they process every tenant. A bug in user code or in a query (doc 14) can't leak across tenants.
- The bucket is computed from `(tenant_id, stream_id)`, so tenants spread across partitions.
- Database-per-tenant is a 3.x item, behind the same `ITenantResolver`, using `NpgsqlDataSource` per tenant.

## Alternatives

- (a) Database-per-tenant only: strong isolation, but a heavy operational load for many small tenants.
- (b) Schema-per-tenant: migrations multiply.
- (c) Stay with YAGNI: rejected because of the schema-break cost later.

## Consequences

- (+) SaaS-ready, and RLS gives defence in depth.
- (−) Wider keys (a few bytes per row). Noisy-neighbour risk in shared tables, mitigated by per-tenant metrics tags (bounded cardinality: top-N tenants, others aggregated) and the 3.x database-per-tenant path.

## Traces

Competitive analysis C1. Doc 01 YAGNI register updated.
