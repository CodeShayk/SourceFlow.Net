namespace SourceFlow.Storage.PostgreSQL;

/// <summary>The adapter's SQL, rendered once for the configured schema (doc 05 §3–§7). Every value is a parameter.</summary>
internal sealed class SqlText
{
    private const string EventColumns = "e.seq, e.tx_id, e.tenant_id, e.bucket, e.stream_id, e.stream_version, e.event_id, e.event_type, e.type_version, e.tags, e.data, e.metadata, e.created_at";

    private const string DeadLetterColumns = "id, consumer, source, destination, destination_is_topic, reason, attributes, body, error_type, error_message, error_detail, attempts, first_failed_at, parked_at, status, status_changed_at, status_note";

    // A fenced write locks the lease row (FOR SHARE) until it commits, so a concurrent acquire waits for it: a holder that
    // paused after the check cannot commit once another owner has the lease (doc 05 §7.4, CR-14).
    private const string LeaseFence = "SELECT 1 FROM {s}.leases WHERE name = @lease AND owner = @lease_owner AND token = @lease_token AND expires_at > now() FOR SHARE";

    // The event insert shared by every append variant; 's' yields the base version, zero rows when the CAS failed.
    private const string AppendTail = """
        ins AS (
            INSERT INTO {s}.events (tenant_id, bucket, stream_id, stream_version, event_id, event_type, type_version, tags, data, metadata, created_at)
            SELECT @tenant, @bucket, @stream, s.base + e.ord, e.id, e.type, e.ver,
                   ARRAY(SELECT jsonb_array_elements_text(e.tags::jsonb)), e.data::json, e.meta::jsonb, e.at
              FROM s, unnest(@ids::uuid[], @types::text[], @vers::int[], @tags::text[], @datas::text[], @metas::text[], @ats::timestamptz[])
                   WITH ORDINALITY AS e(id, type, ver, tags, data, meta, at, ord)
             ORDER BY e.ord
            RETURNING stream_version, seq)
        SELECT count(*), coalesce(max(stream_version), 0), coalesce(max(seq), 0), pg_current_xact_id_if_assigned(), {s}.sf_assert(count(*) > 0, 'append') FROM ins
        """;

