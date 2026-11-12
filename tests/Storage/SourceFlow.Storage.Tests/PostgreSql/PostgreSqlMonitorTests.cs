using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Storage.PostgreSQL;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>The PostgreSQL monitor readings behind the health checks and gauges (doc 08 §3.2, §5; WP-4.3).</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlMonitorTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-HC-02")]
    [Trait("Covers", "doc 08 5")]
    public async Task Probe_ReportsTheSchema_AndFailsOnAnUnmigratedOne()
    {
        await using PostgreSqlStore migrated = await Server.CreateStoreAsync();
        await using var unmigrated = new PostgreSqlStore(new PostgreSqlStorageOptions { DataSource = Server.DataSource, Schema = PostgreSqlServer.NewSchema("empty") });

        StoreProbe current = await migrated.ProbeAsync(Ct);
        StoreProbe missing = await unmigrated.ProbeAsync(Ct);

        current.SchemaCurrent.ShouldBeTrue(current.Detail);
        missing.SchemaCurrent.ShouldBeFalse();
        missing.Detail.ShouldContain("not installed");
    }

    [Fact]
    [Trait("Id", "UT-HC-10")]
    [Trait("Covers", "doc 08 3.2")]
    public async Task Readings_CountTheOutbox_DeadLetters_Events_AndProcesses()
    {
        using IHost host = await StartAsync(sf =>
        {
            sf.Roles = SourceFlowRoles.Api;   // nothing relays or projects: the backlog stays
            sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
        });
        PostgreSqlStore store = host.Services.GetRequiredService<PostgreSqlStore>();
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        await commands.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), null, Ct);
        await commands.SendAsync(new OpenAccount(AccountId.New(), "Bob", 2m), null, Ct);
        StoredEvent first = (await store.ReadAsync(GlobalPosition.Start, BucketRange.All, 1, Ct)).Events.ShouldHaveSingleItem();
        await store.ParkAsync(new DeadLetter
        {
            Id = Guid.CreateVersion7(TimeProvider.System.GetUtcNow()),
            Consumer = "accounts",
            Source = "test",
            Envelope = first.ToEnvelope(),
            Reason = "test",
            Attempts = 1,
            FirstFailedAt = TimeProvider.System.GetUtcNow().AddMinutes(-1),
            ParkedAt = TimeProvider.System.GetUtcNow(),
        }, Ct);

        IReadOnlyList<OutboxBacklog> outbox = await store.GetOutboxBacklogAsync(Ct);
        IReadOnlyDictionary<string, int> deadLetters = await store.CountParkedDeadLettersAsync(Ct);
        long all = await store.CountEventsAfterAsync(GlobalPosition.Start, BucketRange.All, 1000, Ct);
        long capped = await store.CountEventsAfterAsync(GlobalPosition.Start, BucketRange.All, 1, Ct);
        long afterFirst = await store.CountEventsAfterAsync(first.Position, BucketRange.All, 1000, Ct);

        outbox.Sum(o => o.Pending).ShouldBe(2);
        outbox.ShouldAllBe(o => o.OldestDueAt != null && o.OldestDueAt <= TimeProvider.System.GetUtcNow().AddSeconds(5));
        deadLetters["accounts"].ShouldBe(1);
        all.ShouldBe(2);
        capped.ShouldBe(1, "counting stops at the cap");
        afterFirst.ShouldBe(1);
        (await store.CountActiveProcessesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-HC-07")]
    [Trait("Covers", "doc 05 5.4;doc 08 5")]
    public async Task AHeldWritingTransaction_DegradesTheSubscriptionsCheck()
    {
        using IHost host = await StartAsync(sf => sf.Configure(o =>
        {
            o.Health.HorizonAgeDegraded = TimeSpan.FromSeconds(1);
            o.MonitorInterval = TimeSpan.FromMilliseconds(200);
        }));
        HealthCheckService health = host.Services.GetRequiredService<HealthCheckService>();

        await using NpgsqlConnection holder = await Server.DataSource.OpenConnectionAsync(Ct);
        await using NpgsqlTransaction transaction = await holder.BeginTransactionAsync(Ct);
        await using (var xid = new NpgsqlCommand("SELECT pg_current_xact_id()", holder, transaction))
        {
            await xid.ExecuteScalarAsync(Ct);   // a writing transaction: it has a transaction id
        }

        await RealClock.WaitAsync(TimeSpan.FromSeconds(1.6));
        HealthReportEntry held = (await health.CheckHealthAsync(c => c.Name == "sourceflow.subscriptions", Ct)).Entries["sourceflow.subscriptions"];
        await transaction.RollbackAsync(Ct);
        await RealClock.WaitAsync(TimeSpan.FromMilliseconds(600));
        HealthReportEntry released = (await health.CheckHealthAsync(c => c.Name == "sourceflow.subscriptions", Ct)).Entries["sourceflow.subscriptions"];

        held.Status.ShouldBe(HealthStatus.Degraded, held.Description);
        held.Description!.ShouldContain("horizon");
        released.Status.ShouldBe(HealthStatus.Healthy, released.Description);
    }

    private async Task<IHost> StartAsync(Action<SourceFlowBuilder> configure)
    {
        string schema = PostgreSqlServer.NewSchema();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddHealthChecks().AddSourceFlow();
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UsePostgreSql(o =>
            {
                o.DataSource = Server.DataSource;
                o.Schema = schema;
            });
            configure(sf);
        });
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }
}
