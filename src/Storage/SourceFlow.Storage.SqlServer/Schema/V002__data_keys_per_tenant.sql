-- SourceFlow.Net v3 SQL Server schema, version 2: data keys per tenant (doc 10 §4, ADR-022).
-- A data key belonged to the subject id alone, so erasing a subject in one tenant destroyed the key of the same subject id
-- in every tenant. Keys now belong to (tenant, subject). Existing keys move to the default tenant (N'-'): personal data
-- markers written before this version (format v1) read with that key.

ALTER TABLE {schema}.data_keys ADD tenant_id nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL
    CONSTRAINT df_data_keys_tenant DEFAULT N'-';
GO
ALTER TABLE {schema}.data_keys DROP CONSTRAINT df_data_keys_tenant;
ALTER TABLE {schema}.data_keys DROP CONSTRAINT pk_data_keys;
ALTER TABLE {schema}.data_keys ADD CONSTRAINT pk_data_keys PRIMARY KEY CLUSTERED (tenant_id, subject_id);
