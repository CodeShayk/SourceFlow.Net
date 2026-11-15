-- SourceFlow.Net v3 PostgreSQL schema, version 6: data keys per tenant (doc 10 §4, ADR-022).
-- A data key belonged to the subject id alone, so erasing a subject in one tenant destroyed the key of the same subject id
-- in every tenant. Keys now belong to (tenant, subject). Existing keys move to the default tenant ('-'): personal data
-- markers written before this version (format v1) read with that key.

ALTER TABLE {schema}.data_keys ADD COLUMN tenant_id text NOT NULL DEFAULT '-';
ALTER TABLE {schema}.data_keys ALTER COLUMN tenant_id DROP DEFAULT;
ALTER TABLE {schema}.data_keys DROP CONSTRAINT data_keys_pkey;
ALTER TABLE {schema}.data_keys ADD PRIMARY KEY (tenant_id, subject_id);
