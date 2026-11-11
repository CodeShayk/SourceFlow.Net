using System.Data.Common;

namespace SourceFlow.Storage.InMemory;

/// <summary>
/// The in-memory storage adapter: a complete, transactional implementation of every storage port. It is the reference
/// implementation the database adapters are compared against and passes the whole storage conformance kit (WP-1.4).
/// Commits are atomic under one lock; positions are <c>(commit number, index in commit)</c>, so the safe horizon is
/// always the last commit.
/// </summary>
public sealed class InMemoryStore :
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
    ISchemaStore
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<(string Tenant, string Stream), StreamState> _streams = [];
    private readonly List<StoredEvent> _log = [];
    private readonly HashSet<MessageId> _eventIds = [];
    private readonly Dictionary<(string Consumer, MessageId Id), InboxEntry> _inbox = [];
    private readonly Dictionary<(string Subscription, int Partition), GlobalPosition> _checkpoints = [];
    private readonly Dictionary<string, LeaseState> _leases = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, OutboxRecord> _outbox = [];
    private readonly Dictionary<(string Tenant, string Type, string Id), ProcessState> _processes = [];
    private readonly Dictionary<(string Tenant, string Collection, string Id), StoredDocument> _documents = [];
    private readonly Dictionary<Guid, DeadLetter> _deadLetters = [];
    private readonly Dictionary<(string Tenant, string Stream), Snapshot> _snapshots = [];
    private readonly List<Action> _subscribers = [];
    private readonly Dictionary<string, (int Roles, DateTimeOffset HeartbeatAt)> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Type, int Version), Catalog.SchemaEntry> _schemas = [];
    private ulong _commit;
    private long _outboxSequence;

    /// <summary>Creates an empty store.</summary>
    public InMemoryStore(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Fault-injection hooks for chaos and conformance tests (UT-STO-25).</summary>
    public InMemoryFaults Faults { get; } = new();

    /// <summary>The number of commits so far (SCK-20 counts one commit per command).</summary>
    public long CommitCount
    {
        get
        {
            lock (_gate)
            {
                return (long)_commit;
            }
        }
    }

    // ------------------------------------------------------------------ IStorageSessionFactory

    /// <inheritdoc />
    public ValueTask<IStorageSession> BeginAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        return ValueTask.FromResult<IStorageSession>(new Session(this, tenantId));
    }

    // ------------------------------------------------------------------ IEventStore

    /// <inheritdoc />
    public ValueTask<StreamSlice> ReadStreamAsync(string tenantId, StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(ReadStreamLocked(tenantId, stream, fromInclusive, maxCount));
        }
    }

    /// <inheritdoc />
    public ValueTask<StreamInfo?> GetStreamInfoAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_streams.TryGetValue((tenantId, stream.Value), out StreamState? state)
                ? new StreamInfo(stream, state.Type, state.Version, state.Deleted, state.Archived)
                : null);
        }
    }

    /// <inheritdoc />
    public ValueTask ArchiveStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue((tenantId, stream.Value), out StreamState? state))
            {
                state.Archived = true;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_streams.TryGetValue((tenantId, stream.Value), out StreamState? state))
            {
                state.Deleted = true;
            }
        }

        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ IEventLogReader

    /// <inheritdoc />
    public ValueTask<LogBatch> ReadAsync(GlobalPosition after, BucketRange buckets, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        lock (_gate)
        {
            int start = FirstIndexAfter(after);
            var events = new List<StoredEvent>(Math.Min(maxCount, 64));
            for (int i = start; i < _log.Count && events.Count < maxCount; i++)
            {
                if (buckets.Contains(_log[i].Bucket))
                {
                    events.Add(_log[i]);
                }
            }

            return ValueTask.FromResult(new LogBatch(events, Horizon()));
        }
    }

    /// <inheritdoc />
    public ValueTask<GlobalPosition> GetSafeHorizonAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(Horizon());
        }
    }

    // ------------------------------------------------------------------ ICheckpointStore

    /// <inheritdoc />
    public ValueTask<GlobalPosition> GetAsync(string subscription, int partition, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_checkpoints.GetValueOrDefault((subscription, partition)));
        }
    }

    // ------------------------------------------------------------------ ISchemaStore

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Catalog.SchemaConflict>> RegisterAsync(string service, IReadOnlyList<Catalog.SchemaEntry> schemas, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var conflicts = new List<Catalog.SchemaConflict>();
        lock (_gate)
        {
            foreach (Catalog.SchemaEntry schema in schemas)
            {
                if (!_schemas.TryGetValue((schema.Type, schema.Version), out Catalog.SchemaEntry? registered))
                {
                    _schemas[(schema.Type, schema.Version)] = schema;
                }
                else if (registered.Fingerprint != schema.Fingerprint)
                {
                    conflicts.Add(new Catalog.SchemaConflict(schema.Type, schema.Version, registered.Fingerprint, schema.Fingerprint));
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<Catalog.SchemaConflict>>(conflicts);
    }

    /// <inheritdoc />
    ValueTask<IReadOnlyList<Catalog.SchemaEntry>> ISchemaStore.ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<Catalog.SchemaEntry>>([.. _schemas.Values.OrderBy(s => s.Type, StringComparer.Ordinal).ThenBy(s => s.Version)]);
        }
    }

    // ------------------------------------------------------------------ INodeRegistry

    /// <inheritdoc />
    public ValueTask<int> HeartbeatAsync(string nodeId, int roles, TimeSpan liveWindow, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            _nodes[nodeId] = (roles, now);
            int live = _nodes.Count(n => n.Key == nodeId || ((n.Value.Roles & roles) != 0 && n.Value.HeartbeatAt > now - liveWindow));
            return ValueTask.FromResult(live);
        }
    }

    /// <inheritdoc />
    public ValueTask LeaveAsync(string nodeId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _nodes.Remove(nodeId);
        }

        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ ILeaseManager

    /// <inheritdoc />
    public ValueTask<LeaseToken?> TryAcquireAsync(string name, string owner, TimeSpan ttl, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (!_leases.TryGetValue(name, out LeaseState? lease))
            {
                lease = new LeaseState();
                _leases[name] = lease;
            }

            bool held = lease.Owner is not null && lease.ExpiresAt > now;
            if (held && lease.Owner != owner)
            {
                return ValueTask.FromResult<LeaseToken?>(null);
            }

            if (!held || lease.Owner != owner)
            {
                lease.Token++;
            }

            lease.Owner = owner;
            lease.ExpiresAt = now + ttl;
            return ValueTask.FromResult<LeaseToken?>(new LeaseToken(name, owner, lease.Token));
        }
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(LeaseToken lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (_leases.TryGetValue(lease.Name, out LeaseState? state) && state.Owner == lease.Owner && state.Token == lease.Token)
            {
                state.Owner = null;
                state.ExpiresAt = DateTimeOffset.MinValue;
            }
        }

        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ IOutboxStore

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(int partition, LeaseToken lease, int maxCount, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureLeaseLocked(lease);
            DateTimeOffset now = _time.GetUtcNow();
            var blockedKeys = new HashSet<string>(StringComparer.Ordinal);
            var claimed = new List<OutboxRecord>();
            foreach (OutboxRecord record in _outbox.Values)
            {
                if (record.Message.Partition != partition)
                {
                    continue;
                }

                string? key = record.Message.Envelope.EffectivePartitionKey;
                bool isHead = key is null || blockedKeys.Add(key);
                if (isHead && record.Message.DeliverAt <= now)
                {
                    claimed.Add(record);
                    if (claimed.Count == maxCount)
                    {
                        break;
                    }
                }
            }

            return ValueTask.FromResult<IReadOnlyList<OutboxRecord>>(claimed);
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(LeaseToken lease, IReadOnlyList<long> sequences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        lock (_gate)
        {
            EnsureLeaseLocked(lease);
            foreach (long sequence in sequences)
            {
                _outbox.Remove(sequence);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RetryAsync(LeaseToken lease, long sequence, DateTimeOffset deliverAt, string error, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureLeaseLocked(lease);
            if (_outbox.TryGetValue(sequence, out OutboxRecord? record))
            {
                _outbox[sequence] = record with
                {
                    Attempts = record.Attempts + 1,
                    LastError = error,
                    Message = record.Message with { DeliverAt = deliverAt },
                };
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> CountAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_outbox.Count);
        }
    }

    /// <summary>Outbox rows that are due now (excludes future timers), for idle detection in tests.</summary>
    public int DueOutboxCount()
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            return _outbox.Values.Count(r => r.Message.DeliverAt <= now);
        }
    }

    /// <summary>Every outbox row, for tests and diagnostics.</summary>
    public IReadOnlyList<OutboxRecord> OutboxSnapshot()
    {
        lock (_gate)
        {
            return [.. _outbox.Values];
        }
    }

    // ------------------------------------------------------------------ IDeadLetterStore

    /// <inheritdoc />
    public ValueTask ParkAsync(DeadLetter deadLetter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deadLetter);
        lock (_gate)
        {
            _deadLetters[deadLetter.Id] = deadLetter;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DeadLetter>> QueryAsync(DeadLetterQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            DeadLetter[] result = _deadLetters.Values
                .Where(d => query.Consumer is null || d.Consumer == query.Consumer)
                .Where(d => query.Reason is null || d.Reason == query.Reason)
                .Where(d => query.Status is null || d.Status == query.Status)
                .OrderByDescending(d => d.ParkedAt)
                .ThenBy(d => d.Id)
                .Take(query.Limit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<DeadLetter>>(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<DeadLetter?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_deadLetters.GetValueOrDefault(id));
        }
    }

    /// <inheritdoc />
    public ValueTask SetStatusAsync(Guid id, DeadLetterStatus status, string? note, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_deadLetters.TryGetValue(id, out DeadLetter? deadLetter))
            {
                throw new KeyNotFoundException($"Dead letter {id} does not exist.");
            }

            _deadLetters[id] = deadLetter with { Status = status, StatusNote = note, StatusChangedAt = _time.GetUtcNow() };
        }

        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ ISnapshotStore

    /// <inheritdoc />
    ValueTask<Snapshot?> ISnapshotStore.GetAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_snapshots.GetValueOrDefault((tenantId, stream.Value)));
        }
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(string tenantId, Snapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (!_snapshots.TryGetValue((tenantId, snapshot.StreamId.Value), out Snapshot? existing) || existing.Version <= snapshot.Version)
            {
                _snapshots[(tenantId, snapshot.StreamId.Value)] = snapshot;
            }
        }

        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ IDocumentReader

    /// <inheritdoc />
    ValueTask<StoredDocument?> IDocumentReader.GetAsync(string tenantId, string collection, string id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_documents.GetValueOrDefault((tenantId, collection, id)));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<StoredDocument>> ListAsync(string tenantId, string collection, int maxCount, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            StoredDocument[] documents = _documents
                .Where(d => d.Key.Tenant == tenantId && d.Key.Collection == collection)
                .OrderBy(d => d.Key.Id, StringComparer.Ordinal)
                .Take(maxCount)
                .Select(d => d.Value)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<StoredDocument>>(documents);
        }
    }

    // ------------------------------------------------------------------ IStoreRetention

    /// <inheritdoc />
    public ValueTask<int> PurgeInboxAsync(DateTimeOffset processedBefore, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        lock (_gate)
        {
            (string, MessageId)[] expired = [.. _inbox.Where(e => e.Value.ProcessedAt < processedBefore).Select(e => e.Key)];
            foreach ((string, MessageId) key in expired)
            {
                _inbox.Remove(key);
            }

            return ValueTask.FromResult(expired.Length);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PurgeDeadLettersAsync(DateTimeOffset resolvedBefore, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        lock (_gate)
        {
            Guid[] expired = [.. _deadLetters.Values
                .Where(d => d.Status != DeadLetterStatus.Parked && d.StatusChangedAt is { } changed && changed < resolvedBefore)
                .Select(d => d.Id)];
            foreach (Guid id in expired)
            {
                _deadLetters.Remove(id);
            }

            return ValueTask.FromResult(expired.Length);
        }
    }

    // ------------------------------------------------------------------ IStoreNotifications

    /// <inheritdoc />
    public IDisposable Subscribe(Action onCommit)
    {
        ArgumentNullException.ThrowIfNull(onCommit);
        lock (_gate)
        {
            _subscribers.Add(onCommit);
        }

        return new Unsubscriber(this, onCommit);
    }

    // ------------------------------------------------------------------ internals

    private StreamSlice ReadStreamLocked(string tenantId, StreamId stream, StreamVersion fromInclusive, int maxCount)
    {
        if (!_streams.TryGetValue((tenantId, stream.Value), out StreamState? state))
        {
            return new StreamSlice(stream, StreamVersion.None, [], IsEndOfStream: true);
        }

        long from = Math.Max(1, fromInclusive.Value);
        StoredEvent[] events = state.Events.Where(e => e.StreamVersion.Value >= from).Take(maxCount).ToArray();
        bool end = events.Length == 0 || events[^1].StreamVersion == state.Version;
        return new StreamSlice(stream, state.Version, events, end);
    }

    private int FirstIndexAfter(GlobalPosition after)
    {
        int low = 0;
        int high = _log.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_log[mid].Position <= after)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private GlobalPosition Horizon()
    {
        return _log.Count == 0 ? GlobalPosition.Start : _log[^1].Position;
    }

    private void EnsureLeaseLocked(LeaseToken lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!_leases.TryGetValue(lease.Name, out LeaseState? state)
            || state.Owner != lease.Owner || state.Token != lease.Token || state.ExpiresAt <= _time.GetUtcNow())
        {
            throw new LeaseLostException(lease.Name);
        }
    }

    private CommitResult Commit(Session session)
    {
        Action[] subscribers;
        CommitResult result;
        lock (_gate)
        {
            Faults.RaiseBeforeCommit();
            DateTimeOffset now = _time.GetUtcNow();

            // 1. Validate everything first; nothing is applied unless all checks pass (ADR-006 atomicity).
            StreamState? stream = null;
            StreamVersion baseVersion = StreamVersion.None;
            if (session.Append is { } append)
            {
                _streams.TryGetValue((session.TenantId, append.Stream.Value), out stream);
                baseVersion = stream?.Version ?? StreamVersion.None;
                if (stream is { Deleted: true })
                {
                    throw new StreamClosedException(append.Stream, "deleted");
                }

                if (stream is { Archived: true })
                {
                    throw new StreamClosedException(append.Stream, "archived");
                }

                if (!append.Expected.IsSatisfiedBy(baseVersion))
                {
                    throw new ConcurrencyException(append.Stream, append.Expected, baseVersion);
                }

                foreach (EventToAppend e in append.Events)
                {
                    if (_eventIds.Contains(e.EventId))
                    {
                        throw new ConcurrencyException($"Event id {e.EventId} already exists (unique event id backstop).");
                    }
                }
            }

            foreach ((string consumer, MessageId id, _) in session.Inbox)
            {
                if (_inbox.ContainsKey((consumer, id)))
                {
                    throw new DuplicateMessageException(consumer, id);
                }
            }

            foreach ((_, _, _, LeaseToken lease) in session.Checkpoints)
            {
                EnsureLeaseLocked(lease);
            }

            foreach (ProcessWrite write in session.Processes)
            {
                ProcessState? current = _processes.GetValueOrDefault((session.TenantId, write.Type, write.Id));
                long currentVersion = current?.Version ?? 0;
                if (currentVersion != write.ExpectedVersion)
                {
                    throw new ConcurrencyException($"Process '{write.Type}/{write.Id}' is at version {currentVersion}, not {write.ExpectedVersion}.");
                }
            }

            // 2. Apply.
            ulong commit = ++_commit;
            ulong index = 0;
            StreamVersion? newVersion = null;
            GlobalPosition? lastPosition = null;
            if (session.Append is { } a)
            {
                stream ??= _streams[(session.TenantId, a.Stream.Value)] = new StreamState(a.StreamType);
                int bucket = Buckets.Of(session.TenantId, a.Stream);
                StreamVersion version = baseVersion;
                foreach (EventToAppend e in a.Events)
                {
                    version += 1;
                    var stored = new StoredEvent(new GlobalPosition(commit, index++), session.TenantId, a.Stream, version, bucket, e.EventId,
                        e.Type, e.TypeVersion, e.Data.ToArray(), e.Metadata, e.CreatedAt, [.. e.Tags]);
                    stream.Events.Add(stored);
                    _log.Add(stored);
                    _eventIds.Add(e.EventId);
                    lastPosition = stored.Position;
                }

                stream.Version = version;
                newVersion = version;
            }

            foreach (OutboxMessage message in session.Outbox)
            {
                long sequence = ++_outboxSequence;
                _outbox[sequence] = new OutboxRecord(sequence, message, 0, null, now);
            }

            foreach ((string consumer, MessageId id, InboxResult inboxResult) in session.Inbox)
            {
                _inbox[(consumer, id)] = new InboxEntry(consumer, id, now, inboxResult);
            }

            foreach ((string subscription, int partition, GlobalPosition position, _) in session.Checkpoints)
            {
                _checkpoints[(subscription, partition)] = position;
            }

            foreach (ProcessWrite write in session.Processes)
            {
                _processes[(session.TenantId, write.Type, write.Id)] =
                    new ProcessState(write.Type, write.Id, write.ExpectedVersion + 1, write.State.ToArray(), write.Completed, now);
            }

            foreach (DocumentWrite write in session.DocumentWrites)
            {
                var key = (session.TenantId, write.Collection, write.Id);
                if (write.Data is null)
                {
                    _documents.Remove(key);
                }
                else if (!_documents.TryGetValue(key, out StoredDocument? existing) || existing.Version <= write.Version)
                {
                    _documents[key] = new StoredDocument(write.Collection, write.Id, write.Version, write.Data.Value.ToArray());
                }
            }

            result = new CommitResult(newVersion, lastPosition);
            subscribers = [.. _subscribers];
        }

        foreach (Action subscriber in subscribers)
        {
            subscriber();
        }

        return result;
    }

    private InboxEntry? InboxLookup(string consumer, MessageId id)
    {
        lock (_gate)
        {
            return _inbox.GetValueOrDefault((consumer, id));
        }
    }

    private ProcessState? ProcessLookup(string tenant, string type, string id)
    {
        lock (_gate)
        {
            return _processes.GetValueOrDefault((tenant, type, id));
        }
    }

    private StoredDocument? DocumentLookup(string tenant, string collection, string id)
    {
        lock (_gate)
        {
            return _documents.GetValueOrDefault((tenant, collection, id));
        }
    }

    private sealed class StreamState(string type)
    {
        public string Type { get; } = type;

        public StreamVersion Version { get; set; }

        public bool Deleted { get; set; }

        public bool Archived { get; set; }

        public List<StoredEvent> Events { get; } = [];
    }

    private sealed class LeaseState
    {
        public string? Owner { get; set; }

        public long Token { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }
    }

    private sealed record AppendWrite(StreamId Stream, string StreamType, ExpectedVersion Expected, IReadOnlyList<EventToAppend> Events);

    private sealed record ProcessWrite(string Type, string Id, long ExpectedVersion, ReadOnlyMemory<byte> State, bool Completed);

    private sealed record DocumentWrite(string Collection, string Id, long Version, ReadOnlyMemory<byte>? Data);

    private sealed class Unsubscriber(InMemoryStore store, Action callback) : IDisposable
    {
        public void Dispose()
        {
            lock (store._gate)
            {
                store._subscribers.Remove(callback);
            }
        }
    }

    private sealed class Session : IStorageSession, IDocumentSession
    {
        private readonly InMemoryStore _store;
        private bool _completed;
        private bool _wroteOther;

        public Session(InMemoryStore store, string tenantId)
        {
            _store = store;
            TenantId = tenantId;
        }

        public string TenantId { get; }

        public AppendWrite? Append { get; private set; }

        public List<OutboxMessage> Outbox { get; } = [];

        public List<(string Consumer, MessageId Id, InboxResult Result)> Inbox { get; } = [];

        public List<(string Subscription, int Partition, GlobalPosition Position, LeaseToken Lease)> Checkpoints { get; } = [];

        public List<ProcessWrite> Processes { get; } = [];

        public List<DocumentWrite> DocumentWrites { get; } = [];

        public IDocumentSession Documents => this;

        public DbConnection? Connection => null;

        public DbTransaction? Transaction => null;

        public ValueTask<StreamSlice> ReadStreamAsync(StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken)
        {
            return _store.ReadStreamAsync(TenantId, stream, fromInclusive, maxCount, cancellationToken);
        }

        void IStorageSession.Append(StreamId stream, string streamType, ExpectedVersion expected, IReadOnlyList<EventToAppend> events)
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

            if (Append is not null)
            {
                throw new InvalidOperationException($"ES-R2: a session appends to one stream; '{Append.Stream}' is already queued, '{stream}' was requested.");
            }

            Append = new AppendWrite(stream, streamType, expected, [.. events]);
        }

        public void EnqueueOutbox(OutboxMessage message)
        {
            ThrowIfCompleted();
            ArgumentNullException.ThrowIfNull(message);
            Outbox.Add(message);
            _wroteOther = true;
        }

        public ValueTask<InboxEntry?> InboxSeenAsync(string consumer, MessageId messageId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(_store.InboxLookup(consumer, messageId));
        }

        public void RecordInbox(string consumer, MessageId messageId, InboxResult result)
        {
            ThrowIfCompleted();
            ArgumentException.ThrowIfNullOrEmpty(consumer);
            ArgumentNullException.ThrowIfNull(result);
            if (Inbox.Any(i => i.Consumer == consumer && i.Id == messageId))
            {
                throw new DuplicateMessageException(consumer, messageId);
            }

            Inbox.Add((consumer, messageId, result));
            _wroteOther = true;
        }

        public void SaveCheckpoint(string subscription, int partition, GlobalPosition position, LeaseToken lease)
        {
            ThrowIfCompleted();
            ArgumentNullException.ThrowIfNull(lease);
            Checkpoints.Add((subscription, partition, position, lease));
            _wroteOther = true;
        }

        public ValueTask<ProcessState?> LoadProcessStateAsync(string processType, string processId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(_store.ProcessLookup(TenantId, processType, processId));
        }

        public void SaveProcessState(string processType, string processId, long expectedVersion, ReadOnlyMemory<byte> state, bool completed)
        {
            ThrowIfCompleted();
            Processes.Add(new ProcessWrite(processType, processId, expectedVersion, state, completed));
            _wroteOther = true;
        }

        public ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken)
        {
            for (int i = DocumentWrites.Count - 1; i >= 0; i--)
            {
                DocumentWrite write = DocumentWrites[i];
                if (write.Collection == collection && write.Id == id)
                {
                    return ValueTask.FromResult(write.Data is null ? null : new StoredDocument(collection, id, write.Version, write.Data.Value));
                }
            }

            return ValueTask.FromResult(_store.DocumentLookup(TenantId, collection, id));
        }

        public async ValueTask<IReadOnlyList<StoredDocument>> GetManyAsync(string collection, IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(ids);
            var found = new List<StoredDocument>(ids.Count);
            foreach (string id in ids)
            {
                if (await GetAsync(collection, id, cancellationToken).ConfigureAwait(false) is { } document)
                {
                    found.Add(document);
                }
            }

            return found;
        }

        public void Upsert(string collection, string id, long version, ReadOnlyMemory<byte> data)
        {
            ThrowIfCompleted();
            DocumentWrites.Add(new DocumentWrite(collection, id, version, data));
            _wroteOther = true;
        }

        public void Delete(string collection, string id)
        {
            ThrowIfCompleted();
            DocumentWrites.Add(new DocumentWrite(collection, id, 0, null));
            _wroteOther = true;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            // The in-memory store applies a session atomically at commit and has no connection to write through.
            ThrowIfCompleted();
            return ValueTask.CompletedTask;
        }

        public ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken)
        {
            ThrowIfCompleted();
            cancellationToken.ThrowIfCancellationRequested();
            _completed = true;
            return ValueTask.FromResult(_store.Commit(this));
        }

        public ValueTask DisposeAsync()
        {
            _completed = true;
            return ValueTask.CompletedTask;
        }

        private void ThrowIfCompleted()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The storage session is already committed or disposed.");
            }
        }
    }
}

/// <summary>Fault-injection hooks of <see cref="InMemoryStore"/>.</summary>
public sealed class InMemoryFaults
{
    private int _failures;
    private Exception? _failure;

    /// <summary>Makes the next <paramref name="count"/> commits throw <paramref name="exception"/> before applying anything.</summary>
    public void FailNextCommits(int count, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Volatile.Write(ref _failure, exception);
        Volatile.Write(ref _failures, count);
    }

    internal void RaiseBeforeCommit()
    {
        if (Volatile.Read(ref _failures) > 0 && Interlocked.Decrement(ref _failures) >= 0)
        {
            throw Volatile.Read(ref _failure)!;
        }
    }
}
