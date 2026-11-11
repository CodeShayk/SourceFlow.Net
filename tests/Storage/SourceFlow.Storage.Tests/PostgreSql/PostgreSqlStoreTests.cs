using System.Text;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using SourceFlow.Messaging;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>
/// PostgreSQL-specific behaviour (WP-2.2–2.4): the clock-controlled SCK cases with the real database clock, LISTEN across
/// processes, the xmin horizon (doc 05 §5), byte-exact payloads (ADR-028) and custom SQL in the session transaction.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlStoreTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private const string Tenant = MessageEnvelope.DefaultTenant;
    private static readonly StreamId Stream = new("pg-a-1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "SCK-33")]
    public async Task OutboxDueTimes_FollowTheStoreClock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await using PostgreSqlStore store = await Server.CreateStoreAsync(time);
        await EnqueueAsync(store, Row("timer", time.GetUtcNow().AddMinutes(5)));
        LeaseToken lease = (await store.TryAcquireAsync("outbox:0", "node-1", TimeSpan.FromMinutes(1), Ct))!;

        (await store.ClaimAsync(0, lease, 10, Ct)).ShouldBeEmpty();
        (await store.DueOutboxCountAsync(Ct)).ShouldBe(0);
        time.Advance(TimeSpan.FromMinutes(6));

        (await store.ClaimAsync(0, lease, 10, Ct)).Count.ShouldBe(1);
        (await store.DueOutboxCountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-SUB-10")]
    [Trait("Covers", "doc 06 4")]
    public async Task NodeRegistry_CountsLiveNodesWithTheRole_OnTheDatabaseClock()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        TimeSpan window = TimeSpan.FromSeconds(30);

        (await store.HeartbeatAsync("node-1", 4, window, Ct)).ShouldBe(1);
        (await store.HeartbeatAsync("node-2", 4 | 1, window, Ct)).ShouldBe(2);
        (await store.HeartbeatAsync("api-only", 0, window, Ct)).ShouldBe(1);   // no shared role: counts only itself
        (await store.HeartbeatAsync("node-1", 4, window, Ct)).ShouldBe(2);
        (await store.HeartbeatAsync("node-1", 4, TimeSpan.FromTicks(1), Ct)).ShouldBe(1);   // the others are outside a tiny window

        await store.LeaveAsync("node-2", Ct);
        (await store.HeartbeatAsync("node-1", 4, window, Ct)).ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "SCK-44")]
    [Trait("Covers", "ADR-016")]
    public async Task Leases_ExpireOnTheDatabaseClock_AndAStolenLeaseFencesTheOldOwner()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        LeaseToken first = (await store.TryAcquireAsync("sub:p:0", "node-1", TimeSpan.FromMilliseconds(300), Ct))!;
        (await store.TryAcquireAsync("sub:p:0", "node-2", TimeSpan.FromSeconds(30), Ct)).ShouldBeNull();

        await RealClock.WaitAsync(TimeSpan.FromMilliseconds(500));
        LeaseToken stolen = (await store.TryAcquireAsync("sub:p:0", "node-2", TimeSpan.FromSeconds(30), Ct))!;

        stolen.Token.ShouldBeGreaterThan(first.Token);
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        session.SaveCheckpoint("p", 0, new GlobalPosition(5, 1), first);
        await Should.ThrowAsync<LeaseLostException>(() => session.CommitAsync(Ct).AsTask());
    }

    [Fact]
    [Trait("Id", "SCK-40")]
    public async Task ExpiredLease_RejectsTheCheckpoint()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        LeaseToken lease = (await store.TryAcquireAsync("sub:p:0", "node-1", TimeSpan.FromMilliseconds(200), Ct))!;
        await RealClock.WaitAsync(TimeSpan.FromMilliseconds(400));

        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        session.SaveCheckpoint("p", 0, new GlobalPosition(5, 1), lease);

        await Should.ThrowAsync<LeaseLostException>(() => session.CommitAsync(Ct).AsTask());
    }

    [Fact]
    [Trait("Id", "SCK-20")]
    [Trait("Covers", "doc 05 8.1")]
    public async Task Commit_IsOneRoundTrip_WithEveryKindOfWrite()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        LeaseToken lease = (await store.TryAcquireAsync("sub:p:0", "node-1", TimeSpan.FromSeconds(30), Ct))!;
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        session.Append(Stream, "pg", ExpectedVersion.NoStream, [StorageConformance.Event(), StorageConformance.Event()]);
        session.EnqueueOutbox(Row("k", DateTimeOffset.UnixEpoch));
        session.RecordInbox("pg", new MessageId(Guid.CreateVersion7()), new InboxResult(CommandStatus.Accepted, new StreamVersion(2), null));
        session.Documents.Upsert("docs", "1", 1, "{}"u8.ToArray());
        session.SaveProcessState("pm", "1", 0, "{}"u8.ToArray(), completed: false);
        session.SaveCheckpoint("p", 0, new GlobalPosition(1, 1), lease);

        int roundTrips = await CountRoundTripsAsync(() => session.CommitAsync(Ct).AsTask());

        roundTrips.ShouldBe(1);
        (await store.GetStreamInfoAsync(Tenant, Stream, Ct))!.Version.ShouldBe(new StreamVersion(2));
        (await store.GetAsync("p", 0, Ct)).ShouldBe(new GlobalPosition(1, 1));
    }

    [Fact]
    [Trait("Id", "SCK-20")]
    public async Task FailedCheck_InTheBatch_AbortsTheCommit_InOneRoundTrip()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        LeaseToken lease = (await store.TryAcquireAsync("sub:p:0", "node-1", TimeSpan.FromSeconds(30), Ct))!;
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        session.Documents.Upsert("docs", "1", 1, "{}"u8.ToArray());
        session.SaveCheckpoint("p", 0, new GlobalPosition(1, 1), lease with { Token = lease.Token + 5 });

        await Should.ThrowAsync<LeaseLostException>(() => session.CommitAsync(Ct).AsTask());
        (await ((IDocumentReader)store).GetAsync(Tenant, "docs", "1", Ct)).ShouldBeNull();
    }

    private static async Task<int> CountRoundTripsAsync(Func<Task> action)
    {
        using var source = new System.Diagnostics.ActivitySource("SourceFlow.Tests.RoundTrips");
        int count = 0;
        System.Diagnostics.Activity? parent = null;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name is "Npgsql" or "SourceFlow.Tests.RoundTrips",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.Source.Name == "Npgsql" && parent is not null && a.ParentSpanId == parent.SpanId)
                {
                    Interlocked.Increment(ref count);
                }
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using (parent = source.StartActivity("commit"))
        {
            await action();
        }

        return count;
    }

    [Fact]
    [Trait("Id", "SCK-28")]
    public async Task Listen_RelaysCommitsFromAnotherProcess()
    {
        await using PostgreSqlStore listener = await Server.CreateStoreAsync();
        await using var writer = new PostgreSqlStore(new PostgreSqlStorageOptions { DataSource = Server.DataSource, Schema = listener.Options.Schema });
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = listener.Subscribe(() => signalled.TrySetResult());
        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);   // the catch-up signal once LISTEN is active
        signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await AppendAsync(writer, Stream, ExpectedVersion.NoStream);

        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    [Trait("Id", "SCK-28")]
    [Trait("Covers", "doc 05 8.1")]
    public async Task LostListenConnection_Reconnects_AndNoCommitIsMissed()
    {
        await using PostgreSqlStore listener = await Server.CreateStoreAsync(configure: o => o.ListenReconnectDelay = TimeSpan.FromMilliseconds(100));
        await using var writer = new PostgreSqlStore(new PostgreSqlStorageOptions { DataSource = Server.DataSource, Schema = listener.Options.Schema });
        int signals = 0;
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = listener.Subscribe(() =>
        {
            Interlocked.Increment(ref signals);
            signalled.TrySetResult();
        });
        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);   // LISTEN is active

        // Kill the LISTEN backend; while it reconnects, another process commits.
        string listen = "LISTEN \"" + listener.Options.Channel + "\"";
        await Server.ExecuteAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query = '{listen}'");
        int beforeCommit = Volatile.Read(ref signals);
        await AppendAsync(writer, Stream, ExpectedVersion.NoStream);

        // The reconnect raises a catch-up signal, so the commit made while disconnected is never missed.
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref signals) == beforeCommit && TimeProvider.System.GetUtcNow() < deadline)
        {
            await RealClock.WaitAsync(TimeSpan.FromMilliseconds(20));
        }

        Volatile.Read(ref signals).ShouldBeGreaterThan(beforeCommit);

        // And live notifications flow again on the new connection.
        signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await RealClock.WaitAsync(TimeSpan.FromMilliseconds(300));
        signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await AppendAsync(writer, new StreamId("pg-b-2"), ExpectedVersion.NoStream);
        await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    [Trait("Id", "SCK-26")]
    [Trait("Covers", "ADR-010")]
    public async Task InProgressWritingTransaction_HoldsTheHorizon_UntilItEnds()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        await using NpgsqlConnection other = await Server.DataSource.OpenConnectionAsync(Ct);
        await using NpgsqlTransaction open = await other.BeginTransactionAsync(Ct);
        await using (var write = new NpgsqlCommand($"INSERT INTO \"{store.Options.Schema}\".nodes (node_id, roles, heartbeat_at) VALUES ('x', 0, now())", other, open))
        {
            await write.ExecuteNonQueryAsync(Ct);   // takes a transaction id
        }

        CommitResult committed = await AppendAsync(store, Stream, ExpectedVersion.NoStream);
        LogBatch held = await store.ReadAsync(GlobalPosition.Start, BucketRange.All, 10, Ct);
        await RealClock.WaitAsync(TimeSpan.FromMilliseconds(100));
        TimeSpan age = await store.GetHorizonAgeAsync(Ct);
        await open.CommitAsync(Ct);
        LogBatch released = await store.ReadAsync(GlobalPosition.Start, BucketRange.All, 10, Ct);

        held.Events.ShouldBeEmpty();
        held.Horizon.ShouldBeLessThan(committed.LastPosition!.Value);
        age.ShouldBeGreaterThan(TimeSpan.Zero);
        released.Events.Single().Position.ShouldBe(committed.LastPosition!.Value);
        released.Horizon.ShouldBeGreaterThanOrEqualTo(committed.LastPosition!.Value);
    }

    [Fact]
    [Trait("Id", "SCK-18")]
    [Trait("Covers", "ADR-028")]
    public async Task Payloads_RoundTripByteForByte_IncludingWhitespaceKeyOrderAndNulEscapes()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        const string data = "{ \"b\" : 1,\n  \"a\": [ 1, 2 ], \"b\": \"dup\", \"z\": \"\\u0000\" }";

        await AppendAsync(store, Stream, ExpectedVersion.NoStream, data);
        await using (IStorageSession session = await store.BeginAsync(Tenant, Ct))
        {
            session.Documents.Upsert("docs", "1", 1, Encoding.UTF8.GetBytes(data));
            session.SaveProcessState("pm", "1", 0, Encoding.UTF8.GetBytes(data), completed: false);
            await session.CommitAsync(Ct);
        }

        Encoding.UTF8.GetString((await store.ReadStreamAsync(Tenant, Stream, new StreamVersion(1), 1, Ct)).Events[0].Data.Span).ShouldBe(data);
        Encoding.UTF8.GetString((await ((IDocumentReader)store).GetAsync(Tenant, "docs", "1", Ct))!.Data.Span).ShouldBe(data);
        await using IStorageSession read = await store.BeginAsync(Tenant, Ct);
        Encoding.UTF8.GetString((await read.LoadProcessStateAsync("pm", "1", Ct))!.State.Span).ShouldBe(data);
    }

    [Fact]
    [Trait("Id", "SCK-63")]
    [Trait("Covers", "doc 05 8.1")]
    public async Task SessionConnection_RunsCustomSqlInTheSameTransaction()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        string table = $"\"{store.Options.Schema}\".custom_view";
        await Server.ExecuteAsync($"CREATE TABLE {table} (id int PRIMARY KEY)");
        await AppendAsync(store, Stream, ExpectedVersion.NoStream);

        await using (IStorageSession conflicting = await store.BeginAsync(Tenant, Ct))
        {
            await InsertAsync(conflicting, table, 1);
            conflicting.Append(Stream, "pg", ExpectedVersion.NoStream, [StorageConformance.Event()]);
            await Should.ThrowAsync<ConcurrencyException>(() => conflicting.CommitAsync(Ct).AsTask());
        }

        await using (IStorageSession committed = await store.BeginAsync(Tenant, Ct))
        {
            await InsertAsync(committed, table, 2);
            committed.Append(Stream, "pg", ExpectedVersion.Exact(new StreamVersion(1)), [StorageConformance.Event()]);
            await committed.CommitAsync(Ct);
        }

        await using NpgsqlCommand count = Server.DataSource.CreateCommand($"SELECT array_agg(id ORDER BY id) FROM {table}");
        ((int[])(await count.ExecuteScalarAsync(Ct))!).ShouldBe([2]);
    }

    [Fact]
    [Trait("Id", "SCK-20")]
    public async Task Commit_WithoutWrites_IsANoOp_AndDisposeRollsBack()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        await using (IStorageSession empty = await store.BeginAsync(Tenant, Ct))
        {
            (await empty.CommitAsync(Ct)).ShouldBe(default);
        }

        await using (IStorageSession abandoned = await store.BeginAsync(Tenant, Ct))
        {
            abandoned.Append(Stream, "pg", ExpectedVersion.NoStream, [StorageConformance.Event()]);
        }

        (await store.GetStreamInfoAsync(Tenant, Stream, Ct)).ShouldBeNull();
        await using IStorageSession done = await store.BeginAsync(Tenant, Ct);
        await done.CommitAsync(Ct);
        Should.Throw<InvalidOperationException>(() => done.EnqueueOutbox(Row("k", DateTimeOffset.UnixEpoch)));
    }

    private static async Task InsertAsync(IStorageSession session, string table, int id)
    {
        await using var command = new NpgsqlCommand($"INSERT INTO {table} (id) VALUES ({id})", (NpgsqlConnection)session.Connection!, (NpgsqlTransaction)session.Transaction!);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<CommitResult> AppendAsync(PostgreSqlStore store, StreamId stream, ExpectedVersion expected, string data = "{\"n\":1}")
    {
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        session.Append(stream, "pg", expected, [StorageConformance.Event(data: data)]);
        return await session.CommitAsync(Ct);
    }

    private static async Task EnqueueAsync(PostgreSqlStore store, OutboxMessage message)
    {
        await using IStorageSession session = await store.BeginAsync(Tenant, Ct);
        session.EnqueueOutbox(message);
        await session.CommitAsync(Ct);
    }

    private static OutboxMessage Row(string key, DateTimeOffset deliverAt)
    {
        var envelope = new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "pg.sent",
            TypeVersion = 1,
            Source = "pg",
            Time = DateTimeOffset.UnixEpoch,
            PartitionKey = key,
            Body = Encoding.UTF8.GetBytes("{}"),
        };
        return new OutboxMessage(envelope, "dest", OutboxKind.Scheduled, deliverAt, 0);
    }
}
