-- SourceFlow.Net v3 PostgreSQL schema, version 7: row-level security on data_keys (ADR-022, doc 10 §4).
-- Data keys are per tenant since V006; they get the same tenant_isolation policy as the other tenant tables, so a
-- statement without the tenant setting can't read another tenant's wrapped keys. The migrator enables the policy with
-- TenantIsolation.RowLevelSecurity, as for the other tenant tables.

CREATE POLICY tenant_isolation ON {schema}.data_keys
    USING (tenant_id = current_setting('sourceflow.tenant', true))
    WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true));
