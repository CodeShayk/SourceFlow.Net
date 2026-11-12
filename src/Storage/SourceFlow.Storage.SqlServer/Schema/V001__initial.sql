-- SourceFlow.Net v3 SQL Server schema, version 1 (doc 05 §5.5, §7; doc 14 §2.2; ADR-022 tenant columns).
-- {schema} is replaced by the configured, validated schema name, bracket-quoted. The migrator runs this script in one
-- transaction. SQL Server 2022 or later; Azure SQL Database works the same. Key columns are binary-collated
-- (Latin1_General_100_BIN2): ids compare ordinally, as on PostgreSQL, whatever the database default collation.
--
-- Global order: events.rv is a rowversion, assigned when the row is written and unique in the database. Readers read
-- rv < MIN_ACTIVE_ROWVERSION(): every such row is committed, so the log below that horizon is final (doc 05 §5.5).
-- events is the only table with a rowversion, so only in-progress appends hold the horizon.
--
-- Payloads (events.data, documents.data, process_instances.state, snapshots.data) are nvarchar(max) JSON text, kept as
-- written. Key lengths keep every primary key within the index key limits (900 bytes clustered, 1,700 nonclustered).

CREATE TABLE {schema}.streams (
    tenant_id    nvarchar(100) COLLATE Latin1_General_100_BIN2     NOT NULL CONSTRAINT df_streams_tenant DEFAULT N'-',
    stream_id    nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    stream_type  nvarchar(400) COLLATE Latin1_General_100_BIN2     NOT NULL,
    version      bigint            NOT NULL,
    is_deleted   bit               NOT NULL CONSTRAINT df_streams_deleted DEFAULT 0,
    is_archived  bit               NOT NULL CONSTRAINT df_streams_archived DEFAULT 0,
    created_at   datetimeoffset(7) NOT NULL CONSTRAINT df_streams_created DEFAULT SYSDATETIMEOFFSET(),
    updated_at   datetimeoffset(7) NOT NULL CONSTRAINT df_streams_updated DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT pk_streams PRIMARY KEY CLUSTERED (tenant_id, stream_id),
    CONSTRAINT ck_streams_stream_id CHECK (LEN(stream_id) BETWEEN 1 AND 200)
);

CREATE TABLE {schema}.events (
    seq            bigint            IDENTITY(1, 1) NOT NULL,
    rv             rowversion        NOT NULL,
    tenant_id      nvarchar(100) COLLATE Latin1_General_100_BIN2     NOT NULL CONSTRAINT df_events_tenant DEFAULT N'-',
    bucket         smallint          NOT NULL,
    stream_id      nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    stream_version bigint            NOT NULL,
    event_id       uniqueidentifier  NOT NULL,
    event_type     nvarchar(400) COLLATE Latin1_General_100_BIN2     NOT NULL,
    type_version   int               NOT NULL,
    tags           nvarchar(max)     NOT NULL CONSTRAINT df_events_tags DEFAULT N'[]',
    data           nvarchar(max)     NOT NULL,
    metadata       nvarchar(max)     NOT NULL,
    created_at     datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_events PRIMARY KEY CLUSTERED (seq),
    CONSTRAINT uq_events_stream_version UNIQUE (tenant_id, stream_id, stream_version),
    CONSTRAINT uq_events_event_id UNIQUE (event_id),
    CONSTRAINT ck_events_bucket CHECK (bucket BETWEEN 0 AND 1023)
);
CREATE UNIQUE INDEX ix_events_commit_order ON {schema}.events (rv) INCLUDE (bucket);
CREATE INDEX ix_events_type_time ON {schema}.events (tenant_id, event_type, created_at);

