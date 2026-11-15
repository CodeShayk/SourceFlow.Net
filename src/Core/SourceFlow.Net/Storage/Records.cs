using System.Collections.Frozen;
using System.IO.Hashing;
using System.Text;
using SourceFlow.Messaging;

namespace SourceFlow.Storage;

/// <summary>Envelope attributes stored with each event (doc 05 §2 <c>metadata</c>).</summary>
public sealed record EventMetadata
{
    /// <summary>The conversation id.</summary>
    public CorrelationId CorrelationId { get; init; }

    /// <summary>The message that caused the event.</summary>
    public MessageId? CausationId { get; init; }

    /// <summary>The producing service.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Who caused the event.</summary>
    public string? Actor { get; init; }

    /// <summary>W3C <c>traceparent</c>.</summary>
    public string? TraceParent { get; init; }

    /// <summary>W3C <c>tracestate</c>.</summary>
    public string? TraceState { get; init; }

    /// <summary>User headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <inheritdoc />
    public bool Equals(EventMetadata? other)
    {
        return other is not null && CorrelationId == other.CorrelationId && CausationId == other.CausationId && Source == other.Source
            && Actor == other.Actor && TraceParent == other.TraceParent && TraceState == other.TraceState
            && Headers.Count == other.Headers.Count && Headers.All(h => other.Headers.TryGetValue(h.Key, out string? v) && v == h.Value);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(CorrelationId, CausationId, Source, Actor);
    }
}

/// <summary>An event queued for append.</summary>
/// <param name="EventId">The event's message id (unique across the store).</param>
/// <param name="Type">The logical type name.</param>
/// <param name="TypeVersion">The type version.</param>
/// <param name="Data">The serialized payload.</param>
/// <param name="Metadata">Envelope attributes.</param>
/// <param name="CreatedAt">The decision time (<c>ctx.Now</c>), stored explicitly.</param>
/// <param name="Tags">DCB tags (ADR-021).</param>
public sealed record EventToAppend(
    MessageId EventId,
    string Type,
    int TypeVersion,
    ReadOnlyMemory<byte> Data,
    EventMetadata Metadata,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags);

/// <summary>An event read from the store.</summary>
public sealed record StoredEvent(
    GlobalPosition Position,
    string TenantId,
    StreamId StreamId,
    StreamVersion StreamVersion,
    int Bucket,
    MessageId EventId,
    string Type,
    int TypeVersion,
    ReadOnlyMemory<byte> Data,
    EventMetadata Metadata,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags)
{
    /// <summary>Rebuilds the event's envelope (subject = stream id).</summary>
    public MessageEnvelope ToEnvelope()
    {
        return new MessageEnvelope
        {
            MessageId = EventId,
            Type = Type,
            TypeVersion = TypeVersion,
            Source = Metadata.Source,
            Time = CreatedAt,
            CorrelationId = Metadata.CorrelationId,
            CausationId = Metadata.CausationId,
            Subject = StreamId.Value,
            StreamVersion = StreamVersion.Value,
            TenantId = TenantId,
            Actor = Metadata.Actor,
            TraceParent = Metadata.TraceParent,
            TraceState = Metadata.TraceState,
            Headers = Metadata.Headers,
            Body = Data,
        };
    }

    /// <summary>The projection/handler context of the event.</summary>
    public EventContext ToContext()
    {
        return new EventContext(EventId, StreamId, StreamVersion, Position, CreatedAt, Metadata.CorrelationId, Metadata.CausationId,
            TenantId == MessageEnvelope.DefaultTenant ? null : TenantId);
    }
}

/// <summary>A slice of one stream.</summary>
/// <param name="StreamId">The stream.</param>
/// <param name="CurrentVersion">The stream's current version (<see cref="StreamVersion.None"/> when it does not exist).</param>
/// <param name="Events">The events read, in version order.</param>
/// <param name="IsEndOfStream">True when no later events exist.</param>
public sealed record StreamSlice(StreamId StreamId, StreamVersion CurrentVersion, IReadOnlyList<StoredEvent> Events, bool IsEndOfStream);

/// <summary>Stream information.</summary>
public sealed record StreamInfo(StreamId StreamId, string StreamType, StreamVersion Version, bool IsDeleted, bool IsArchived);

