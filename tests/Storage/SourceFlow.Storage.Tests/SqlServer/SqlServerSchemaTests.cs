using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Storage.SqlServer;

namespace SourceFlow.Storage.Tests.SqlServer;

/// <summary>The SQL Server migrator (SCK-M01..06, doc 05 §11) and monitor readings (UT-HC, doc 08 §5) on SQL Server (WP-4.4).</summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "SqlServer")]
[Trait("Requires", "Docker")]
public sealed class SqlServerSchemaTests(SqlServerServer server) : SqlServerTest(server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "SCK-M01")]
    [Trait("Covers", "doc 05 11")]
    public async Task Migrate_IsIdempotent_AndRecordsHistory()
    {
        var migrator = new SqlServerSchemaMigrator(Server.ConnectionString, SqlServerServer.NewSchema("m"));

        IReadOnlyList<int> first = await migrator.MigrateAsync(Ct);
        IReadOnlyList<int> second = await migrator.MigrateAsync(Ct);

        first.ShouldBe([1, 2, 3]);
        second.ShouldBeEmpty();
        (await migrator.GetAppliedVersionsAsync(Ct)).ShouldBe([1, 2, 3]);
        await migrator.VerifyAsync(Ct);
    }

    [Fact]
    [Trait("Id", "SCK-M02")]
    public async Task ConcurrentMigrators_ApplyOnce()
    {
        string schema = SqlServerServer.NewSchema("m");

        IReadOnlyList<int>[] results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => new SqlServerSchemaMigrator(Server.ConnectionString, schema).MigrateAsync(Ct))));

        results.Count(r => r.Count == 3).ShouldBe(1, "the application lock serialises migrators: one applies, the others find nothing to do");
        results.Count(r => r.Count == 0).ShouldBe(3);
    }

    [Fact]
    [Trait("Id", "SCK-M03")]
    public async Task Verify_FailsOnAMissingSchema_AndOnAModifiedScript()
    {
        string schema = SqlServerServer.NewSchema("m");
        var migrator = new SqlServerSchemaMigrator(Server.ConnectionString, schema);

        (await Should.ThrowAsync<SqlServerSchemaException>(() => migrator.VerifyAsync(Ct))).Message.ShouldContain("not installed");
        await migrator.MigrateAsync(Ct);
        await Server.ExecuteAsync($"UPDATE [{schema}].schema_history SET checksum = N'tampered' WHERE version = 1");

        (await Should.ThrowAsync<SqlServerSchemaException>(() => migrator.VerifyAsync(Ct))).Message.ShouldContain("was modified");
        (await Should.ThrowAsync<SqlServerSchemaException>(() => migrator.MigrateAsync(Ct))).Message.ShouldContain("was modified");
    }

    [Fact]
    [Trait("Id", "SCK-M04")]
    public void Script_HasTheLockTheTablesAndTheHistory()
    {
        string script = new SqlServerSchemaMigrator(Server.ConnectionString, "sales").Script();

        script.ShouldStartWith("SET XACT_ABORT ON;\nBEGIN TRANSACTION;\nEXEC sp_getapplock");
        script.ShouldContain("CREATE TABLE [sales].events");
        script.ShouldContain("CREATE UNIQUE INDEX ix_events_commit_order ON [sales].events (rv) INCLUDE (bucket);");
        script.ShouldContain("INSERT INTO [sales].schema_history (version, name, checksum) VALUES (1, N'initial', N'" + SqlServerSchemaMigrator.EmbeddedScripts[0].Checksum + "');");
        script.ShouldEndWith("COMMIT TRANSACTION;\n");
    }

    [Theory]
    [Trait("Id", "SCK-M05")]
    [InlineData("Sales")]
    [InlineData("sales; DROP TABLE x")]
    [InlineData("")]
    public void InvalidSchemaNames_AreRejected(string schema)
    {
        Should.Throw<InvalidOperationException>(() => new SqlServerStore(new SqlServerStorageOptions { ConnectionString = "Server=x", Schema = schema }));
    }

    [Fact]
    [Trait("Id", "UT-HC-10")]
    [Trait("Covers", "doc 08 3.2")]
    public async Task MonitorReadings_AndTheStoreHealthCheck()
    {
        string schema = SqlServerServer.NewSchema();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddHealthChecks().AddSourceFlow();
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Roles = SourceFlowRoles.Api;   // nothing relays: the outbox backlog stays
            sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
            sf.UseSqlServer(Server.ConnectionString, o => o.Schema = schema);
        });
        using IHost host = builder.Build();
        await host.StartAsync(Ct);
        SqlServerStore store = host.Services.GetRequiredService<SqlServerStore>();
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        await commands.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), null, Ct);
        await commands.SendAsync(new OpenAccount(AccountId.New(), "Bob", 2m), null, Ct);
        StoredEvent first = (await store.ReadAsync(GlobalPosition.Start, BucketRange.All, 1, Ct)).Events.ShouldHaveSingleItem();

        StoreProbe probe = await store.ProbeAsync(Ct);
        IReadOnlyList<OutboxBacklog> outbox = await store.GetOutboxBacklogAsync(Ct);
        long all = await store.CountEventsAfterAsync(GlobalPosition.Start, BucketRange.All, 1000, Ct);
        long afterFirst = await store.CountEventsAfterAsync(first.Position, BucketRange.All, 1000, Ct);
        HealthReport health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "sourceflow.store", Ct);

        probe.SchemaCurrent.ShouldBeTrue(probe.Detail);
        outbox.Sum(o => o.Pending).ShouldBe(2);
        outbox.ShouldAllBe(o => o.OldestDueAt != null);
        all.ShouldBe(2);
        afterFirst.ShouldBe(1);
        (await store.CountParkedDeadLettersAsync(Ct)).ShouldBeEmpty();
        (await store.CountActiveProcessesAsync(Ct)).ShouldBeEmpty();
        (await store.GetHorizonAgeAsync(Ct)).ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        health.Entries["sourceflow.store"].Status.ShouldNotBe(HealthStatus.Unhealthy, health.Entries["sourceflow.store"].Description);
    }
}
