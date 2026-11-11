using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>An <see cref="IStorageFixture"/> over a PostgreSQL store on its own schema.</summary>
internal sealed class PostgreSqlStorageFixture(PostgreSqlStore store) : IStorageFixture
{
    public PostgreSqlStore Store { get; } = store;

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

    // Leases use the database clock by design (doc 05 §7.4); the clock-controlled cases have PostgreSQL variants in PostgreSqlStoreTests.
    public bool SupportsClockControl => false;

    public DateTimeOffset Now => TimeProvider.System.GetUtcNow();

    public ValueTask AdvanceClockAsync(TimeSpan by)
    {
        throw new NotSupportedException("The PostgreSQL store uses the database clock for leases.");
    }

    public ValueTask DisposeAsync()
    {
        return Store.DisposeAsync();
    }
}

/// <summary>The storage conformance kit against PostgreSQL (SCK-01..58, plan 06 §2; WP-2.2–2.4, WP-2.9).</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlStorageConformanceTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    public static TheoryData<string> CaseIds => new(StorageConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-008;ADR-009;ADR-010;ES-R1;ES-R2;GAP-15")]
    public async Task Sck(string id)
    {
        ConformanceCase<IStorageFixture> @case = StorageConformance.Cases.Single(c => c.Id == id);
        if (@case.RequiresClockControl)
        {
            Assert.Skip("Needs a controllable store clock; PostgreSQL leases use the database clock (see PostgreSqlStoreTests).");
        }

        await using var fixture = new PostgreSqlStorageFixture(await Server.CreateStoreAsync());

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
