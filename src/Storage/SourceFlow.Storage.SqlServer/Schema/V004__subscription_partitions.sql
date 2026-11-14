-- SourceFlow.Net v3 SQL Server schema, version 4: each subscription's partition count (doc 06 §3, CR-18), as
-- PostgreSQL's V009. The first node to run a subscription stores its configured count; a node configured otherwise refuses
-- that subscription. `projection repartition` changes it in the same transaction as the checkpoints.

CREATE TABLE {schema}.subscription_partitions (
    subscription nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    partitions   int               NOT NULL CONSTRAINT ck_subscription_partitions_positive CHECK (partitions > 0),
    updated_at   datetimeoffset(7) NOT NULL CONSTRAINT df_subscription_partitions_updated DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT pk_subscription_partitions PRIMARY KEY CLUSTERED (subscription)
);
