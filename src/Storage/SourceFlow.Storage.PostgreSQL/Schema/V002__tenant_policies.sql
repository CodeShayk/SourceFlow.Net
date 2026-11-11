-- SourceFlow.Net v3 PostgreSQL schema, version 2: row-level security policies for the tenant tables (ADR-022).
-- The policies are always created; they take effect only where the migrator enables row-level security, which it
-- does when TenantIsolation is RowLevelSecurity. A missing 'sourceflow.tenant' setting matches no rows.

CREATE POLICY tenant_isolation ON {schema}.streams
    USING (tenant_id = current_setting('sourceflow.tenant', true))
    WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true));

CREATE POLICY tenant_isolation ON {schema}.events
    USING (tenant_id = current_setting('sourceflow.tenant', true))
    WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true));

CREATE POLICY tenant_isolation ON {schema}.snapshots
    USING (tenant_id = current_setting('sourceflow.tenant', true))
    WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true));

CREATE POLICY tenant_isolation ON {schema}.documents
    USING (tenant_id = current_setting('sourceflow.tenant', true))
    WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true));

CREATE POLICY tenant_isolation ON {schema}.process_instances
    USING (tenant_id = current_setting('sourceflow.tenant', true))
    WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true));

CREATE POLICY tenant_isolation ON {schema}.subject_streams
    USING (tenant_id = current_setting('sourceflow.tenant', true))
    WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true));