    public SqlText(string schema)
    {
        string s = "\"" + schema + "\"";
        string R(string sql) => sql.Replace("{s}", s, StringComparison.Ordinal);

        ReadStream = R($$"""
            SELECT s.version, {{EventColumns}}
              FROM {s}.streams s
              LEFT JOIN LATERAL (
                   SELECT * FROM {s}.events e
                    WHERE e.tenant_id = s.tenant_id AND e.stream_id = s.stream_id AND e.stream_version >= @from
                    ORDER BY e.stream_version
                    LIMIT @max) e ON true
             WHERE s.tenant_id = @tenant AND s.stream_id = @stream
             ORDER BY e.stream_version
            """);
        StreamInfo = R("SELECT stream_type, version, is_deleted, is_archived FROM {s}.streams WHERE tenant_id = @tenant AND stream_id = @stream");
        ArchivedStreams = R("SELECT stream_id FROM {s}.streams WHERE tenant_id = @tenant AND stream_id = ANY(@streams) AND is_archived");
        ArchiveStream = R("UPDATE {s}.streams SET is_archived = true, updated_at = now() WHERE tenant_id = @tenant AND stream_id = @stream");
        DeleteStream = R("UPDATE {s}.streams SET is_deleted = true, updated_at = now() WHERE tenant_id = @tenant AND stream_id = @stream");

        AppendExact = R("""
            WITH s AS (
                UPDATE {s}.streams SET version = version + @count, updated_at = now()
                 WHERE tenant_id = @tenant AND stream_id = @stream AND version = @expected AND NOT is_deleted AND NOT is_archived
                RETURNING version - @count AS base),
            """ + AppendTail);
        AppendNoStream = R("""
            WITH s AS (
                INSERT INTO {s}.streams (tenant_id, stream_id, stream_type, version) VALUES (@tenant, @stream, @type, @count)
                ON CONFLICT (tenant_id, stream_id) DO NOTHING
                RETURNING 0::bigint AS base),
            """ + AppendTail);

        // StreamExists and Any: the UPDATE takes the row lock and applies to the committed version it then sees, so it
        // is a compare-and-set on that version (doc 05 §3.1, §5.3) without an extra round trip.
        AppendStreamExists = R("""
            WITH s AS (
                UPDATE {s}.streams SET version = version + @count, updated_at = now()
                 WHERE tenant_id = @tenant AND stream_id = @stream AND NOT is_deleted AND NOT is_archived
                RETURNING version - @count AS base),
            """ + AppendTail);
        AppendAny = R("""
            WITH s AS (
                INSERT INTO {s}.streams AS t (tenant_id, stream_id, stream_type, version) VALUES (@tenant, @stream, @type, @count)
                ON CONFLICT (tenant_id, stream_id) DO UPDATE SET version = t.version + @count, updated_at = now()
                 WHERE NOT t.is_deleted AND NOT t.is_archived
                RETURNING version - @count AS base),
            """ + AppendTail);

        InsertOutbox = R("""
            INSERT INTO {s}.outbox (message_id, partition, partition_key, destination, kind, attributes, body, deliver_at)
            SELECT o.id, o.p, o.k, o.d, o.kind, o.a::jsonb, o.b, o.at
              FROM unnest(@ids::uuid[], @parts::smallint[], @keys::text[], @dests::text[], @kinds::smallint[], @attrs::text[], @bodies::bytea[], @ats::timestamptz[])
                   WITH ORDINALITY AS o(id, p, k, d, kind, a, b, at, ord)
             ORDER BY o.ord
            """);
        InsertInbox = R("""
            INSERT INTO {s}.inbox (consumer, message_id, processed_at, result_status, result_version, result_error, result_commit, result_seq)
            VALUES (@consumer, @id, @now, @status, @version, @error, @commit, @seq)
            """);
        InboxSeen = R("SELECT processed_at, result_status, result_version, result_error, result_commit, result_seq FROM {s}.inbox WHERE consumer = @consumer AND message_id = @id");

        // Conditional writes assert their outcome in SQL, so a failure aborts the batch before its COMMIT (V003).
        SaveCheckpoint = R("""
            WITH c AS (
                INSERT INTO {s}.checkpoints AS c (subscription, partition, commit_pos, seq_pos, updated_at)
                SELECT @sub, @p, @tx, @seq, now() FROM {s}.leases l
                 WHERE l.name = @lease AND l.owner = @lease_owner AND l.token = @lease_token AND l.expires_at > now()
                   FOR SHARE OF l
                ON CONFLICT (subscription, partition) DO UPDATE
                   SET commit_pos = excluded.commit_pos, seq_pos = excluded.seq_pos, updated_at = excluded.updated_at
                RETURNING 1)
            SELECT {s}.sf_assert(count(*) = 1, @check) FROM c
            """);
        GetCheckpoint = R("SELECT commit_pos, seq_pos FROM {s}.checkpoints WHERE subscription = @sub AND partition = @p");

        LoadProcess = R("SELECT version, state, completed, updated_at FROM {s}.process_instances WHERE tenant_id = @tenant AND process_type = @type AND process_id = @id");
        InsertProcess = R("""
            WITH i AS (
                INSERT INTO {s}.process_instances (tenant_id, process_type, process_id, version, state, completed)
                VALUES (@tenant, @type, @id, 1, @state, @completed)
                ON CONFLICT (tenant_id, process_type, process_id) DO NOTHING
                RETURNING 1)
            SELECT {s}.sf_assert(count(*) = 1, @check) FROM i
            """);
        UpdateProcess = R("""
            WITH u AS (
                UPDATE {s}.process_instances SET version = version + 1, state = @state, completed = @completed, updated_at = now()
                 WHERE tenant_id = @tenant AND process_type = @type AND process_id = @id AND version = @expected
                RETURNING 1)
            SELECT {s}.sf_assert(count(*) = 1, @check) FROM u
            """);

        GetDocument = R("SELECT version, data FROM {s}.documents WHERE tenant_id = @tenant AND collection = @collection AND id = @id");
        GetDocuments = R("SELECT id, version, data FROM {s}.documents WHERE tenant_id = @tenant AND collection = @collection AND id = ANY(@ids)");
        ListDocuments = R("SELECT id, version, data FROM {s}.documents WHERE tenant_id = @tenant AND collection = @collection ORDER BY id COLLATE \"C\" LIMIT @max");
        UpsertDocument = R("""
            INSERT INTO {s}.documents AS d (tenant_id, collection, id, version, data) VALUES (@tenant, @collection, @id, @version, @data)
            ON CONFLICT (tenant_id, collection, id) DO UPDATE SET version = excluded.version, data = excluded.data, updated_at = now()
             WHERE d.version <= excluded.version
            """);
        UpsertDocuments = R("""
            INSERT INTO {s}.documents AS d (tenant_id, collection, id, version, data)
            SELECT @tenant, u.c, u.i, u.v, u.j::json FROM unnest(@collections::text[], @ids::text[], @versions::bigint[], @datas::text[]) AS u(c, i, v, j)
            ON CONFLICT (tenant_id, collection, id) DO UPDATE SET version = excluded.version, data = excluded.data, updated_at = now()
             WHERE d.version <= excluded.version
            """);
        DeleteDocuments = R("DELETE FROM {s}.documents WHERE tenant_id = @tenant AND (collection, id) IN (SELECT * FROM unnest(@collections::text[], @ids::text[]))");
        DeleteDocument = R("DELETE FROM {s}.documents WHERE tenant_id = @tenant AND collection = @collection AND id = @id");

        ReadLog = R($$"""
            WITH h AS (SELECT pg_snapshot_xmin(pg_current_snapshot()) AS xmin)
            SELECT h.xmin, {{EventColumns}}
              FROM h
              LEFT JOIN LATERAL (
                   SELECT * FROM {s}.events e
                    WHERE (e.tx_id, e.seq) > (@tx, @seq) AND e.tx_id < h.xmin AND e.bucket BETWEEN @lo AND @hi
                    ORDER BY e.tx_id, e.seq
                    LIMIT @max) e ON true
             ORDER BY e.tx_id, e.seq
            """);

        AcquireLease = R("""
            INSERT INTO {s}.leases AS l (name, owner, token, expires_at) VALUES (@name, @owner, 1, now() + @ttl)
            ON CONFLICT (name) DO UPDATE
               SET owner = excluded.owner, expires_at = excluded.expires_at,
                   token = CASE WHEN l.owner = excluded.owner AND l.expires_at > now() THEN l.token ELSE l.token + 1 END
             WHERE l.owner IS NULL OR l.owner = excluded.owner OR l.expires_at IS NULL OR l.expires_at <= now()
            RETURNING token
            """);
        NodeHeartbeat = R("""
            WITH up AS (
                INSERT INTO {s}.nodes AS n (node_id, roles, heartbeat_at) VALUES (@node, @roles, now())
                ON CONFLICT (node_id) DO UPDATE SET roles = excluded.roles, heartbeat_at = excluded.heartbeat_at
                RETURNING 1)
            SELECT 1 + (SELECT count(*) FROM {s}.nodes WHERE node_id <> @node AND (roles & @roles) <> 0 AND heartbeat_at > now() - @window)
              FROM up
            """);
        RegisterSchemas = R("""
            WITH input AS (
                SELECT * FROM unnest(@types::text[], @versions::int[], @kinds::text[], @schemas::text[], @prints::text[]) AS u(t, v, k, j, f)),
            ins AS (
                INSERT INTO {s}.schemas (message_type, type_version, kind, service, json_schema, fingerprint)
                SELECT t, v, k, @service, j::jsonb, f FROM input
                ON CONFLICT (tenant_id, message_type, type_version) DO NOTHING
                RETURNING 1)
            SELECT i.t, i.v, x.fingerprint, i.f
              FROM input i
              JOIN {s}.schemas x ON x.tenant_id = '-' AND x.message_type = i.t AND x.type_version = i.v
             WHERE x.fingerprint <> i.f
            """);
        ListSchemas = R("SELECT message_type, type_version, kind, json_schema::text FROM {s}.schemas WHERE tenant_id = '-' ORDER BY message_type, type_version");
        DropCollection = R("DELETE FROM {s}.documents WHERE collection = @collection");
        ListAliases = R("SELECT alias, target, previous, building, switched_at FROM {s}.collection_aliases ORDER BY alias");
        ClaimAlias = R("INSERT INTO {s}.collection_aliases (alias, target) VALUES (@alias, @target) ON CONFLICT (alias) DO NOTHING");
        SwitchAlias = R("""
            UPDATE {s}.collection_aliases
               SET target = @target, previous = target, switched_at = now(),
                   building = CASE WHEN building = @target THEN NULL ELSE building END
             WHERE alias = @alias AND target = @expected
            """);
        RequestAlias = R("""
            INSERT INTO {s}.collection_aliases (alias, target, building) VALUES (@alias, @target, @building)
            ON CONFLICT (alias) DO UPDATE SET building = excluded.building
            """);
        ClearPreviousAlias = R("UPDATE {s}.collection_aliases SET previous = NULL WHERE alias = @alias AND previous = @previous");
        NodeLeave = R("DELETE FROM {s}.nodes WHERE node_id = @node");
        ReleaseLease = R("UPDATE {s}.leases SET owner = NULL, expires_at = NULL WHERE name = @lease AND owner = @lease_owner AND token = @lease_token");

        ClaimOutbox = R($$"""
            WITH f AS (SELECT EXISTS ({{LeaseFence}}) AS ok)
            SELECT f.ok, o.seq, o.attributes, o.body, o.destination, o.kind, o.deliver_at, o.partition, o.attempts, o.last_error, o.created_at
              FROM f
              LEFT JOIN LATERAL (
                   SELECT * FROM {s}.outbox o
                    WHERE f.ok AND o.partition = @p AND o.deliver_at <= @now
                      AND (o.partition_key IS NULL OR NOT EXISTS (
                           SELECT 1 FROM {s}.outbox h
                            WHERE h.partition = o.partition AND h.partition_key = o.partition_key AND h.seq < o.seq))
                    ORDER BY o.seq
                    LIMIT @max) o ON true
             ORDER BY o.seq
            """);
        CompleteOutbox = R($$"""
            WITH f AS (SELECT EXISTS ({{LeaseFence}}) AS ok),
                 d AS (DELETE FROM {s}.outbox WHERE seq = ANY(@seqs) AND (SELECT ok FROM f))
            SELECT ok FROM f
            """);
        RetryOutbox = R($$"""
            WITH f AS (SELECT EXISTS ({{LeaseFence}}) AS ok),
                 u AS (UPDATE {s}.outbox SET attempts = attempts + 1, last_error = @error, deliver_at = @at, first_due_at = COALESCE(first_due_at, greatest(deliver_at, created_at)) WHERE seq = @seq AND (SELECT ok FROM f))
            SELECT ok FROM f
            """);
        CountOutbox = R("SELECT count(*) FROM {s}.outbox");
        CountDueOutbox = R("SELECT count(*) FROM {s}.outbox WHERE deliver_at <= @now");
        // A message is late from when it first became due; a retry moves deliver_at but not first_due_at.
        OutboxBacklog = R("SELECT partition, count(*), min(COALESCE(first_due_at, greatest(deliver_at, created_at))) FILTER (WHERE deliver_at <= now() OR first_due_at IS NOT NULL) FROM {s}.outbox GROUP BY partition ORDER BY partition");
        ParkedDeadLettersByConsumer = R("SELECT consumer, count(*) FROM {s}.dead_letters WHERE status = @status GROUP BY consumer");
        ActiveProcessesByType = R("SELECT process_type, count(*) FROM {s}.process_instances WHERE NOT completed GROUP BY process_type");
        // The workflow-instances read model (doc 15 §7): its documents carry the status, so the gauge needs no engine.
        ActiveWorkflows = R("""
            SELECT data->>'workflow', (data->>'workflowVersion')::int, data->>'status', count(*) FROM {s}.documents
             WHERE collection = 'workflow-instances@1' AND data->>'status' IN ('Running', 'Compensating', 'ManualIntervention')
             GROUP BY 1, 2, 3
            """);
        CountEventsAfter = R("""
            SELECT count(*) FROM (
                SELECT 1 FROM {s}.events e
                 WHERE (e.tx_id, e.seq) > (@tx, @seq) AND e.bucket BETWEEN @lo AND @hi
                 LIMIT @cap) x
            """);

        ParkDeadLetter = R($$"""
            INSERT INTO {s}.dead_letters ({{DeadLetterColumns}}, message_id, message_type)
            VALUES (@id, @consumer, @source, @destination, @is_topic, @reason, @attributes, @body, @error_type, @error_message, @error_detail,
                    @attempts, @first_failed_at, @parked_at, @status, NULL, @status_note, @message_id, @message_type)
            ON CONFLICT (id) DO UPDATE SET
                consumer = excluded.consumer, source = excluded.source, destination = excluded.destination,
                destination_is_topic = excluded.destination_is_topic, reason = excluded.reason, attributes = excluded.attributes,
                body = excluded.body, error_type = excluded.error_type, error_message = excluded.error_message,
                error_detail = excluded.error_detail, attempts = excluded.attempts, first_failed_at = excluded.first_failed_at,
                parked_at = excluded.parked_at, status = excluded.status, status_note = excluded.status_note,
                message_id = excluded.message_id, message_type = excluded.message_type
            """);
        QueryDeadLetters = R($$"""
            SELECT {{DeadLetterColumns}} FROM {s}.dead_letters
             WHERE (@consumer IS NULL OR consumer = @consumer)
               AND (@reason IS NULL OR reason = @reason)
               AND (@status IS NULL OR status = @status)
               AND (@type IS NULL OR message_type = @type)
               AND (@after IS NULL OR parked_at >= @after)
               AND (@before IS NULL OR parked_at < @before)
             ORDER BY parked_at DESC, id
             LIMIT @max
            """);
        GetDeadLetter = R($"SELECT {DeadLetterColumns} FROM {{s}}.dead_letters WHERE id = @id");
        SetDeadLetterStatus = R("UPDATE {s}.dead_letters SET status = @status, status_note = @note, status_changed_at = @now WHERE id = @id");

        GetSnapshot = R("SELECT version, state_type, schema_hash, data FROM {s}.snapshots WHERE tenant_id = @tenant AND stream_id = @stream");
        SaveSnapshot = R("""
            INSERT INTO {s}.snapshots AS t (tenant_id, stream_id, version, state_type, schema_hash, data) VALUES (@tenant, @stream, @version, @state_type, @schema_hash, @data)
            ON CONFLICT (tenant_id, stream_id) DO UPDATE
               SET version = excluded.version, state_type = excluded.state_type, schema_hash = excluded.schema_hash, data = excluded.data, created_at = now()
             WHERE t.version <= excluded.version
            """);

        Notify = "SELECT pg_notify(@channel, @origin)";

        // Bounded batches (doc 05 §9): each statement deletes at most @batch rows and holds its locks briefly.
        PurgeInbox = R("DELETE FROM {s}.inbox WHERE ctid IN (SELECT ctid FROM {s}.inbox WHERE processed_at < @before LIMIT @batch)");
        PurgeDeadLetters = R("DELETE FROM {s}.dead_letters WHERE id IN (SELECT id FROM {s}.dead_letters WHERE status <> 0 AND status_changed_at < @before LIMIT @batch)");
    }

