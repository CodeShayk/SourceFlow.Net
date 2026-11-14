-- SourceFlow.Net v3 PostgreSQL schema, version 8: the position of cross-stream documents (doc 06 §5, CR-31).
-- A document fed by many streams has no stream version to guard it, so it records the global position (commit, sequence)
-- of the last event applied. An event at or below it was applied already, by a replay or a repartition, and is skipped.
-- Documents keyed by their source stream leave both columns null; their version is the guard.

ALTER TABLE {schema}.documents
    ADD COLUMN position_commit xid8,
    ADD COLUMN position_seq    bigint;
