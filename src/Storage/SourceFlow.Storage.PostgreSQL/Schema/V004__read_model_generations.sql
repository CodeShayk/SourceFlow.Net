-- SourceFlow.Net v3 PostgreSQL schema, version 4: blue/green read-model generations (doc 06 §6, ADR-032).
-- One alias row per projection name: the generation serving reads (target), the one retired by the last switch
-- (previous, dropped after RetainPreviousFor) and a rebuild requested by the CLI (building). Every change is a
-- compare-and-set, so concurrent nodes switch an alias once.

ALTER TABLE {schema}.collection_aliases
    ADD COLUMN previous text NULL,
    ADD COLUMN building text NULL;
