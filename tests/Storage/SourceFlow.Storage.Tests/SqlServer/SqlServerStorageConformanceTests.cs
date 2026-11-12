using SourceFlow.Storage.SqlServer;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Storage.Tests.SqlServer;

/// <summary>An <see cref="IStorageFixture"/> over a SQL Server store on its own schema.</summary>
internal sealed class SqlServerStorageFixture(SqlServerStore store) : IStorageFixture
{
    public SqlServerStore Store { get; } = store;

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

    // Leases use the database clock by design (doc 05 §7.4).
    public bool SupportsClockControl => false;

    public DateTimeOffset Now => TimeProvider.System.GetUtcNow();

    public ValueTask AdvanceClockAsync(TimeSpan by)
    {
        throw new NotSupportedException("The SQL Server store uses the database clock for leases.");
    }

    public ValueTask DisposeAsync()
    {
        return Store.DisposeAsync();
    }
}

/// <summary>The storage conformance kit against SQL Server (SCK-01..58, plan 06 §2; WP-4.4).</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "SqlServer")]
[Trait("Requires", "Docker")]
public sealed class SqlServerStorageConformanceTests(SqlServerServer server) : SqlServerTest(server)
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
            Assert.Skip("Needs a controllable store clock; SQL Server leases use the database clock.");
        }

        await using var fixture = new SqlServerStorageFixture(await Server.CreateStoreAsync());

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Id", "SCK-L01")]
    [Trait("Covers", "ADR-006;ADR-008")]
    public void AppendsAndOutbox_AreLinearizable()
    {
        SourceFlow.Tests.Shared.StorageLinearizability.AppendsAndOutbox(
            () => new SqlServerStorageFixture(Server.CreateStoreAsync().GetAwaiter().GetResult()), iterations: 40, threads: 4);
    }
}
