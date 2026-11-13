using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Tool;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>E2E-25..28 (plan 06): <c>dotnet sourceflow</c> against PostgreSQL, as the global tool and inside a service.</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlCliTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "E2E-25")]
    [Trait("Covers", "doc 04 9.3")]
    public async Task GlobalTool_MigratesTheSchema_AndReplaysADeadLetterThroughTheOutbox()
    {
        string schema = PostgreSqlServer.NewSchema("cli");
        (int migrated, string first, _) = await RunAsync(null, "db", "migrate", "--connection", Server.ConnectionString, "--schema", schema);
        (int again, string second, _) = await RunAsync(null, "db", "migrate", "--connection", Server.ConnectionString, "--schema", schema);

        migrated.ShouldBe(0);
        first.ShouldContain("Applied V001");
        again.ShouldBe(0);
        second.ShouldContain("up to date");

        await using var store = new PostgreSqlStore(new PostgreSqlStorageOptions { DataSource = Server.DataSource, Schema = schema });
        var envelope = new MessageEnvelope
        {
            MessageId = MessageIds.New(TimeProvider.System),
            Type = "bank.account.open",
            TypeVersion = 1,
            Source = "accounts",
            Time = TimeProvider.System.GetUtcNow(),
            Body = "{}"u8.ToArray(),
        };
        var id = Guid.CreateVersion7();
        await store.ParkAsync(new DeadLetter
        {
            Id = id,
            Consumer = "accounts",
            Source = "receive",
            Envelope = envelope,
            Destination = "accounts",
            Reason = "PermanentFailure",
            FirstFailedAt = envelope.Time,
            ParkedAt = envelope.Time,
        }, Ct);

        (int listed, string list, _) = await RunAsync(null, "dlq", "list", "--connection", Server.ConnectionString, "--schema", schema);
        (int replayed, _, _) = await RunAsync(null, "dlq", "replay", id.ToString(), "--connection", Server.ConnectionString, "--schema", schema, "--note", "fixed");

        listed.ShouldBe(0);
        list.ShouldContain(id.ToString());
        replayed.ShouldBe(0);
        (await store.GetAsync(id, Ct))!.Status.ShouldBe(DeadLetterStatus.Replayed);
        (await store.DueOutboxCountAsync(Ct)).ShouldBe(1);   // the service's relay sends it
    }

    [Fact]
    [Trait("Id", "E2E-26")]
    [Trait("Covers", "doc 06 6")]
    public async Task GlobalTool_RebuildsAProjectionInPlace_TheServiceReplaysIt()
    {
        string schema = PostgreSqlServer.NewSchema("cli");
        var id = AccountId.New();
        using (IHost running = await StartAsync(schema))
        {
            await running.Services.GetRequiredService<ICommandSender>().SendAsync(new OpenAccount(id, "Ada", 5m), null, Ct);
            await EventuallyAsync(running, id, exists: true);
            await running.StopAsync(Ct);   // releases the subscription leases
        }

        (int rebuilt, string output, _) = await RunAsync(null, "projection", "rebuild", "account-summary@1", "--in-place", "--connection", Server.ConnectionString, "--schema", schema);

        rebuilt.ShouldBe(0);
        output.ShouldContain("dropped 1 document(s)");
        using IHost restarted = await StartAsync(schema);
        await EventuallyAsync(restarted, id, exists: true);
    }

    [Fact]
    [Trait("Id", "E2E-08")]
    [Trait("Id", "E2E-26")]
    [Trait("Covers", "doc 06 6;ADR-032")]
    public async Task GlobalTool_RebuildsBlueGreen_WhileTheServiceRuns_AndQueriesFollowTheAlias()
    {
        string schema = PostgreSqlServer.NewSchema("cli");
        var ada = AccountId.New();
        using IHost host = await StartAsync(schema);
        await host.Services.GetRequiredService<ICommandSender>().SendAsync(new OpenAccount(ada, "Ada", 5m), null, Ct);
        await EventuallyAsync(host, ada, exists: true);
        PostgreSqlStore store = host.Services.GetRequiredService<PostgreSqlStore>();
        await AliasAsync(store, "account-summary@1");

        (int rebuilt, string output, _) = await RunAsync(null, "projection", "rebuild", "account-summary@1", "--connection", Server.ConnectionString, "--schema", schema);
        rebuilt.ShouldBe(0);
        output.ShouldContain("building account-summary@1.2 beside account-summary@1");
        await AliasAsync(store, "account-summary@1.2");

        // The service's queries now read the rebuilt generation (the compiler binds the collection as a parameter).
        (await ((IDocumentReader)store).GetAsync(MessageEnvelope.DefaultTenant, "account-summary@1.2", ada.ToStreamSegment(), Ct)).ShouldNotBeNull();
        await store.DropCollectionAsync("account-summary@1", Ct);   // what the retention would do later
        Query.QueryPage page = await host.Services.GetRequiredService<Query.IQueryService>()
            .QueryPageAsync("from readmodel \"account-summary\" | where holder = @h | project holder, balance", new Dictionary<string, object?> { ["h"] = "Ada" }, cancellationToken: Ct);
        page.Rows.ShouldHaveSingleItem()["balance"].ShouldBe(5m);
        (int listed, string generations, _) = await RunAsync(null, "projection", "generations", "--connection", Server.ConnectionString, "--schema", schema);
        listed.ShouldBe(0);
        generations.ShouldContain("account-summary  serving account-summary@1.2  building -  previous account-summary@1");
    }

    [Fact]
    [Trait("Id", "E2E-27")]
    [Trait("Id", "E2E-28")]
    [Trait("Covers", "doc 07;doc 14 7.3")]
    public async Task InsideTheService_TopologyValidates_AndExplainShowsTheParameterisedSql()
    {
        using IHost host = await StartAsync(PostgreSqlServer.NewSchema("cli"));

        (int valid, string validated, _) = await RunAsync(host.Services, "topology", "validate");
        (int explained, string plan, _) = await RunAsync(host.Services, "query", "from readmodel \"account-summary\" | where holder = @h", "--param", "h=Ada", "--explain");

        valid.ShouldBe(0);
        validated.ShouldContain("in place");
        explained.ShouldBe(0);
        plan.ShouldContain("engine:     postgresql");
        plan.ShouldContain("$1::text");
        plan.ShouldNotContain("Ada");   // values are parameters, never in the statement (I-3)
    }

    [Fact]
    [Trait("Id", "E2E-25")]
    [Trait("Covers", "ADR-022")]
    public async Task InsideTheService_DbMigrate_KeepsTheServicesTenantIsolation()
    {
        string schema = PostgreSqlServer.NewSchema("cli");
        await new PostgreSqlSchemaMigrator(Server.DataSource, schema, TenantIsolation.RowLevelSecurity).MigrateAsync(Ct);
        using (IHost rls = await StartAsync(schema, TenantIsolation.RowLevelSecurity))
        {
            (int migrated, string output, string error) = await RunAsync(rls.Services, "db", "migrate");   // no --rls

            migrated.ShouldBe(0, error);
            output.ShouldContain("up to date");
        }

        // The policies are still in place: the schema verifies as row-level security, not as runtime isolation.
        await new PostgreSqlSchemaMigrator(Server.DataSource, schema, TenantIsolation.RowLevelSecurity).VerifyAsync(Ct);
        await Should.ThrowAsync<SchemaMigrationException>(() => new PostgreSqlSchemaMigrator(Server.DataSource, schema, TenantIsolation.Runtime).VerifyAsync(Ct));

        using IHost runtime = await StartAsync(PostgreSqlServer.NewSchema("cli"));
        (int refused, _, string conflict) = await RunAsync(runtime.Services, "db", "migrate", "--rls");
        refused.ShouldBe(1);
        conflict.ShouldContain("--rls conflicts");
    }

    private async Task<IHost> StartAsync(string schema, TenantIsolation isolation = TenantIsolation.Runtime)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<Bank.Contracts.ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.Projections(p => p.Add<AccountSummaryProjection>().Async());
            sf.Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(50));
            sf.UsePostgreSql(o =>
            {
                o.DataSource = Server.DataSource;
                o.Schema = schema;
                o.TenantIsolation = isolation;
                if (isolation == TenantIsolation.RowLevelSecurity)
                {
                    o.SystemDataSource = Server.DataSource;
                }
            });
            sf.UseInMemoryTransport();
        });
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static async Task AliasAsync(PostgreSqlStore store, string target)
    {
        for (int i = 0; i < 300; i++)
        {
            if ((await store.ListAliasesAsync(Ct)).Any(a => a.Alias == "account-summary" && a.Target == target))
            {
                return;
            }

            await RealClock.WaitAsync(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"account-summary did not switch to {target}.");
    }

    private static async Task EventuallyAsync(IHost host, AccountId id, bool exists)
    {
        IDocumentReader reader = host.Services.GetRequiredService<PostgreSqlStore>();
        for (int i = 0; i < 300; i++)
        {
            if ((await reader.GetAsync(MessageEnvelope.DefaultTenant, "account-summary@1", id.ToStreamSegment(), Ct) is not null) == exists)
            {
                return;
            }

            await RealClock.WaitAsync(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The account summary didn't appear.");
    }

    [Fact]
    [Trait("Id", "SCK-67")]
    [Trait("Covers", "doc 05 9")]
    public async Task GlobalTool_PartitionsTheEventsTable_AndManagesPartitions()
    {
        string schema = PostgreSqlServer.NewSchema("cli");
        string[] target = ["--connection", Server.ConnectionString, "--schema", schema];

        (int migrated, _, _) = await RunAsync(null, ["db", "migrate", "--partition-events", .. target]);
        (int listed, string list, _) = await RunAsync(null, ["db", "partitions", "list", .. target]);
        (int ensured, string ensure, _) = await RunAsync(null, ["db", "partitions", "ensure", .. target]);
        (int refused, _, string error) = await RunAsync(null, ["db", "partitions", "detach", "events_initial", .. target]);
        (int scripted, string script, _) = await RunAsync(null, "db", "script", "--schema", "sales", "--partition-events");

        migrated.ShouldBe(0);
        listed.ShouldBe(0);
        list.ShouldContain("events_initial   (start)");
        list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1 + PostgreSqlPartitioning.MigratorMonthsAhead);
        ensured.ShouldBe(0);
        ensure.ShouldContain("Created 0 partition(s).");
        refused.ShouldBe(1);
        error.ShouldContain("not in the past");
        scripted.ShouldBe(0);
        script.ShouldContain("PARTITION BY RANGE (created_at)");
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(IServiceProvider? services, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await SourceFlowCli.RunAsync(args, services, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }
}
