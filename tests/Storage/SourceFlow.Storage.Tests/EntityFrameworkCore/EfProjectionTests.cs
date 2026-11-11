using Bank.Accounts;
using Bank.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage.EntityFrameworkCore;
using SourceFlow.Storage.InMemory;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Testing.Conformance;

namespace SourceFlow.Storage.Tests.EntityFrameworkCore;

/// <summary>UT-EF: EF Core projection wiring that needs no database (WP-2.7).</summary>
[Trait("Category", "Unit")]
[Trait("Provider", "EfCore")]
public sealed class EfProjectionUnitTests
{
    [Fact]
    [Trait("Id", "UT-EF-01")]
    public void SessionProjection_WithoutItsUnitOfWork_FailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "reporting";
            sf.AddModule<AccountsModule>();
            sf.AddModule<ReportingModule>();
            sf.Projections(p => p.Add<AccountActivityProjection>().Inline());
            sf.UseInMemoryStorage();
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<SourceFlowRuntime>())
            .Message.ShouldContain("UseEntityFrameworkProjections<ReportingDbContext>");
    }

    [Fact]
    [Trait("Id", "UT-EF-02")]
    public async Task EfContext_NeedsARelationalSession()
    {
        var store = new InMemoryStore();
        var factory = new EfSessionContextFactory<ReportingDbContext>(ReportingDbContext.Configure, ReportingDbContext.Create);
        await using IStorageSession session = await store.BeginAsync(MessageEnvelope.DefaultTenant, TestContext.Current.CancellationToken);

        (await Should.ThrowAsync<InvalidOperationException>(() => factory.CreateAsync(session, TestContext.Current.CancellationToken).AsTask()))
            .Message.ShouldContain("relational store");
    }

    [Fact]
    [Trait("Id", "UT-EF-03")]
    public void SessionProjection_IsRegisteredByItsModule_AsASessionProjection()
    {
        var model = new SourceFlowModelBuilder();
        ReportingModule.Register(model);
        AccountsModule.Register(model);

        ProjectionDescriptor projection = model.Build().Projections
            .Single(p => p.ProjectionType == typeof(AccountActivityProjection));
        projection.ShouldNotBeAssignableTo<DocumentProjectionDescriptor>();
        projection.Collection.ShouldBe("account-activity@1");
        projection.HandledEvents.ShouldBe([typeof(AccountOpened), typeof(MoneyDeposited), typeof(MoneyWithdrawn)], ignoreOrder: true);
    }
}

