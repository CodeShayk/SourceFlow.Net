namespace SourceFlow.Storage.SqlServer;

/// <summary>
/// The adapter's T-SQL, rendered once for the configured schema (doc 05 §5.5, §7). Every value is a parameter. Statements
/// that must not take effect when a condition fails raise <c>THROW 50001</c> (append) or <c>THROW 50002</c> with the check's
/// code (process state, fenced checkpoint); the session rolls the transaction back and maps the code.
/// </summary>
internal sealed class SqlText
{
    public const int AppendFailed = 50001;
    public const int CheckFailed = 50002;

    private const string EventColumns = "CAST(e.rv AS bigint) AS rv, e.seq, e.tenant_id, e.bucket, e.stream_id, e.stream_version, e.event_id, e.event_type, e.type_version, e.tags, e.data, e.metadata, e.created_at";

    private const string DeadLetterColumns = "id, consumer, source, destination, destination_is_topic, reason, attributes, body, error_type, error_message, error_detail, attempts, first_failed_at, parked_at, status, status_changed_at, status_note";

    private const string LeaseFence = "EXISTS (SELECT 1 FROM {s}.leases WHERE name = @lease AND owner = @lease_owner AND token = @lease_token AND expires_at > SYSDATETIMEOFFSET())";

    public SqlText(string schema)
    {
        string s = "[" + schema + "]";
        string R(string sql) => sql.Replace("{s}", s, StringComparison.Ordinal);
        Schema = s;

        ReadStream = R($$"""
            SELECT s.version, {{EventColumns}}
              FROM {s}.streams s
              OUTER APPLY (
                   SELECT TOP (@max) * FROM {s}.events e
                    WHERE e.tenant_id = s.tenant_id AND e.stream_id = s.stream_id AND e.stream_version >= @from
                    ORDER BY e.stream_version) e
             WHERE s.tenant_id = @tenant AND s.stream_id = @stream
             ORDER BY e.stream_version
            """);
        StreamInfo = R("SELECT stream_type, version, is_deleted, is_archived FROM {s}.streams WHERE tenant_id = @tenant AND stream_id = @stream");
        ArchiveStream = R("UPDATE {s}.streams SET is_archived = 1, updated_at = SYSDATETIMEOFFSET() WHERE tenant_id = @tenant AND stream_id = @stream");
        DeleteStream = R("UPDATE {s}.streams SET is_deleted = 1, updated_at = SYSDATETIMEOFFSET() WHERE tenant_id = @tenant AND stream_id = @stream");

        // The stream row is updated (row lock) before the events are inserted, so each event's rowversion is assigned
        // after the previous version committed: per-stream order is global order (doc 05 §5.3, §5.5).
        AppendExact = R("""
            UPDATE {s}.streams WITH (ROWLOCK) SET @base = version, version = version + @count, updated_at = SYSDATETIMEOFFSET()
             WHERE tenant_id = @tenant AND stream_id = @stream AND version = @expected AND is_deleted = 0 AND is_archived = 0;
            IF @@ROWCOUNT = 0 BEGIN; THROW 50001, N'append', 1; END;
            """);
        AppendNoStream = R("""
            IF EXISTS (SELECT 1 FROM {s}.streams WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant AND stream_id = @stream) BEGIN; THROW 50001, N'append', 1; END;
            INSERT INTO {s}.streams (tenant_id, stream_id, stream_type, version) VALUES (@tenant, @stream, @type, @count);
            SET @base = 0;
            """);
        AppendStreamExists = R("""
            UPDATE {s}.streams WITH (ROWLOCK) SET @base = version, version = version + @count, updated_at = SYSDATETIMEOFFSET()
             WHERE tenant_id = @tenant AND stream_id = @stream AND is_deleted = 0 AND is_archived = 0;
            IF @@ROWCOUNT = 0 BEGIN; THROW 50001, N'append', 1; END;
            """);
        AppendAny = R("""
            UPDATE {s}.streams WITH (UPDLOCK, HOLDLOCK) SET @base = version, version = version + @count, updated_at = SYSDATETIMEOFFSET()
             WHERE tenant_id = @tenant AND stream_id = @stream AND is_deleted = 0 AND is_archived = 0;
            IF @@ROWCOUNT = 0
            BEGIN
                IF EXISTS (SELECT 1 FROM {s}.streams WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant AND stream_id = @stream) BEGIN; THROW 50001, N'append', 1; END;
                INSERT INTO {s}.streams (tenant_id, stream_id, stream_type, version) VALUES (@tenant, @stream, @type, @count);
                SET @base = 0;
            END;
            """);
        AppendTail = R("""
            INSERT INTO {s}.events (tenant_id, bucket, stream_id, stream_version, event_id, event_type, type_version, tags, data, metadata, created_at)
            SELECT @tenant, @bucket, @stream, @base + r.ord, r.id, r.type, r.ver, r.tags, r.data, r.meta, r.at
              FROM OPENJSON(@rows) WITH (ord bigint '$.o', id uniqueidentifier '$.i', type nvarchar(400) '$.t', ver int '$.v',
                                         tags nvarchar(max) '$.g', data nvarchar(max) '$.d', meta nvarchar(max) '$.m', at datetimeoffset(7) '$.a') r
             ORDER BY r.ord;
            SELECT TOP (1) @count, stream_version, CAST(rv AS bigint), seq FROM {s}.events
             WHERE tenant_id = @tenant AND stream_id = @stream ORDER BY stream_version DESC;
            """);

        InsertOutbox = R("""
            INSERT INTO {s}.outbox (message_id, partition, partition_key, destination, kind, attributes, body, deliver_at)
            SELECT r.id, r.p, r.k, r.d, r.kind, r.a, CONVERT(varbinary(max), r.b, 2), r.at
              FROM OPENJSON(@rows) WITH (ord int '$.o', id uniqueidentifier '$.i', p smallint '$.p', k nvarchar(400) '$.k', d nvarchar(400) '$.d',
                                         kind smallint '$.n', a nvarchar(max) '$.a', b varchar(max) '$.b', at datetimeoffset(7) '$.t') r
             ORDER BY r.ord;
            """);
        InsertInbox = R("""
            INSERT INTO {s}.inbox (consumer, message_id, processed_at, result_status, result_version, result_error, result_commit, result_seq)
            VALUES (@consumer, @id, @now, @status, @version, @error, @commit, @seq);
            """);
        InboxSeen = R("SELECT processed_at, result_status, result_version, result_error, result_commit, result_seq FROM {s}.inbox WHERE consumer = @consumer AND message_id = @id");

        SaveCheckpoint = R($$"""
            IF NOT {{LeaseFence}} BEGIN; THROW 50002, @check, 1; END;
            UPDATE {s}.checkpoints SET commit_pos = @commit, seq_pos = @seq, updated_at = SYSDATETIMEOFFSET() WHERE subscription = @sub AND partition = @p;
            IF @@ROWCOUNT = 0 INSERT INTO {s}.checkpoints (subscription, partition, commit_pos, seq_pos) VALUES (@sub, @p, @commit, @seq);
            """);
        GetCheckpoint = R("SELECT commit_pos, seq_pos FROM {s}.checkpoints WHERE subscription = @sub AND partition = @p");

        LoadProcess = R("SELECT version, state, completed, updated_at FROM {s}.process_instances WHERE tenant_id = @tenant AND process_type = @type AND process_id = @id");
        InsertProcess = R("""
            INSERT INTO {s}.process_instances (tenant_id, process_type, process_id, version, state, completed)
            SELECT @tenant, @type, @id, 1, @state, @completed
             WHERE NOT EXISTS (SELECT 1 FROM {s}.process_instances WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant AND process_type = @type AND process_id = @id);
            IF @@ROWCOUNT = 0 BEGIN; THROW 50002, @check, 1; END;
            """);
        UpdateProcess = R("""
            UPDATE {s}.process_instances SET version = version + 1, state = @state, completed = @completed, updated_at = SYSDATETIMEOFFSET()
             WHERE tenant_id = @tenant AND process_type = @type AND process_id = @id AND version = @expected;
            IF @@ROWCOUNT = 0 BEGIN; THROW 50002, @check, 1; END;
            """);

        GetDocument = R("SELECT version, data FROM {s}.documents WHERE tenant_id = @tenant AND collection = @collection AND id = @id");
        GetDocuments = R("SELECT d.id, d.version, d.data FROM {s}.documents d JOIN OPENJSON(@ids) WITH (id nvarchar(200) '$') i ON d.id = i.id WHERE d.tenant_id = @tenant AND d.collection = @collection");
        ListDocuments = R("SELECT TOP (@max) id, version, data FROM {s}.documents WHERE tenant_id = @tenant AND collection = @collection ORDER BY id COLLATE Latin1_General_BIN2");
        UpsertDocument = R("""
            UPDATE {s}.documents SET version = @version, data = @data, updated_at = SYSDATETIMEOFFSET()
             WHERE tenant_id = @tenant AND collection = @collection AND id = @id AND version <= @version;
            IF @@ROWCOUNT = 0 AND NOT EXISTS (SELECT 1 FROM {s}.documents WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id = @tenant AND collection = @collection AND id = @id)
                INSERT INTO {s}.documents (tenant_id, collection, id, version, data) VALUES (@tenant, @collection, @id, @version, @data);
            """);
        UpsertDocuments = R("""
            MERGE {s}.documents WITH (HOLDLOCK) AS d
            USING (SELECT c, i, v, j FROM OPENJSON(@rows) WITH (c nvarchar(200) '$.c', i nvarchar(200) '$.i', v bigint '$.v', j nvarchar(max) '$.j')) AS u
               ON d.tenant_id = @tenant AND d.collection = u.c AND d.id = u.i
             WHEN MATCHED AND d.version <= u.v THEN UPDATE SET version = u.v, data = u.j, updated_at = SYSDATETIMEOFFSET()
             WHEN NOT MATCHED THEN INSERT (tenant_id, collection, id, version, data) VALUES (@tenant, u.c, u.i, u.v, u.j);
            """);
        DeleteDocuments = R("""
            DELETE d FROM {s}.documents d
              JOIN OPENJSON(@rows) WITH (c nvarchar(200) '$.c', i nvarchar(200) '$.i') u ON d.collection = u.c AND d.id = u.i
             WHERE d.tenant_id = @tenant;
            """);
        DeleteDocument = R("DELETE FROM {s}.documents WHERE tenant_id = @tenant AND collection = @collection AND id = @id;");

        // Rows below MIN_ACTIVE_ROWVERSION() are committed; the horizon reported is inclusive (doc 05 §5.5, ADR-028).
        ReadLog = R($$"""
            DECLARE @h bigint = CAST(MIN_ACTIVE_ROWVERSION() AS bigint);
            SELECT @h, {{EventColumns}}
              FROM (SELECT 1 AS one) x
              OUTER APPLY (
                   SELECT TOP (@max) * FROM {s}.events e
                    WHERE e.rv > CONVERT(binary(8), @rv) AND e.rv < CONVERT(binary(8), @h) AND e.bucket BETWEEN @lo AND @hi
                    ORDER BY e.rv) e
             ORDER BY e.rv
            """);
        SafeHorizon = "SELECT CAST(MIN_ACTIVE_ROWVERSION() AS bigint)";

        AcquireLease = R("""
            MERGE {s}.leases WITH (HOLDLOCK) AS l
            USING (SELECT @name AS name) AS i ON l.name = i.name
             WHEN NOT MATCHED THEN INSERT (name, owner, token, expires_at) VALUES (@name, @owner, 1, DATEADD(millisecond, @ttl, SYSDATETIMEOFFSET()))
             WHEN MATCHED AND (l.owner IS NULL OR l.owner = @owner OR l.expires_at IS NULL OR l.expires_at <= SYSDATETIMEOFFSET()) THEN
                  UPDATE SET token = CASE WHEN l.owner = @owner AND l.expires_at > SYSDATETIMEOFFSET() THEN l.token ELSE l.token + 1 END,
                             owner = @owner, expires_at = DATEADD(millisecond, @ttl, SYSDATETIMEOFFSET())
            OUTPUT inserted.token;
            """);
        ReleaseLease = R("UPDATE {s}.leases SET owner = NULL, expires_at = NULL WHERE name = @lease AND owner = @lease_owner AND token = @lease_token");
        NodeHeartbeat = R("""
            MERGE {s}.nodes WITH (HOLDLOCK) AS n
            USING (SELECT @node AS node_id) AS i ON n.node_id = i.node_id
             WHEN MATCHED THEN UPDATE SET roles = @roles, heartbeat_at = SYSDATETIMEOFFSET()
             WHEN NOT MATCHED THEN INSERT (node_id, roles, heartbeat_at) VALUES (@node, @roles, SYSDATETIMEOFFSET());
            SELECT 1 + COUNT(*) FROM {s}.nodes
             WHERE node_id <> @node AND (roles & @roles) <> 0 AND heartbeat_at > DATEADD(millisecond, -@window, SYSDATETIMEOFFSET());
            """);
        NodeLeave = R("DELETE FROM {s}.nodes WHERE node_id = @node");

        RegisterSchemas = R("""
            DECLARE @input TABLE (t nvarchar(200), v int, k nvarchar(50), j nvarchar(max), f nvarchar(200));
            INSERT INTO @input SELECT t, v, k, j, f FROM OPENJSON(@rows) WITH (t nvarchar(200) '$.t', v int '$.v', k nvarchar(50) '$.k', j nvarchar(max) '$.j', f nvarchar(200) '$.f');
            INSERT INTO {s}.schemas (message_type, type_version, kind, service, json_schema, fingerprint)
            SELECT i.t, i.v, i.k, @service, i.j, i.f FROM @input i
             WHERE NOT EXISTS (SELECT 1 FROM {s}.schemas x WITH (UPDLOCK, HOLDLOCK) WHERE x.tenant_id = N'-' AND x.message_type = i.t AND x.type_version = i.v);
            SELECT i.t, i.v, x.fingerprint, i.f
              FROM @input i JOIN {s}.schemas x ON x.tenant_id = N'-' AND x.message_type = i.t AND x.type_version = i.v
             WHERE x.fingerprint <> i.f;
            """);
        ListSchemas = R("SELECT message_type, type_version, kind, json_schema FROM {s}.schemas WHERE tenant_id = N'-' ORDER BY message_type, type_version");
        DropCollection = R("DELETE FROM {s}.documents WHERE collection = @collection");
        ListAliases = R("SELECT alias, target, previous, building, switched_at FROM {s}.collection_aliases ORDER BY alias");
        ClaimAlias = R("""
            INSERT INTO {s}.collection_aliases (alias, target) SELECT @alias, @target
             WHERE NOT EXISTS (SELECT 1 FROM {s}.collection_aliases WITH (UPDLOCK, HOLDLOCK) WHERE alias = @alias)
            """);
        SwitchAlias = R("""
            UPDATE {s}.collection_aliases
               SET target = @target, previous = target, switched_at = SYSDATETIMEOFFSET(),
                   building = CASE WHEN building = @target THEN NULL ELSE building END
             WHERE alias = @alias AND target = @expected
            """);
        RequestAlias = R("""
            MERGE {s}.collection_aliases WITH (HOLDLOCK) AS a
            USING (SELECT @alias AS alias) AS i ON a.alias = i.alias
             WHEN MATCHED THEN UPDATE SET building = @building
             WHEN NOT MATCHED THEN INSERT (alias, target, building) VALUES (@alias, @target, @building);
            """);
        ClearPreviousAlias = R("UPDATE {s}.collection_aliases SET previous = NULL WHERE alias = @alias AND previous = @previous");

        // The fence is read once; the claim returns it in every row, or alone when nothing is due.
        ClaimOutbox = R($$"""
            DECLARE @ok bit = CASE WHEN {{LeaseFence}} THEN 1 ELSE 0 END;
            SELECT @ok, o.seq, o.attributes, o.body, o.destination, o.kind, o.deliver_at, o.partition, o.attempts, o.last_error, o.created_at
              FROM (SELECT 1 AS one) x
              OUTER APPLY (
                   SELECT TOP (@max) * FROM {s}.outbox o
                    WHERE @ok = 1 AND o.partition = @p AND o.deliver_at <= @now
                      AND (o.partition_key IS NULL OR NOT EXISTS (
                           SELECT 1 FROM {s}.outbox h
                            WHERE h.partition = o.partition AND h.partition_key = o.partition_key AND h.seq < o.seq))
                    ORDER BY o.seq) o
             ORDER BY o.seq
            """);
        CompleteOutbox = R($$"""
            DECLARE @ok bit = CASE WHEN {{LeaseFence}} THEN 1 ELSE 0 END;
            IF @ok = 1 DELETE o FROM {s}.outbox o JOIN OPENJSON(@seqs) WITH (seq bigint '$') s ON o.seq = s.seq;
            SELECT @ok;
            """);
        RetryOutbox = R($$"""
            DECLARE @ok bit = CASE WHEN {{LeaseFence}} THEN 1 ELSE 0 END;
            IF @ok = 1 UPDATE {s}.outbox SET attempts = attempts + 1, last_error = @error, deliver_at = @at WHERE seq = @seq;
            SELECT @ok;
            """);
        CountOutbox = R("SELECT COUNT_BIG(*) FROM {s}.outbox");
        CountDueOutbox = R("SELECT COUNT_BIG(*) FROM {s}.outbox WHERE deliver_at <= @now");
        OutboxBacklog = R("""
            SELECT partition, COUNT_BIG(*),
                   MIN(CASE WHEN deliver_at <= SYSDATETIMEOFFSET() THEN CASE WHEN deliver_at > created_at THEN deliver_at ELSE created_at END END)
              FROM {s}.outbox GROUP BY partition ORDER BY partition
            """);
        ParkedDeadLettersByConsumer = R("SELECT consumer, COUNT_BIG(*) FROM {s}.dead_letters WHERE status = @status GROUP BY consumer");
        ActiveProcessesByType = R("SELECT process_type, COUNT_BIG(*) FROM {s}.process_instances WHERE completed = 0 GROUP BY process_type");
        CountEventsAfter = R("""
            SELECT COUNT_BIG(*) FROM (
                SELECT TOP (@cap) 1 AS one FROM {s}.events e
                 WHERE e.rv > CONVERT(binary(8), @rv) AND e.bucket BETWEEN @lo AND @hi) x
            """);

        ParkDeadLetter = R($$"""
            MERGE {s}.dead_letters WITH (HOLDLOCK) AS t
            USING (SELECT @id AS id) AS i ON t.id = i.id
             WHEN MATCHED THEN UPDATE SET
                consumer = @consumer, source = @source, destination = @destination, destination_is_topic = @is_topic, reason = @reason,
                attributes = @attributes, body = @body, error_type = @error_type, error_message = @error_message, error_detail = @error_detail,
                attempts = @attempts, first_failed_at = @first_failed_at, parked_at = @parked_at, status = @status, status_note = @status_note,
                message_id = @message_id, message_type = @message_type
             WHEN NOT MATCHED THEN INSERT ({{DeadLetterColumns}}, message_id, message_type)
                VALUES (@id, @consumer, @source, @destination, @is_topic, @reason, @attributes, @body, @error_type, @error_message, @error_detail,
                        @attempts, @first_failed_at, @parked_at, @status, NULL, @status_note, @message_id, @message_type);
            """);
        QueryDeadLetters = R($$"""
            SELECT TOP (@max) {{DeadLetterColumns}} FROM {s}.dead_letters
             WHERE (@consumer IS NULL OR consumer = @consumer)
               AND (@reason IS NULL OR reason = @reason)
               AND (@status IS NULL OR status = @status)
               AND (@type IS NULL OR message_type = @type)
               AND (@after IS NULL OR parked_at >= @after)
               AND (@before IS NULL OR parked_at < @before)
             ORDER BY parked_at DESC, id
            """);
        GetDeadLetter = R($"SELECT {DeadLetterColumns} FROM {{s}}.dead_letters WHERE id = @id");
        SetDeadLetterStatus = R("UPDATE {s}.dead_letters SET status = @status, status_note = @note, status_changed_at = @now WHERE id = @id");

        GetSnapshot = R("SELECT version, state_type, schema_hash, data FROM {s}.snapshots WHERE tenant_id = @tenant AND stream_id = @stream");
        SaveSnapshot = R("""
            MERGE {s}.snapshots WITH (HOLDLOCK) AS t
            USING (SELECT @tenant AS tenant_id, @stream AS stream_id) AS i ON t.tenant_id = i.tenant_id AND t.stream_id = i.stream_id
             WHEN MATCHED AND t.version <= @version THEN
                  UPDATE SET version = @version, state_type = @state_type, schema_hash = @schema_hash, data = @data, created_at = SYSDATETIMEOFFSET()
             WHEN NOT MATCHED THEN INSERT (tenant_id, stream_id, version, state_type, schema_hash, data) VALUES (@tenant, @stream, @version, @state_type, @schema_hash, @data);
            """);

        // Bounded batches (doc 05 §9): each statement deletes at most @batch rows and holds its locks briefly.
        PurgeInbox = R("DELETE TOP (@batch) FROM {s}.inbox WHERE processed_at < @before");
        PurgeDeadLetters = R("DELETE TOP (@batch) FROM {s}.dead_letters WHERE status <> 0 AND status_changed_at < @before");

        // The oldest transaction that wrote to this database (holds MIN_ACTIVE_ROWVERSION back); needs VIEW SERVER STATE
        // (or VIEW DATABASE PERFORMANCE STATE on Azure SQL Database).
        HorizonAge = """
            SELECT ISNULL(MAX(DATEDIFF_BIG(millisecond, d.database_transaction_begin_time, SYSDATETIME())), 0)
              FROM sys.dm_tran_database_transactions d
              JOIN sys.dm_tran_session_transactions st ON st.transaction_id = d.transaction_id
             WHERE d.database_id = DB_ID() AND d.database_transaction_begin_time IS NOT NULL AND st.session_id <> @@SPID
            """;
    }

