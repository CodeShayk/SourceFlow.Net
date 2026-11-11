-- SourceFlow.Net v3 PostgreSQL schema, version 1 (doc 05 §2, §7; doc 14 §2.2; ADR-022 tenant columns; ADR-028 payload types).
-- {schema} is replaced by the configured, validated schema name. The migrator runs this script in one transaction.
--
-- Payload columns (events.data, documents.data, process_instances.state, snapshots.data) are 'json', not 'jsonb':
-- 'json' keeps the serializer's exact bytes (the storage conformance kit round-trips them byte for byte) and accepts
-- every valid JSON text, including the \u0000 escape that 'jsonb' rejects. The ->, ->> and #>> operators and
-- expression indexes work on 'json' as well. Envelope metadata and attributes are 'jsonb' for containment queries.

CREATE TABLE {schema}.streams (
    tenant_id    text        NOT NULL DEFAULT '-',
    stream_id    text        NOT NULL,
    stream_type  text        NOT NULL,
    version      bigint      NOT NULL,
    is_deleted   boolean     NOT NULL DEFAULT false,
    is_archived  boolean     NOT NULL DEFAULT false,
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, stream_id),
    CONSTRAINT ck_streams_stream_id CHECK (length(stream_id) BETWEEN 1 AND 200)
);

CREATE TABLE {schema}.events (
    seq            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tx_id          xid8        NOT NULL DEFAULT pg_current_xact_id(),
    tenant_id      text        NOT NULL DEFAULT '-',
    bucket         smallint    NOT NULL,
    stream_id      text        NOT NULL,
    stream_version bigint      NOT NULL,
    event_id       uuid        NOT NULL,
    event_type     text        NOT NULL,
    type_version   int         NOT NULL,
    tags           text[]      NOT NULL DEFAULT '{}',
    data           json        NOT NULL,
    metadata       jsonb       NOT NULL,
    created_at     timestamptz NOT NULL,
    CONSTRAINT uq_events_stream_version UNIQUE (tenant_id, stream_id, stream_version),
    CONSTRAINT uq_events_event_id UNIQUE (event_id),
    CONSTRAINT ck_events_bucket CHECK (bucket BETWEEN 0 AND 1023)
);
CREATE INDEX ix_events_commit_order ON {schema}.events (tx_id, seq) INCLUDE (bucket);
CREATE INDEX ix_events_type_time    ON {schema}.events (tenant_id, event_type, created_at);
CREATE INDEX ix_events_correlation  ON {schema}.events ((metadata->>'correlationId'));
CREATE INDEX ix_events_causation    ON {schema}.events ((metadata->>'causationId'));

CREATE TABLE {schema}.outbox (
    seq           bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    message_id    uuid        NOT NULL,
    partition     smallint    NOT NULL,
    partition_key text,
    destination   text        NOT NULL,
    kind          smallint    NOT NULL,
    attributes    jsonb       NOT NULL,
    body          bytea       NOT NULL,
    deliver_at    timestamptz NOT NULL,
    attempts      int         NOT NULL DEFAULT 0,
    last_error    text,
    created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_outbox_ready ON {schema}.outbox (partition, deliver_at, seq);
CREATE INDEX ix_outbox_key   ON {schema}.outbox (partition, partition_key, seq) WHERE partition_key IS NOT NULL;

CREATE TABLE {schema}.inbox (
    consumer       text        NOT NULL,
    message_id     uuid        NOT NULL,
    processed_at   timestamptz NOT NULL DEFAULT now(),
    result_status  smallint    NOT NULL,
    result_version bigint      NOT NULL,
    result_error   text,
    result_commit  xid8        NOT NULL DEFAULT '0',
    result_seq     bigint      NOT NULL DEFAULT 0,
    PRIMARY KEY (consumer, message_id)
);
CREATE INDEX ix_inbox_processed_at ON {schema}.inbox (processed_at);

CREATE TABLE {schema}.leases (
    name       text PRIMARY KEY,
    owner      text,
    token      bigint NOT NULL DEFAULT 0,
    expires_at timestamptz
);

CREATE TABLE {schema}.checkpoints (
    subscription text        NOT NULL,
    partition    int         NOT NULL,
    commit_pos   xid8        NOT NULL DEFAULT '0',
    seq_pos      bigint      NOT NULL DEFAULT 0,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (subscription, partition)
);

CREATE TABLE {schema}.dead_letters (
    id                   uuid        PRIMARY KEY,
    consumer             text        NOT NULL,
    source               text        NOT NULL,
    message_id           uuid        NOT NULL,
    message_type         text,
    destination          text,
    destination_is_topic boolean     NOT NULL DEFAULT false,
    attributes           jsonb       NOT NULL,
    body                 bytea       NOT NULL,
    reason               text        NOT NULL,
    error_type           text,
    error_message        text,
    error_detail         text,
    attempts             int         NOT NULL,
    first_failed_at      timestamptz NOT NULL,
    parked_at            timestamptz NOT NULL DEFAULT now(),
    status               smallint    NOT NULL DEFAULT 0,
    status_changed_at    timestamptz,
    status_note          text
);
CREATE INDEX ix_dead_letters_status ON {schema}.dead_letters (status, parked_at);

CREATE TABLE {schema}.process_instances (
    tenant_id    text        NOT NULL DEFAULT '-',
    process_type text        NOT NULL,
    process_id   text        NOT NULL,
    version      bigint      NOT NULL,
    state        json        NOT NULL,
    completed    boolean     NOT NULL DEFAULT false,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, process_type, process_id)
);

CREATE TABLE {schema}.snapshots (
    tenant_id   text        NOT NULL DEFAULT '-',
    stream_id   text        NOT NULL,
    version     bigint      NOT NULL,
    state_type  text        NOT NULL,
    schema_hash text        NOT NULL,
    data        json        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, stream_id)
);

CREATE TABLE {schema}.documents (
    tenant_id  text        NOT NULL DEFAULT '-',
    collection text        NOT NULL,
    id         text        NOT NULL,
    version    bigint      NOT NULL,
    data       json        NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, collection, id)
);

CREATE TABLE {schema}.nodes (
    node_id      text        PRIMARY KEY,
    roles        int         NOT NULL,
    heartbeat_at timestamptz NOT NULL
);

CREATE TABLE {schema}.collection_aliases (
    alias       text        PRIMARY KEY,
    target      text        NOT NULL,
    switched_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE {schema}.subject_streams (
    tenant_id  text NOT NULL,
    subject_id text NOT NULL,
    stream_id  text NOT NULL,
    PRIMARY KEY (tenant_id, subject_id, stream_id)
);

CREATE TABLE {schema}.schemas (
    tenant_id     text        NOT NULL DEFAULT '-',
    message_type  text        NOT NULL,
    type_version  int         NOT NULL,
    kind          text        NOT NULL,
    service       text        NOT NULL,
    json_schema   jsonb       NOT NULL,
    fingerprint   text        NOT NULL,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, message_type, type_version)
);

CREATE TABLE {schema}.data_keys (
    subject_id  text        PRIMARY KEY,
    key_ref     text        NOT NULL,
    wrapped_key bytea       NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    shredded_at timestamptz
);