/// <summary>A batch read from the global log.</summary>
/// <param name="Events">Events in commit order, after the requested position and below the safe horizon.</param>
/// <param name="Horizon">The safe horizon at the time of the read: positions at or below it are final.</param>
public sealed record LogBatch(IReadOnlyList<StoredEvent> Events, GlobalPosition Horizon);

/// <summary>The result of a commit.</summary>
/// <param name="StreamVersion">The appended stream's new version, if the session appended.</param>
/// <param name="LastPosition">The global position of the last appended event, if any.</param>
public readonly record struct CommitResult(StreamVersion? StreamVersion, GlobalPosition? LastPosition);

/// <summary>The stored result of a processed message (doc 04 §6).</summary>
public sealed record InboxResult(CommandStatus Status, StreamVersion Version, string? ErrorCode, GlobalPosition Position = default);

/// <summary>An inbox row.</summary>
public sealed record InboxEntry(string Consumer, MessageId MessageId, DateTimeOffset ProcessedAt, InboxResult Result);

/// <summary>A fenced lease (doc 05 §7.4). The token increases every time ownership changes.</summary>
public sealed record LeaseToken(string Name, string Owner, long Token);

/// <summary>How an outbox row is delivered.</summary>
public enum OutboxKind
{
    /// <summary>A command sent to an endpoint.</summary>
    Send = 1,

    /// <summary>An event published to a topic.</summary>
    Publish = 2,

    /// <summary>A delayed message (timer or delayed retry).</summary>
    Scheduled = 3,
}

/// <summary>A message queued in the outbox.</summary>
/// <param name="Envelope">The message.</param>
/// <param name="Destination">The logical endpoint or topic.</param>
/// <param name="Kind">How to deliver it.</param>
/// <param name="DeliverAt">The earliest delivery time.</param>
/// <param name="Partition">The outbox partition, from <see cref="OutboxPartitioning.PartitionOf"/>.</param>
public sealed record OutboxMessage(MessageEnvelope Envelope, string Destination, OutboxKind Kind, DateTimeOffset DeliverAt, int Partition);

/// <summary>A claimed outbox row.</summary>
public sealed record OutboxRecord(long Sequence, OutboxMessage Message, int Attempts, string? LastError, DateTimeOffset CreatedAt)
{
    /// <summary>When the message first became due, kept from its first retry on: the outbox age counts from here (doc 08 §6).</summary>
    public DateTimeOffset? FirstDueAt { get; init; }
}

/// <summary>Persisted process-manager state.</summary>
public sealed record ProcessState(string ProcessType, string ProcessId, long Version, ReadOnlyMemory<byte> State, bool Completed, DateTimeOffset UpdatedAt);

/// <summary>A read-model document with its version (DC-2).</summary>
public sealed record StoredDocument(string Collection, string Id, long Version, ReadOnlyMemory<byte> Data)
{
    /// <summary>
    /// The global position of the last event applied to a cross-stream document (doc 06 §5, CR-31); null for documents keyed
    /// by their source stream, whose version is the guard.
    /// </summary>
    public GlobalPosition? Position { get; init; }
}

/// <summary>A snapshot of aggregate state (doc 05 §8.1).</summary>
public sealed record Snapshot(StreamId StreamId, StreamVersion Version, string StateType, string SchemaHash, ReadOnlyMemory<byte> Data);

/// <summary>The status of a parked message.</summary>
public enum DeadLetterStatus
{
    /// <summary>Parked, awaiting an operator.</summary>
    Parked = 0,

    /// <summary>Replayed to its original destination.</summary>
    Replayed = 1,

    /// <summary>Discarded with a reason.</summary>
    Discarded = 2,
}

/// <summary>A parked message (doc 04 §9.3, GAP-11).</summary>
public sealed record DeadLetter
{
    /// <summary>The dead-letter id.</summary>
    public required Guid Id { get; init; }

    /// <summary>The consumer that failed (endpoint, subscription or outbox).</summary>
    public required string Consumer { get; init; }

    /// <summary>Where it was parked from: <c>receive</c>, <c>subscription</c> or <c>outbox</c>.</summary>
    public required string Source { get; init; }

    /// <summary>The full original envelope.</summary>
    public required MessageEnvelope Envelope { get; init; }

    /// <summary>Where a replay sends the message: the original endpoint or topic.</summary>
    public string? Destination { get; init; }

