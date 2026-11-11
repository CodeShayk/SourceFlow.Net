using System.Data.Common;

namespace SourceFlow.Storage;

/// <summary>Reads streams (doc 05 §1). Implemented by every storage adapter; verified by the storage conformance kit.</summary>
public interface IEventStore
{
    /// <summary>Reads up to <paramref name="maxCount"/> events of a stream from <paramref name="fromInclusive"/>.</summary>
    ValueTask<StreamSlice> ReadStreamAsync(string tenantId, StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken);

    /// <summary>Gets stream information, or null when the stream does not exist (GAP-15).</summary>
    ValueTask<StreamInfo?> GetStreamInfoAsync(string tenantId, StreamId stream, CancellationToken cancellationToken);

    /// <summary>Marks a stream archived: appends are rejected, reads still work (doc 05 §9).</summary>
    ValueTask ArchiveStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken);

    /// <summary>Soft-deletes a stream: appends are rejected (doc 05 §9).</summary>
    ValueTask DeleteStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken);
}

/// <summary>Reads the global, commit-ordered log below the safe horizon (ADR-010).</summary>
public interface IEventLogReader
{
    /// <summary>Reads events after <paramref name="after"/> (exclusive) in commit order, filtered to <paramref name="buckets"/>.</summary>
    ValueTask<LogBatch> ReadAsync(GlobalPosition after, BucketRange buckets, int maxCount, CancellationToken cancellationToken);

    /// <summary>The safe horizon: every position at or below it is final; no event can later appear there (ADR-010).</summary>
    ValueTask<GlobalPosition> GetSafeHorizonAsync(CancellationToken cancellationToken);
}

/// <summary>Starts storage sessions (one database transaction each).</summary>
public interface IStorageSessionFactory
{
    /// <summary>Begins a session scoped to a tenant.</summary>
    ValueTask<IStorageSession> BeginAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// One unit of work. Writes are queued and flushed in one round trip by <see cref="CommitAsync"/>; only reads that are
/// needed before deciding go to the server earlier. Disposing without committing rolls everything back.
/// ES-R1: the stream append must be the first write. ES-R2: at most one stream per session.
/// </summary>
public interface IStorageSession : IAsyncDisposable
{
    /// <summary>The session's tenant.</summary>
    string TenantId { get; }

    /// <summary>Reads a stream inside the session.</summary>
    ValueTask<StreamSlice> ReadStreamAsync(StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken);

    /// <summary>Queues an append. Conflicts surface as <see cref="ConcurrencyException"/> from <see cref="CommitAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">ES-R1 or ES-R2 is violated.</exception>
    void Append(StreamId stream, string streamType, ExpectedVersion expected, IReadOnlyList<EventToAppend> events);

    /// <summary>Queues an outbox row.</summary>
    void EnqueueOutbox(OutboxMessage message);

    /// <summary>Returns the inbox entry when <paramref name="messageId"/> was already processed by <paramref name="consumer"/>.</summary>
    ValueTask<InboxEntry?> InboxSeenAsync(string consumer, MessageId messageId, CancellationToken cancellationToken);

    /// <summary>Queues an inbox row. A duplicate surfaces as <see cref="DuplicateMessageException"/> at commit.</summary>
    void RecordInbox(string consumer, MessageId messageId, InboxResult result);

    /// <summary>Queues a fenced checkpoint save. A lost lease surfaces as <see cref="LeaseLostException"/> at commit.</summary>
    void SaveCheckpoint(string subscription, int partition, GlobalPosition position, LeaseToken lease);

    /// <summary>Loads process state, or null when the instance does not exist.</summary>
    ValueTask<ProcessState?> LoadProcessStateAsync(string processType, string processId, CancellationToken cancellationToken);

    /// <summary>Queues a process-state save with optimistic concurrency (0 = new instance).</summary>
    void SaveProcessState(string processType, string processId, long expectedVersion, ReadOnlyMemory<byte> state, bool completed);

    /// <summary>Read-model writes in this transaction.</summary>
    IDocumentSession Documents { get; }

    /// <summary>The underlying connection, for EF Core or custom projections; null for in-memory.</summary>
    DbConnection? Connection { get; }

    /// <summary>The underlying transaction; null for in-memory.</summary>
    DbTransaction? Transaction { get; }

    /// <summary>Flushes every queued write atomically.</summary>
    ValueTask<CommitResult> CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Versioned document writes inside a session (doc 05 §10, DC-2).</summary>
public interface IDocumentSession
{
    /// <summary>Gets a document, seeing this session's own queued writes.</summary>
    ValueTask<StoredDocument?> GetAsync(string collection, string id, CancellationToken cancellationToken);

    /// <summary>Queues an upsert. A write older than the stored version is ignored (at-least-once guard, doc 06 §5).</summary>
    void Upsert(string collection, string id, long version, ReadOnlyMemory<byte> data);

    /// <summary>Queues a delete.</summary>
    void Delete(string collection, string id);
}

/// <summary>Reads documents outside a session (query side).</summary>
public interface IDocumentReader
{
    /// <summary>Gets a document with its version.</summary>
    ValueTask<StoredDocument?> GetAsync(string tenantId, string collection, string id, CancellationToken cancellationToken);

