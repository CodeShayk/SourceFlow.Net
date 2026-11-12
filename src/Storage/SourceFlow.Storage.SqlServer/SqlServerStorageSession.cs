using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SourceFlow.Storage.SqlServer;

/// <summary>
/// One storage session: one Read Committed transaction on one pooled connection (doc 05 §1, §6). Reads go to the server
/// immediately; writes are queued and sent by <see cref="CommitAsync"/> as one batch (more only beyond SQL Server's 2,100
/// parameters), with the stream append first (ES-R1, doc 05 §5.3). Conditional statements raise <c>THROW 5000x</c>; the
/// first failure rolls the whole transaction back.
/// </summary>
internal sealed partial class SqlServerStorageSession : IStorageSession, IDocumentSession
{
    // SQL Server allows 2,100 parameters per request; a batch is split before it.
    private const int MaxParameters = 2000;

    private readonly SqlServerStore _store;
    private readonly SqlConnection _connection;
    private readonly SqlTransaction _transaction;
    private readonly List<OutboxMessage> _outbox = [];
    private readonly List<(string Consumer, MessageId Id, InboxResult Result)> _inbox = [];
    private readonly List<(string Subscription, int Partition, GlobalPosition Position, LeaseToken Lease)> _checkpoints = [];
    private readonly List<ProcessWrite> _processes = [];
    private readonly List<DocumentWrite> _documents = [];
    private AppendWrite? _append;
    private bool _wroteOther;
    private bool _completed;
    private bool _transactionEnded;
    private bool _appendSent;
    private bool _notify;
    private CommitResult _appendResult;

    public SqlServerStorageSession(SqlServerStore store, string tenantId, SqlConnection connection, SqlTransaction transaction)
    {
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
        return _store.ReadStreamAsync(_connection, _transaction, TenantId, stream, fromInclusive, maxCount, cancellationToken);
    }

