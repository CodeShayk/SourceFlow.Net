-- SourceFlow.Net v3 PostgreSQL schema, version 9: each subscription's partition count (doc 06 §3, CR-18).
-- The count decides which buckets each partition reads, so every node must use the same one. The first node to run a
-- subscription stores its configured count; a node configured otherwise refuses that subscription. `projection
-- repartition` changes it in the same transaction as the checkpoints.

CREATE TABLE {schema}.subscription_partitions (
    subscription text        PRIMARY KEY,
    partitions   int         NOT NULL CHECK (partitions > 0),
    updated_at   timestamptz NOT NULL DEFAULT now()
);