    /// <summary>Lists a collection, ordered by id (bounded).</summary>
    ValueTask<IReadOnlyList<StoredDocument>> ListAsync(string tenantId, string collection, int maxCount, CancellationToken cancellationToken);
}

/// <summary>Reads subscription checkpoints.</summary>
public interface ICheckpointStore
{
    /// <summary>The last committed position of a subscription partition.</summary>
    ValueTask<GlobalPosition> GetAsync(string subscription, int partition, CancellationToken cancellationToken);
}

/// <summary>Fenced leases using the store's clock (doc 05 §7.4).</summary>
public interface ILeaseManager
{
    /// <summary>Acquires or renews a lease; returns null when another owner holds it.</summary>
    ValueTask<LeaseToken?> TryAcquireAsync(string name, string owner, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>Releases a lease held by <paramref name="lease"/>'s owner.</summary>
    ValueTask ReleaseAsync(LeaseToken lease, CancellationToken cancellationToken);
}

/// <summary>Outbox relay operations; every call is fenced by the partition lease (doc 05 §7.1).</summary>
public interface IOutboxStore
{
    /// <summary>Claims due rows of a partition; only the head row of each partition key is eligible.</summary>
    ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(int partition, LeaseToken lease, int maxCount, CancellationToken cancellationToken);

    /// <summary>Deletes delivered rows.</summary>
    ValueTask CompleteAsync(LeaseToken lease, IReadOnlyList<long> sequences, CancellationToken cancellationToken);

    /// <summary>Records a failed attempt and reschedules the row.</summary>
    ValueTask RetryAsync(LeaseToken lease, long sequence, DateTimeOffset deliverAt, string error, CancellationToken cancellationToken);

    /// <summary>Counts rows waiting in the outbox (for metrics and idle detection).</summary>
    ValueTask<int> CountAsync(CancellationToken cancellationToken);
}

/// <summary>Parked messages (doc 04 §9.3).</summary>
public interface IDeadLetterStore
{
    /// <summary>Parks a message.</summary>
    ValueTask ParkAsync(DeadLetter deadLetter, CancellationToken cancellationToken);

    /// <summary>Queries parked messages, newest first.</summary>
    ValueTask<IReadOnlyList<DeadLetter>> QueryAsync(DeadLetterQuery query, CancellationToken cancellationToken);

    /// <summary>Gets one dead letter.</summary>
    ValueTask<DeadLetter?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Changes the status of a dead letter (replayed or discarded), with an audit note.</summary>
    ValueTask SetStatusAsync(Guid id, DeadLetterStatus status, string? note, CancellationToken cancellationToken);
}

/// <summary>Aggregate snapshots, written outside the command transaction (doc 05 §8.1).</summary>
public interface ISnapshotStore
{
    /// <summary>Gets the latest snapshot of a stream.</summary>
    ValueTask<Snapshot?> GetAsync(string tenantId, StreamId stream, CancellationToken cancellationToken);

    /// <summary>Saves a snapshot (replacing an older one).</summary>
    ValueTask SaveAsync(string tenantId, Snapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Commit notifications that wake subscriptions and the outbox relay (doc 06 §2).</summary>
public interface IStoreNotifications
{
    /// <summary>Calls <paramref name="onCommit"/> after every commit; dispose to unsubscribe.</summary>
    IDisposable Subscribe(Action onCommit);
}

/// <summary>An append conflicted: the stream was not at the expected version (ADR-008).</summary>
public sealed class ConcurrencyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ConcurrencyException(StreamId stream, ExpectedVersion expected, StreamVersion actual)
        : base($"Stream '{stream}' is at version {actual}, not {expected}.")
    {
        Stream = stream;
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Creates the exception for a non-stream conflict, such as process state.</summary>
    public ConcurrencyException(string message)
        : base(message)
    {
    }

    /// <summary>The stream.</summary>
    public StreamId Stream { get; }

    /// <summary>The expected version.</summary>
    public ExpectedVersion Expected { get; }

    /// <summary>The actual version.</summary>
    public StreamVersion Actual { get; }
}

/// <summary>A message was already recorded in the inbox by the same consumer (ADR-006).</summary>
public sealed class DuplicateMessageException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DuplicateMessageException(string consumer, MessageId messageId)
        : base($"Message {messageId} was already processed by '{consumer}'.")
    {
        Consumer = consumer;
        MessageId = messageId;
    }

    /// <summary>The consumer.</summary>
    public string Consumer { get; }

    /// <summary>The message id.</summary>
    public MessageId MessageId { get; }
}

/// <summary>The lease fencing check failed: another owner took over, or the lease expired (doc 05 §7.3).</summary>
public sealed class LeaseLostException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LeaseLostException(string lease)
        : base($"Lease '{lease}' is no longer held by this owner.")
    {
        Lease = lease;
    }

    /// <summary>The lease name.</summary>
    public string Lease { get; }
}

/// <summary>A stream is deleted or archived and rejects appends (doc 05 §9).</summary>
public sealed class StreamClosedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public StreamClosedException(StreamId stream, string state)
        : base($"Stream '{stream}' is {state} and rejects appends.")
    {
        Stream = stream;
    }

    /// <summary>The stream.</summary>
    public StreamId Stream { get; }
}
