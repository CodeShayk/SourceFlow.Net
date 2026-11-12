using System.Text;
using SourceFlow.Messaging;
using SourceFlow.Storage;

namespace SourceFlow.Testing.Conformance;

/// <summary>A storage adapter under test: every storage port of one fresh, isolated store.</summary>
public interface IStorageFixture : IAsyncDisposable
{
    /// <summary>Stream reads.</summary>
    IEventStore Events { get; }

    /// <summary>Global log reads.</summary>
    IEventLogReader Log { get; }

    /// <summary>Sessions.</summary>
    IStorageSessionFactory Sessions { get; }

    /// <summary>Checkpoint reads.</summary>
    ICheckpointStore Checkpoints { get; }

    /// <summary>Leases.</summary>
    ILeaseManager Leases { get; }

    /// <summary>Outbox relay operations.</summary>
    IOutboxStore Outbox { get; }

    /// <summary>Dead letters.</summary>
    IDeadLetterStore DeadLetters { get; }

    /// <summary>Snapshots.</summary>
    ISnapshotStore Snapshots { get; }

    /// <summary>Document reads.</summary>
    IDocumentReader Documents { get; }

    /// <summary>Commit notifications.</summary>
    IStoreNotifications Notifications { get; }

    /// <summary>Retention cleanup (doc 05 §9); by default the event store when it implements it.</summary>
    IStoreRetention? Retention => Events as IStoreRetention;

    /// <summary>Read-model aliases (doc 06 §6); by default the event store when it implements them.</summary>
    ICollectionAliases? Aliases => Events as ICollectionAliases;

    /// <summary>True when <see cref="AdvanceClockAsync"/> moves the store's clock (in-memory with a fake clock).</summary>
    bool SupportsClockControl { get; }

    /// <summary>Moves the store's clock forward.</summary>
    ValueTask AdvanceClockAsync(TimeSpan by);

    /// <summary>The store's current time.</summary>
    DateTimeOffset Now { get; }
}

/// <summary>
/// The storage conformance kit (SCK, plan 06 §2). A storage adapter is done only when every mandatory case passes.
/// Run each case against a fresh fixture.
/// </summary>
public static class StorageConformance
{
    private const string Tenant = MessageEnvelope.DefaultTenant;
    private static readonly StreamId StreamA = new("sck-a-1");
    private static readonly StreamId StreamB = new("sck-b-2");

    /// <summary>Every case, ordered by id.</summary>
    public static IReadOnlyList<ConformanceCase<IStorageFixture>> Cases { get; } =
    [
        new("SCK-01", "Append NoStream to a new stream assigns versions 1..n", AppendNoStream),
        new("SCK-02", "Exact(v) succeeds; a stale Exact conflicts", AppendExact),
        new("SCK-03", "NoStream on an existing stream conflicts", NoStreamOnExisting),
        new("SCK-04", "Any and StreamExists are compare-and-set on the current version", AnyAndStreamExists),
        new("SCK-05", "A conflict rolls back every queued write", ConflictRollsBackEverything),
        new("SCK-06", "ES-R1: append after another write is rejected", EsR1),
        new("SCK-07", "ES-R2: a second stream in a session is rejected", EsR2),
        new("SCK-08", "Duplicate event id maps to a concurrency conflict", DuplicateEventId),
        new("SCK-09", "1, 3 and 100 events per append keep versions contiguous", ManyEvents),
        new("SCK-10", "Read from a version, bounded; an empty stream reads empty", ReadSlices),
        new("SCK-11", "Streams are isolated", StreamsIsolated),
        new("SCK-12", "Metadata round trips", MetadataRoundTrip),
        new("SCK-13", "Tags round trip", TagsRoundTrip),
        new("SCK-14", "Decision time is stored as the event time", DecisionTime),
        new("SCK-15", "A deleted stream rejects appends", DeletedStream),
        new("SCK-16", "An archived stream rejects appends, stays readable and is reported archived", ArchivedStream),
        new("SCK-17", "Not-found semantics: empty slice and null stream info", NotFound),
        new("SCK-18", "A 1 MB payload round trips", LargePayload),
        new("SCK-19", "Unicode data and metadata round trip", Unicode),
        new("SCK-21", "The log reads in commit order with an exclusive 'after'", LogOrder),
        new("SCK-22", "Bucket range filtering", BucketFilter),
        new("SCK-23", "The safe horizon excludes written but uncommitted events", HorizonExcludesInFlight),
        new("SCK-24", "Gap-freedom under concurrent writers with a checkpointing reader", GapFreedom),
        new("SCK-25", "Per-stream log order equals version order", PerStreamOrder),
        new("SCK-27", "Notifications are raised after commit", Notifications),
        new("SCK-29", "Outbox rows commit atomically with the append", OutboxAtomic),
        new("SCK-30", "Outbox claim returns only the head row per key", OutboxHeadOfKey),
        new("SCK-31", "A failed head blocks its key, not other keys; retry reschedules", OutboxFailureBlocksKey),
        new("SCK-32", "Outbox operations need the partition lease", OutboxNeedsLease),
        new("SCK-33", "Rows due in the future are not claimed", OutboxDeliverAt, RequiresClockControl: true),
        new("SCK-34", "The inbox records the result", InboxRecords),
        new("SCK-35", "A duplicate inbox row fails the commit", InboxDuplicate),
        new("SCK-37", "Retention: expired inbox rows and resolved dead letters are purged in batches", Retention),
        new("SCK-38", "Checkpoints save with a valid lease", CheckpointWithLease),
        new("SCK-39", "A stale lease token rejects the checkpoint", CheckpointStaleToken),
        new("SCK-40", "An expired lease rejects the checkpoint", CheckpointExpired, RequiresClockControl: true),
        new("SCK-41", "Checkpoint and document writes are atomic", CheckpointAndDocumentAtomic),
        new("SCK-42", "Leases: acquire and renew keep the token", LeaseAcquireRenew),
        new("SCK-43", "Leases: a held lease is refused to others", LeaseHeld),
        new("SCK-44", "Leases: steal after expiry increments the token", LeaseSteal, RequiresClockControl: true),
        new("SCK-45", "Leases: release lets another owner acquire", LeaseRelease),
        new("SCK-46", "Process state: save and load", ProcessSaveLoad),
        new("SCK-47", "Process state: concurrent save conflicts", ProcessConflict),
        new("SCK-48", "Process state: completed flag persists", ProcessCompleted),
        new("SCK-49", "Dead letters: park with the full envelope and query", DeadLetterPark),
        new("SCK-50", "Dead letters: mark replayed and discard", DeadLetterStatus),
        new("SCK-51", "Snapshots: save and get the latest", SnapshotSaveGet),
        new("SCK-52", "Snapshots: an older snapshot never replaces a newer one", SnapshotNewerWins),
        new("SCK-53", "Snapshots: state type, schema hash and data round trip", SnapshotFields),
        new("SCK-55", "Snapshots: a 1 MB state round trips", SnapshotLarge),
        new("SCK-56", "Tenancy: the same stream id in two tenants is isolated", TenantStreams),
        new("SCK-57", "Tenancy: documents are tenant scoped", TenantDocuments),
        new("SCK-58", "Documents: read own writes, versions, older writes ignored", DocumentVersions),
        new("SCK-73", "Aliases: the first generation claims once; a switch is a compare-and-set that records the previous target", AliasSwitch),
        new("SCK-74", "Aliases: a requested generation is kept until it is switched in; previous is cleared only when it matches", AliasRequest),
        new("SCK-75", "Aliases: concurrent switches from the same target, exactly one wins", AliasRace),
    ];