    public string Schema { get; }

    public string ReadStream { get; }

    public string StreamInfo { get; }

    public string ArchiveStream { get; }

    public string DeleteStream { get; }

    public string AppendExact { get; }

    public string AppendNoStream { get; }

    public string AppendStreamExists { get; }

    public string AppendAny { get; }

    public string AppendTail { get; }

    public string InsertOutbox { get; }

    public string InsertInbox { get; }

    public string InboxSeen { get; }

    public string SaveCheckpoint { get; }

    public string GetCheckpoint { get; }

    public string LoadProcess { get; }

    public string InsertProcess { get; }

    public string UpdateProcess { get; }

    public string GetDocument { get; }

    public string GetDocuments { get; }

    public string ListDocuments { get; }

    public string UpsertDocument { get; }

    public string UpsertDocuments { get; }

    public string DeleteDocuments { get; }

    public string DeleteDocument { get; }

    public string ReadLog { get; }

    public string SafeHorizon { get; }

    public string AcquireLease { get; }

    public string ReleaseLease { get; }

    public string NodeHeartbeat { get; }

    public string NodeLeave { get; }

    public string RegisterSchemas { get; }

    public string ListSchemas { get; }

    public string DropCollection { get; }

    public string ListAliases { get; }

    public string ClaimAlias { get; }

    public string SwitchAlias { get; }

    public string RequestAlias { get; }

    public string ClearPreviousAlias { get; }

    public string ClaimOutbox { get; }

    public string CompleteOutbox { get; }

    public string RetryOutbox { get; }

    public string CountOutbox { get; }

    public string CountDueOutbox { get; }

    public string OutboxBacklog { get; }

    public string ParkedDeadLettersByConsumer { get; }

    public string ActiveProcessesByType { get; }

    public string CountEventsAfter { get; }

    public string ParkDeadLetter { get; }

    public string QueryDeadLetters { get; }

    public string GetDeadLetter { get; }

    public string SetDeadLetterStatus { get; }

    public string GetSnapshot { get; }

    public string SaveSnapshot { get; }

    public string PurgeInbox { get; }

    public string PurgeDeadLetters { get; }

    public string HorizonAge { get; }
}
