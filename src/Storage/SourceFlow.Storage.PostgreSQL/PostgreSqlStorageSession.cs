using System.Data.Common;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace SourceFlow.Storage.PostgreSQL;

/// <summary>
/// One storage session: one Read Committed transaction on one pooled connection (doc 05 §1, §6). Reads go to the server
/// immediately; writes are queued and sent as one <see cref="NpgsqlBatch"/> by <see cref="CommitAsync"/>, with the stream
/// append first (ES-R1, doc 05 §5.3). Conditional statements (append, process state, fenced checkpoints) are checked in
/// batch order; the first failure rolls the whole transaction back.
/// </summary>
internal sealed class PostgreSqlStorageSession : IStorageSession, IDocumentSession
{
    private readonly PostgreSqlStore _store;
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly List<OutboxMessage> _outbox = [];
    private readonly List<(string Consumer, MessageId Id, InboxResult Result)> _inbox = [];
    private readonly List<(string Subscription, int Partition, GlobalPosition Position, LeaseToken Lease)> _checkpoints = [];
    private readonly List<ProcessWrite> _processes = [];
    private readonly List<DocumentWrite> _documents = [];
    private AppendWrite? _append;
    private bool _wroteOther;
    private bool _completed;
    private bool _transactionEnded;
    private bool _tenantSet;
    private bool _appendSent;
    private bool _notify;
    private CommitResult _appendResult;

    public PostgreSqlStorageSession(PostgreSqlStore store, string tenantId, NpgsqlConnection connection, NpgsqlTransaction transaction, bool tenantSet)
    {
        _tenantSet = tenantSet;
        _store = store;
        TenantId = tenantId;
        _connection = connection;
        _transaction = transaction;
    }

    public string TenantId { get; }

    public IDocumentSession Documents => this;

    public DbConnection? Connection => _connection;

    public DbTransaction? Transaction => _transaction;

    // ------------------------------------------------------------------ reads

    public ValueTask<StreamSlice> ReadStreamAsync(StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        return _store.ReadStreamAsync(_connection, _transaction, TakeTenant(), TenantId, stream, fromInclusive, maxCount, cancellationToken);
    }

    public async ValueTask<InboxEntry?> InboxSeenAsync(string consumer, MessageId messageId, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        await using var command = new NpgsqlCommand(_store.Sql.InboxSeen, _connection, _transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("consumer", consumer));
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", messageId.Value));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var result = new InboxResult(
            (CommandStatus)reader.GetInt16(1),
            new StreamVersion(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            new GlobalPosition(reader.GetFieldValue<ulong>(4), (ulong)reader.GetInt64(5)));
        return new InboxEntry(consumer, messageId, reader.GetFieldValue<DateTimeOffset>(0), result);
    }

