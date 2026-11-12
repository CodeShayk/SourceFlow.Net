using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SourceFlow.Storage.SqlServer;

/// <summary>
/// The SQL Server storage adapter (doc 05 §5.5; ADR-009, ADR-010): every storage port over pooled SqlClient connections.
/// Sessions are one Read Committed transaction each, with write-behind batching; the global log is ordered by the event
/// rowversion and read below <c>MIN_ACTIVE_ROWVERSION()</c>, so it is gap-free. Leases use the database clock; outbox due
/// times use the configured <see cref="TimeProvider"/> (business time, like timers).
/// </summary>
/// <remarks>
/// Commit notifications are in-process (doc 06 §2): subscribers in other processes find new events on their polling
/// interval (<c>SourceFlowOptions.PollInterval</c>). Tenants are isolated by the queries (<c>TenantIsolation.Runtime</c>).
/// </remarks>
public sealed partial class SqlServerStore :
    IEventStore,
    IEventLogReader,
    IStorageSessionFactory,
    ICheckpointStore,
    ILeaseManager,
    IOutboxStore,
    IDeadLetterStore,
    ISnapshotStore,
    IDocumentReader,
    IStoreNotifications,
    IStoreRetention,
    INodeRegistry,
    ISchemaStore,
    IDocumentAdmin,
    ICollectionAliases,
    IAsyncDisposable
{
    /// <summary>Creates the store. The schema must already be migrated (<see cref="SqlServerSchemaMigrator"/>).</summary>
    public SqlServerStore(SqlServerStorageOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        Time = time ?? TimeProvider.System;
        Sql = new SqlText(options.Schema);
    }

    /// <summary>The options.</summary>
    public SqlServerStorageOptions Options { get; }

    internal TimeProvider Time { get; }

    internal SqlText Sql { get; }

    /// <summary>A migrator for this store's schema.</summary>
    public SqlServerSchemaMigrator CreateMigrator()
    {
        return new SqlServerSchemaMigrator(Options.ConnectionString!, Options.Schema);
    }

    /// <summary>Every port of this store.</summary>
    public Runtime.StoragePorts ToPorts()
    {
        return new Runtime.StoragePorts(this, this, this, this, this, this, this, this, this, this) { Retention = this, Nodes = this, Schemas = this, DocumentAdmin = this, Aliases = this, Monitor = this, PersonalData = this };
    }

    internal async ValueTask<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(Options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------ IStorageSessionFactory

    /// <inheritdoc />
    public async ValueTask<IStorageSession> BeginAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            return new SqlServerStorageSession(this, tenantId, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------ IEventStore

    /// <inheritdoc />
    public async ValueTask<StreamSlice> ReadStreamAsync(string tenantId, StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStreamAsync(connection, null, tenantId, stream, fromInclusive, maxCount, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<StreamSlice> ReadStreamAsync(SqlConnection connection, SqlTransaction? transaction, string tenantId, StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        await using SqlCommand command = Command(Sql.ReadStream, connection, transaction,
            Text("@tenant", tenantId), Text("@stream", stream.Value), Int64("@from", Math.Max(1, fromInclusive.Value)), Int32("@max", maxCount));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        StreamVersion current = StreamVersion.None;
        var events = new List<StoredEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            current = new StreamVersion(reader.GetInt64(0));
            if (!reader.IsDBNull(1))
            {
                events.Add(ReadEvent(reader, 1));
            }
        }

        bool end = events.Count == 0 || events[^1].StreamVersion == current;
        return new StreamSlice(stream, current, events, end);
    }

    /// <inheritdoc />
    public async ValueTask<StreamInfo?> GetStreamInfoAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetStreamInfoAsync(connection, null, tenantId, stream, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<StreamInfo?> GetStreamInfoAsync(SqlConnection connection, SqlTransaction? transaction, string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(Sql.StreamInfo, connection, transaction, Text("@tenant", tenantId), Text("@stream", stream.Value));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StreamInfo(stream, reader.GetString(0), new StreamVersion(reader.GetInt64(1)), reader.GetBoolean(2), reader.GetBoolean(3))
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlySet<StreamId>> GetArchivedStreamsAsync(string tenantId, IReadOnlyCollection<StreamId> streams, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var archived = new HashSet<StreamId>();
        if (streams.Count == 0)
        {
            return archived;
        }

        string json = JsonSerializer.Serialize([.. streams.Select(s => s.Value).Distinct(StringComparer.Ordinal)], SqlServerJsonContext.Default.StringArray);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ArchivedStreams, connection, null, Text("@tenant", tenantId), MaxText("@streams", json));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            archived.Add(new StreamId(reader.GetString(0)));
        }

        return archived;
    }

    /// <inheritdoc />
    public ValueTask ArchiveStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        return ExecuteAsync(Sql.ArchiveStream, cancellationToken, Text("@tenant", tenantId), Text("@stream", stream.Value));
    }

    /// <inheritdoc />
    public ValueTask DeleteStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        return ExecuteAsync(Sql.DeleteStream, cancellationToken, Text("@tenant", tenantId), Text("@stream", stream.Value));
    }

    // ------------------------------------------------------------------ IEventLogReader

    /// <inheritdoc />
    public async ValueTask<LogBatch> ReadAsync(GlobalPosition after, BucketRange buckets, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ReadLog, connection, null,
            Int64("@rv", checked((long)after.Commit)), Int16("@lo", buckets.Low), Int16("@hi", buckets.High), Int32("@max", maxCount));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        GlobalPosition horizon = GlobalPosition.Start;
        var events = new List<StoredEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            horizon = HorizonOf(reader.GetInt64(0));
            if (!reader.IsDBNull(1))
            {
                events.Add(ReadEvent(reader, 1));
            }
        }

        return new LogBatch(events, horizon);
    }

    /// <inheritdoc />
    public async ValueTask<GlobalPosition> GetSafeHorizonAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.SafeHorizon, connection, null);
        return HorizonOf((long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    // Every row with rv < MIN_ACTIVE_ROWVERSION() is committed, so every position up to (h - 1, max) is final.
    private static GlobalPosition HorizonOf(long minActive)
    {
        return minActive <= 1 ? GlobalPosition.Start : new GlobalPosition((ulong)(minActive - 1), long.MaxValue);
    }

    // ------------------------------------------------------------------ ICheckpointStore

    /// <inheritdoc />
    public async ValueTask<GlobalPosition> GetAsync(string subscription, int partition, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.GetCheckpoint, connection, null, Text("@sub", subscription), Int32("@p", partition));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new GlobalPosition((ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1))
            : GlobalPosition.Start;
    }

    // ------------------------------------------------------------------ ILeaseManager, INodeRegistry

    /// <inheritdoc />
    public async ValueTask<LeaseToken?> TryAcquireAsync(string name, string owner, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.AcquireLease, connection, null, Text("@name", name), Text("@owner", owner), Int32("@ttl", Milliseconds(ttl)));
        object? token = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return token is long value ? new LeaseToken(name, owner, value) : null;
    }

    /// <inheritdoc />
    public async ValueTask<int> HeartbeatAsync(string nodeId, int roles, TimeSpan liveWindow, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.NodeHeartbeat, connection, null, Text("@node", nodeId), Int32("@roles", roles), Int32("@window", Milliseconds(liveWindow)));
        object? live = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return live is int count ? count : 1;
    }

    /// <inheritdoc />
    public ValueTask LeaveAsync(string nodeId, CancellationToken cancellationToken)
    {
        return ExecuteAsync(Sql.NodeLeave, cancellationToken, Text("@node", nodeId));
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(LeaseToken lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ExecuteAsync(Sql.ReleaseLease, cancellationToken, LeaseParameters(lease));
    }

    internal static SqlParameter[] LeaseParameters(LeaseToken lease)
    {
        return [Text("@lease", lease.Name), Text("@lease_owner", lease.Owner), Int64("@lease_token", lease.Token)];
    }

    // ------------------------------------------------------------------ ISchemaStore, IDocumentAdmin, ICollectionAliases

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Catalog.SchemaConflict>> RegisterAsync(string service, IReadOnlyList<Catalog.SchemaEntry> schemas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        if (schemas.Count == 0)
        {
            return [];
        }

        string rows = Rows.Write(schemas, static (w, s) =>
        {
            w.WriteString("t", s.Type);
            w.WriteNumber("v", s.Version);
            w.WriteString("k", Catalog.SchemaEntry.KindName(s.Kind));
            w.WriteString("j", s.Canonical);
            w.WriteString("f", s.Fingerprint);
        });
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.RegisterSchemas, connection, null, MaxText("@rows", rows), Text("@service", service));
        var conflicts = new List<Catalog.SchemaConflict>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            conflicts.Add(new Catalog.SchemaConflict(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        }

        return conflicts;
    }

    /// <inheritdoc />
    async ValueTask<IReadOnlyList<Catalog.SchemaEntry>> ISchemaStore.ListAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ListSchemas, connection, null);
        var entries = new List<Catalog.SchemaEntry>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var schema = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(3))!;
            entries.Add(new Catalog.SchemaEntry(reader.GetString(0), reader.GetInt32(1), Catalog.SchemaEntry.ParseKind(reader.GetString(2)), schema));
        }

        return entries;
    }

    /// <inheritdoc />
    public async ValueTask<int> DropCollectionAsync(string collection, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.DropCollection, connection, null, Text("@collection", collection));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CollectionAlias>> ListAliasesAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ListAliases, connection, null);
        var aliases = new List<CollectionAlias>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            aliases.Add(new CollectionAlias(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetDateTimeOffset(4)));
        }

        return aliases;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SwitchAsync(string alias, string? expectedTarget, string target, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(alias);
        ArgumentException.ThrowIfNullOrEmpty(target);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = expectedTarget is null
            ? Command(Sql.ClaimAlias, connection, null, Text("@alias", alias), Text("@target", target))
            : Command(Sql.SwitchAlias, connection, null, Text("@alias", alias), Text("@target", target), Text("@expected", expectedTarget));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public ValueTask RequestAsync(string alias, string currentTarget, string building, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(alias);
        ArgumentException.ThrowIfNullOrEmpty(currentTarget);
        ArgumentException.ThrowIfNullOrEmpty(building);
        return ExecuteAsync(Sql.RequestAlias, cancellationToken, Text("@alias", alias), Text("@target", currentTarget), Text("@building", building));
    }

    /// <inheritdoc />
    public ValueTask ClearPreviousAsync(string alias, string previous, CancellationToken cancellationToken)
    {
        return ExecuteAsync(Sql.ClearPreviousAlias, cancellationToken, Text("@alias", alias), Text("@previous", previous));
    }

    // ------------------------------------------------------------------ IOutboxStore

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(int partition, LeaseToken lease, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ClaimOutbox, connection, null,
            [.. LeaseParameters(lease), Int16("@p", partition), At("@now", Time.GetUtcNow()), Int32("@max", maxCount)]);
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<OutboxRecord>();
        bool fenced = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fenced = reader.GetBoolean(0);
            if (!reader.IsDBNull(1))
            {
                Messaging.MessageEnvelope envelope = JsonCodecs.ReadEnvelope(Encoding.UTF8.GetBytes(reader.GetString(2)), (byte[])reader.GetValue(3));
                var message = new OutboxMessage(envelope, reader.GetString(4), (OutboxKind)reader.GetInt16(5), reader.GetDateTimeOffset(6), reader.GetInt16(7));
                records.Add(new OutboxRecord(reader.GetInt64(1), message, reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetDateTimeOffset(10)));
            }
        }

        return fenced ? records : throw new LeaseLostException(lease.Name);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(LeaseToken lease, IReadOnlyList<long> sequences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sequences);
        string seqs = "[" + string.Join(',', sequences.Select(s => s.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        if (!await FencedAsync(Sql.CompleteOutbox, cancellationToken, [.. LeaseParameters(lease), MaxText("@seqs", seqs)]).ConfigureAwait(false))
        {
            throw new LeaseLostException(lease.Name);
        }
    }

    /// <inheritdoc />
    public async ValueTask RetryAsync(LeaseToken lease, long sequence, DateTimeOffset deliverAt, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!await FencedAsync(Sql.RetryOutbox, cancellationToken, [.. LeaseParameters(lease), Int64("@seq", sequence), At("@at", deliverAt), MaxText("@error", error ?? string.Empty)]).ConfigureAwait(false))
        {
            throw new LeaseLostException(lease.Name);
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> CountAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.CountOutbox, connection, null);
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    /// <summary>Outbox rows due now by the store's clock (excludes future timers), for idle detection in tests.</summary>
    public async ValueTask<int> DueOutboxCountAsync(CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.CountDueOutbox, connection, null, At("@now", Time.GetUtcNow()));
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    // ------------------------------------------------------------------ IDeadLetterStore

    /// <inheritdoc />
    public ValueTask ParkAsync(DeadLetter deadLetter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deadLetter);
        return ExecuteAsync(Sql.ParkDeadLetter, cancellationToken,
            Uuid("@id", deadLetter.Id),
            Text("@consumer", deadLetter.Consumer),
            Text("@source", deadLetter.Source),
            Uuid("@message_id", deadLetter.Envelope.MessageId.Value),
            Text("@message_type", deadLetter.Envelope.Type),
            Text("@destination", deadLetter.Destination),
            Bit("@is_topic", deadLetter.DestinationIsTopic),
            MaxText("@attributes", JsonCodecs.WriteAttributes(deadLetter.Envelope)),
            Binary("@body", deadLetter.Envelope.Body.ToArray()),
            Text("@reason", deadLetter.Reason),
            Text("@error_type", deadLetter.ErrorType),
            MaxText("@error_message", deadLetter.ErrorMessage),
            MaxText("@error_detail", deadLetter.ErrorDetail),
            Int32("@attempts", deadLetter.Attempts),
            At("@first_failed_at", deadLetter.FirstFailedAt),
            At("@parked_at", deadLetter.ParkedAt),
            Int16("@status", (int)deadLetter.Status),
            MaxText("@status_note", deadLetter.StatusNote));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DeadLetter>> QueryAsync(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.QueryDeadLetters, connection, null,
            Text("@consumer", query.Consumer),
            Text("@reason", query.Reason),
            new SqlParameter("@status", SqlDbType.SmallInt) { Value = query.Status is { } s ? (short)s : DBNull.Value },
            Int32("@max", Math.Max(0, query.Limit)),
            Text("@type", query.MessageType),
            new SqlParameter("@after", SqlDbType.DateTimeOffset) { Value = query.ParkedAfter is { } after ? after.ToUniversalTime() : DBNull.Value },
            new SqlParameter("@before", SqlDbType.DateTimeOffset) { Value = query.ParkedBefore is { } before ? before.ToUniversalTime() : DBNull.Value });
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<DeadLetter>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadDeadLetter(reader));
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<DeadLetter?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.GetDeadLetter, connection, null, Uuid("@id", id));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDeadLetter(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask SetStatusAsync(Guid id, DeadLetterStatus status, string? note, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.SetDeadLetterStatus, connection, null, Uuid("@id", id), Int16("@status", (int)status), MaxText("@note", note), At("@now", Time.GetUtcNow()));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException($"Dead letter {id} does not exist.");
        }
    }

    private static DeadLetter ReadDeadLetter(SqlDataReader reader)
    {
        return new DeadLetter
        {
            Id = reader.GetGuid(0),
            Consumer = reader.GetString(1),
            Source = reader.GetString(2),
            Destination = reader.IsDBNull(3) ? null : reader.GetString(3),
            DestinationIsTopic = reader.GetBoolean(4),
            Reason = reader.GetString(5),
            Envelope = JsonCodecs.ReadEnvelope(Encoding.UTF8.GetBytes(reader.GetString(6)), (byte[])reader.GetValue(7)),
            ErrorType = reader.IsDBNull(8) ? null : reader.GetString(8),
            ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
            ErrorDetail = reader.IsDBNull(10) ? null : reader.GetString(10),
            Attempts = reader.GetInt32(11),
            FirstFailedAt = reader.GetDateTimeOffset(12),
            ParkedAt = reader.GetDateTimeOffset(13),
            Status = (DeadLetterStatus)reader.GetInt16(14),
            StatusChangedAt = reader.IsDBNull(15) ? null : reader.GetDateTimeOffset(15),
            StatusNote = reader.IsDBNull(16) ? null : reader.GetString(16),
        };
    }

    // ------------------------------------------------------------------ ISnapshotStore

    /// <inheritdoc />
    async ValueTask<Snapshot?> ISnapshotStore.GetAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.GetSnapshot, connection, null, Text("@tenant", tenantId), Text("@stream", stream.Value));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new Snapshot(stream, new StreamVersion(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), Encoding.UTF8.GetBytes(reader.GetString(3)))
            : null;
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(string tenantId, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ExecuteAsync(Sql.SaveSnapshot, cancellationToken,
            Text("@tenant", tenantId),
            Text("@stream", snapshot.StreamId.Value),
            Int64("@version", snapshot.Version.Value),
            Text("@state_type", snapshot.StateType),
            Text("@schema_hash", snapshot.SchemaHash),
            Json("@data", snapshot.Data));
    }

    // ------------------------------------------------------------------ IDocumentReader

    /// <inheritdoc />
    async ValueTask<StoredDocument?> IDocumentReader.GetAsync(string tenantId, string collection, string id, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetDocumentAsync(connection, null, tenantId, collection, id, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<StoredDocument?> GetDocumentAsync(SqlConnection connection, SqlTransaction? transaction, string tenantId, string collection, string id, CancellationToken cancellationToken)
    {
        await using SqlCommand command = Command(Sql.GetDocument, connection, transaction, Text("@tenant", tenantId), Text("@collection", collection), Text("@id", id));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredDocument(collection, id, reader.GetInt64(0), Encoding.UTF8.GetBytes(reader.GetString(1)))
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<StoredDocument>> ListAsync(string tenantId, string collection, int maxCount, CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(Sql.ListDocuments, connection, null, Text("@tenant", tenantId), Text("@collection", collection), Int32("@max", Math.Max(0, maxCount)));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var documents = new List<StoredDocument>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new StoredDocument(collection, reader.GetString(0), reader.GetInt64(1), Encoding.UTF8.GetBytes(reader.GetString(2))));
        }

        return documents;
    }

    // ------------------------------------------------------------------ IStoreRetention

    /// <inheritdoc />
    public ValueTask<int> PurgeInboxAsync(DateTimeOffset processedBefore, int batchSize, CancellationToken cancellationToken)
    {
        return PurgeAsync(Sql.PurgeInbox, processedBefore, batchSize, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeDeadLettersAsync(DateTimeOffset resolvedBefore, int batchSize, CancellationToken cancellationToken)
    {
        return PurgeAsync(Sql.PurgeDeadLetters, resolvedBefore, batchSize, cancellationToken);
    }

    private async ValueTask<int> PurgeAsync(string sql, DateTimeOffset before, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int total = 0;
        while (true)
        {
            await using SqlCommand command = Command(sql, connection, null, At("@before", before), Int32("@batch", batchSize));
            int deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < batchSize)
            {
                return total;
            }
        }
    }

    // ------------------------------------------------------------------ lifetime

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;   // SqlClient pools connections per connection string
    }

    // ------------------------------------------------------------------ helpers

    internal static StoredEvent ReadEvent(SqlDataReader reader, int o)
    {
        // rv, seq, tenant_id, bucket, stream_id, stream_version, event_id, event_type, type_version, tags, data, metadata, created_at
        return new StoredEvent(
            new GlobalPosition((ulong)reader.GetInt64(o), (ulong)reader.GetInt64(o + 1)),
            reader.GetString(o + 2),
            new StreamId(reader.GetString(o + 4)),
            new StreamVersion(reader.GetInt64(o + 5)),
            reader.GetInt16(o + 3),
            new MessageId(reader.GetGuid(o + 6)),
            reader.GetString(o + 7),
            reader.GetInt32(o + 8),
            Encoding.UTF8.GetBytes(reader.GetString(o + 10)),
            JsonCodecs.ReadMetadata(Encoding.UTF8.GetBytes(reader.GetString(o + 11))),
            reader.GetDateTimeOffset(o + 12),
            JsonCodecs.ReadTags(reader.GetString(o + 9)));
    }

    internal static SqlCommand Command(string sql, SqlConnection connection, SqlTransaction? transaction, params SqlParameter[] parameters)
    {
        var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        return command;
    }

    internal static SqlParameter Text(string name, string? value)
    {
        return new SqlParameter(name, SqlDbType.NVarChar, 4000) { Value = (object?)value ?? DBNull.Value };
    }

    internal static SqlParameter MaxText(string name, string? value)
    {
        return new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = (object?)value ?? DBNull.Value };
    }

    internal static SqlParameter Json(string name, ReadOnlyMemory<byte> value)
    {
        return MaxText(name, Encoding.UTF8.GetString(value.Span));
    }

    internal static SqlParameter Binary(string name, byte[] value)
    {
        return new SqlParameter(name, SqlDbType.VarBinary, -1) { Value = value };
    }

    internal static SqlParameter Int16(string name, int value)
    {
        return new SqlParameter(name, SqlDbType.SmallInt) { Value = checked((short)value) };
    }

    internal static SqlParameter Int32(string name, int value)
    {
        return new SqlParameter(name, SqlDbType.Int) { Value = value };
    }

    internal static SqlParameter Int64(string name, long value)
    {
        return new SqlParameter(name, SqlDbType.BigInt) { Value = value };
    }

    internal static SqlParameter Bit(string name, bool value)
    {
        return new SqlParameter(name, SqlDbType.Bit) { Value = value };
    }

    internal static SqlParameter Uuid(string name, Guid value)
    {
        return new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = value };
    }

    internal static SqlParameter At(string name, DateTimeOffset value)
    {
        return new SqlParameter(name, SqlDbType.DateTimeOffset) { Value = value.ToUniversalTime() };
    }

    private static int Milliseconds(TimeSpan value)
    {
        return (int)Math.Min(int.MaxValue, Math.Ceiling(value.TotalMilliseconds));
    }

    private async ValueTask ExecuteAsync(string sql, CancellationToken cancellationToken, params SqlParameter[] parameters)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(sql, connection, null, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Runs a statement whose last result is whether the lease fence held.
    private async ValueTask<bool> FencedAsync(string sql, CancellationToken cancellationToken, SqlParameter[] parameters)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlCommand command = Command(sql, connection, null, parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