CREATE TABLE {schema}.outbox (
    seq           bigint            IDENTITY(1, 1) NOT NULL,
    message_id    uniqueidentifier  NOT NULL,
    partition     smallint          NOT NULL,
    partition_key nvarchar(400) COLLATE Latin1_General_100_BIN2     NULL,
    destination   nvarchar(400) COLLATE Latin1_General_100_BIN2     NOT NULL,
    kind          smallint          NOT NULL,
    attributes    nvarchar(max)     NOT NULL,
    body          varbinary(max)    NOT NULL,
    deliver_at    datetimeoffset(7) NOT NULL,
    attempts      int               NOT NULL CONSTRAINT df_outbox_attempts DEFAULT 0,
    last_error    nvarchar(max)     NULL,
    created_at    datetimeoffset(7) NOT NULL CONSTRAINT df_outbox_created DEFAULT SYSDATETIMEOFFSET(),
    first_due_at  datetimeoffset(7) NULL,   -- set by the first retry: the outbox age counts from here (doc 08 §6)
    CONSTRAINT pk_outbox PRIMARY KEY CLUSTERED (seq)
);
CREATE INDEX ix_outbox_ready ON {schema}.outbox (partition, deliver_at, seq);
CREATE INDEX ix_outbox_key ON {schema}.outbox (partition, partition_key, seq) WHERE partition_key IS NOT NULL;

CREATE TABLE {schema}.inbox (
    consumer       nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    message_id     uniqueidentifier  NOT NULL,
    processed_at   datetimeoffset(7) NOT NULL,
    result_status  smallint          NOT NULL,
    result_version bigint            NOT NULL,
    result_error   nvarchar(max)     NULL,
    result_commit  bigint            NOT NULL CONSTRAINT df_inbox_commit DEFAULT 0,
    result_seq     bigint            NOT NULL CONSTRAINT df_inbox_seq DEFAULT 0,
    CONSTRAINT pk_inbox PRIMARY KEY CLUSTERED (consumer, message_id)
);
CREATE INDEX ix_inbox_processed_at ON {schema}.inbox (processed_at);

CREATE TABLE {schema}.leases (
    name       nvarchar(400) COLLATE Latin1_General_100_BIN2     NOT NULL,
    owner      nvarchar(400) COLLATE Latin1_General_100_BIN2     NULL,
    token      bigint            NOT NULL CONSTRAINT df_leases_token DEFAULT 0,
    expires_at datetimeoffset(7) NULL,
    CONSTRAINT pk_leases PRIMARY KEY CLUSTERED (name)
);

CREATE TABLE {schema}.checkpoints (
    subscription nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    partition    int               NOT NULL,
    commit_pos   bigint            NOT NULL CONSTRAINT df_checkpoints_commit DEFAULT 0,
    seq_pos      bigint            NOT NULL CONSTRAINT df_checkpoints_seq DEFAULT 0,
    updated_at   datetimeoffset(7) NOT NULL CONSTRAINT df_checkpoints_updated DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT pk_checkpoints PRIMARY KEY CLUSTERED (subscription, partition)
);

CREATE TABLE {schema}.dead_letters (
    id                   uniqueidentifier  NOT NULL,
    consumer             nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    source               nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    message_id           uniqueidentifier  NOT NULL,
    message_type         nvarchar(400) COLLATE Latin1_General_100_BIN2     NULL,
    destination          nvarchar(400) COLLATE Latin1_General_100_BIN2     NULL,
    destination_is_topic bit               NOT NULL CONSTRAINT df_dead_letters_topic DEFAULT 0,
    attributes           nvarchar(max)     NOT NULL,
    body                 varbinary(max)    NOT NULL,
    reason               nvarchar(400) COLLATE Latin1_General_100_BIN2     NOT NULL,
    error_type           nvarchar(400) COLLATE Latin1_General_100_BIN2     NULL,
    error_message        nvarchar(max)     NULL,
    error_detail         nvarchar(max)     NULL,
    attempts             int               NOT NULL,
    first_failed_at      datetimeoffset(7) NOT NULL,
    parked_at            datetimeoffset(7) NOT NULL,
    status               smallint          NOT NULL CONSTRAINT df_dead_letters_status DEFAULT 0,
    status_changed_at    datetimeoffset(7) NULL,
    status_note          nvarchar(max)     NULL,
    CONSTRAINT pk_dead_letters PRIMARY KEY CLUSTERED (id)
);
CREATE INDEX ix_dead_letters_status ON {schema}.dead_letters (status, parked_at);