    public async ValueTask<ProcessState?> LoadProcessStateAsync(string processType, string processId, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        await using PostgreSqlStore.TenantReader scope = await _store.ReadTenantAsync(_connection, _transaction, TenantId, TakeTenant(), _store.Sql.LoadProcess,
        [
            new NpgsqlParameter<string>("tenant", TenantId),
            new NpgsqlParameter<string>("type", processType),
            new NpgsqlParameter<string>("id", processId),
        ], cancellationToken).ConfigureAwait(false);
        NpgsqlDataReader reader = scope.Reader;
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProcessState(processType, processId, reader.GetInt64(0), reader.GetFieldValue<byte[]>(1), reader.GetBoolean(2), reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    public ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        for (int i = _documents.Count - 1; i >= 0; i--)
        {
            DocumentWrite write = _documents[i];
            if (write.Collection == collection && write.Id == id)
            {
                return ValueTask.FromResult(write.Data is null ? null : new StoredDocument(collection, id, write.Version, write.Data.Value));
            }
        }

        return _store.GetDocumentAsync(_connection, _transaction, TakeTenant(), TenantId, collection, id, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<StoredDocument>> GetManyAsync(string collection, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        ArgumentNullException.ThrowIfNull(ids);
        var found = new Dictionary<string, StoredDocument?>(StringComparer.Ordinal);
        if (ids.Count > 0)
        {
            await using PostgreSqlStore.TenantReader scope = await _store.ReadTenantAsync(_connection, _transaction, TenantId, TakeTenant(), _store.Sql.GetDocuments,
            [
                new NpgsqlParameter<string>("tenant", TenantId),
                new NpgsqlParameter<string>("collection", collection),
                new NpgsqlParameter<string[]>("ids", [.. ids]),
            ], cancellationToken).ConfigureAwait(false);
            while (await scope.Reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string id = scope.Reader.GetString(0);
                found[id] = new StoredDocument(collection, id, scope.Reader.GetInt64(1), scope.Reader.GetFieldValue<byte[]>(2));
            }
        }

        // This session's queued writes win (read own writes).
        foreach (DocumentWrite write in _documents)
        {
            if (write.Collection == collection && ids.Contains(write.Id))
            {
                found[write.Id] = write.Data is null ? null : new StoredDocument(collection, write.Id, write.Version, write.Data.Value);
            }
        }

        return [.. found.Values.OfType<StoredDocument>()];
    }

    // ------------------------------------------------------------------ queued writes

    public void Append(StreamId stream, string streamType, ExpectedVersion expected, IReadOnlyList<EventToAppend> events)
    {
        ThrowIfCompleted();
        ArgumentException.ThrowIfNullOrEmpty(streamType);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            throw new ArgumentException("An append needs at least one event.", nameof(events));
        }

        if (_wroteOther)
        {
            throw new InvalidOperationException("ES-R1: the stream append must be the first write of a session (doc 05 §5.3).");
        }

        if (_append is not null)
        {
            throw new InvalidOperationException($"ES-R2: a session appends to one stream; '{_append.Stream}' is already queued, '{stream}' was requested.");
        }

        _append = new AppendWrite(stream, streamType, expected, [.. events]);
        _notify = true;
    }

    public void EnqueueOutbox(OutboxMessage message)
    {
        ThrowIfCompleted();
        ArgumentNullException.ThrowIfNull(message);
        _outbox.Add(message);
        _wroteOther = true;
        _notify = true;
    }

    public void RecordInbox(string consumer, MessageId messageId, InboxResult result)
    {
        ThrowIfCompleted();
        ArgumentException.ThrowIfNullOrEmpty(consumer);
        ArgumentNullException.ThrowIfNull(result);
        if (_inbox.Any(i => i.Consumer == consumer && i.Id == messageId))
        {
            throw new DuplicateMessageException(consumer, messageId);
        }

        _inbox.Add((consumer, messageId, result));
        _wroteOther = true;
    }

    public void SaveCheckpoint(string subscription, int partition, GlobalPosition position, LeaseToken lease)
    {
        ThrowIfCompleted();
        ArgumentNullException.ThrowIfNull(lease);
        _checkpoints.Add((subscription, partition, position, lease));
        _wroteOther = true;
    }

    public void SaveProcessState(string processType, string processId, long expectedVersion, ReadOnlyMemory<byte> state, bool completed)
    {
        ThrowIfCompleted();
        _processes.Add(new ProcessWrite(processType, processId, expectedVersion, state, completed));
        _wroteOther = true;
    }

    public void Upsert(string collection, string id, long version, ReadOnlyMemory<byte> data)
    {
        ThrowIfCompleted();
        _documents.Add(new DocumentWrite(collection, id, version, data));
        _wroteOther = true;
    }

    public void Delete(string collection, string id)
    {
        ThrowIfCompleted();
        _documents.Add(new DocumentWrite(collection, id, 0, null));
        _wroteOther = true;
    }

    // ------------------------------------------------------------------ commit

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        cancellationToken.ThrowIfCancellationRequested();
        await SendAsync(includeNotify: false, commit: false, cancellationToken).ConfigureAwait(false);
    }

    // One round trip (doc 05 §3.2): the queued writes and COMMIT go in one batch; a failed in-SQL check aborts it first.
    public async ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        cancellationToken.ThrowIfCancellationRequested();
        _completed = true;
        if (!await SendAsync(includeNotify: false, commit: true, cancellationToken).ConfigureAwait(false))
        {
            _transactionEnded = true;
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_notify)
        {
            _store.RaiseCommitted();
            _store.AnnounceCommitted();
        }

        return _appendResult;
    }

