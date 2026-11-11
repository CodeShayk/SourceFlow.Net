using Npgsql;
using NpgsqlTypes;

namespace SourceFlow.Storage.PostgreSQL;

/// <summary>
/// The PostgreSQL storage adapter (doc 05; ADR-009, ADR-010): every storage port over one <see cref="NpgsqlDataSource"/>.
/// Sessions are one Read Committed transaction each, with write-behind batching; the global log is ordered by
/// <c>(tx_id, seq)</c> and read below <c>pg_snapshot_xmin</c>, so it is gap-free. Leases use the database clock; outbox
/// due times use the configured <see cref="TimeProvider"/> (business time, like timers).
/// </summary>
public sealed partial class PostgreSqlStore :
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
    IAsyncDisposable
{
    private readonly bool _ownsDataSource;
    private readonly bool _ownsSystemDataSource;

    /// <summary>Creates the store over a data source. The schema must already be migrated (<see cref="PostgreSqlSchemaMigrator"/>).</summary>
    public PostgreSqlStore(PostgreSqlStorageOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        Time = time ?? TimeProvider.System;
        _ownsDataSource = options.DataSource is null;
        DataSource = options.DataSource ?? NpgsqlDataSource.Create(options.ConnectionString!);
        _ownsSystemDataSource = options.SystemDataSource is null && options.SystemConnectionString is not null;
        SystemDataSource = options.SystemDataSource
            ?? (options.SystemConnectionString is { } system ? NpgsqlDataSource.Create(system) : DataSource);
        Sql = new SqlText(options.Schema);
        _notifications = new Notifier(this);
    }

    /// <summary>The data source.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>The options.</summary>
    public PostgreSqlStorageOptions Options { get; }

    internal TimeProvider Time { get; }

    internal SqlText Sql { get; }

    /// <summary>A migrator for this store's schema.</summary>
    public PostgreSqlSchemaMigrator CreateMigrator()
    {
        return new PostgreSqlSchemaMigrator(DataSource, Options.Schema, Options.TenantIsolation);
    }

    /// <summary>Every port of this store.</summary>
    public Runtime.StoragePorts ToPorts()
    {
        return new Runtime.StoragePorts(this, this, this, this, this, this, this, this, this, this);
    }

    // ------------------------------------------------------------------ IStorageSessionFactory

    /// <inheritdoc />
    public async ValueTask<IStorageSession> BeginAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // BEGIN is sent lazily with the first command, so starting a session costs no round trip.
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
            if (RowLevelSecurity)
            {
                // Set the tenant up front, so custom SQL on session.Connection (EF Core, custom projections) is scoped
                // too. set_config writes nothing, so it assigns no transaction id and ES-R1 still holds.
                await using var batch = new NpgsqlBatch(connection, transaction) { BatchCommands = { SetTenantCommand(tenantId) } };
                await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return new PostgreSqlStorageSession(this, tenantId, connection, transaction, tenantSet: RowLevelSecurity);
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
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadStreamAsync(connection, null, setTenant: true, tenantId, stream, fromInclusive, maxCount, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<StreamSlice> ReadStreamAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, bool setTenant, string tenantId, StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        await using TenantReader scope = await ReadTenantAsync(connection, transaction, tenantId, setTenant, Sql.ReadStream,
        [
            new NpgsqlParameter<string>("tenant", tenantId),
            new NpgsqlParameter<string>("stream", stream.Value),
            new NpgsqlParameter<long>("from", Math.Max(1, fromInclusive.Value)),
            new NpgsqlParameter<int>("max", maxCount),
        ], cancellationToken).ConfigureAwait(false);
        NpgsqlDataReader reader = scope.Reader;

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
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetStreamInfoAsync(connection, tenantId, stream, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<StreamInfo?> GetStreamInfoAsync(NpgsqlConnection connection, string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        await using TenantReader scope = await ReadTenantAsync(connection, null, tenantId, setTenant: true, Sql.StreamInfo,
            [new NpgsqlParameter<string>("tenant", tenantId), new NpgsqlParameter<string>("stream", stream.Value)], cancellationToken).ConfigureAwait(false);
        NpgsqlDataReader reader = scope.Reader;
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StreamInfo(stream, reader.GetString(0), new StreamVersion(reader.GetInt64(1)), reader.GetBoolean(2), reader.GetBoolean(3))
            : null;
    }

    /// <inheritdoc />
    public ValueTask ArchiveStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        return IgnoreCount(ExecuteTenantAsync(tenantId, Sql.ArchiveStream, cancellationToken,
            new NpgsqlParameter<string>("tenant", tenantId), new NpgsqlParameter<string>("stream", stream.Value)));
    }

    /// <inheritdoc />
    public ValueTask DeleteStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        return IgnoreCount(ExecuteTenantAsync(tenantId, Sql.DeleteStream, cancellationToken,
            new NpgsqlParameter<string>("tenant", tenantId), new NpgsqlParameter<string>("stream", stream.Value)));
    }

    // ------------------------------------------------------------------ IEventLogReader

    /// <inheritdoc />
    public async ValueTask<LogBatch> ReadAsync(GlobalPosition after, BucketRange buckets, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        // The global log spans every tenant: the system role (BYPASSRLS under row-level security).
        await using NpgsqlConnection connection = await SystemDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.ReadLog, connection);
        command.Parameters.Add(Xid("tx", after.Commit));
        command.Parameters.Add(new NpgsqlParameter<long>("seq", checked((long)after.Sequence)));
        command.Parameters.Add(new NpgsqlParameter<short>("lo", checked((short)buckets.Low)));
        command.Parameters.Add(new NpgsqlParameter<short>("hi", checked((short)buckets.High)));
        command.Parameters.Add(new NpgsqlParameter<int>("max", maxCount));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        GlobalPosition horizon = GlobalPosition.Start;
        var events = new List<StoredEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            horizon = HorizonOf(reader.GetFieldValue<ulong>(0));
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
        await using NpgsqlConnection connection = await SystemDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT pg_snapshot_xmin(pg_current_snapshot())", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return HorizonOf(reader.GetFieldValue<ulong>(0));
    }

    /// <summary>
    /// The age of the oldest in-progress writing transaction, which holds the horizon back (doc 05 §5.4,
    /// <c>sourceflow.subscription.horizon_age</c>); zero when none is running.
    /// </summary>
    public async ValueTask<TimeSpan> GetHorizonAgeAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT coalesce(max(extract(epoch FROM now() - xact_start)), 0)::float8 FROM pg_stat_activity WHERE backend_xid IS NOT NULL AND pid <> pg_backend_pid()",
            connection);
        object? seconds = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return TimeSpan.FromSeconds(seconds is double s ? Math.Max(0, s) : 0);
    }

    // Every transaction with tx_id < xmin has finished, so every position up to (xmin - 1, max) is final.
    private static GlobalPosition HorizonOf(ulong xmin)
    {
        return xmin == 0 ? GlobalPosition.Start : new GlobalPosition(xmin - 1, long.MaxValue);
    }

    // ------------------------------------------------------------------ ICheckpointStore

    /// <inheritdoc />
    public async ValueTask<GlobalPosition> GetAsync(string subscription, int partition, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.GetCheckpoint, connection);
        command.Parameters.Add(new NpgsqlParameter<string>("sub", subscription));
        command.Parameters.Add(new NpgsqlParameter<int>("p", partition));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new GlobalPosition(reader.GetFieldValue<ulong>(0), (ulong)reader.GetInt64(1))
            : GlobalPosition.Start;
    }

    // ------------------------------------------------------------------ ILeaseManager

    /// <inheritdoc />
    public async ValueTask<LeaseToken?> TryAcquireAsync(string name, string owner, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.AcquireLease, connection);
        command.Parameters.Add(new NpgsqlParameter<string>("name", name));
        command.Parameters.Add(new NpgsqlParameter<string>("owner", owner));
        command.Parameters.Add(new NpgsqlParameter<TimeSpan>("ttl", ttl));
        object? token = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return token is long value ? new LeaseToken(name, owner, value) : null;
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(LeaseToken lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ExecuteAsync(Sql.ReleaseLease, cancellationToken, LeaseParameters(lease));
    }

    internal static NpgsqlParameter[] LeaseParameters(LeaseToken lease)
    {
        return
        [
            new NpgsqlParameter<string>("lease", lease.Name),
            new NpgsqlParameter<string>("lease_owner", lease.Owner),
            new NpgsqlParameter<long>("lease_token", lease.Token),
        ];
    }

    // ------------------------------------------------------------------ IOutboxStore

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(int partition, LeaseToken lease, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.ClaimOutbox, connection);
        command.Parameters.AddRange(LeaseParameters(lease));
        command.Parameters.Add(new NpgsqlParameter<short>("p", checked((short)partition)));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", Time.GetUtcNow().ToUniversalTime()));
        command.Parameters.Add(new NpgsqlParameter<int>("max", maxCount));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var records = new List<OutboxRecord>();
        bool fenced = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fenced = reader.GetBoolean(0);
            if (!reader.IsDBNull(1))
            {
                MessageEnvelopeAndBody(reader, 2, 3, out Messaging.MessageEnvelope envelope);
                var message = new OutboxMessage(envelope, reader.GetString(4), (OutboxKind)reader.GetInt16(5),
                    reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt16(7));
                records.Add(new OutboxRecord(reader.GetInt64(1), message, reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetFieldValue<DateTimeOffset>(10)));
            }
        }

        return fenced ? records : throw new LeaseLostException(lease.Name);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(LeaseToken lease, IReadOnlyList<long> sequences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(sequences);
        bool fenced = await FencedAsync(Sql.CompleteOutbox, cancellationToken, [.. LeaseParameters(lease), new NpgsqlParameter<long[]>("seqs", [.. sequences])]).ConfigureAwait(false);
        if (!fenced)
        {
            throw new LeaseLostException(lease.Name);
        }
    }

    /// <inheritdoc />
    public async ValueTask RetryAsync(LeaseToken lease, long sequence, DateTimeOffset deliverAt, string error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        bool fenced = await FencedAsync(Sql.RetryOutbox, cancellationToken,
        [
            .. LeaseParameters(lease),
            new NpgsqlParameter<long>("seq", sequence),
            new NpgsqlParameter<DateTimeOffset>("at", deliverAt.ToUniversalTime()),
            new NpgsqlParameter<string>("error", error ?? string.Empty),
        ]).ConfigureAwait(false);
        if (!fenced)
        {
            throw new LeaseLostException(lease.Name);
        }
    }

    /// <inheritdoc />
    public async ValueTask<int> CountAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.CountOutbox, connection);
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    /// <summary>Outbox rows due now by the store's clock (excludes future timers), for idle detection in tests.</summary>
    public async ValueTask<int> DueOutboxCountAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.CountDueOutbox, connection);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", Time.GetUtcNow().ToUniversalTime()));
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
    }

    // ------------------------------------------------------------------ IDeadLetterStore

    /// <inheritdoc />
    public ValueTask ParkAsync(DeadLetter deadLetter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deadLetter);
        return ExecuteAsync(Sql.ParkDeadLetter, cancellationToken,
            new NpgsqlParameter<Guid>("id", deadLetter.Id),
            new NpgsqlParameter<string>("consumer", deadLetter.Consumer),
            new NpgsqlParameter<string>("source", deadLetter.Source),
            new NpgsqlParameter<Guid>("message_id", deadLetter.Envelope.MessageId.Value),
            new NpgsqlParameter<string>("message_type", deadLetter.Envelope.Type),
            Text("destination", deadLetter.Destination),
            new NpgsqlParameter<bool>("is_topic", deadLetter.DestinationIsTopic),
            new NpgsqlParameter<string>("attributes", NpgsqlDbType.Jsonb) { TypedValue = JsonCodecs.WriteAttributes(deadLetter.Envelope) },
            new NpgsqlParameter<byte[]>("body", deadLetter.Envelope.Body.ToArray()),
            new NpgsqlParameter<string>("reason", deadLetter.Reason),
            Text("error_type", deadLetter.ErrorType),
            Text("error_message", deadLetter.ErrorMessage),
            Text("error_detail", deadLetter.ErrorDetail),
            new NpgsqlParameter<int>("attempts", deadLetter.Attempts),
            new NpgsqlParameter<DateTimeOffset>("first_failed_at", deadLetter.FirstFailedAt.ToUniversalTime()),
            new NpgsqlParameter<DateTimeOffset>("parked_at", deadLetter.ParkedAt.ToUniversalTime()),
            new NpgsqlParameter<short>("status", (short)deadLetter.Status),
            Text("status_note", deadLetter.StatusNote));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DeadLetter>> QueryAsync(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.QueryDeadLetters, connection);
        command.Parameters.Add(Text("consumer", query.Consumer));
        command.Parameters.Add(Text("reason", query.Reason));
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Smallint) { Value = query.Status is { } s ? (short)s : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<int>("max", Math.Max(0, query.Limit)));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
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
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.GetDeadLetter, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDeadLetter(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask SetStatusAsync(Guid id, DeadLetterStatus status, string? note, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(Sql.SetDeadLetterStatus, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        command.Parameters.Add(new NpgsqlParameter<short>("status", (short)status));
        command.Parameters.Add(Text("note", note));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", Time.GetUtcNow().ToUniversalTime()));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException($"Dead letter {id} does not exist.");
        }
    }

    private static DeadLetter ReadDeadLetter(NpgsqlDataReader reader)
    {
        MessageEnvelopeAndBody(reader, 6, 7, out Messaging.MessageEnvelope envelope);
        return new DeadLetter
        {
            Id = reader.GetGuid(0),
            Consumer = reader.GetString(1),
            Source = reader.GetString(2),
            Destination = reader.IsDBNull(3) ? null : reader.GetString(3),
            DestinationIsTopic = reader.GetBoolean(4),
            Reason = reader.GetString(5),
            Envelope = envelope,
            ErrorType = reader.IsDBNull(8) ? null : reader.GetString(8),
            ErrorMessage = reader.IsDBNull(9) ? null : reader.GetString(9),
            ErrorDetail = reader.IsDBNull(10) ? null : reader.GetString(10),
            Attempts = reader.GetInt32(11),
            FirstFailedAt = reader.GetFieldValue<DateTimeOffset>(12),
            ParkedAt = reader.GetFieldValue<DateTimeOffset>(13),
            Status = (DeadLetterStatus)reader.GetInt16(14),
            StatusChangedAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
            StatusNote = reader.IsDBNull(16) ? null : reader.GetString(16),
        };
    }

    // ------------------------------------------------------------------ ISnapshotStore

    /// <inheritdoc />
    async ValueTask<Snapshot?> ISnapshotStore.GetAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using TenantReader scope = await ReadTenantAsync(connection, null, tenantId, setTenant: true, Sql.GetSnapshot,
            [new NpgsqlParameter<string>("tenant", tenantId), new NpgsqlParameter<string>("stream", stream.Value)], cancellationToken).ConfigureAwait(false);
        NpgsqlDataReader reader = scope.Reader;
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new Snapshot(stream, new StreamVersion(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<byte[]>(3))
            : null;
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(string tenantId, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return IgnoreCount(ExecuteTenantAsync(tenantId, Sql.SaveSnapshot, cancellationToken,
            new NpgsqlParameter<string>("tenant", tenantId),
            new NpgsqlParameter<string>("stream", snapshot.StreamId.Value),
            new NpgsqlParameter<long>("version", snapshot.Version.Value),
            new NpgsqlParameter<string>("state_type", snapshot.StateType),
            new NpgsqlParameter<string>("schema_hash", snapshot.SchemaHash),
            Json("data", snapshot.Data)));
    }

    // ------------------------------------------------------------------ IDocumentReader

    /// <inheritdoc />
    async ValueTask<StoredDocument?> IDocumentReader.GetAsync(string tenantId, string collection, string id, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetDocumentAsync(connection, null, setTenant: true, tenantId, collection, id, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<StoredDocument?> GetDocumentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, bool setTenant, string tenantId, string collection, string id, CancellationToken cancellationToken)
    {
        await using TenantReader scope = await ReadTenantAsync(connection, transaction, tenantId, setTenant, Sql.GetDocument,
        [
            new NpgsqlParameter<string>("tenant", tenantId),
            new NpgsqlParameter<string>("collection", collection),
            new NpgsqlParameter<string>("id", id),
        ], cancellationToken).ConfigureAwait(false);
        NpgsqlDataReader reader = scope.Reader;
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredDocument(collection, id, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1))
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<StoredDocument>> ListAsync(string tenantId, string collection, int maxCount, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using TenantReader scope = await ReadTenantAsync(connection, null, tenantId, setTenant: true, Sql.ListDocuments,
        [
            new NpgsqlParameter<string>("tenant", tenantId),
            new NpgsqlParameter<string>("collection", collection),
            new NpgsqlParameter<int>("max", Math.Max(0, maxCount)),
        ], cancellationToken).ConfigureAwait(false);
        NpgsqlDataReader reader = scope.Reader;
        var documents = new List<StoredDocument>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(new StoredDocument(collection, reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<byte[]>(2)));
        }

        return documents;
    }

    // ------------------------------------------------------------------ lifetime

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _notifications.DisposeAsync().ConfigureAwait(false);
        if (_ownsSystemDataSource)
        {
            await SystemDataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (_ownsDataSource)
        {
            await DataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ helpers

    internal static StoredEvent ReadEvent(NpgsqlDataReader reader, int o)
    {
        // seq, tx_id, tenant_id, bucket, stream_id, stream_version, event_id, event_type, type_version, tags, data, metadata, created_at
        return new StoredEvent(
            new GlobalPosition(reader.GetFieldValue<ulong>(o + 1), (ulong)reader.GetInt64(o)),
            reader.GetString(o + 2),
            new StreamId(reader.GetString(o + 4)),
            new StreamVersion(reader.GetInt64(o + 5)),
            reader.GetInt16(o + 3),
            new MessageId(reader.GetGuid(o + 6)),
            reader.GetString(o + 7),
            reader.GetInt32(o + 8),
            reader.GetFieldValue<byte[]>(o + 10),
            JsonCodecs.ReadMetadata(reader.GetFieldValue<byte[]>(o + 11)),
            reader.GetFieldValue<DateTimeOffset>(o + 12),
            reader.GetFieldValue<string[]>(o + 9));
    }

    private static void MessageEnvelopeAndBody(NpgsqlDataReader reader, int attributes, int body, out Messaging.MessageEnvelope envelope)
    {
        envelope = JsonCodecs.ReadEnvelope(reader.GetFieldValue<byte[]>(attributes), reader.GetFieldValue<byte[]>(body));
    }

    internal static NpgsqlParameter Xid(string name, ulong value)
    {
        return new NpgsqlParameter<ulong>(name, value) { DataTypeName = "xid8" };
    }

    internal static NpgsqlParameter Text(string name, string? value)
    {
        return new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
    }

    internal static NpgsqlParameter Json(string name, ReadOnlyMemory<byte> value)
    {
        return new NpgsqlParameter<ReadOnlyMemory<byte>>(name, NpgsqlDbType.Json) { TypedValue = value };
    }

    private static async ValueTask IgnoreCount(ValueTask<int> operation)
    {
        await operation.ConfigureAwait(false);
    }

    private async ValueTask ExecuteAsync(string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Runs a statement whose first result column says whether the lease fence held.
    private async ValueTask<bool> FencedAsync(string sql, CancellationToken cancellationToken, NpgsqlParameter[] parameters)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