    // ------------------------------------------------------------------ helpers

    /// <summary>Creates a test event.</summary>
    public static EventToAppend Event(string type = "sck.happened", string data = "{\"n\":1}", IReadOnlyList<string>? tags = null)
    {
        return new EventToAppend(new MessageId(Guid.CreateVersion7()), type, 1, Encoding.UTF8.GetBytes(data),
            new EventMetadata { Source = "sck", CorrelationId = new CorrelationId("corr") }, DateTimeOffset.UnixEpoch, tags ?? []);
    }

    private static EventToAppend[] Events(int count)
    {
        return Enumerable.Range(1, count).Select(i => Event(data: $"{{\"n\":{i}}}")).ToArray();
    }

    private static async Task<CommitResult> AppendAsync(IStorageFixture f, StreamId stream, ExpectedVersion expected, IReadOnlyList<EventToAppend> events, CancellationToken ct, string tenant = Tenant)
    {
        await using IStorageSession session = await f.Sessions.BeginAsync(tenant, ct).ConfigureAwait(false);
        session.Append(stream, "sck", expected, events);
        return await session.CommitAsync(ct).ConfigureAwait(false);
    }

    // Events at or below the safe horizon are final. A database may hold the horizon back briefly while any writing
    // transaction is in progress (doc 05 §5.4), so log assertions read until the horizon has passed their last commit.
    private static async Task<LogBatch> ReadFinalAsync(IStorageFixture f, GlobalPosition after, BucketRange buckets, int maxCount, GlobalPosition through, CancellationToken ct)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (true)
        {
            LogBatch batch = await f.Log.ReadAsync(after, buckets, maxCount, ct).ConfigureAwait(false);
            if (batch.Horizon >= through || TimeProvider.System.GetUtcNow() > deadline)
            {
                return batch;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, ct).ConfigureAwait(false);
        }
    }

    private static GlobalPosition Max(GlobalPosition a, GlobalPosition? b)
    {
        return b is { } value && value > a ? value : a;
    }

    private static OutboxMessage OutboxRow(string key, int partition = 0, DateTimeOffset? deliverAt = null)
    {
        var envelope = new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "sck.sent",
            TypeVersion = 1,
            Source = "sck",
            Time = DateTimeOffset.UnixEpoch,
            PartitionKey = key,
            Body = Encoding.UTF8.GetBytes("{}"),
        };
        return new OutboxMessage(envelope, "dest", OutboxKind.Send, deliverAt ?? DateTimeOffset.MinValue, partition);
    }

    private static async Task EnqueueAsync(IStorageFixture f, CancellationToken ct, params OutboxMessage[] rows)
    {
        await using IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        foreach (OutboxMessage row in rows)
        {
            session.EnqueueOutbox(row);
        }

        await session.CommitAsync(ct).ConfigureAwait(false);
    }

    private static async Task<LeaseToken> LeaseAsync(IStorageFixture f, string name, CancellationToken ct, string owner = "node-1")
    {
        return Check.NotNull(await f.Leases.TryAcquireAsync(name, owner, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false), $"lease {name}");
    }

    // ------------------------------------------------------------------ appends

    private static async Task AppendNoStream(IStorageFixture f, CancellationToken ct)
    {
        CommitResult result = await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(3), ct).ConfigureAwait(false);

        Check.Equal(new StreamVersion(3), result.StreamVersion!.Value, "new version");
        StreamSlice slice = await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 10, ct).ConfigureAwait(false);
        Check.SequenceEqual([1L, 2L, 3L], slice.Events.Select(e => e.StreamVersion.Value), "versions");
    }

    private static async Task AppendExact(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(2), ct).ConfigureAwait(false);
        await AppendAsync(f, StreamA, ExpectedVersion.Exact(new StreamVersion(2)), Events(1), ct).ConfigureAwait(false);

        await Check.ThrowsAsync<ConcurrencyException>(
            () => AppendAsync(f, StreamA, ExpectedVersion.Exact(new StreamVersion(2)), Events(1), ct), "stale Exact").ConfigureAwait(false);
        StreamInfo info = Check.NotNull(await f.Events.GetStreamInfoAsync(Tenant, StreamA, ct).ConfigureAwait(false), "info");
        Check.Equal(new StreamVersion(3), info.Version, "version after conflict");
    }

    private static async Task NoStreamOnExisting(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);

        await Check.ThrowsAsync<ConcurrencyException>(() => AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(1), ct), "NoStream on existing").ConfigureAwait(false);
    }

    private static async Task AnyAndStreamExists(IStorageFixture f, CancellationToken ct)
    {
        await Check.ThrowsAsync<ConcurrencyException>(() => AppendAsync(f, StreamA, ExpectedVersion.StreamExists, Events(1), ct), "StreamExists on a new stream").ConfigureAwait(false);
        await AppendAsync(f, StreamA, ExpectedVersion.Any, Events(1), ct).ConfigureAwait(false);
        CommitResult result = await AppendAsync(f, StreamA, ExpectedVersion.StreamExists, Events(1), ct).ConfigureAwait(false);
        Check.Equal(new StreamVersion(2), result.StreamVersion!.Value, "version");
    }

    private static async Task ConflictRollsBackEverything(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);
        LeaseToken lease = await LeaseAsync(f, "sub:sck:0", ct).ConfigureAwait(false);
        int outboxBefore = await f.Outbox.CountAsync(ct).ConfigureAwait(false);

        await Check.ThrowsAsync<ConcurrencyException>(async () =>
        {
            await using IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
            session.Append(StreamA, "sck", ExpectedVersion.NoStream, Events(1));
            session.EnqueueOutbox(OutboxRow("k"));
            session.RecordInbox("sck", new MessageId(Guid.CreateVersion7()), new InboxResult(CommandStatus.Accepted, new StreamVersion(1), null));
            session.Documents.Upsert("docs", "1", 1, Encoding.UTF8.GetBytes("{}"));
            session.SaveCheckpoint("sck", 0, new GlobalPosition(99, 0), lease);
            session.SaveProcessState("pm", "1", 0, Encoding.UTF8.GetBytes("{}"), completed: false);
            await session.CommitAsync(ct).ConfigureAwait(false);
        }, "conflicting commit").ConfigureAwait(false);

        Check.Equal(outboxBefore, await f.Outbox.CountAsync(ct).ConfigureAwait(false), "outbox untouched");
        Check.Null(await f.Documents.GetAsync(Tenant, "docs", "1", ct).ConfigureAwait(false), "document untouched");
        Check.Equal(GlobalPosition.Start, await f.Checkpoints.GetAsync("sck", 0, ct).ConfigureAwait(false), "checkpoint untouched");
        await using IStorageSession check = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        Check.Null(await check.LoadProcessStateAsync("pm", "1", ct).ConfigureAwait(false), "process state untouched");
    }

    private static async Task EsR1(IStorageFixture f, CancellationToken ct)
    {
        await using IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        session.EnqueueOutbox(OutboxRow("k"));

        Check.Throws<InvalidOperationException>(() => session.Append(StreamA, "sck", ExpectedVersion.NoStream, Events(1)), "append after another write");
    }

    private static async Task EsR2(IStorageFixture f, CancellationToken ct)
    {
        await using IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        session.Append(StreamA, "sck", ExpectedVersion.NoStream, Events(1));

        Check.Throws<InvalidOperationException>(() => session.Append(StreamB, "sck", ExpectedVersion.NoStream, Events(1)), "second stream");
    }

    private static async Task DuplicateEventId(IStorageFixture f, CancellationToken ct)
    {
        EventToAppend e = Event();
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, [e], ct).ConfigureAwait(false);

        await Check.ThrowsAsync<ConcurrencyException>(() => AppendAsync(f, StreamB, ExpectedVersion.NoStream, [e], ct), "duplicate event id").ConfigureAwait(false);
    }

    private static async Task ManyEvents(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);
        await AppendAsync(f, StreamA, ExpectedVersion.Exact(new StreamVersion(1)), Events(3), ct).ConfigureAwait(false);
        await AppendAsync(f, StreamA, ExpectedVersion.Exact(new StreamVersion(4)), Events(100), ct).ConfigureAwait(false);

        StreamSlice slice = await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 1000, ct).ConfigureAwait(false);
        Check.SequenceEqual(Enumerable.Range(1, 104).Select(i => (long)i), slice.Events.Select(e => e.StreamVersion.Value), "contiguous versions");
    }

    private static async Task ReadSlices(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(10), ct).ConfigureAwait(false);

        StreamSlice slice = await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(4), 3, ct).ConfigureAwait(false);
        Check.SequenceEqual([4L, 5L, 6L], slice.Events.Select(e => e.StreamVersion.Value), "bounded slice");
        Check.True(!slice.IsEndOfStream, "more events exist");
        Check.Equal(new StreamVersion(10), slice.CurrentVersion, "current version");
        StreamSlice tail = await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(9), 10, ct).ConfigureAwait(false);
        Check.True(tail.IsEndOfStream, "end of stream");
        StreamSlice beyond = await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(11), 10, ct).ConfigureAwait(false);
        Check.Equal(0, beyond.Events.Count, "nothing beyond the end");
    }

    private static async Task StreamsIsolated(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(2), ct).ConfigureAwait(false);
        await AppendAsync(f, StreamB, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);

        StreamSlice b = await f.Events.ReadStreamAsync(Tenant, StreamB, new StreamVersion(1), 10, ct).ConfigureAwait(false);
        Check.Equal(1, b.Events.Count, "stream B events");
        Check.True(b.Events.All(e => e.StreamId == StreamB), "only stream B");
    }

    private static async Task MetadataRoundTrip(IStorageFixture f, CancellationToken ct)
    {
        var metadata = new EventMetadata
        {
            CorrelationId = new CorrelationId("c-1"),
            CausationId = new MessageId(Guid.CreateVersion7()),
            Source = "accounts",
            Actor = "user:42",
            TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            TraceState = "a=b",
            Headers = new Dictionary<string, string> { ["x"] = "y" },
        };
        EventToAppend e = Event() with { Metadata = metadata };
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, [e], ct).ConfigureAwait(false);

        StoredEvent stored = (await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 1, ct).ConfigureAwait(false)).Events[0];
        Check.Equal(metadata, stored.Metadata, "metadata");
        Check.Equal(e.EventId, stored.EventId, "event id");
    }

    private static async Task TagsRoundTrip(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, [Event(tags: ["account:1", "branch:7"])], ct).ConfigureAwait(false);

        StoredEvent stored = (await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 1, ct).ConfigureAwait(false)).Events[0];
        Check.SequenceEqual(["account:1", "branch:7"], stored.Tags, "tags");
    }

    private static async Task DecisionTime(IStorageFixture f, CancellationToken ct)
    {
        var at = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 123, TimeSpan.Zero);
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, [Event() with { CreatedAt = at }], ct).ConfigureAwait(false);

        StoredEvent stored = (await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 1, ct).ConfigureAwait(false)).Events[0];
        Check.Equal(at, stored.CreatedAt, "created at");
    }

    private static async Task DeletedStream(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);
        await f.Events.DeleteStreamAsync(Tenant, StreamA, ct).ConfigureAwait(false);

        await Check.ThrowsAsync<StreamClosedException>(() => AppendAsync(f, StreamA, ExpectedVersion.Any, Events(1), ct), "append to deleted").ConfigureAwait(false);
        Check.True((await f.Events.GetStreamInfoAsync(Tenant, StreamA, ct).ConfigureAwait(false))!.IsDeleted, "deleted flag");
    }

    private static async Task ArchivedStream(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(2), ct).ConfigureAwait(false);
        await f.Events.ArchiveStreamAsync(Tenant, StreamA, ct).ConfigureAwait(false);

        await Check.ThrowsAsync<StreamClosedException>(() => AppendAsync(f, StreamA, ExpectedVersion.Any, Events(1), ct), "append to archived").ConfigureAwait(false);
        StreamSlice slice = await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 10, ct).ConfigureAwait(false);
        Check.Equal(2, slice.Events.Count, "still readable");

        // Stream ids are ordinal: a stream whose id differs only in case is another, open stream.
        var upper = new StreamId(StreamA.Value.ToUpperInvariant());
        await AppendAsync(f, upper, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);
        IReadOnlySet<StreamId> archived = await f.Events.GetArchivedStreamsAsync(Tenant, [StreamA, StreamB, upper], ct).ConfigureAwait(false);
        Check.Equal(1, archived.Count, "one archived stream");
        Check.True(archived.Contains(StreamA), "the archived stream is reported");
        Check.Equal(0, (await f.Events.GetArchivedStreamsAsync("other-tenant", [StreamA], ct).ConfigureAwait(false)).Count, "archiving is per tenant");
    }

    private static async Task NotFound(IStorageFixture f, CancellationToken ct)
    {
        StreamSlice slice = await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 10, ct).ConfigureAwait(false);

        Check.Equal(0, slice.Events.Count, "empty");
        Check.Equal(StreamVersion.None, slice.CurrentVersion, "no version");
        Check.Null(await f.Events.GetStreamInfoAsync(Tenant, StreamA, ct).ConfigureAwait(false), "no info");
    }

    private static async Task LargePayload(IStorageFixture f, CancellationToken ct)
    {
        string big = "{\"blob\":\"" + new string('x', 1024 * 1024) + "\"}";
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, [Event(data: big)], ct).ConfigureAwait(false);

        StoredEvent stored = (await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 1, ct).ConfigureAwait(false)).Events[0];
        Check.Equal(big, Encoding.UTF8.GetString(stored.Data.Span), "payload");
    }

    private static async Task Unicode(IStorageFixture f, CancellationToken ct)
    {
        const string text = "{\"name\":\"Zoë 名前 😀 \\u0000-safe\"}";
        EventToAppend e = Event(data: text) with { Metadata = new EventMetadata { Source = "sck", Actor = "Ünïcødé 用户" } };
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, [e], ct).ConfigureAwait(false);

        StoredEvent stored = (await f.Events.ReadStreamAsync(Tenant, StreamA, new StreamVersion(1), 1, ct).ConfigureAwait(false)).Events[0];
        Check.Equal(text, Encoding.UTF8.GetString(stored.Data.Span), "data");
        Check.Equal("Ünïcødé 用户", stored.Metadata.Actor, "actor");
    }

    // ------------------------------------------------------------------ global log

    private static async Task LogOrder(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(2), ct).ConfigureAwait(false);
        await AppendAsync(f, StreamB, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);
        CommitResult last = await AppendAsync(f, StreamA, ExpectedVersion.Exact(new StreamVersion(2)), Events(1), ct).ConfigureAwait(false);

        LogBatch all = await ReadFinalAsync(f, GlobalPosition.Start, BucketRange.All, 100, last.LastPosition!.Value, ct).ConfigureAwait(false);
        Check.SequenceEqual(["sck-a-1:1", "sck-a-1:2", "sck-b-2:1", "sck-a-1:3"], all.Events.Select(e => $"{e.StreamId}:{e.StreamVersion}"), "commit order");
        Check.True(all.Events.Zip(all.Events.Skip(1)).All(p => p.First.Position < p.Second.Position), "strictly increasing positions");

        LogBatch after = await f.Log.ReadAsync(all.Events[1].Position, BucketRange.All, 100, ct).ConfigureAwait(false);
        Check.Equal(2, after.Events.Count, "exclusive after");
        Check.Equal(all.Events[2].EventId, after.Events[0].EventId, "first after");
    }

    private static async Task BucketFilter(IStorageFixture f, CancellationToken ct)
    {
        var streams = Enumerable.Range(0, 40).Select(i => new StreamId($"sck-bucket-{i}")).ToArray();
        GlobalPosition last = GlobalPosition.Start;
        foreach (StreamId stream in streams)
        {
            last = Max(last, (await AppendAsync(f, stream, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false)).LastPosition);
        }

        int seen = 0;
        for (int p = 0; p < 4; p++)
        {
            BucketRange range = Buckets.Range(p, 4);
            LogBatch batch = await ReadFinalAsync(f, GlobalPosition.Start, range, 100, last, ct).ConfigureAwait(false);
            Check.True(batch.Events.All(e => range.Contains(e.Bucket) && e.Bucket == Buckets.Of(Tenant, e.StreamId)), "bucket filter");
            seen += batch.Events.Count;
        }

        Check.Equal(streams.Length, seen, "every event in exactly one partition");
    }

    private static async Task GapFreedom(IStorageFixture f, CancellationToken ct)
    {
        const int writers = 32;
        const int perWriter = 5;
        var seen = new HashSet<MessageId>();
        GlobalPosition checkpoint = GlobalPosition.Start;
        GlobalPosition last = GlobalPosition.Start;
        var lastGate = new Lock();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                LogBatch batch = await f.Log.ReadAsync(checkpoint, BucketRange.All, 50, ct).ConfigureAwait(false);
                foreach (StoredEvent e in batch.Events)
                {
                    Check.True(seen.Add(e.EventId), "each event seen once");
                    checkpoint = e.Position;
                }

                await Task.Yield();
            }
        }, ct);

        var random = new Random(24);
        int[] yields = Enumerable.Range(0, writers).Select(_ => random.Next(0, 5)).ToArray();
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            var stream = new StreamId($"sck-gap-{w}");
            for (int i = 0; i < perWriter; i++)
            {
                for (int y = 0; y < yields[w]; y++)
                {
                    await Task.Yield();
                }

                CommitResult committed = await AppendAsync(f, stream, ExpectedVersion.Exact(new StreamVersion(i)), Events(1), ct).ConfigureAwait(false);
                lock (lastGate)
                {
                    last = Max(last, committed.LastPosition);
                }
            }
        }, ct))).ConfigureAwait(false);

        await stop.CancelAsync().ConfigureAwait(false);
        await reader.ConfigureAwait(false);
        LogBatch rest = await ReadFinalAsync(f, checkpoint, BucketRange.All, 10_000, last, ct).ConfigureAwait(false);
        foreach (StoredEvent e in rest.Events)
        {
            Check.True(seen.Add(e.EventId), "each event seen once");
        }

        Check.Equal(writers * perWriter, seen.Count, "every committed event seen");
    }

    private static async Task HorizonExcludesInFlight(IStorageFixture f, CancellationToken ct)
    {
        CommitResult first = await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);
        await using IStorageSession inFlight = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        inFlight.Append(StreamB, "sck", ExpectedVersion.NoStream, Events(1));
        await inFlight.FlushAsync(ct).ConfigureAwait(false);   // written inside the transaction, not committed

        LogBatch during = await ReadFinalAsync(f, GlobalPosition.Start, BucketRange.All, 100, first.LastPosition!.Value, ct).ConfigureAwait(false);
        Check.True(during.Events.All(e => e.StreamId != StreamB), "an uncommitted event is invisible");
        CommitResult second = await inFlight.CommitAsync(ct).ConfigureAwait(false);

        Check.True(second.LastPosition!.Value > during.Horizon, "a later commit lands above the earlier horizon (nothing appears below it)");
        LogBatch after = await ReadFinalAsync(f, GlobalPosition.Start, BucketRange.All, 100, second.LastPosition!.Value, ct).ConfigureAwait(false);
        Check.True(after.Events.Any(e => e.StreamId == StreamB), "visible once committed");
    }

    private static async Task PerStreamOrder(IStorageFixture f, CancellationToken ct)
    {
        GlobalPosition last = GlobalPosition.Start;
        var lastGate = new Lock();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < 10; i++)
            {
                while (true)
                {
                    StreamInfo? info = await f.Events.GetStreamInfoAsync(Tenant, StreamA, ct).ConfigureAwait(false);
                    try
                    {
                        CommitResult committed = await AppendAsync(f, StreamA, ExpectedVersion.Exact(info?.Version ?? StreamVersion.None), Events(1), ct).ConfigureAwait(false);
                        lock (lastGate)
                        {
                            last = Max(last, committed.LastPosition);
                        }

                        break;
                    }
                    catch (ConcurrencyException)
                    {
                        await Task.Yield();
                    }
                }
            }
        }, ct))).ConfigureAwait(false);

        LogBatch log = await ReadFinalAsync(f, GlobalPosition.Start, BucketRange.All, 1000, last, ct).ConfigureAwait(false);
        Check.SequenceEqual(Enumerable.Range(1, 80).Select(i => (long)i), log.Events.Select(e => e.StreamVersion.Value), "log order = version order");
    }

    private static async Task Notifications(IStorageFixture f, CancellationToken ct)
    {
        int signals = 0;
        using (f.Notifications.Subscribe(() => Interlocked.Increment(ref signals)))
        {
            await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(1), ct).ConfigureAwait(false);
        }

        Check.True(Volatile.Read(ref signals) >= 1, "notified after commit");
    }

    // ------------------------------------------------------------------ outbox

    private static async Task OutboxAtomic(IStorageFixture f, CancellationToken ct)
    {
        await using (IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            session.Append(StreamA, "sck", ExpectedVersion.NoStream, Events(1));
            session.EnqueueOutbox(OutboxRow("k"));

            // Disposed without commit: nothing is written.
        }

        Check.Equal(0, await f.Outbox.CountAsync(ct).ConfigureAwait(false), "rolled back");
        await using IStorageSession committed = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        committed.Append(StreamA, "sck", ExpectedVersion.NoStream, Events(1));
        committed.EnqueueOutbox(OutboxRow("k"));
        await committed.CommitAsync(ct).ConfigureAwait(false);
        Check.Equal(1, await f.Outbox.CountAsync(ct).ConfigureAwait(false), "committed with the append");
    }

    private static async Task OutboxHeadOfKey(IStorageFixture f, CancellationToken ct)
    {
        await EnqueueAsync(f, ct, OutboxRow("a"), OutboxRow("a"), OutboxRow("b"), OutboxRow("a")).ConfigureAwait(false);
        LeaseToken lease = await LeaseAsync(f, "outbox:0", ct).ConfigureAwait(false);

        IReadOnlyList<OutboxRecord> claimed = await f.Outbox.ClaimAsync(0, lease, 10, ct).ConfigureAwait(false);

        Check.SequenceEqual(["a", "b"], claimed.Select(r => r.Message.Envelope.PartitionKey!), "one head per key");
        await f.Outbox.CompleteAsync(lease, [claimed[0].Sequence], ct).ConfigureAwait(false);
        IReadOnlyList<OutboxRecord> next = await f.Outbox.ClaimAsync(0, lease, 10, ct).ConfigureAwait(false);
        Check.SequenceEqual(["a", "b"], next.Select(r => r.Message.Envelope.PartitionKey!), "next head of a");
        Check.True(next[0].Sequence > claimed[0].Sequence, "in sequence order");
    }

    private static async Task OutboxFailureBlocksKey(IStorageFixture f, CancellationToken ct)
    {
        await EnqueueAsync(f, ct, OutboxRow("a"), OutboxRow("a"), OutboxRow("b")).ConfigureAwait(false);
        LeaseToken lease = await LeaseAsync(f, "outbox:0", ct).ConfigureAwait(false);
        IReadOnlyList<OutboxRecord> claimed = await f.Outbox.ClaimAsync(0, lease, 10, ct).ConfigureAwait(false);

        await f.Outbox.RetryAsync(lease, claimed[0].Sequence, f.Now.AddHours(1), "broker down", ct).ConfigureAwait(false);
        IReadOnlyList<OutboxRecord> after = await f.Outbox.ClaimAsync(0, lease, 10, ct).ConfigureAwait(false);

        Check.SequenceEqual(["b"], after.Select(r => r.Message.Envelope.PartitionKey!), "a is blocked by its failed head, b is not");
    }

    private static async Task OutboxNeedsLease(IStorageFixture f, CancellationToken ct)
    {
        await EnqueueAsync(f, ct, OutboxRow("a")).ConfigureAwait(false);
        var forged = new LeaseToken("outbox:0", "intruder", 42);

        await Check.ThrowsAsync<LeaseLostException>(() => f.Outbox.ClaimAsync(0, forged, 10, ct).AsTask(), "claim without lease").ConfigureAwait(false);
        await Check.ThrowsAsync<LeaseLostException>(() => f.Outbox.CompleteAsync(forged, [1], ct).AsTask(), "complete without lease").ConfigureAwait(false);
    }

    private static async Task OutboxDeliverAt(IStorageFixture f, CancellationToken ct)
    {
        await EnqueueAsync(f, ct, OutboxRow("timer", deliverAt: f.Now.AddMinutes(5))).ConfigureAwait(false);
        LeaseToken lease = await LeaseAsync(f, "outbox:0", ct).ConfigureAwait(false);

        Check.Equal(0, (await f.Outbox.ClaimAsync(0, lease, 10, ct).ConfigureAwait(false)).Count, "not due yet");
        await f.AdvanceClockAsync(TimeSpan.FromMinutes(6)).ConfigureAwait(false);
        lease = await LeaseAsync(f, "outbox:0", ct).ConfigureAwait(false);
        Check.Equal(1, (await f.Outbox.ClaimAsync(0, lease, 10, ct).ConfigureAwait(false)).Count, "due");
    }

    // ------------------------------------------------------------------ inbox

    private static async Task InboxRecords(IStorageFixture f, CancellationToken ct)
    {
        var id = new MessageId(Guid.CreateVersion7());
        var result = new InboxResult(CommandStatus.Rejected, new StreamVersion(4), "account.not_open");
        await using (IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            Check.Null(await session.InboxSeenAsync("accounts", id, ct).ConfigureAwait(false), "not seen yet");
            session.RecordInbox("accounts", id, result);
            await session.CommitAsync(ct).ConfigureAwait(false);
        }

        await using IStorageSession next = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        InboxEntry entry = Check.NotNull(await next.InboxSeenAsync("accounts", id, ct).ConfigureAwait(false), "seen");
        Check.Equal(result, entry.Result, "stored result");
        Check.Null(await next.InboxSeenAsync("other-consumer", id, ct).ConfigureAwait(false), "per consumer");
    }

    private static async Task InboxDuplicate(IStorageFixture f, CancellationToken ct)
    {
        var id = new MessageId(Guid.CreateVersion7());
        var result = new InboxResult(CommandStatus.Accepted, new StreamVersion(1), null);
        await using IStorageSession first = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        await using IStorageSession second = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        first.RecordInbox("accounts", id, result);
        second.RecordInbox("accounts", id, result);
        await first.CommitAsync(ct).ConfigureAwait(false);

        await Check.ThrowsAsync<DuplicateMessageException>(() => second.CommitAsync(ct).AsTask(), "duplicate at commit").ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ checkpoints and leases

    private static async Task Retention(IStorageFixture f, CancellationToken ct)
    {
        IStoreRetention retention = Check.NotNull(f.Retention, "retention port");
        MessageId[] ids = [.. Enumerable.Range(0, 3).Select(_ => new MessageId(Guid.CreateVersion7()))];
        foreach (MessageId id in ids)
        {
            await using IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
            session.RecordInbox("sck", id, new InboxResult(CommandStatus.Accepted, new StreamVersion(1), null));
            await session.CommitAsync(ct).ConfigureAwait(false);
        }

        Check.Equal(0, await retention.PurgeInboxAsync(f.Now.AddMinutes(-5), 2, ct).ConfigureAwait(false), "recent inbox rows kept");
        Check.Equal(3, await retention.PurgeInboxAsync(f.Now.AddMinutes(5), 2, ct).ConfigureAwait(false), "expired inbox rows purged across batches");
        await using (IStorageSession check = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            Check.Null(await check.InboxSeenAsync("sck", ids[0], ct).ConfigureAwait(false), "purged row gone");
        }

        DeadLetter parked = SampleDeadLetter();
        DeadLetter discarded = SampleDeadLetter();
        await f.DeadLetters.ParkAsync(parked, ct).ConfigureAwait(false);
        await f.DeadLetters.ParkAsync(discarded, ct).ConfigureAwait(false);
        await f.DeadLetters.SetStatusAsync(discarded.Id, Storage.DeadLetterStatus.Discarded, "obsolete", ct).ConfigureAwait(false);

        Check.Equal(0, await retention.PurgeDeadLettersAsync(f.Now.AddMinutes(-5), 10, ct).ConfigureAwait(false), "recently resolved kept");
        Check.Equal(1, await retention.PurgeDeadLettersAsync(f.Now.AddMinutes(5), 10, ct).ConfigureAwait(false), "resolved dead letter purged");
        Check.NotNull(await f.DeadLetters.GetAsync(parked.Id, ct).ConfigureAwait(false), "parked dead letters are never purged");
        Check.Null(await f.DeadLetters.GetAsync(discarded.Id, ct).ConfigureAwait(false), "discarded one gone");
    }

    private static async Task CheckpointWithLease(IStorageFixture f, CancellationToken ct)
    {
        LeaseToken lease = await LeaseAsync(f, "sub:p:0", ct).ConfigureAwait(false);
        await using (IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            session.SaveCheckpoint("p", 0, new GlobalPosition(7, 2), lease);
            await session.CommitAsync(ct).ConfigureAwait(false);
        }

        Check.Equal(new GlobalPosition(7, 2), await f.Checkpoints.GetAsync("p", 0, ct).ConfigureAwait(false), "checkpoint");
    }

    private static async Task CheckpointStaleToken(IStorageFixture f, CancellationToken ct)
    {
        LeaseToken lease = await LeaseAsync(f, "sub:p:0", ct).ConfigureAwait(false);
        await using IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        session.SaveCheckpoint("p", 0, new GlobalPosition(7, 2), lease with { Token = lease.Token - 1 });

        await Check.ThrowsAsync<LeaseLostException>(() => session.CommitAsync(ct).AsTask(), "stale token").ConfigureAwait(false);
    }

    private static async Task CheckpointExpired(IStorageFixture f, CancellationToken ct)
    {
        LeaseToken lease = await LeaseAsync(f, "sub:p:0", ct).ConfigureAwait(false);
        await f.AdvanceClockAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        await using IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        session.SaveCheckpoint("p", 0, new GlobalPosition(7, 2), lease);

        await Check.ThrowsAsync<LeaseLostException>(() => session.CommitAsync(ct).AsTask(), "expired lease").ConfigureAwait(false);
    }

    private static async Task CheckpointAndDocumentAtomic(IStorageFixture f, CancellationToken ct)
    {
        LeaseToken lease = await LeaseAsync(f, "sub:p:0", ct).ConfigureAwait(false);
        await using IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        session.Documents.Upsert("docs", "1", 1, Encoding.UTF8.GetBytes("{\"a\":1}"));
        session.SaveCheckpoint("p", 0, new GlobalPosition(7, 2), lease with { Token = lease.Token + 1 });

        await Check.ThrowsAsync<LeaseLostException>(() => session.CommitAsync(ct).AsTask(), "fenced").ConfigureAwait(false);
        Check.Null(await f.Documents.GetAsync(Tenant, "docs", "1", ct).ConfigureAwait(false), "document rolled back with the checkpoint");
    }

    private static async Task LeaseAcquireRenew(IStorageFixture f, CancellationToken ct)
    {
        LeaseToken first = await LeaseAsync(f, "l", ct).ConfigureAwait(false);
        LeaseToken renewed = await LeaseAsync(f, "l", ct).ConfigureAwait(false);

        Check.Equal(first.Token, renewed.Token, "renewal keeps the token");
    }

    private static async Task LeaseHeld(IStorageFixture f, CancellationToken ct)
    {
        await LeaseAsync(f, "l", ct).ConfigureAwait(false);

        Check.Null(await f.Leases.TryAcquireAsync("l", "node-2", TimeSpan.FromSeconds(30), ct).ConfigureAwait(false), "held by node-1");
    }

    private static async Task LeaseSteal(IStorageFixture f, CancellationToken ct)
    {
        LeaseToken first = await LeaseAsync(f, "l", ct).ConfigureAwait(false);
        await f.AdvanceClockAsync(TimeSpan.FromSeconds(31)).ConfigureAwait(false);

        LeaseToken stolen = await LeaseAsync(f, "l", ct, owner: "node-2").ConfigureAwait(false);
        Check.True(stolen.Token > first.Token, "new owner gets a higher token");
    }

    private static async Task LeaseRelease(IStorageFixture f, CancellationToken ct)
    {
        LeaseToken first = await LeaseAsync(f, "l", ct).ConfigureAwait(false);
        await f.Leases.ReleaseAsync(first, ct).ConfigureAwait(false);

        LeaseToken second = await LeaseAsync(f, "l", ct, owner: "node-2").ConfigureAwait(false);
        Check.True(second.Token > first.Token, "token increases on handover");
    }

    // ------------------------------------------------------------------ process state

    private static async Task ProcessSaveLoad(IStorageFixture f, CancellationToken ct)
    {
        await using (IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            session.SaveProcessState("transfer", "t1", 0, Encoding.UTF8.GetBytes("{\"stage\":1}"), completed: false);
            await session.CommitAsync(ct).ConfigureAwait(false);
        }

        await using IStorageSession read = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        ProcessState state = Check.NotNull(await read.LoadProcessStateAsync("transfer", "t1", ct).ConfigureAwait(false), "state");
        Check.Equal(1L, state.Version, "version");
        Check.Equal("{\"stage\":1}", Encoding.UTF8.GetString(state.State.Span), "state data");
    }

    private static async Task ProcessConflict(IStorageFixture f, CancellationToken ct)
    {
        await using (IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            session.SaveProcessState("transfer", "t1", 0, Encoding.UTF8.GetBytes("{}"), completed: false);
            await session.CommitAsync(ct).ConfigureAwait(false);
        }

        await using IStorageSession stale = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        stale.SaveProcessState("transfer", "t1", 0, Encoding.UTF8.GetBytes("{}"), completed: false);
        await Check.ThrowsAsync<ConcurrencyException>(() => stale.CommitAsync(ct).AsTask(), "stale process version").ConfigureAwait(false);
    }

    private static async Task ProcessCompleted(IStorageFixture f, CancellationToken ct)
    {
        await using (IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            session.SaveProcessState("transfer", "t1", 0, Encoding.UTF8.GetBytes("{}"), completed: true);
            await session.CommitAsync(ct).ConfigureAwait(false);
        }

        await using IStorageSession read = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        Check.True((await read.LoadProcessStateAsync("transfer", "t1", ct).ConfigureAwait(false))!.Completed, "completed");
    }

    // ------------------------------------------------------------------ dead letters, snapshots, tenancy, documents

    private static DeadLetter SampleDeadLetter(string reason = "UnknownMessageType")
    {
        return new DeadLetter
        {
            Id = Guid.CreateVersion7(),
            Consumer = "accounts",
            Source = "receive",
            Envelope = OutboxRow("k").Envelope with { Headers = new Dictionary<string, string> { ["h"] = "v" } },
            Reason = reason,
            ErrorType = "System.InvalidOperationException",
            ErrorMessage = "boom",
            Attempts = 3,
            FirstFailedAt = DateTimeOffset.UnixEpoch,
            ParkedAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
        };
    }

    private static async Task DeadLetterPark(IStorageFixture f, CancellationToken ct)
    {
        DeadLetter parked = SampleDeadLetter();
        await f.DeadLetters.ParkAsync(parked, ct).ConfigureAwait(false);
        await f.DeadLetters.ParkAsync(SampleDeadLetter("Exhausted"), ct).ConfigureAwait(false);

        DeadLetter loaded = Check.NotNull(await f.DeadLetters.GetAsync(parked.Id, ct).ConfigureAwait(false), "get");
        Check.Equal(parked.Envelope, loaded.Envelope, "full envelope");
        IReadOnlyList<DeadLetter> unknown = await f.DeadLetters.QueryAsync(new DeadLetterQuery(Reason: "UnknownMessageType"), ct).ConfigureAwait(false);
        Check.Equal(1, unknown.Count, "query by reason");
    }

    private static async Task DeadLetterStatus(IStorageFixture f, CancellationToken ct)
    {
        DeadLetter a = SampleDeadLetter();
        DeadLetter b = SampleDeadLetter();
        await f.DeadLetters.ParkAsync(a, ct).ConfigureAwait(false);
        await f.DeadLetters.ParkAsync(b, ct).ConfigureAwait(false);

        await f.DeadLetters.SetStatusAsync(a.Id, Storage.DeadLetterStatus.Replayed, "fixed", ct).ConfigureAwait(false);
        await f.DeadLetters.SetStatusAsync(b.Id, Storage.DeadLetterStatus.Discarded, "obsolete", ct).ConfigureAwait(false);

        Check.Equal(0, (await f.DeadLetters.QueryAsync(new DeadLetterQuery(), ct).ConfigureAwait(false)).Count, "nothing parked");
        Check.Equal("obsolete", (await f.DeadLetters.GetAsync(b.Id, ct).ConfigureAwait(false))!.StatusNote, "audit note");
    }

    private static async Task SnapshotSaveGet(IStorageFixture f, CancellationToken ct)
    {
        await f.Snapshots.SaveAsync(Tenant, new Snapshot(StreamA, new StreamVersion(100), "Account", "h1", Encoding.UTF8.GetBytes("{\"b\":1}")), ct).ConfigureAwait(false);
        await f.Snapshots.SaveAsync(Tenant, new Snapshot(StreamA, new StreamVersion(200), "Account", "h1", Encoding.UTF8.GetBytes("{\"b\":2}")), ct).ConfigureAwait(false);

        Snapshot latest = Check.NotNull(await f.Snapshots.GetAsync(Tenant, StreamA, ct).ConfigureAwait(false), "snapshot");
        Check.Equal(new StreamVersion(200), latest.Version, "latest");
        Check.Null(await f.Snapshots.GetAsync("other", StreamA, ct).ConfigureAwait(false), "tenant scoped");
    }

    private static async Task SnapshotNewerWins(IStorageFixture f, CancellationToken ct)
    {
        await f.Snapshots.SaveAsync(Tenant, new Snapshot(StreamA, new StreamVersion(200), "Account", "h1", Encoding.UTF8.GetBytes("{\"b\":2}")), ct).ConfigureAwait(false);
        await f.Snapshots.SaveAsync(Tenant, new Snapshot(StreamA, new StreamVersion(100), "Account", "h1", Encoding.UTF8.GetBytes("{\"b\":1}")), ct).ConfigureAwait(false);

        Snapshot latest = Check.NotNull(await f.Snapshots.GetAsync(Tenant, StreamA, ct).ConfigureAwait(false), "snapshot");
        Check.Equal(new StreamVersion(200), latest.Version, "newer kept");
        Check.Equal("{\"b\":2}", Encoding.UTF8.GetString(latest.Data.Span), "newer data kept");
    }

    private static async Task SnapshotFields(IStorageFixture f, CancellationToken ct)
    {
        const string data = "{ \"balance\" : 10.50, \"holder\": \"Zoë\" }";
        await f.Snapshots.SaveAsync(Tenant, new Snapshot(StreamA, new StreamVersion(7), "Bank.Accounts.BankAccount", "9f86d081884c7d65", Encoding.UTF8.GetBytes(data)), ct).ConfigureAwait(false);
        await f.Snapshots.SaveAsync(Tenant, new Snapshot(StreamA, new StreamVersion(8), "Bank.Accounts.BankAccount", "aaaaaaaaaaaaaaaa", Encoding.UTF8.GetBytes(data)), ct).ConfigureAwait(false);

        Snapshot stored = Check.NotNull(await f.Snapshots.GetAsync(Tenant, StreamA, ct).ConfigureAwait(false), "snapshot");
        Check.Equal("Bank.Accounts.BankAccount", stored.StateType, "state type");
        Check.Equal("aaaaaaaaaaaaaaaa", stored.SchemaHash, "schema hash replaced with the newer snapshot");
        Check.Equal(data, Encoding.UTF8.GetString(stored.Data.Span), "data bytes");
        Check.Equal(StreamA, stored.StreamId, "stream");
    }

    private static async Task SnapshotLarge(IStorageFixture f, CancellationToken ct)
    {
        string big = "{\"blob\":\"" + new string('s', 1024 * 1024) + "\"}";
        await f.Snapshots.SaveAsync(Tenant, new Snapshot(StreamA, new StreamVersion(1), "Big", "h", Encoding.UTF8.GetBytes(big)), ct).ConfigureAwait(false);

        Snapshot stored = Check.NotNull(await f.Snapshots.GetAsync(Tenant, StreamA, ct).ConfigureAwait(false), "snapshot");
        Check.Equal(big, Encoding.UTF8.GetString(stored.Data.Span), "payload");
    }

    private static async Task TenantStreams(IStorageFixture f, CancellationToken ct)
    {
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(2), ct, tenant: "eu").ConfigureAwait(false);
        await AppendAsync(f, StreamA, ExpectedVersion.NoStream, Events(1), ct, tenant: "us").ConfigureAwait(false);

        Check.Equal(new StreamVersion(2), (await f.Events.GetStreamInfoAsync("eu", StreamA, ct).ConfigureAwait(false))!.Version, "eu");
        Check.Equal(new StreamVersion(1), (await f.Events.GetStreamInfoAsync("us", StreamA, ct).ConfigureAwait(false))!.Version, "us");
        Check.Null(await f.Events.GetStreamInfoAsync(Tenant, StreamA, ct).ConfigureAwait(false), "default tenant sees nothing");
    }

    private static async Task TenantDocuments(IStorageFixture f, CancellationToken ct)
    {
        await using (IStorageSession session = await f.Sessions.BeginAsync("eu", ct).ConfigureAwait(false))
        {
            session.Documents.Upsert("docs", "1", 1, Encoding.UTF8.GetBytes("{}"));
            await session.CommitAsync(ct).ConfigureAwait(false);
        }

        Check.NotNull(await f.Documents.GetAsync("eu", "docs", "1", ct).ConfigureAwait(false), "eu document");
        Check.Null(await f.Documents.GetAsync("us", "docs", "1", ct).ConfigureAwait(false), "us cannot read it");
    }

    // ------------------------------------------------------------------ read-model aliases (doc 06 §6)

    private static async Task<CollectionAlias> AliasAsync(ICollectionAliases aliases, string name, CancellationToken ct)
    {
        IReadOnlyList<CollectionAlias> rows = await aliases.ListAliasesAsync(ct).ConfigureAwait(false);
        return Check.NotNull(rows.FirstOrDefault(a => a.Alias == name), "alias row " + name);
    }

    private static async Task AliasSwitch(IStorageFixture f, CancellationToken ct)
    {
        ICollectionAliases aliases = Check.NotNull(f.Aliases, "aliases port");
        string name = "sck-" + Guid.CreateVersion7().ToString("N");
        Check.True(await aliases.SwitchAsync(name, null, name + "@1", ct).ConfigureAwait(false), "first claim");
        Check.True(!await aliases.SwitchAsync(name, null, name + "@1", ct).ConfigureAwait(false), "second claim refused");
        CollectionAlias claimed = await AliasAsync(aliases, name, ct).ConfigureAwait(false);
        Check.Equal(name + "@1", claimed.Target, "claimed target");
        Check.Null(claimed.Previous, "no previous after a claim");

        Check.True(!await aliases.SwitchAsync(name, name + "@9", name + "@2", ct).ConfigureAwait(false), "stale expectation refused");
        Check.True(await aliases.SwitchAsync(name, name + "@1", name + "@2", ct).ConfigureAwait(false), "switch");
        CollectionAlias switched = await AliasAsync(aliases, name, ct).ConfigureAwait(false);
        Check.Equal(name + "@2", switched.Target, "switched target");
        Check.Equal(name + "@1", switched.Previous, "previous target");
        Check.True(switched.SwitchedAt >= claimed.SwitchedAt, "switch time moves forward");
    }

    private static async Task AliasRequest(IStorageFixture f, CancellationToken ct)
    {
        ICollectionAliases aliases = Check.NotNull(f.Aliases, "aliases port");
        string name = "sck-" + Guid.CreateVersion7().ToString("N");
        await aliases.RequestAsync(name, name + "@1", name + "@1.2", ct).ConfigureAwait(false);
        CollectionAlias requested = await AliasAsync(aliases, name, ct).ConfigureAwait(false);
        Check.Equal(name + "@1", requested.Target, "request creates the row at the current target");
        Check.Equal(name + "@1.2", requested.Building, "building");

        await aliases.RequestAsync(name, name + "@1", name + "@1.3", ct).ConfigureAwait(false);
        Check.Equal(name + "@1.3", (await AliasAsync(aliases, name, ct).ConfigureAwait(false)).Building, "a later request replaces the building generation");
        Check.True(await aliases.SwitchAsync(name, name + "@1", name + "@1.3", ct).ConfigureAwait(false), "switch to the requested generation");
        CollectionAlias switched = await AliasAsync(aliases, name, ct).ConfigureAwait(false);
        Check.Null(switched.Building, "building cleared by its switch");

        await aliases.ClearPreviousAsync(name, name + "@0", ct).ConfigureAwait(false);
        Check.Equal(name + "@1", (await AliasAsync(aliases, name, ct).ConfigureAwait(false)).Previous, "a different previous is kept");
        await aliases.ClearPreviousAsync(name, name + "@1", ct).ConfigureAwait(false);
        Check.Null((await AliasAsync(aliases, name, ct).ConfigureAwait(false)).Previous, "previous cleared");
    }

    private static async Task AliasRace(IStorageFixture f, CancellationToken ct)
    {
        ICollectionAliases aliases = Check.NotNull(f.Aliases, "aliases port");
        string name = "sck-" + Guid.CreateVersion7().ToString("N");
        Check.True(await aliases.SwitchAsync(name, null, name + "@1", ct).ConfigureAwait(false), "claim");
        bool[] wins = await Task.WhenAll(Enumerable.Range(2, 8).Select(v => aliases.SwitchAsync(name, name + "@1", $"{name}@{v}", ct).AsTask())).ConfigureAwait(false);
        Check.Equal(1, wins.Count(w => w), "exactly one switch wins");
        CollectionAlias alias = await AliasAsync(aliases, name, ct).ConfigureAwait(false);
        Check.Equal(name + "@1", alias.Previous, "the winner's previous");
    }

    private static async Task DocumentVersions(IStorageFixture f, CancellationToken ct)
    {
        await using (IStorageSession session = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            session.Documents.Upsert("docs", "1", 5, Encoding.UTF8.GetBytes("{\"v\":5}"));
            StoredDocument own = Check.NotNull(await session.Documents.GetAsync("docs", "1", ct).ConfigureAwait(false), "own write");
            Check.Equal(5L, own.Version, "own version");
            await session.CommitAsync(ct).ConfigureAwait(false);
        }

        await using (IStorageSession older = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            older.Documents.Upsert("docs", "1", 4, Encoding.UTF8.GetBytes("{\"v\":4}"));
            await older.CommitAsync(ct).ConfigureAwait(false);
        }

        StoredDocument stored = Check.NotNull(await f.Documents.GetAsync(Tenant, "docs", "1", ct).ConfigureAwait(false), "document");
        Check.Equal(5L, stored.Version, "older write ignored");
        Check.Equal("{\"v\":5}", Encoding.UTF8.GetString(stored.Data.Span), "data");

        await using (IStorageSession many = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false))
        {
            many.Documents.Upsert("docs", "2", 1, Encoding.UTF8.GetBytes("{\"v\":1}"));
            IReadOnlyList<StoredDocument> found = await many.Documents.GetManyAsync("docs", ["1", "2", "missing"], ct).ConfigureAwait(false);
            Check.SequenceEqual(["1:5", "2:1"], found.Select(d => $"{d.Id}:{d.Version}").Order(StringComparer.Ordinal), "batch read: stored and own writes, absent omitted");
        }

        await using IStorageSession delete = await f.Sessions.BeginAsync(Tenant, ct).ConfigureAwait(false);
        delete.Documents.Delete("docs", "1");
        await delete.CommitAsync(ct).ConfigureAwait(false);
        Check.Null(await f.Documents.GetAsync(Tenant, "docs", "1", ct).ConfigureAwait(false), "deleted");
    }
}