    // Sends every write queued since the last send as one batch, the append first. Conditional writes assert their outcome
    // in SQL (sf_assert, V003), so the first failed check aborts the batch, including a trailing COMMIT. Any failure rolls
    // the transaction back and ends the session. Returns true when the batch committed.
    private async ValueTask<bool> SendAsync(bool includeNotify, bool commit, CancellationToken cancellationToken)
    {
        var steps = new List<Step>();
        var checks = new Dictionary<string, Func<Exception>>(StringComparer.Ordinal);
        AppendWrite? append = _append is not null && !_appendSent ? _append : null;
        if (append is not null)
        {
            steps.Add(new Step(AppendCommand(append), MapAppendError, IsQuery: true));
        }

        if (_outbox.Count > 0)
        {
            steps.Add(new Step(OutboxCommand(), null));
        }

        foreach ((string consumer, MessageId id, InboxResult result) in _inbox)
        {
            steps.Add(new Step(InboxCommand(consumer, id, result),
                e => e.SqlState == PostgresErrorCodes.UniqueViolation ? new DuplicateMessageException(consumer, id) : null));
        }

        steps.AddRange(DocumentSteps());

        foreach (ProcessWrite write in _processes)
        {
            string check = "process:" + checks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            checks[check] = () => new ConcurrencyException($"Process '{write.Type}/{write.Id}' is not at version {write.ExpectedVersion}.");
            steps.Add(new Step(ProcessCommand(write, check), null, IsQuery: true));
        }

        foreach ((string subscription, int partition, GlobalPosition position, LeaseToken lease) in _checkpoints)
        {
            string check = "checkpoint:" + checks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            checks[check] = () => new LeaseLostException(lease.Name);
            steps.Add(new Step(CheckpointCommand(subscription, partition, position, lease, check), null, IsQuery: true));
        }

        if (includeNotify)
        {
            var command = new NpgsqlBatchCommand(_store.Sql.Notify);
            command.Parameters.Add(new NpgsqlParameter<string>("channel", _store.Options.Channel));
            command.Parameters.Add(new NpgsqlParameter<string>("origin", _store.Origin));
            steps.Add(new Step(command, null, IsQuery: true));
        }

        if (steps.Count == 0)
        {
            return false;
        }

        await using var batch = new NpgsqlBatch(_connection, _transaction);

        // Row-level security: the tenant setting precedes every write of the transaction (ADR-022).
        bool prefixed = _store.RowLevelSecurity && TakeTenant();
        if (prefixed)
        {
            batch.BatchCommands.Add(PostgreSqlStore.SetTenantCommand(TenantId));
        }

        foreach (Step step in steps)
        {
            batch.BatchCommands.Add(step.Command);
        }

        if (commit)
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand("COMMIT"));
        }

        long appended = -1;
        long newVersion = 0;
        long lastSequence = 0;
        ulong transactionId = 0;
        PostgresException? failure = null;
        try
        {
            await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (prefixed)
            {
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            }

            if (append is not null && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                appended = reader.GetInt64(0);
                newVersion = reader.GetInt64(1);
                lastSequence = reader.GetInt64(2);
                transactionId = reader.IsDBNull(3) ? 0 : reader.GetFieldValue<ulong>(3);
            }

            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
            }
        }
        catch (PostgresException exception)
        {
            failure = exception;
        }

        if (append is not null && appended == 0)
        {
            await FailAsync().ConfigureAwait(false);
            throw await DiagnoseAppendAsync(append, cancellationToken).ConfigureAwait(false);
        }

        if (failure is { SqlState: AssertionFailed } && failure.MessageText == "append" && append is not null)
        {
            await FailAsync().ConfigureAwait(false);
            throw await DiagnoseAppendAsync(append, cancellationToken).ConfigureAwait(false);
        }

        if (failure is { SqlState: AssertionFailed } && checks.TryGetValue(failure.MessageText, out Func<Exception>? violation))
        {
            await FailAsync().ConfigureAwait(false);
            throw violation();
        }

        if (failure is not null)
        {
            await FailAsync().ConfigureAwait(false);
            Step? failed = FailedStep(steps, failure);
            Exception? mapped = failed?.MapError?.Invoke(failure);
            if (mapped is null && failed?.Command is { } command && append is not null && ReferenceEquals(command, steps[0].Command))
            {
                mapped = await DiagnoseAppendAsync(append, cancellationToken).ConfigureAwait(false);
            }

            if (mapped is not null)
            {
                throw mapped;
            }

            throw failure;
        }

        if (commit)
        {
            _transactionEnded = true;
        }

        if (append is not null)
        {
            _appendSent = true;
            _appendResult = new CommitResult(new StreamVersion(newVersion), new GlobalPosition(transactionId, (ulong)lastSequence));
        }

        _outbox.Clear();
        _inbox.Clear();
        _documents.Clear();
        _processes.Clear();
        _checkpoints.Clear();
        return commit;
    }

    // SQLSTATE raised by sf_assert (V003).
    private const string AssertionFailed = "SF001";

    private async ValueTask FailAsync()
    {
        _completed = true;
        await RollbackAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _completed = true;
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ batch commands

    private NpgsqlBatchCommand AppendCommand(AppendWrite append)
    {
        string sql = append.Expected.Kind switch
        {
            ExpectedVersionKind.NoStream => _store.Sql.AppendNoStream,
            ExpectedVersionKind.StreamExists => _store.Sql.AppendStreamExists,
            ExpectedVersionKind.Any => _store.Sql.AppendAny,
            _ => _store.Sql.AppendExact,
        };

        int count = append.Events.Count;
        var ids = new Guid[count];
        var types = new string[count];
        var versions = new int[count];
        var tags = new string[count];
        var data = new string[count];
        var metadata = new string[count];
        var times = new DateTimeOffset[count];
        for (int i = 0; i < count; i++)
        {
            EventToAppend e = append.Events[i];
            ids[i] = e.EventId.Value;
            types[i] = e.Type;
            versions[i] = e.TypeVersion;
            tags[i] = JsonCodecs.WriteTags(e.Tags);
            data[i] = Encoding.UTF8.GetString(e.Data.Span);
            metadata[i] = JsonCodecs.WriteMetadata(e.Metadata);
            times[i] = e.CreatedAt.ToUniversalTime();
        }

        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>("stream", append.Stream.Value));
        command.Parameters.Add(new NpgsqlParameter<string>("type", append.StreamType));
        command.Parameters.Add(new NpgsqlParameter<long>("count", count));
        command.Parameters.Add(new NpgsqlParameter<long>("expected", append.Expected.Version.Value));
        command.Parameters.Add(new NpgsqlParameter<short>("bucket", (short)Buckets.Of(TenantId, append.Stream)));
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", ids));
        command.Parameters.Add(new NpgsqlParameter<string[]>("types", types));
        command.Parameters.Add(new NpgsqlParameter<int[]>("vers", versions));
        command.Parameters.Add(new NpgsqlParameter<string[]>("tags", tags));
        command.Parameters.Add(new NpgsqlParameter<string[]>("datas", data));
        command.Parameters.Add(new NpgsqlParameter<string[]>("metas", metadata));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("ats", times));
        return command;
    }

    private NpgsqlBatchCommand OutboxCommand()
    {
        int count = _outbox.Count;
        var ids = new Guid[count];
        var partitions = new short[count];
        var keys = new string?[count];
        var destinations = new string[count];
        var kinds = new short[count];
        var attributes = new string[count];
        var bodies = new byte[count][];
        var deliverAt = new DateTimeOffset[count];
        for (int i = 0; i < count; i++)
        {
            OutboxMessage message = _outbox[i];
            ids[i] = message.Envelope.MessageId.Value;
            partitions[i] = checked((short)message.Partition);
            keys[i] = message.Envelope.EffectivePartitionKey;
            destinations[i] = message.Destination;
            kinds[i] = (short)message.Kind;
            attributes[i] = JsonCodecs.WriteAttributes(message.Envelope);
            bodies[i] = message.Envelope.Body.ToArray();
            deliverAt[i] = message.DeliverAt.ToUniversalTime();
        }

        var command = new NpgsqlBatchCommand(_store.Sql.InsertOutbox);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", ids));
        command.Parameters.Add(new NpgsqlParameter<short[]>("parts", partitions));
        command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = keys });
        command.Parameters.Add(new NpgsqlParameter<string[]>("dests", destinations));
        command.Parameters.Add(new NpgsqlParameter<short[]>("kinds", kinds));
        command.Parameters.Add(new NpgsqlParameter<string[]>("attrs", attributes));
        command.Parameters.Add(new NpgsqlParameter<byte[][]>("bodies", bodies));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("ats", deliverAt));
        return command;
    }

    private NpgsqlBatchCommand InboxCommand(string consumer, MessageId id, InboxResult result)
    {
        var command = new NpgsqlBatchCommand(_store.Sql.InsertInbox);
        command.Parameters.Add(new NpgsqlParameter<string>("consumer", consumer));
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id.Value));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", _store.Time.GetUtcNow().ToUniversalTime()));
        command.Parameters.Add(new NpgsqlParameter<short>("status", (short)result.Status));
        command.Parameters.Add(new NpgsqlParameter<long>("version", result.Version.Value));
        command.Parameters.Add(PostgreSqlStore.Text("error", result.ErrorCode));
        command.Parameters.Add(PostgreSqlStore.Xid("commit", result.Position.Commit));
        command.Parameters.Add(new NpgsqlParameter<long>("seq", checked((long)result.Position.Sequence)));
        return command;
    }

    // One multi-row upsert and one multi-row delete when every document is written once (the projection scope's case);
    // otherwise one statement per write, so repeated writes to a document keep their exact order and version rules.
    private IEnumerable<Step> DocumentSteps()
    {
        if (_documents.Count == 0)
        {
            yield break;
        }

        bool distinct = _documents.Select(d => (d.Collection, d.Id)).Distinct().Count() == _documents.Count;
        if (!distinct || _documents.Count == 1)
        {
            foreach (DocumentWrite write in _documents)
            {
                yield return new Step(DocumentCommand(write), null);
            }

            yield break;
        }

        DocumentWrite[] upserts = [.. _documents.Where(d => d.Data is not null)];
        DocumentWrite[] deletes = [.. _documents.Where(d => d.Data is null)];
        if (upserts.Length > 0)
        {
            var command = new NpgsqlBatchCommand(_store.Sql.UpsertDocuments);
            command.Parameters.Add(new NpgsqlParameter<string>("tenant", TenantId));
            command.Parameters.Add(new NpgsqlParameter<string[]>("collections", [.. upserts.Select(d => d.Collection)]));
            command.Parameters.Add(new NpgsqlParameter<string[]>("ids", [.. upserts.Select(d => d.Id)]));
            command.Parameters.Add(new NpgsqlParameter<long[]>("versions", [.. upserts.Select(d => d.Version)]));
            command.Parameters.Add(new NpgsqlParameter<string[]>("datas", [.. upserts.Select(d => Encoding.UTF8.GetString(d.Data!.Value.Span))]));
            yield return new Step(command, null);
        }

        if (deletes.Length > 0)
        {
            var command = new NpgsqlBatchCommand(_store.Sql.DeleteDocuments);
            command.Parameters.Add(new NpgsqlParameter<string>("tenant", TenantId));
            command.Parameters.Add(new NpgsqlParameter<string[]>("collections", [.. deletes.Select(d => d.Collection)]));
            command.Parameters.Add(new NpgsqlParameter<string[]>("ids", [.. deletes.Select(d => d.Id)]));
            yield return new Step(command, null);
        }
    }

    private NpgsqlBatchCommand DocumentCommand(DocumentWrite write)
    {
        var command = new NpgsqlBatchCommand(write.Data is null ? _store.Sql.DeleteDocument : _store.Sql.UpsertDocument);
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>("collection", write.Collection));
        command.Parameters.Add(new NpgsqlParameter<string>("id", write.Id));
        if (write.Data is { } data)
        {
            command.Parameters.Add(new NpgsqlParameter<long>("version", write.Version));
            command.Parameters.Add(PostgreSqlStore.Json("data", data));
        }

        return command;
    }

    private NpgsqlBatchCommand ProcessCommand(ProcessWrite write, string check)
    {
        var command = new NpgsqlBatchCommand(write.ExpectedVersion == 0 ? _store.Sql.InsertProcess : _store.Sql.UpdateProcess);
        command.Parameters.Add(new NpgsqlParameter<string>("tenant", TenantId));
        command.Parameters.Add(new NpgsqlParameter<string>("type", write.Type));
        command.Parameters.Add(new NpgsqlParameter<string>("id", write.Id));
        command.Parameters.Add(PostgreSqlStore.Json("state", write.State));
        command.Parameters.Add(new NpgsqlParameter<bool>("completed", write.Completed));
        if (write.ExpectedVersion != 0)
        {
            command.Parameters.Add(new NpgsqlParameter<long>("expected", write.ExpectedVersion));
        }

        command.Parameters.Add(new NpgsqlParameter<string>("check", check));

        return command;
    }

    private NpgsqlBatchCommand CheckpointCommand(string subscription, int partition, GlobalPosition position, LeaseToken lease, string check)
    {
        var command = new NpgsqlBatchCommand(_store.Sql.SaveCheckpoint);
        command.Parameters.Add(new NpgsqlParameter<string>("sub", subscription));
        command.Parameters.Add(new NpgsqlParameter<int>("p", partition));
        command.Parameters.Add(PostgreSqlStore.Xid("tx", position.Commit));
        command.Parameters.Add(new NpgsqlParameter<long>("seq", checked((long)position.Sequence)));
        command.Parameters.AddRange(PostgreSqlStore.LeaseParameters(lease));
        command.Parameters.Add(new NpgsqlParameter<string>("check", check));
        return command;
    }

    // ------------------------------------------------------------------ failures

    private Exception? MapAppendError(PostgresException exception)
    {
        if (exception.SqlState != PostgresErrorCodes.UniqueViolation || _append is null)
        {
            return null;
        }

        // Partitioned events (doc 05 §9) report the partition's index: events_initial_event_id, events_2026_12_event_id_created_at_key.
        return exception.ConstraintName?.Contains("event_id", StringComparison.Ordinal) == true
            ? new ConcurrencyException($"An event id appended to '{_append.Stream}' already exists (unique event id backstop).")
            : null;
    }

    // The append affected no rows: the stream is closed, or it is not at the expected version.
    private async ValueTask<Exception> DiagnoseAppendAsync(AppendWrite append, CancellationToken cancellationToken)
    {
        StreamInfo? info = await _store.GetStreamInfoAsync(_connection, TenantId, append.Stream, cancellationToken).ConfigureAwait(false);
        if (info is { IsDeleted: true })
        {
            return new StreamClosedException(append.Stream, "deleted");
        }

        if (info is { IsArchived: true })
        {
            return new StreamClosedException(append.Stream, "archived");
        }

        return new ConcurrencyException(append.Stream, append.Expected, info?.Version ?? StreamVersion.None);
    }

    private async ValueTask RollbackAsync()
    {
        if (!_transactionEnded)
        {
            _transactionEnded = true;
            await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // True when the next statement must set the tenant: once per transaction.
    private bool TakeTenant()
    {
        if (_tenantSet)
        {
            return false;
        }

        _tenantSet = true;
        return true;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The storage session is already committed or disposed.");
        }
    }

    // Npgsql does not always attach the failing batch command to the exception. Writes that completed report their row
    // count and the rest report -1, so the failing write is the first one without a count; event-table errors are the append.
    private static Step? FailedStep(List<Step> steps, PostgresException failure)
    {
        if (steps.FirstOrDefault(s => ReferenceEquals(s.Command, failure.BatchCommand)) is { } reported)
        {
            return reported;
        }

        if (failure.TableName?.StartsWith("events", StringComparison.Ordinal) == true || failure.ConstraintName?.StartsWith("uq_events_", StringComparison.Ordinal) == true)
        {
            return steps.FirstOrDefault(s => s.IsQuery);
        }

        return steps.FirstOrDefault(s => !s.IsQuery && s.Command.RecordsAffected < 0);
    }

    private sealed record Step(NpgsqlBatchCommand Command, Func<PostgresException, Exception?>? MapError, bool IsQuery = false);

    private sealed record AppendWrite(StreamId Stream, string StreamType, ExpectedVersion Expected, IReadOnlyList<EventToAppend> Events);

    private sealed record ProcessWrite(string Type, string Id, long ExpectedVersion, ReadOnlyMemory<byte> State, bool Completed);

    private sealed record DocumentWrite(string Collection, string Id, long Version, ReadOnlyMemory<byte>? Data);
}
