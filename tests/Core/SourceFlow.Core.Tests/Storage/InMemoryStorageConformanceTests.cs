using Microsoft.Extensions.Time.Testing;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Core.Tests.Storage;

internal sealed class InMemoryStorageFixture : IStorageFixture
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public InMemoryStorageFixture()
    {
        Store = new InMemoryStore(_time);
    }

    public InMemoryStore Store { get; }

    public IEventStore Events => Store;

    public IEventLogReader Log => Store;

    public IStorageSessionFactory Sessions => Store;

    public ICheckpointStore Checkpoints => Store;

    public ILeaseManager Leases => Store;

    public IOutboxStore Outbox => Store;

    public IDeadLetterStore DeadLetters => Store;

    public ISnapshotStore Snapshots => Store;

    public IDocumentReader Documents => Store;

    public IStoreNotifications Notifications => Store;

    public bool SupportsClockControl => true;

    public DateTimeOffset Now => _time.GetUtcNow();

    public ValueTask AdvanceClockAsync(TimeSpan by)
    {
        _time.Advance(by);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>Linearisability of appends and the outbox on the in-memory store (WP-2.9).</summary>
[Trait("Category", "Conformance")]
public sealed class InMemoryLinearizabilityTests
{
    [Fact]
    [Trait("Id", "SCK-L01")]
    [Trait("Category", "Property")]
    [Trait("Covers", "ADR-006;ADR-008")]
    public void AppendsAndOutbox_AreLinearizable()
    {
        SourceFlow.Tests.Shared.StorageLinearizability.AppendsAndOutbox(() => new InMemoryStorageFixture(), iterations: 200, threads: Environment.ProcessorCount);
    }
}

/// <summary>The storage conformance kit against the in-memory store (SCK-01..58, plan 06 §2).</summary>
[Trait("Category", "Conformance")]
public sealed class InMemoryStorageConformanceTests
{
    public static TheoryData<string> CaseIds => new(StorageConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-008;ADR-010;ES-R1;ES-R2;GAP-15")]
    public async Task Sck(string id)
    {
        ConformanceCase<IStorageFixture> @case = StorageConformance.Cases.Single(c => c.Id == id);
        await using var fixture = new InMemoryStorageFixture();

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
