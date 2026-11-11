using System.Text;
using Npgsql;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>The whole storage conformance kit with row-level security on, through the DML-only application role (WP-2.6).</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlRowLevelSecurityConformanceTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    public static TheoryData<string> CaseIds => new(StorageConformance.Cases.Where(c => !c.RequiresClockControl).Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait("Covers", "ADR-022")]
    public async Task Sck(string id)
    {
        ConformanceCase<IStorageFixture> @case = StorageConformance.Cases.Single(c => c.Id == id);
        await using var fixture = new PostgreSqlStorageFixture(await Server.CreateRowLevelSecurityStoreAsync());

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}

/// <summary>SCK-56..60: tenant isolation enforced by the database (ADR-022, WP-2.6).</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlTenancyTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static readonly StreamId Stream = new("acct-1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "SCK-59")]
    [Trait("Covers", "ADR-022")]
    public async Task ForgedQueries_SeeOnlyTheSessionTenant_AndCannotWriteAnotherTenant()
    {
        await using PostgreSqlStore store = await Server.CreateRowLevelSecurityStoreAsync();
        await SeedAsync(store, "eu", 2);
        await SeedAsync(store, "us", 3);
        string events = $"\"{store.Options.Schema}\".events";
        string documents = $"\"{store.Options.Schema}\".documents";

        // User SQL inside a session (an EF Core projection, a custom projection) with no tenant predicate at all.
        await using (IStorageSession eu = await store.BeginAsync("eu", Ct))
        {
            eu.Documents.Upsert("docs", "probe", 1, "{}"u8.ToArray());   // queues nothing on the wire yet
            (await ScalarAsync<long>(eu, $"SELECT count(*) FROM {events}")).ShouldBe(2);
            (await ScalarAsync<long>(eu, $"SELECT count(*) FROM {events} WHERE tenant_id = 'us'")).ShouldBe(0);
            (await ScalarAsync<long>(eu, $"SELECT count(*) FROM {documents}")).ShouldBe(1);

            PostgresException forged = await Should.ThrowAsync<PostgresException>(() =>
                ScalarAsync<long>(eu, $"WITH i AS (INSERT INTO {documents} (tenant_id, collection, id, version, data) VALUES ('us', 'docs', 'x', 1, '{{}}') RETURNING 1) SELECT count(*) FROM i"));
            forged.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        // Without a tenant setting the application role sees nothing.
        await using NpgsqlDataSource app = NpgsqlDataSource.Create(Server.ConnectionStringFor(Server.AppRole));
        await using NpgsqlCommand bare = app.CreateCommand($"SELECT count(*) FROM {events}");
        ((long)(await bare.ExecuteScalarAsync(Ct))!).ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "SCK-60")]
    [Trait("Covers", "ADR-022")]
    public async Task TheGlobalLog_UsesTheSystemRole_AndSeesEveryTenant()
    {
        await using PostgreSqlStore store = await Server.CreateRowLevelSecurityStoreAsync();
        await SeedAsync(store, "eu", 1);
        CommitResult last = await SeedAsync(store, "us", 1);

        LogBatch log = await ReadFinalAsync(store, last.LastPosition!.Value);

        log.Events.Select(e => e.TenantId).ShouldBe(["eu", "us"]);
        log.Events.ShouldAllBe(e => e.Bucket == Buckets.Of(e.TenantId, e.StreamId));
        Buckets.Of("eu", Stream).ShouldNotBe(Buckets.Of("us", Stream));
    }

    [Fact]
    [Trait("Id", "SCK-60")]
    [Trait("Covers", "ADR-022")]
    public async Task TenantScopedReads_OutsideSessions_AreIsolatedByTheDatabase()
    {
        await using PostgreSqlStore store = await Server.CreateRowLevelSecurityStoreAsync();
        await SeedAsync(store, "eu", 2);
        await store.SaveAsync("eu", new Snapshot(Stream, new StreamVersion(2), "Account", "h", "{}"u8.ToArray()), Ct);
        await store.ArchiveStreamAsync("us", Stream, Ct);   // another tenant's archive touches nothing

        (await store.ReadStreamAsync("eu", Stream, new StreamVersion(1), 10, Ct)).Events.Count.ShouldBe(2);
        (await store.ReadStreamAsync("us", Stream, new StreamVersion(1), 10, Ct)).Events.ShouldBeEmpty();
        (await store.GetStreamInfoAsync("eu", Stream, Ct))!.IsArchived.ShouldBeFalse();
        (await ((ISnapshotStore)store).GetAsync("us", Stream, Ct)).ShouldBeNull();
        (await ((IDocumentReader)store).ListAsync("us", "docs", 10, Ct)).ShouldBeEmpty();
        (await ((IDocumentReader)store).ListAsync("eu", "docs", 10, Ct)).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "SCK-M07")]
    [Trait("Covers", "ADR-022")]
    public async Task Verify_RejectsAnIsolationMismatch_AndMigrateFixesIt()
    {
        string schema = PostgreSqlServer.NewSchema();
        await new PostgreSqlSchemaMigrator(Server.DataSource, schema).MigrateAsync(Ct);
        var rls = new PostgreSqlSchemaMigrator(Server.DataSource, schema, TenantIsolation.RowLevelSecurity);
        var runtime = new PostgreSqlSchemaMigrator(Server.DataSource, schema, TenantIsolation.Runtime);

        await runtime.VerifyAsync(Ct);
        (await Should.ThrowAsync<SchemaMigrationException>(() => rls.VerifyAsync(Ct))).Message.ShouldContain("events");
        (await rls.MigrateAsync(Ct)).ShouldBeEmpty();
        await rls.VerifyAsync(Ct);
        await Should.ThrowAsync<SchemaMigrationException>(() => runtime.VerifyAsync(Ct));
        rls.Script().ShouldContain($"ALTER TABLE \"{schema}\".events ENABLE ROW LEVEL SECURITY, FORCE ROW LEVEL SECURITY;");
    }

    private static async Task<CommitResult> SeedAsync(PostgreSqlStore store, string tenant, int events)
    {
        await using IStorageSession session = await store.BeginAsync(tenant, Ct);
        session.Append(Stream, "acct", ExpectedVersion.NoStream, [.. Enumerable.Range(0, events).Select(_ => StorageConformance.Event())]);
        session.Documents.Upsert("docs", Stream.Value, events, Encoding.UTF8.GetBytes($"{{\"tenant\":\"{tenant}\"}}"));
        return await session.CommitAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(IStorageSession session, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)session.Connection!, (NpgsqlTransaction)session.Transaction!);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<LogBatch> ReadFinalAsync(PostgreSqlStore store, GlobalPosition through)
    {
        while (true)
        {
            LogBatch batch = await store.ReadAsync(GlobalPosition.Start, BucketRange.All, 100, Ct);
            if (batch.Horizon >= through)
            {
                return batch;
            }

            await RealClock.WaitAsync(TimeSpan.FromMilliseconds(10));
        }
    }
}
