using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SourceFlow.Hosting;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.PostgreSQL.Query;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>
/// SCK-65..68, 72: the partitioned events variant (<see cref="PostgreSqlStorageOptions.PartitionEvents"/>, doc 05 §9,
/// doc 09 §5; WP-4.5). The store clock (a fake) decides <c>created_at</c>, so a test can write on both sides of a
/// partition boundary without waiting for a month to pass.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlPartitionTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private const string Tenant = "-";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "SCK-65")]
    [Trait("Covers", "doc 05 9;doc 09 5")]
    public async Task Append_AcrossAPartitionBoundary()
    {
        (PostgreSqlStore store, SettableTime time, DateTimeOffset boundary) = await PartitionedAsync();
        var stream = new StreamId("p-a-1");
        time.SetUtcNow(boundary - TimeSpan.FromSeconds(1));
        await AppendAsync(store, stream, ExpectedVersion.NoStream, 2);
        time.SetUtcNow(boundary + TimeSpan.FromSeconds(1));
        await AppendAsync(store, stream, ExpectedVersion.Exact(new StreamVersion(2)), 2);

        StreamSlice slice = await store.ReadStreamAsync(Tenant, stream, new StreamVersion(1), 10, Ct);

        slice.Events.Select(e => e.StreamVersion.Value).ShouldBe([1L, 2L, 3L, 4L]);
        (await PartitionsOfAsync(store, stream)).ShouldBe(["events_initial", "events_initial", PartitionName(boundary), PartitionName(boundary)]);
        await Should.ThrowAsync<ConcurrencyException>(() => AppendAsync(store, stream, ExpectedVersion.Exact(new StreamVersion(2)), 1));   // the streams CAS still guards versions
    }

    [Fact]
    [Trait("Id", "SCK-66")]
    [Trait("Covers", "ADR-010;doc 05 9")]
    public async Task GlobalLog_IsInCommitOrder_AcrossPartitions()
    {
        (PostgreSqlStore store, SettableTime time, DateTimeOffset boundary) = await PartitionedAsync();
        DateTimeOffset[] times = [boundary.AddMonths(1).AddDays(3), boundary.AddDays(-3), boundary.AddDays(10), boundary.AddMonths(1).AddDays(1)];
        for (int i = 0; i < times.Length; i++)
        {
            time.SetUtcNow(times[i]);
            await AppendAsync(store, new StreamId($"p-log-{i}"), ExpectedVersion.NoStream, 1);
        }

        LogBatch log = await store.ReadAsync(GlobalPosition.Start, BucketRange.All, 100, Ct);
        GlobalPosition horizon = await store.GetSafeHorizonAsync(Ct);

        log.Events.Select(e => e.StreamId.Value).ShouldBe(["p-log-0", "p-log-1", "p-log-2", "p-log-3"], "commit order, not created_at order");
        log.Events.Select(e => e.Position).ShouldBeInOrder();
        log.Events[^1].Position.CompareTo(horizon).ShouldBeLessThanOrEqualTo(0);
        (await store.ReadAsync(log.Events[1].Position, BucketRange.All, 100, Ct)).Events.Select(e => e.StreamId.Value).ShouldBe(["p-log-2", "p-log-3"]);
        (await store.CountEventsAfterAsync(log.Events[0].Position, BucketRange.All, 100, Ct)).ShouldBe(3);
    }

    [Fact]
    [Trait("Id", "SCK-67")]
    [Trait("Covers", "doc 05 9")]
    public async Task Maintenance_CreatesPartitionsAhead_Idempotently()
    {
        (PostgreSqlStore store, SettableTime time, DateTimeOffset boundary) = await PartitionedAsync(o => o.PartitionMonthsAhead = 2);
        IReadOnlyList<EventPartition> initial = await store.ListEventPartitionsAsync(Ct);

        initial[0].ShouldBe(new EventPartition("events_initial", null, boundary, null));
        Contiguous(initial);

        time.SetUtcNow(boundary.AddMonths(5).AddDays(2));
        int created = await store.EnsureEventPartitionsAsync(Ct);
        int again = await store.EnsureEventPartitionsAsync(Ct);
        IReadOnlyList<EventPartition> later = await store.ListEventPartitionsAsync(Ct);

        created.ShouldBeGreaterThan(0);
        again.ShouldBe(0);
        Contiguous(later);
        later[^1].To.ShouldBe(boundary.AddMonths(8), "the current month and two after it");
        await AppendAsync(store, new StreamId("p-ahead"), ExpectedVersion.NoStream, 1);   // writes land in the new partitions

        // Concurrent maintenance (every node runs it) creates each partition once.
        time.SetUtcNow(boundary.AddMonths(9));
        int[] concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.EnsureEventPartitionsAsync(Ct)));
        concurrent.Sum().ShouldBe(4);
        Contiguous(await store.ListEventPartitionsAsync(Ct));
    }

    [Fact]
    [Trait("Id", "SCK-67")]
    [Trait("Covers", "doc 05 9")]
    public async Task UsePostgreSql_WithPartitionEvents_MigratesAndRunsTheMaintenanceJob()
    {
        string schema = PostgreSqlServer.NewSchema("pm");
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<Bank.Accounts.AccountsModule>();
            sf.UsePostgreSql(o =>
            {
                o.DataSource = Server.DataSource;
                o.Schema = schema;
                o.PartitionEvents = true;
                o.PartitionMonthsAhead = 6;
            });
        });
        using IHost host = builder.Build();
        await host.StartAsync(Ct);
        PostgreSqlStore store = host.Services.GetRequiredService<PostgreSqlStore>();

        host.Services.GetServices<IHostedService>().OfType<PostgreSqlPartitionMaintenance>().ShouldHaveSingleItem();
        await EventuallyAsync(async () => (await store.ListEventPartitionsAsync(Ct)).Count == 7);   // initial + 6 ahead
        (await host.Services.GetRequiredService<ICommandSender>().SendAsync(new Bank.Accounts.OpenAccount(Bank.Accounts.AccountId.New(), "Ada", 1m), null, Ct))
            .Status.ShouldBe(CommandStatus.Accepted);
        await store.CreateMigrator().VerifyAsync(Ct);
        await host.StopAsync(Ct);
    }

    [Fact]
    [Trait("Id", "SCK-68")]
    [Trait("Covers", "doc 05 9")]
    public async Task Detach_AnOldPartition_OnlyWhenEveryStreamInItIsArchived()
    {
        (PostgreSqlStore store, SettableTime time, DateTimeOffset boundary) = await PartitionedAsync();
        var archived = new StreamId("p-cold");
        var open = new StreamId("p-warm");
        time.SetUtcNow(boundary - TimeSpan.FromDays(1));
        await AppendAsync(store, archived, ExpectedVersion.NoStream, 3);
        await AppendAsync(store, open, ExpectedVersion.NoStream, 1);
        time.SetUtcNow(boundary + TimeSpan.FromDays(1));
        await AppendAsync(store, open, ExpectedVersion.Exact(new StreamVersion(1)), 1);
        await store.ArchiveStreamAsync(Tenant, archived, Ct);

        (await Should.ThrowAsync<EventPartitionException>(() => store.DetachEventPartitionAsync(PartitionName(boundary), Ct))).Message.ShouldContain("not in the past");
        (await Should.ThrowAsync<EventPartitionException>(() => store.DetachEventPartitionAsync("events_initial", Ct))).Message.ShouldContain("1 stream(s)");
        (await Should.ThrowAsync<EventPartitionException>(() => store.DetachEventPartitionAsync("events; DROP TABLE x", Ct))).Message.ShouldContain("not an events partition");

        await store.ArchiveStreamAsync(Tenant, open, Ct);
        await store.DetachEventPartitionAsync("events_initial", Ct);

        (await store.ListEventPartitionsAsync(Ct))[0].DetachedAt.ShouldNotBeNull();
        (await store.ReadStreamAsync(Tenant, archived, new StreamVersion(1), 10, Ct)).Events.ShouldBeEmpty("detached events leave the store");
        (await store.ReadAsync(GlobalPosition.Start, BucketRange.All, 100, Ct)).Events.ShouldHaveSingleItem().StreamId.ShouldBe(open);
        (await Should.ThrowAsync<EventPartitionException>(() => store.DetachEventPartitionAsync("events_initial", Ct))).Message.ShouldContain("already detached");
        await AppendAsync(store, new StreamId("p-new"), ExpectedVersion.NoStream, 1);   // the table keeps working
        long kept;
        await using (NpgsqlCommand count = Server.DataSource.CreateCommand($"SELECT count(*) FROM \"{store.Options.Schema}\".events_initial"))
        {
            kept = (long)(await count.ExecuteScalarAsync(Ct))!;
        }

        kept.ShouldBe(4, "the detached table keeps its rows for archiving");
    }

    [Fact]
    [Trait("Id", "SCK-72")]
    [Trait("Covers", "doc 05 9;doc 05 11")]
    public async Task Converting_AnExistingStore_KeepsEveryEventAndPosition()
    {
        var time = new SettableTime(TimeProvider.System.GetUtcNow());
        PostgreSqlStore plain = await Server.CreateStoreAsync(time, o => o.PartitionEvents = false);
        string schema = plain.Options.Schema;
        for (int i = 0; i < 5; i++)
        {
            await AppendAsync(plain, new StreamId($"c-{i % 2}"), i < 2 ? ExpectedVersion.NoStream : ExpectedVersion.Any, 1);
        }

        LogBatch before = await plain.ReadAsync(GlobalPosition.Start, BucketRange.All, 100, Ct);
        await Should.ThrowAsync<SchemaMigrationException>(() => new PostgreSqlSchemaMigrator(Server.DataSource, schema, partitionEvents: true).VerifyAsync(Ct));

        var migrator = new PostgreSqlSchemaMigrator(Server.DataSource, schema, partitionEvents: true);
        (await migrator.MigrateAsync(Ct)).ShouldBeEmpty();   // no new versions: the conversion is not a numbered script
        await migrator.MigrateAsync(Ct);                    // and running it again changes nothing
        await migrator.VerifyAsync(Ct);
        await using var partitioned = new PostgreSqlStore(new PostgreSqlStorageOptions { DataSource = Server.DataSource, Schema = schema, PartitionEvents = true }, time);

        LogBatch after = await partitioned.ReadAsync(GlobalPosition.Start, BucketRange.All, 100, Ct);
        after.Events.Select(e => (e.EventId, e.Position, e.StreamId, e.StreamVersion)).ShouldBe(before.Events.Select(e => (e.EventId, e.Position, e.StreamId, e.StreamVersion)));
        CommitResult next = await AppendAsync(partitioned, new StreamId("c-0"), ExpectedVersion.Any, 1);
        next.LastPosition!.Value.CompareTo(before.Events[^1].Position).ShouldBeGreaterThan(0);
        (await partitioned.ReadStreamAsync(Tenant, new StreamId("c-0"), new StreamVersion(1), 10, Ct)).Events.Count.ShouldBe(4);

        // The event-id backstop still holds inside a partition.
        EventToAppend duplicate = StorageConformance.Event() with { CreatedAt = time.GetUtcNow() };
        await AppendAsync(partitioned, new StreamId("c-dup-1"), ExpectedVersion.NoStream, [duplicate]);
        await Should.ThrowAsync<ConcurrencyException>(() => AppendAsync(partitioned, new StreamId("c-dup-2"), ExpectedVersion.NoStream, [duplicate]));

        // Query-index sync builds on the partitioned table (without CONCURRENTLY).
        QueryCatalog catalog = QueryCatalog.Build(new SourceFlowModelBuilder().AddModule<Bank.Accounts.AccountsModule>().Build());
        QueryIndexPlan plan = await PostgreSqlIndexSync.ApplyAsync(Server.DataSource, catalog, schema, concurrently: true, Ct);
        plan.Create.ShouldContain(i => i.Table == "events");
        (await PostgreSqlIndexSync.PlanAsync(Server.DataSource, catalog, schema, Ct)).Create.ShouldBeEmpty();

        string script = new PostgreSqlSchemaMigrator(Server.DataSource, "sales", partitionEvents: true).Script();
        script.ShouldContain("PARTITION BY RANGE (created_at)");
        script.ShouldContain("SELECT \"sales\".sf_ensure_event_partitions(now(), 3);");
    }

    // ================================================================ helpers

    private async Task<(PostgreSqlStore Store, SettableTime Time, DateTimeOffset Boundary)> PartitionedAsync(Action<PostgreSqlStorageOptions>? configure = null)
    {
        var time = new SettableTime(TimeProvider.System.GetUtcNow());
        PostgreSqlStore store = await Server.CreateStoreAsync(time, o =>
        {
            o.PartitionEvents = true;
            configure?.Invoke(o);
        });
        DateTimeOffset boundary = (await store.ListEventPartitionsAsync(Ct))[0].To;
        return (store, time, boundary);
    }

    private static string PartitionName(DateTimeOffset from)
    {
        return "events_" + from.UtcDateTime.ToString("yyyy_MM", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Contiguous(IReadOnlyList<EventPartition> partitions)
    {
        for (int i = 1; i < partitions.Count; i++)
        {
            partitions[i].From.ShouldBe(partitions[i - 1].To, partitions[i].Name);
            partitions[i].Name.ShouldBe(PartitionName(partitions[i].From!.Value));
        }
    }

    // Events are stamped with the store clock, as the runtime does: created_at decides the partition.
    private static Task<CommitResult> AppendAsync(PostgreSqlStore store, StreamId stream, ExpectedVersion expected, int count)
    {
        DateTimeOffset now = store.Time.GetUtcNow();
        return AppendAsync(store, stream, expected, [.. Enumerable.Range(0, count).Select(_ => StorageConformance.Event() with { CreatedAt = now })]);
    }

    private static async Task<CommitResult> AppendAsync(PostgreSqlStore store, StreamId stream, ExpectedVersion expected, IReadOnlyList<EventToAppend> events)
    {
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        session.Append(stream, "partition-test", expected, events);
        return await session.CommitAsync(Ct);
    }

    private async Task<string[]> PartitionsOfAsync(PostgreSqlStore store, StreamId stream)
    {
        await using NpgsqlCommand command = Server.DataSource.CreateCommand(
            $"SELECT tableoid::regclass::text FROM \"{store.Options.Schema}\".events WHERE stream_id = @s ORDER BY stream_version");
        command.Parameters.Add(new NpgsqlParameter<string>("s", stream.Value));
        var names = new List<string>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            string name = reader.GetString(0);
            names.Add(name[(name.LastIndexOf('.') + 1)..].Trim('"'));
        }

        return [.. names];
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException("The condition was not met.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
        }
    }
}

/// <summary>A clock the test sets, forwards or backwards (created_at decides the partition).</summary>
internal sealed class SettableTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public void SetUtcNow(DateTimeOffset value)
    {
        _now = value;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }
}