/// <summary>SCK-61..64 and UT-EF-04..07: EF Core read models in the storage transaction, on PostgreSQL (doc 05 §10).</summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "EfCore")]
[Trait("Requires", "Docker")]
public sealed class EfProjectionTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await ReportingDbContext.EnsureCreatedAsync(Server.DataSource);
    }

    [Fact]
    [Trait("Id", "SCK-61")]
    [Trait("Covers", "doc 05 8.1")]
    public async Task InlineEfProjection_CommitsWithTheEvents()
    {
        using IHost host = await StartAsync(ProjectionMode.Inline);
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        var id = AccountId.New();

        await commands.SendAsync(new OpenAccount(id, "Ada", 100m), null, Ct);
        await commands.SendAsync(new Deposit(id, 50m), null, Ct);
        await commands.SendAsync(new Withdraw(id, 30m), null, Ct);
        (await commands.SendAsync(new Withdraw(id, 1_000m), null, Ct)).Status.ShouldBe(CommandStatus.Rejected);

        AccountActivity row = (await ActivityAsync(id))!;
        row.Holder.ShouldBe("Ada");
        row.Balance.ShouldBe(120m);
        row.Movements.ShouldBe(2);
        row.Version.ShouldBe(3);
    }

    [Fact]
    [Trait("Id", "SCK-62")]
    [Trait("Covers", "ADR-008")]
    public async Task InlineEfProjection_ConflictingAttempts_LeaveNoTrace()
    {
        using IHost host = await StartAsync(ProjectionMode.Inline, sf => sf.Configure(o => o.MaxConcurrencyRetries = 50));
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        var id = AccountId.New();
        await commands.SendAsync(new OpenAccount(id, "Ada", 1m), null, Ct);

        CommandResult[] results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(async () => await commands.SendAsync(new Deposit(id, 1m), null, Ct))));

        results.ShouldAllBe(r => r.Status == CommandStatus.Accepted);
        AccountActivity row = (await ActivityAsync(id))!;
        row.Balance.ShouldBe(13m);
        row.Movements.ShouldBe(12);
        row.Version.ShouldBe(13);
    }

    [Fact]
    [Trait("Id", "SCK-62")]
    [Trait("Covers", "ES-R1")]
    public async Task Flush_SendsTheAppendFirst_SoAConflictSurfacesBeforeAnyEfWrite()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        var stream = new StreamId("bank-account-flush");
        await AppendAsync(store, stream);

        await using IStorageSession session = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        session.Append(stream, "account", ExpectedVersion.NoStream, [StorageConformance.Event()]);

        await Should.ThrowAsync<ConcurrencyException>(() => session.FlushAsync(Ct).AsTask());
        Should.Throw<InvalidOperationException>(() => session.EnqueueOutbox(null!));
    }

    [Fact]
    [Trait("Id", "SCK-63")]
    [Trait("Covers", "ADR-016")]
    public async Task LostLease_RollsBackTheEfChanges()
    {
        await using PostgreSqlStore store = await Server.CreateStoreAsync();
        var factory = new EfSessionContextFactory<ReportingDbContext>(ReportingDbContext.Configure, ReportingDbContext.Create);
        LeaseToken lease = (await store.TryAcquireAsync("sub:account-activity:0", "node-1", TimeSpan.FromSeconds(30), Ct))!;
        var id = Guid.CreateVersion7();

        await using (IStorageSession session = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct))
        {
            await using ReportingDbContext db = await factory.CreateAsync(session, Ct);
            db.Activity.Add(new AccountActivity { AccountId = id, Holder = "Ada", Version = 1 });
            await db.SaveChangesAsync(Ct);
            session.SaveCheckpoint("projection:account-activity@1", 0, new GlobalPosition(9, 1), lease with { Token = lease.Token + 1 });

            await Should.ThrowAsync<LeaseLostException>(() => session.CommitAsync(Ct).AsTask());
        }

        (await ActivityAsync(new AccountId(id))).ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "SCK-64")]
    [Trait("Covers", "ADR-016")]
    public async Task AsyncEfProjection_CatchesUpThroughFencedCheckpoints()
    {
        using IHost host = await StartAsync(ProjectionMode.Async, sf => sf.Configure(o => o.SubscriptionPartitions = 2));
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId[] ids = [.. Enumerable.Range(0, 6).Select(_ => AccountId.New())];
        foreach (AccountId id in ids)
        {
            await commands.SendAsync(new OpenAccount(id, "Grace", 10m), null, Ct);
            await commands.SendAsync(new Deposit(id, 5m), null, Ct);
        }

        foreach (AccountId id in ids)
        {
            await EventuallyAsync(async () => (await ActivityAsync(id)) is { Balance: 15m, Version: 2 }, $"activity of {id}");
        }

        PostgreSqlStore store = host.Services.GetRequiredService<PostgreSqlStore>();
        (await store.GetAsync("projection:account-activity@1", 0, Ct)).ShouldBeGreaterThan(GlobalPosition.Start);
    }

    [Fact]
    [Trait("Id", "UT-EF-04")]
    public async Task IdempotentUpsert_IgnoresOlderAndEqualVersions()
    {
        var options = new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(Server.DataSource).Options;
        var id = Guid.CreateVersion7();
        await using (var db = new ReportingDbContext(options))
        {
            (await db.Activity.UpsertAsync(id, 5, _ => new AccountActivity { AccountId = id, Balance = 5m }, Ct)).ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = new ReportingDbContext(options))
        {
            (await db.Activity.UpsertAsync(id, 4, row => { row!.Balance = 4m; return row; }, Ct)).ShouldBeFalse();
            (await db.Activity.UpsertAsync(id, 5, row => { row!.Balance = 99m; return row; }, Ct)).ShouldBeFalse();
            (await db.Activity.UpsertAsync(id, 6, row => new AccountActivity { AccountId = id, Balance = 6m }, Ct)).ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        }

        AccountActivity stored = (await ActivityAsync(new AccountId(id)))!;
        stored.Balance.ShouldBe(6m);
        stored.Version.ShouldBe(6);
    }

    private async Task<IHost> StartAsync(ProjectionMode mode, Action<SourceFlowBuilder>? configure = null)
    {
        string schema = PostgreSqlServer.NewSchema();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.AddModule<ReportingModule>();
            sf.Projections(p =>
            {
                ProjectionModeBuilder activity = p.Add<AccountActivityProjection>();
                _ = mode == ProjectionMode.Inline ? activity.Inline() : activity.Async();
            });
            sf.UsePostgreSql(o =>
            {
                o.DataSource = Server.DataSource;
                o.Schema = schema;
            });
            sf.UseEntityFrameworkProjections<ReportingDbContext>(ReportingDbContext.Configure, ReportingDbContext.Create);
            sf.UseInMemoryTransport();
            configure?.Invoke(sf);
        });
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private async Task<AccountActivity?> ActivityAsync(AccountId id)
    {
        var options = new DbContextOptionsBuilder<ReportingDbContext>().UseNpgsql(Server.DataSource).Options;
        await using var db = new ReportingDbContext(options);
        return await db.Activity.AsNoTracking().SingleOrDefaultAsync(a => a.AccountId == id.Value, Ct);
    }

    private static async Task AppendAsync(PostgreSqlStore store, StreamId stream)
    {
        await using IStorageSession session = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        session.Append(stream, "account", ExpectedVersion.NoStream, [StorageConformance.Event()]);
        await session.CommitAsync(Ct);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}.");
            }

            await RealClock.WaitAsync(TimeSpan.FromMilliseconds(50));
        }
    }
}
