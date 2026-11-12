namespace SourceFlow.Storage.PostgreSQL;

/// <summary>A monthly partition of the events table (<see cref="PostgreSqlStorageOptions.PartitionEvents"/>, doc 05 §9).</summary>
/// <param name="Name">The partition table, for example <c>events_2026_12</c>; the first one is <c>events_initial</c>.</param>
/// <param name="From">The inclusive lower bound of <c>created_at</c>; null for the first partition (from the beginning).</param>
/// <param name="To">The exclusive upper bound of <c>created_at</c>.</param>
/// <param name="DetachedAt">When the partition was detached from the events table; null while attached.</param>
public sealed record EventPartition(string Name, DateTimeOffset? From, DateTimeOffset To, DateTimeOffset? DetachedAt);

/// <summary>A partition can't be detached: it still holds events of open streams, or its range is not in the past.</summary>
public sealed class EventPartitionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public EventPartitionException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The SQL of the partitioned events variant (doc 05 §9, WP-4.5). The conversion is one idempotent block: it does
/// nothing when <c>events</c> is already partitioned. The rows already written stay where they are: the old table is
/// attached as the first partition, <c>events_initial</c>, from MINVALUE up to the start of the month after both its
/// newest row and now, so no data is copied. Monthly partitions follow, created ahead of time by
/// <c>sf_ensure_event_partitions</c>; <c>event_partitions</c> records the ranges.
/// </summary>
/// <remarks>
/// A partitioned table's unique constraints must include the partition key, so <c>(seq, created_at)</c> is the primary
/// key and the event-id and stream-version constraints include <c>created_at</c>. They become per-partition backstops:
/// the <c>streams</c> compare-and-set still enforces versions. <c>seq</c> comes from the <c>events_seq</c> sequence
/// (PostgreSQL 15 and 16 do not allow identity columns on partitioned tables); an application role needs
/// <c>USAGE</c> on it.
/// </remarks>
internal static class PostgreSqlPartitioning
{
    /// <summary>Partitions created ahead by the migrator, before the maintenance job runs.</summary>
    public const int MigratorMonthsAhead = 3;

    public const string Convert = """
        DO $convert$
        DECLARE
            boundary timestamptz;
            managed record;
        BEGIN
            IF (SELECT c.relkind FROM pg_class c WHERE c.oid = '{schema}.events'::regclass) = 'p' THEN
                RETURN;
            END IF;

            LOCK TABLE {schema}.events IN ACCESS EXCLUSIVE MODE;
            CREATE SEQUENCE {schema}.events_seq AS bigint;
            PERFORM setval('{schema}.events_seq', COALESCE((SELECT max(seq) FROM {schema}.events), 0) + 1, false);
            ALTER TABLE {schema}.events ALTER COLUMN seq DROP IDENTITY;

            -- Query-index sync (doc 14 §7.2) recreates its indexes on the partitioned table.
            FOR managed IN
                SELECT i.relname FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid
                 WHERE x.indrelid = '{schema}.events'::regclass AND starts_with(i.relname, 'sfq_')
            LOOP
                EXECUTE format('DROP INDEX {schema}.%I', managed.relname);
            END LOOP;

            ALTER TABLE {schema}.events RENAME TO events_initial;
            ALTER TABLE {schema}.events_initial DROP CONSTRAINT events_pkey;   -- the attach builds the (seq, created_at) key
            ALTER TABLE {schema}.events_initial RENAME CONSTRAINT uq_events_stream_version TO events_initial_stream_version;
            ALTER TABLE {schema}.events_initial RENAME CONSTRAINT uq_events_event_id TO events_initial_event_id;
            ALTER INDEX {schema}.ix_events_commit_order RENAME TO events_initial_commit_order;
            ALTER INDEX {schema}.ix_events_type_time RENAME TO events_initial_type_time;
            ALTER INDEX {schema}.ix_events_correlation RENAME TO events_initial_correlation;
            ALTER INDEX {schema}.ix_events_causation RENAME TO events_initial_causation;

            CREATE TABLE {schema}.events (
                seq            bigint      NOT NULL DEFAULT nextval('{schema}.events_seq'),
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
                CONSTRAINT pk_events PRIMARY KEY (seq, created_at),
                CONSTRAINT uq_events_stream_version UNIQUE (tenant_id, stream_id, stream_version, created_at),
                CONSTRAINT uq_events_event_id UNIQUE (event_id, created_at),
                CONSTRAINT ck_events_bucket CHECK (bucket BETWEEN 0 AND 1023)
            ) PARTITION BY RANGE (created_at);
            ALTER SEQUENCE {schema}.events_seq OWNED BY {schema}.events.seq;
            CREATE INDEX ix_events_commit_order ON {schema}.events (tx_id, seq) INCLUDE (bucket);
            CREATE INDEX ix_events_type_time    ON {schema}.events (tenant_id, event_type, created_at);
            CREATE INDEX ix_events_correlation  ON {schema}.events ((metadata->>'correlationId'));
            CREATE INDEX ix_events_causation    ON {schema}.events ((metadata->>'causationId'));
            CREATE POLICY tenant_isolation ON {schema}.events
                USING (tenant_id = current_setting('sourceflow.tenant', true))
                WITH CHECK (tenant_id = current_setting('sourceflow.tenant', true));

            CREATE TABLE {schema}.event_partitions (
                name        text        PRIMARY KEY,
                from_at     timestamptz NULL,
                to_at       timestamptz NOT NULL,
                created_at  timestamptz NOT NULL DEFAULT now(),
                detached_at timestamptz NULL
            );

            SELECT (date_trunc('month', greatest(now(), COALESCE(max(created_at), now())) AT TIME ZONE 'UTC') + interval '1 month') AT TIME ZONE 'UTC'
              INTO boundary FROM {schema}.events_initial;
            EXECUTE format('ALTER TABLE {schema}.events ATTACH PARTITION {schema}.events_initial FOR VALUES FROM (MINVALUE) TO (%L)', boundary);
            INSERT INTO {schema}.event_partitions (name, from_at, to_at) VALUES ('events_initial', NULL, boundary);

            CREATE FUNCTION {schema}.sf_ensure_event_partitions(as_of timestamptz, ahead int) RETURNS int
                LANGUAGE plpgsql
                AS $ensure$
            DECLARE
                horizon timestamptz := (date_trunc('month', as_of AT TIME ZONE 'UTC') + make_interval(months => ahead + 1)) AT TIME ZONE 'UTC';
                lower_bound timestamptz;
                upper_bound timestamptz;
                part text;
                created int := 0;
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('sourceflow.partitions:{schema}', 0));
                SELECT max(to_at) INTO lower_bound FROM {schema}.event_partitions;
                WHILE lower_bound < horizon LOOP
                    upper_bound := ((lower_bound AT TIME ZONE 'UTC') + interval '1 month') AT TIME ZONE 'UTC';
                    part := 'events_' || to_char(lower_bound AT TIME ZONE 'UTC', 'YYYY_MM');
                    EXECUTE format('CREATE TABLE {schema}.%I PARTITION OF {schema}.events FOR VALUES FROM (%L) TO (%L)', part, lower_bound, upper_bound);
                    INSERT INTO {schema}.event_partitions (name, from_at, to_at) VALUES (part, lower_bound, upper_bound);
                    lower_bound := upper_bound;
                    created := created + 1;
                END LOOP;
                RETURN created;
            END
            $ensure$;
        END
        $convert$;
        """;

    public const string IsPartitioned = """
        SELECT COALESCE((SELECT c.relkind = 'p' FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                          WHERE n.nspname = @schema AND c.relname = 'events'), false)
        """;
}