    /// <summary>True when <see cref="Destination"/> is a topic (publish) rather than an endpoint (send).</summary>
    public bool DestinationIsTopic { get; init; }

    /// <summary>A short reason, for example <c>UnknownMessageType</c>.</summary>
    public required string Reason { get; init; }

    /// <summary>The exception type.</summary>
    public string? ErrorType { get; init; }

    /// <summary>The exception message.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The exception chain.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>Delivery attempts made.</summary>
    public int Attempts { get; init; }

    /// <summary>The first failure time.</summary>
    public DateTimeOffset FirstFailedAt { get; init; }

    /// <summary>The parking time.</summary>
    public DateTimeOffset ParkedAt { get; init; }

    /// <summary>The status.</summary>
    public DeadLetterStatus Status { get; init; }

    /// <summary>When the status last changed.</summary>
    public DateTimeOffset? StatusChangedAt { get; init; }

    /// <summary>The operator note for the last status change.</summary>
    public string? StatusNote { get; init; }
}

/// <summary>A dead-letter query.</summary>
/// <param name="Consumer">Only this consumer.</param>
/// <param name="Reason">Only this reason.</param>
/// <param name="Status">Only this status (default parked; null for any).</param>
/// <param name="Limit">The most rows (newest first).</param>
/// <param name="MessageType">Only this message type (the envelope's <c>type</c>).</param>
/// <param name="ParkedAfter">Only parked at or after this time.</param>
/// <param name="ParkedBefore">Only parked before this time.</param>
public sealed record DeadLetterQuery(
    string? Consumer = null,
    string? Reason = null,
    DeadLetterStatus? Status = DeadLetterStatus.Parked,
    int Limit = 100,
    string? MessageType = null,
    DateTimeOffset? ParkedAfter = null,
    DateTimeOffset? ParkedBefore = null);

/// <summary>A contiguous range of buckets, inclusive (doc 06 §3).</summary>
public readonly record struct BucketRange(int Low, int High)
{
    /// <summary>Every bucket.</summary>
    public static BucketRange All => new(0, Buckets.Count - 1);

    /// <summary>True when <paramref name="bucket"/> is in the range.</summary>
    public bool Contains(int bucket)
    {
        return bucket >= Low && bucket <= High;
    }
}

/// <summary>Bucket math: <c>xxHash32(tenant + '/' + stream) &amp; 1023</c>, computed by the client at append (doc 05 §1.1).</summary>
public static class Buckets
{
    /// <summary>The number of buckets.</summary>
    public const int Count = 1024;

    /// <summary>The bucket of a stream in a tenant.</summary>
    public static int Of(string tenantId, StreamId stream)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        int length = Encoding.UTF8.GetByteCount(tenantId) + 1 + Encoding.UTF8.GetByteCount(stream.Value);
        Span<byte> buffer = length <= 512 ? stackalloc byte[length] : new byte[length];
        int written = Encoding.UTF8.GetBytes(tenantId, buffer);
        buffer[written++] = (byte)'/';
        Encoding.UTF8.GetBytes(stream.Value, buffer[written..]);
        return (int)(XxHash32.HashToUInt32(buffer) & (Count - 1));
    }

    /// <summary>The bucket range of <paramref name="partition"/> out of <paramref name="partitions"/> (a power of two up to 1024).</summary>
    public static BucketRange Range(int partition, int partitions)
    {
        if (partitions < 1 || partitions > Count || (partitions & (partitions - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitions), partitions, "Partitions must be a power of two between 1 and 1024.");
        }

        if (partition < 0 || partition >= partitions)
        {
            throw new ArgumentOutOfRangeException(nameof(partition), partition, $"Partition must be in [0, {partitions}).");
        }

        int size = Count / partitions;
        return new BucketRange(partition * size, ((partition + 1) * size) - 1);
    }
}

/// <summary>Outbox partitioning: <c>hash(partition key) mod partitions</c> (doc 04 §7).</summary>
public static class OutboxPartitioning
{
    /// <summary>The partition of a key; null keys (unordered rows) go to partition 0.</summary>
    public static int PartitionOf(string? partitionKey, int partitions)
    {
        if (partitions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(partitions), partitions, "At least one outbox partition is required.");
        }

        if (string.IsNullOrEmpty(partitionKey))
        {
            return 0;
        }

        return (int)(XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(partitionKey)) % (uint)partitions);
    }
}