    public string ReadStream { get; }

    public string StreamInfo { get; }

    public string ArchiveStream { get; }

    public string ArchivedStreams { get; }

    public string DeleteStream { get; }

    public string AppendExact { get; }

    public string AppendNoStream { get; }

    public string AppendStreamExists { get; }

    public string AppendAny { get; }

    public string InsertOutbox { get; }

    public string InsertInbox { get; }

    public string InboxSeen { get; }

    public string SaveCheckpoint { get; }

    public string GetCheckpoint { get; }

    public string LoadProcess { get; }

    public string InsertProcess { get; }

    public string UpdateProcess { get; }

    public string GetDocument { get; }

    public string ListDocuments { get; }

    public string GetDocuments { get; }

    public string UpsertDocument { get; }

    public string DeleteDocument { get; }

    public string UpsertDocuments { get; }

    public string DeleteDocuments { get; }

    public string ReadLog { get; }

    public string AcquireLease { get; }

    public string ReleaseLease { get; }

    public string NodeHeartbeat { get; }

    public string NodeLeave { get; }

    public string DropCollection { get; }

    public string ListAliases { get; }

    public string ClaimAlias { get; }

    public string SwitchAlias { get; }

    public string RequestAlias { get; }

    public string ClearPreviousAlias { get; }

    public string RegisterSchemas { get; }

    public string ListSchemas { get; }

    public string ClaimOutbox { get; }

    public string CompleteOutbox { get; }

    public string RetryOutbox { get; }

    public string CountOutbox { get; }

    public string OutboxBacklog { get; }

    public string ParkedDeadLettersByConsumer { get; }

    public string ActiveProcessesByType { get; }

    public string ActiveWorkflows { get; }

    public string CountEventsAfter { get; }

    public string CountDueOutbox { get; }

    public string ParkDeadLetter { get; }

    public string QueryDeadLetters { get; }

    public string GetDeadLetter { get; }

    public string SetDeadLetterStatus { get; }

    public string GetSnapshot { get; }

    public string SaveSnapshot { get; }

    public string Notify { get; }

    public string PurgeInbox { get; }

    public string PurgeDeadLetters { get; }
}