    public async ValueTask<InboxEntry?> InboxSeenAsync(string consumer, MessageId messageId, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        await using SqlCommand command = SqlServerStore.Command(_store.Sql.InboxSeen, _connection, _transaction,
            SqlServerStore.Text("@consumer", consumer), SqlServerStore.Uuid("@id", messageId.Value));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var result = new InboxResult(
            (CommandStatus)reader.GetInt16(1),
            new StreamVersion(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            new GlobalPosition((ulong)reader.GetInt64(4), (ulong)reader.GetInt64(5)));
        return new InboxEntry(consumer, messageId, reader.GetDateTimeOffset(0), result);
    }

    public async ValueTask<ProcessState?> LoadProcessStateAsync(string processType, string processId, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        await using SqlCommand command = SqlServerStore.Command(_store.Sql.LoadProcess, _connection, _transaction,
            SqlServerStore.Text("@tenant", TenantId), SqlServerStore.Text("@type", processType), SqlServerStore.Text("@id", processId));
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ProcessState(processType, processId, reader.GetInt64(0), Encoding.UTF8.GetBytes(reader.GetString(1)), reader.GetBoolean(2), reader.GetDateTimeOffset(3))
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

        return _store.GetDocumentAsync(_connection, _transaction, TenantId, collection, id, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<StoredDocument>> GetManyAsync(string collection, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        ArgumentNullException.ThrowIfNull(ids);
        var found = new Dictionary<string, StoredDocument?>(StringComparer.Ordinal);
        if (ids.Count > 0)
        {
            string json = System.Text.Json.JsonSerializer.Serialize([.. ids], SqlServerJsonContext.Default.StringArray);
            await using SqlCommand command = SqlServerStore.Command(_store.Sql.GetDocuments, _connection, _transaction,
                SqlServerStore.Text("@tenant", TenantId), SqlServerStore.Text("@collection", collection), SqlServerStore.MaxText("@ids", json));
            await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string id = reader.GetString(0);
                found[id] = new StoredDocument(collection, id, reader.GetInt64(1), Encoding.UTF8.GetBytes(reader.GetString(2)));
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
        await SendAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        cancellationToken.ThrowIfCancellationRequested();
        _completed = true;
        await SendAsync(cancellationToken).ConfigureAwait(false);
        _transactionEnded = true;
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (_notify)
        {
            _store.RaiseCommitted();
        }

        return _appendResult;
    }

    // Sends every write queued since the last send, the append first. Any failure rolls the transaction back and ends the session.
    private async ValueTask SendAsync(CancellationToken cancellationToken)
    {
        var steps = new List<Step>();
        var checks = new Dictionary<string, Func<Exception>>(StringComparer.Ordinal);
        AppendWrite? append = _append is not null && !_appendSent ? _append : null;
        if (append is not null)
        {
            steps.Add(AppendStep(append));
        }

        if (_outbox.Count > 0)
        {
            steps.Add(OutboxStep());
        }

        foreach ((string consumer, MessageId id, InboxResult result) in _inbox)
        {
            steps.Add(InboxStep(consumer, id, result));
        }

        steps.AddRange(DocumentSteps());

        foreach (ProcessWrite write in _processes)
        {
            string check = "process:" + checks.Count.ToString(CultureInfo.InvariantCulture);
            checks[check] = () => new ConcurrencyException($"Process '{write.Type}/{write.Id}' is not at version {write.ExpectedVersion}.");
            steps.Add(ProcessStep(write, check));
        }

        foreach ((string subscription, int partition, GlobalPosition position, LeaseToken lease) in _checkpoints)
        {
            string check = "checkpoint:" + checks.Count.ToString(CultureInfo.InvariantCulture);
            checks[check] = () => new LeaseLostException(lease.Name);
            steps.Add(CheckpointStep(subscription, partition, position, lease, check));
        }

        if (steps.Count == 0)
        {
            return;
        }

        try
        {
            foreach (List<Step> batch in Split(steps))
            {
                await ExecuteAsync(batch, append is not null && ReferenceEquals(batch[0], steps[0]), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqlException failure)
        {
            await FailAsync().ConfigureAwait(false);
            throw await MapAsync(failure, append, checks, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await FailAsync().ConfigureAwait(false);
            throw;
        }

        if (append is not null)
        {
            _appendSent = true;
        }

        _outbox.Clear();
        _inbox.Clear();
        _documents.Clear();
        _processes.Clear();
        _checkpoints.Clear();
    }

    private async Task ExecuteAsync(List<Step> batch, bool withAppend, CancellationToken cancellationToken)
    {
        var sql = new StringBuilder();
        if (withAppend)
        {
            sql.Append("DECLARE @base bigint;\n");
        }

        await using var command = new SqlCommand { Connection = _connection, Transaction = _transaction };
        for (int i = 0; i < batch.Count; i++)
        {
            string prefix = "@s" + i.ToString(CultureInfo.InvariantCulture) + "_";
            string text = batch[i].Sql;
            foreach (SqlParameter parameter in batch[i].Parameters)
            {
                string name = parameter.ParameterName;
                text = ParameterName(name[1..]).Replace(text, prefix + name[1..]);
                parameter.ParameterName = prefix + name[1..];
                command.Parameters.Add(parameter);
            }

            sql.Append(text).Append('\n');
        }

        command.CommandText = sql.ToString();
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (withAppend && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _appendResult = new CommitResult(new StreamVersion(reader.GetInt64(1)), new GlobalPosition((ulong)reader.GetInt64(2), (ulong)reader.GetInt64(3)));
        }

        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }

    // Steps in order, cut before a batch would exceed the parameter limit; the append stays first in the first batch.
    private static IEnumerable<List<Step>> Split(List<Step> steps)
    {
        var batch = new List<Step>();
        int parameters = 0;
        foreach (Step step in steps)
        {
            if (batch.Count > 0 && parameters + step.Parameters.Length > MaxParameters)
            {
                yield return batch;
                batch = [];
                parameters = 0;
            }

            batch.Add(step);
            parameters += step.Parameters.Length;
        }

        yield return batch;
    }

    private async ValueTask<Exception> MapAsync(SqlException failure, AppendWrite? append, Dictionary<string, Func<Exception>> checks, CancellationToken cancellationToken)
    {
        if (failure.Number == SqlText.AppendFailed && append is not null)
        {
            return await DiagnoseAppendAsync(append, cancellationToken).ConfigureAwait(false);
        }

        if (failure.Number == SqlText.CheckFailed && checks.TryGetValue(failure.Message, out Func<Exception>? violation))
        {
            return violation();
        }

        if (failure.Number is 2627 or 2601)
        {
            string message = failure.Message;
            if (message.Contains("uq_events_event_id", StringComparison.OrdinalIgnoreCase) && append is not null)
            {
                return new ConcurrencyException($"An event id appended to '{append.Stream}' already exists (unique event id backstop).");
            }

            if ((message.Contains("uq_events_stream_version", StringComparison.OrdinalIgnoreCase) || message.Contains("pk_streams", StringComparison.OrdinalIgnoreCase)) && append is not null)
            {
                return await DiagnoseAppendAsync(append, cancellationToken).ConfigureAwait(false);
            }

            if (message.Contains("pk_inbox", StringComparison.OrdinalIgnoreCase))
            {
                foreach ((string consumer, MessageId id, _) in _inbox)
                {
                    if (message.Contains(id.Value.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        return new DuplicateMessageException(consumer, id);
                    }
                }
            }
        }

        return failure;
    }

    // The append changed no row: the stream is closed, or it is not at the expected version.
    private async ValueTask<Exception> DiagnoseAppendAsync(AppendWrite append, CancellationToken cancellationToken)
    {
        StreamInfo? info = await _store.GetStreamInfoAsync(_connection, null, TenantId, append.Stream, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask FailAsync()
    {
        _completed = true;
        if (!_transactionEnded)
        {
            _transactionEnded = true;
            if (_transaction.Connection is not null)
            {
                await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _completed = true;
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ statements

    private Step AppendStep(AppendWrite append)
    {
        string head = append.Expected.Kind switch
        {
            ExpectedVersionKind.NoStream => _store.Sql.AppendNoStream,
            ExpectedVersionKind.StreamExists => _store.Sql.AppendStreamExists,
            ExpectedVersionKind.Any => _store.Sql.AppendAny,
            _ => _store.Sql.AppendExact,
        };

        int ordinal = 0;
        string rows = Rows.Write(append.Events, (w, e) =>
        {
            w.WriteNumber("o", ++ordinal);
            w.WriteString("i", e.EventId.Value);
            w.WriteString("t", e.Type);
            w.WriteNumber("v", e.TypeVersion);
            w.WriteString("g", JsonCodecs.WriteTags(e.Tags));
            w.WriteString("d", Encoding.UTF8.GetString(e.Data.Span));
            w.WriteString("m", JsonCodecs.WriteMetadata(e.Metadata));
            w.WriteString("a", e.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        });

        return new Step(head + "\n" + _store.Sql.AppendTail,
        [
            SqlServerStore.Text("@tenant", TenantId),
            SqlServerStore.Text("@stream", append.Stream.Value),
            SqlServerStore.Text("@type", append.StreamType),
            SqlServerStore.Int64("@count", append.Events.Count),
            SqlServerStore.Int64("@expected", append.Expected.Version.Value),
            SqlServerStore.Int16("@bucket", Buckets.Of(TenantId, append.Stream)),
            SqlServerStore.MaxText("@rows", rows),
        ]);
    }

    private Step OutboxStep()
    {
        int ordinal = 0;
        string rows = Rows.Write(_outbox, (w, m) =>
        {
            w.WriteNumber("o", ++ordinal);
            w.WriteString("i", m.Envelope.MessageId.Value);
            w.WriteNumber("p", m.Partition);
            if (m.Envelope.EffectivePartitionKey is { } key)
            {
                w.WriteString("k", key);
            }

            w.WriteString("d", m.Destination);
            w.WriteNumber("n", (short)m.Kind);
            w.WriteString("a", JsonCodecs.WriteAttributes(m.Envelope));
            w.WriteString("b", Convert.ToHexString(m.Envelope.Body.Span));
            w.WriteString("t", m.DeliverAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        });
        return new Step(_store.Sql.InsertOutbox, [SqlServerStore.MaxText("@rows", rows)]);
    }

    private Step InboxStep(string consumer, MessageId id, InboxResult result)
    {
        return new Step(_store.Sql.InsertInbox,
        [
            SqlServerStore.Text("@consumer", consumer),
            SqlServerStore.Uuid("@id", id.Value),
            SqlServerStore.At("@now", _store.Time.GetUtcNow()),
            SqlServerStore.Int16("@status", (int)result.Status),
            SqlServerStore.Int64("@version", result.Version.Value),
            SqlServerStore.MaxText("@error", result.ErrorCode),
            SqlServerStore.Int64("@commit", checked((long)result.Position.Commit)),
            SqlServerStore.Int64("@seq", checked((long)Math.Min(result.Position.Sequence, long.MaxValue))),
        ]);
    }

    // One MERGE and one DELETE for distinct documents (the projection scope's case); otherwise one statement per write,
    // so repeated writes to a document keep their order and version rules.
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
                yield return write.Data is { } data
                    ? new Step(_store.Sql.UpsertDocument,
                      [
                          SqlServerStore.Text("@tenant", TenantId), SqlServerStore.Text("@collection", write.Collection), SqlServerStore.Text("@id", write.Id),
                          SqlServerStore.Int64("@version", write.Version), SqlServerStore.Json("@data", data),
                      ])
                    : new Step(_store.Sql.DeleteDocument, [SqlServerStore.Text("@tenant", TenantId), SqlServerStore.Text("@collection", write.Collection), SqlServerStore.Text("@id", write.Id)]);
            }

            yield break;
        }

        DocumentWrite[] upserts = [.. _documents.Where(d => d.Data is not null)];
        DocumentWrite[] deletes = [.. _documents.Where(d => d.Data is null)];
        if (upserts.Length > 0)
        {
            string rows = Rows.Write(upserts, static (w, d) =>
            {
                w.WriteString("c", d.Collection);
                w.WriteString("i", d.Id);
                w.WriteNumber("v", d.Version);
                w.WriteString("j", Encoding.UTF8.GetString(d.Data!.Value.Span));
            });
            yield return new Step(_store.Sql.UpsertDocuments, [SqlServerStore.Text("@tenant", TenantId), SqlServerStore.MaxText("@rows", rows)]);
        }

        if (deletes.Length > 0)
        {
            string rows = Rows.Write(deletes, static (w, d) =>
            {
                w.WriteString("c", d.Collection);
                w.WriteString("i", d.Id);
            });
            yield return new Step(_store.Sql.DeleteDocuments, [SqlServerStore.Text("@tenant", TenantId), SqlServerStore.MaxText("@rows", rows)]);
        }
    }

    private Step ProcessStep(ProcessWrite write, string check)
    {
        List<SqlParameter> parameters =
        [
            SqlServerStore.Text("@tenant", TenantId),
            SqlServerStore.Text("@type", write.Type),
            SqlServerStore.Text("@id", write.Id),
            SqlServerStore.Json("@state", write.State),
            SqlServerStore.Bit("@completed", write.Completed),
            SqlServerStore.Text("@check", check),
        ];
        if (write.ExpectedVersion != 0)
        {
            parameters.Add(SqlServerStore.Int64("@expected", write.ExpectedVersion));
        }

        return new Step(write.ExpectedVersion == 0 ? _store.Sql.InsertProcess : _store.Sql.UpdateProcess, [.. parameters]);
    }

    private Step CheckpointStep(string subscription, int partition, GlobalPosition position, LeaseToken lease, string check)
    {
        return new Step(_store.Sql.SaveCheckpoint,
        [
            SqlServerStore.Text("@sub", subscription),
            SqlServerStore.Int32("@p", partition),
            SqlServerStore.Int64("@commit", checked((long)position.Commit)),
            SqlServerStore.Int64("@seq", checked((long)Math.Min(position.Sequence, long.MaxValue))),
            .. SqlServerStore.LeaseParameters(lease),
            SqlServerStore.Text("@check", check),
        ]);
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The storage session is already committed or disposed.");
        }
    }

    // A parameter reference: '@name' not part of '@@...' and not followed by more identifier characters.
    private static Regex ParameterName(string name)
    {
        return new Regex("(?<![@A-Za-z0-9_])@" + Regex.Escape(name) + "(?![A-Za-z0-9_])", RegexOptions.CultureInvariant);
    }

    private sealed record Step(string Sql, SqlParameter[] Parameters);

    private sealed record AppendWrite(StreamId Stream, string StreamType, ExpectedVersion Expected, IReadOnlyList<EventToAppend> Events);

    private sealed record ProcessWrite(string Type, string Id, long ExpectedVersion, ReadOnlyMemory<byte> State, bool Completed);

    private sealed record DocumentWrite(string Collection, string Id, long Version, ReadOnlyMemory<byte>? Data);
}

/// <summary>Source-generated JSON for the few values the adapter serialises (AOT-safe).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class SqlServerJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
