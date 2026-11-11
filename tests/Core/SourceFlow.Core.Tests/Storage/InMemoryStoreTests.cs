using System.Text;
using SourceFlow.Messaging;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Core.Tests.Storage;

/// <summary>UT-STO (plan 05 §3.2): in-memory specifics beyond the SCK.</summary>
[Trait("Category", "Unit")]
public sealed class InMemoryStoreTests
{
    private const string Tenant = MessageEnvelope.DefaultTenant;
    private static readonly StreamId Stream = new("acct-1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-STO-01")]
    [Trait("Covers", "ADR-008")]
    public async Task Append_ExpectedVersionMatches_AssignsSequentialVersionsAndPositions()
    {
        var store = new InMemoryStore();
        CommitResult first = await Append(store, ExpectedVersion.NoStream, 2);
        CommitResult second = await Append(store, ExpectedVersion.Exact(new StreamVersion(2)), 1);

        first.StreamVersion.ShouldBe(new StreamVersion(2));
        second.StreamVersion.ShouldBe(new StreamVersion(3));
        second.LastPosition!.Value.ShouldBeGreaterThan(first.LastPosition!.Value);
    }

    [Fact]
    [Trait("Id", "UT-STO-02")]
    [Trait("Covers", "ADR-008;GAP-05")]
    public async Task Append_ExpectedVersionStale_ThrowsConcurrency_AndWritesNothing()
    {
        var store = new InMemoryStore();
        await Append(store, ExpectedVersion.NoStream, 1);

        ConcurrencyException conflict = await Should.ThrowAsync<ConcurrencyException>(() => Append(store, ExpectedVersion.NoStream, 1).AsTask());

        conflict.Actual.ShouldBe(new StreamVersion(1));
        (await store.ReadStreamAsync(Tenant, Stream, new StreamVersion(1), 10, Ct)).Events.Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-STO-05")]
    public async Task Dispose_WithoutCommit_DiscardsEverything()
    {
        var store = new InMemoryStore();
        await using (IStorageSession session = await store.BeginAsync(Tenant, Ct))
        {
            session.Append(Stream, "acct", ExpectedVersion.NoStream, [StorageConformance.Event()]);
        }

        (await store.GetStreamInfoAsync(Tenant, Stream, Ct)).ShouldBeNull();
        store.CommitCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "UT-STO-08")]
    [Trait("Covers", "ADR-006")]
    public async Task Inbox_DuplicateInSameSession_Throws()
    {
        var store = new InMemoryStore();
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        var id = new MessageId(Guid.CreateVersion7(DateTimeOffset.UnixEpoch));
        session.RecordInbox("c", id, new InboxResult(CommandStatus.Accepted, StreamVersion.None, null));

        Should.Throw<DuplicateMessageException>(() => session.RecordInbox("c", id, new InboxResult(CommandStatus.Accepted, StreamVersion.None, null)));
    }

    [Fact]
    [Trait("Id", "UT-STO-19")]
    public void Outbox_PartitionAssignment_IsStableForKey()
    {
        int partition = OutboxPartitioning.PartitionOf("acct-1", 16);

        Enumerable.Range(0, 100).Select(_ => OutboxPartitioning.PartitionOf("acct-1", 16)).ShouldAllBe(p => p == partition);
        partition.ShouldBeInRange(0, 15);
        OutboxPartitioning.PartitionOf(null, 16).ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => OutboxPartitioning.PartitionOf("k", 0));
    }

    [Fact]
    [Trait("Id", "UT-STO-24")]
    public async Task Notifications_SignalledAfterCommit_AndUnsubscribe()
    {
        var store = new InMemoryStore();
        int calls = 0;
        IDisposable subscription = store.Subscribe(() => calls++);

        await Append(store, ExpectedVersion.NoStream, 1);
        subscription.Dispose();
        await Append(store, ExpectedVersion.Exact(new StreamVersion(1)), 1);

        calls.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-STO-25")]
    public async Task FaultHook_BeforeCommit_ThrowLeavesNoTrace()
    {
        var store = new InMemoryStore();
        store.Faults.FailNextCommits(1, new TimeoutException("injected"));

        await Should.ThrowAsync<TimeoutException>(() => Append(store, ExpectedVersion.NoStream, 1).AsTask());
        (await store.GetStreamInfoAsync(Tenant, Stream, Ct)).ShouldBeNull();
        await Append(store, ExpectedVersion.NoStream, 1);
        (await store.GetStreamInfoAsync(Tenant, Stream, Ct))!.Version.ShouldBe(new StreamVersion(1));
    }

    [Fact]
    public async Task Session_CannotBeUsedAfterCommit()
    {
        var store = new InMemoryStore();
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        await session.CommitAsync(Ct);

        Should.Throw<InvalidOperationException>(() => session.EnqueueOutbox(null!));
    }

    [Theory]
    [Trait("Id", "UT-STO-31")]
    [Trait("Covers", "doc 06 3")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(1024)]
    public void Buckets_Range_CoversAllBucketsExactlyOnce(int partitions)
    {
        int[] covered = Enumerable.Range(0, partitions)
            .Select(p => Buckets.Range(p, partitions))
            .SelectMany(r => Enumerable.Range(r.Low, r.High - r.Low + 1))
            .ToArray();

        covered.ShouldBe(Enumerable.Range(0, Buckets.Count).ToArray());
    }

    [Theory]
    [Trait("Id", "UT-STO-32")]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(2048)]
    public void Buckets_InvalidPartitionCount_Throws(int partitions)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Buckets.Range(0, partitions));
    }

    [Fact]
    [Trait("Id", "UT-STO-33")]
    public void Buckets_TenantIsPartOfTheHash()
    {
        int[] differing = Enumerable.Range(0, 200)
            .Where(i => Buckets.Of("eu", new StreamId($"s-{i}")) != Buckets.Of("us", new StreamId($"s-{i}")))
            .ToArray();

        differing.Length.ShouldBeGreaterThan(150);
    }

    [Fact]
    [Trait("Id", "UT-STO-35")]
    public void Buckets_DistributionIsRoughlyUniform()
    {
        const int samples = 100_000;
        int[] counts = new int[16];
        for (int i = 0; i < samples; i++)
        {
            counts[Buckets.Of("-", new StreamId($"bank-account-{i:x8}")) / 64]++;
        }

        double expected = samples / 16.0;
        double chiSquared = counts.Sum(c => (c - expected) * (c - expected) / expected);
        chiSquared.ShouldBeLessThan(37.7); // p = 0.001 for 15 degrees of freedom
    }

    private static async ValueTask<CommitResult> Append(InMemoryStore store, ExpectedVersion expected, int count)
    {
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        session.Append(Stream, "acct", expected, Enumerable.Range(0, count).Select(_ => StorageConformance.Event()).ToArray());
        return await session.CommitAsync(Ct);
    }
}
