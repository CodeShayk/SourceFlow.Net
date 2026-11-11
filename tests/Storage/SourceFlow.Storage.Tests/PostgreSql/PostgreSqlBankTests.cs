using System.Text.Json;
using Bank.Accounts;
using Bank.Contracts;
using Bank.Transfers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage.PostgreSQL;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>
/// The Bank sample on PostgreSQL with the real clock (plan 06 §4, PostgreSQL leg): commands, inline and async
/// projections, the outbox relay, the transfer process manager, subscriptions with fenced checkpoints, and
/// re-decide on real conflicts.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlBankTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "E2E-01")]
    [Trait("Covers", "ADR-002;ADR-009")]
    public async Task AccountLifecycle_InlineSummary()
    {
        using IHost host = await StartAsync(inlineSummary: true);
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId id = AccountId.New();

        (await commands.SendAsync(new OpenAccount(id, "Ada", 100m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        (await commands.SendAsync(new Deposit(id, 50m), null, Ct)).Version.ShouldBe(new StreamVersion(2));
        (await commands.SendAsync(new Withdraw(id, 30m), null, Ct)).Version.ShouldBe(new StreamVersion(3));
        CommandResult rejected = await commands.SendAsync(new Withdraw(id, 1000m), null, Ct);

        rejected.Status.ShouldBe(CommandStatus.Rejected);
        (await SummaryAsync(host, id))!.ShouldBe(new AccountSummary(id, "Ada", 120m, AccountStatus.Open, 3));
    }

    [Fact]
    [Trait("Id", "E2E-03")]
    [Trait("Covers", "ADR-006")]
    public async Task IdempotencyKey_IsDeduplicatedByTheInbox()
    {
        using IHost host = await StartAsync(inlineSummary: true);
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId id = AccountId.New();
        await commands.SendAsync(new OpenAccount(id, "Ada", 1m), null, Ct);
        var options = new SendOptions { IdempotencyKey = "pg-deposit-1" };

        CommandResult first = await commands.SendAsync(new Deposit(id, 5m), options, Ct);
        CommandResult second = await commands.SendAsync(new Deposit(id, 5m), options, Ct);

        second.Status.ShouldBe(CommandStatus.Duplicate);
        second.Version.ShouldBe(first.Version);
        (await SummaryAsync(host, id))!.Balance.ShouldBe(6m);
    }

    [Fact]
    [Trait("Id", "E2E-04")]
    [Trait("Covers", "ADR-008;GAP-05")]
    public async Task ParallelDeposits_ReDecideOnRealConflicts()
    {
        using IHost host = await StartAsync(inlineSummary: true, configure: sf => sf.Configure(o => o.MaxConcurrencyRetries = 50));
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId id = AccountId.New();
        await commands.SendAsync(new OpenAccount(id, "Ada", 0.01m), null, Ct);

        CommandResult[] results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await commands.SendAsync(new Deposit(id, 1m), null, Ct))));

        results.ShouldAllBe(r => r.Status == CommandStatus.Accepted);
        results.Select(r => r.Version.Value).Distinct().Count().ShouldBe(20);
        (await SummaryAsync(host, id))!.Balance.ShouldBe(20.01m);
    }

    [Theory]
    [Trait("Id", "UT-SNAP-06")]
    [Trait("Covers", "doc 05 8.2")]
    [InlineData(AggregateCacheMode.ReadTail)]
    [InlineData(AggregateCacheMode.Optimistic)]
    public async Task ParallelDeposits_WithTheAggregateCache_StayExact(AggregateCacheMode mode)
    {
        using IHost host = await StartAsync(inlineSummary: true, configure: sf => sf.Configure(o =>
        {
            o.MaxConcurrencyRetries = 50;
            o.AggregateCache = mode;
        }));
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId id = AccountId.New();
        await commands.SendAsync(new OpenAccount(id, "Ada", 1m), null, Ct);

        CommandResult[] results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(async () => await commands.SendAsync(new Deposit(id, 1m), null, Ct))));
        CommandResult tooMuch = await commands.SendAsync(new Withdraw(id, 22m), null, Ct);
        CommandResult exact = await commands.SendAsync(new Withdraw(id, 21m), null, Ct);

        results.ShouldAllBe(r => r.Status == CommandStatus.Accepted);
        tooMuch.Status.ShouldBe(CommandStatus.Rejected);
        exact.Status.ShouldBe(CommandStatus.Accepted);
        (await SummaryAsync(host, id))!.Balance.ShouldBe(0m);
    }

    [Fact]
    [Trait("Id", "E2E-06")]
    [Trait("Covers", "ADR-010;ADR-016")]
    public async Task AsyncProjection_CatchesUpThroughFencedCheckpoints()
    {
        using IHost host = await StartAsync(inlineSummary: false, configure: sf => sf.Configure(o => o.SubscriptionPartitions = 4));
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId[] ids = [.. Enumerable.Range(0, 8).Select(_ => AccountId.New())];
        foreach (AccountId id in ids)
        {
            await commands.SendAsync(new OpenAccount(id, "Ada", 10m), null, Ct);
            await commands.SendAsync(new Deposit(id, 5m), null, Ct);
        }

        foreach (AccountId id in ids)
        {
            await EventuallyAsync(async () => (await SummaryAsync(host, id))?.Balance == 15m, $"summary of {id}");
        }
    }

    [Fact]
    [Trait("Id", "E2E-13")]
    [Trait("Covers", "ADR-006;ADR-020")]
    public async Task MoneyTransfer_ProcessManagerCompletesThroughTheOutbox()
    {
        using IHost host = await StartAsync(inlineSummary: true);
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId from = AccountId.New();
        AccountId to = AccountId.New();
        await commands.SendAsync(new OpenAccount(from, "Ada", 100m), null, Ct);
        await commands.SendAsync(new OpenAccount(to, "Grace", 0.5m), null, Ct);

        var transfer = TransferId.New();
        (await commands.SendAsync(new RequestTransfer(transfer, from, to, 40m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);

        await EventuallyAsync(async () => (await SummaryAsync(host, from))?.Balance == 60m && (await SummaryAsync(host, to))?.Balance == 40.5m, "transfer applied");
        PostgreSqlStore store = host.Services.GetRequiredService<PostgreSqlStore>();
        // The process manager completes the transfer after the deposit; wait for that before looking at the outbox.
        await EventuallyAsync(async () => (await store.ReadStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(Transfer.AggregateName, transfer), new StreamVersion(1), 10, Ct))
            .Events.Any(e => e.Type == "bank.transfer.completed"), "transfer completed");
        // The process manager's timeout stays scheduled in the outbox until it is due; nothing else is left.
        await EventuallyAsync(async () => await store.DueOutboxCountAsync(Ct) == 0, "due outbox drained");
        (await store.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "E2E-13")]
    [Trait("Covers", "ADR-022")]
    public async Task MoneyTransfer_UnderRowLevelSecurity_WithAnAsyncProjection()
    {
        using IHost host = await StartAsync(inlineSummary: false, rowLevelSecurity: true);
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        AccountId from = AccountId.New();
        AccountId to = AccountId.New();
        await commands.SendAsync(new OpenAccount(from, "Ada", 100m), null, Ct);
        (await commands.SendAsync(new OpenAccount(to, "Grace", 1m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);

        (await commands.SendAsync(new RequestTransfer(TransferId.New(), from, to, 25m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);

        await EventuallyAsync(async () => (await SummaryAsync(host, from))?.Balance == 75m && (await SummaryAsync(host, to))?.Balance == 26m, "transfer applied under RLS");
    }

    private async Task<IHost> StartAsync(bool inlineSummary, Action<SourceFlowBuilder>? configure = null, bool rowLevelSecurity = false)
    {
        string schema = PostgreSqlServer.NewSchema();
        if (rowLevelSecurity)
        {
            // The owner migrates; the host runs as the DML-only application role and only verifies the schema.
            await new PostgreSqlSchemaMigrator(Server.DataSource, schema, TenantIsolation.RowLevelSecurity).MigrateAsync(Ct);
            await Server.GrantAsync(schema);
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.AddModule<TransfersModule>();
            sf.Projections(p =>
            {
                ProjectionModeBuilder summary = p.Add<AccountSummaryProjection>();
                if (inlineSummary)
                {
                    summary.Inline();
                }
                else
                {
                    summary.Async();
                }
            });
            sf.UsePostgreSql(o =>
            {
                o.Schema = schema;
                if (rowLevelSecurity)
                {
                    PostgreSqlStorageOptions rls = Server.RowLevelSecurityOptions(schema);
                    o.ConnectionString = rls.ConnectionString;
                    o.SystemConnectionString = rls.SystemConnectionString;
                    o.TenantIsolation = TenantIsolation.RowLevelSecurity;
                    o.AutoMigrate = false;
                }
                else
                {
                    o.DataSource = Server.DataSource;
                }
            });
            sf.UseInMemoryTransport();
            configure?.Invoke(sf);
        });
        IHost host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    private static async Task<AccountSummary?> SummaryAsync(IHost host, AccountId id)
    {
        RuntimeModel model = host.Services.GetRequiredService<RuntimeModel>();
        ProjectionDescriptor projection = model.Projections.Single(p => p.ProjectionType == typeof(AccountSummaryProjection));
        StoredDocument? stored = await host.Services.GetRequiredService<PostgreSqlStore>()
            .ToPorts().Documents.GetAsync(Messaging.MessageEnvelope.DefaultTenant, projection.Collection, id.ToStreamSegment(), Ct);
        return stored is null ? null : (AccountSummary?)JsonSerializer.Deserialize(stored.Data.Span, model.JsonTypeInfoFor(typeof(AccountSummary)));
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + Patience;
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