CREATE TABLE {schema}.process_instances (
    tenant_id    nvarchar(100) COLLATE Latin1_General_100_BIN2     NOT NULL CONSTRAINT df_process_tenant DEFAULT N'-',
    process_type nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    process_id   nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    version      bigint            NOT NULL,
    state        nvarchar(max)     NOT NULL,
    completed    bit               NOT NULL CONSTRAINT df_process_completed DEFAULT 0,
    updated_at   datetimeoffset(7) NOT NULL CONSTRAINT df_process_updated DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT pk_process_instances PRIMARY KEY NONCLUSTERED (tenant_id, process_type, process_id)
);

CREATE TABLE {schema}.snapshots (
    tenant_id   nvarchar(100) COLLATE Latin1_General_100_BIN2     NOT NULL CONSTRAINT df_snapshots_tenant DEFAULT N'-',
    stream_id   nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    version     bigint            NOT NULL,
    state_type  nvarchar(400) COLLATE Latin1_General_100_BIN2     NOT NULL,
    schema_hash nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    data        nvarchar(max)     NOT NULL,
    created_at  datetimeoffset(7) NOT NULL CONSTRAINT df_snapshots_created DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT pk_snapshots PRIMARY KEY CLUSTERED (tenant_id, stream_id)
);

CREATE TABLE {schema}.documents (
    tenant_id  nvarchar(100) COLLATE Latin1_General_100_BIN2     NOT NULL CONSTRAINT df_documents_tenant DEFAULT N'-',
    collection nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    id         nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    version    bigint            NOT NULL,
    data       nvarchar(max)     NOT NULL,
    updated_at datetimeoffset(7) NOT NULL CONSTRAINT df_documents_updated DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT pk_documents PRIMARY KEY NONCLUSTERED (tenant_id, collection, id)
);

CREATE TABLE {schema}.nodes (
    node_id      nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    roles        int               NOT NULL,
    heartbeat_at datetimeoffset(7) NOT NULL,
    CONSTRAINT pk_nodes PRIMARY KEY CLUSTERED (node_id)
);

CREATE TABLE {schema}.collection_aliases (
    alias       nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    target      nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    previous    nvarchar(200) COLLATE Latin1_General_100_BIN2     NULL,
    building    nvarchar(200) COLLATE Latin1_General_100_BIN2     NULL,
    switched_at datetimeoffset(7) NOT NULL CONSTRAINT df_aliases_switched DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT pk_collection_aliases PRIMARY KEY CLUSTERED (alias)
);

CREATE TABLE {schema}.subject_streams (
    tenant_id  nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    subject_id nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    stream_id  nvarchar(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT pk_subject_streams PRIMARY KEY NONCLUSTERED (tenant_id, subject_id, stream_id)
);

CREATE TABLE {schema}.schemas (
    tenant_id     nvarchar(100) COLLATE Latin1_General_100_BIN2     NOT NULL CONSTRAINT df_schemas_tenant DEFAULT N'-',
    message_type  nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    type_version  int               NOT NULL,
    kind          nvarchar(50) COLLATE Latin1_General_100_BIN2      NOT NULL,
    service       nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    json_schema   nvarchar(max)     NOT NULL,
    fingerprint   nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    first_seen_at datetimeoffset(7) NOT NULL CONSTRAINT df_schemas_first_seen DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT pk_schemas PRIMARY KEY CLUSTERED (tenant_id, message_type, type_version)
);

CREATE TABLE {schema}.data_keys (
    subject_id  nvarchar(200) COLLATE Latin1_General_100_BIN2     NOT NULL,
    key_ref     nvarchar(400) COLLATE Latin1_General_100_BIN2     NOT NULL,
    wrapped_key varbinary(max)    NOT NULL,
    created_at  datetimeoffset(7) NOT NULL CONSTRAINT df_data_keys_created DEFAULT SYSDATETIMEOFFSET(),
    shredded_at datetimeoffset(7) NULL,
    CONSTRAINT pk_data_keys PRIMARY KEY CLUSTERED (subject_id)
);
