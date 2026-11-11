using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Bank.Accounts;
using Bank.Contracts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>Version 2 of the account summary: it also counts the movements (a new field means a rebuild, doc 06 §6).</summary>
public sealed record AccountSummaryV2(AccountId Id, string Holder, decimal Balance, [property: Queryable] AccountStatus Status, int Movements, long Version);

/// <summary>The next version of the Bank's <c>account-summary</c> projection.</summary>
[Projection("account-summary", Version = 2)]
public sealed partial class AccountSummaryV2Projection : IDocumentProjection<AccountSummaryV2, AccountId>
{
    private static AccountSummaryV2 When(AccountOpened e, AccountSummaryV2? current, EventContext ctx)
    {
        return new AccountSummaryV2(e.AccountId, e.Holder, e.InitialDeposit, AccountStatus.Open, 0, ctx.StreamVersion.Value);
    }

    private static AccountSummaryV2? When(MoneyDeposited e, AccountSummaryV2? current, EventContext ctx)
    {
        return current is null ? null : current with { Balance = e.BalanceAfter, Movements = current.Movements + 1, Version = ctx.StreamVersion.Value };
    }

    private static AccountSummaryV2? When(MoneyWithdrawn e, AccountSummaryV2? current, EventContext ctx)
    {
        return current is null ? null : current with { Balance = e.BalanceAfter, Movements = current.Movements + 1, Version = ctx.StreamVersion.Value };
    }

    private static AccountSummaryV2? When(AccountClosed e, AccountSummaryV2? current, EventContext ctx)
    {
        return current is null ? null : current with { Status = AccountStatus.Closed, Version = ctx.StreamVersion.Value };
    }
}

/// <summary>A side effect per opened account; a rebuild must never run it again (GAP-06).</summary>
public sealed class WelcomeCounter : IEventHandler<AccountOpened>
{
    public static ConcurrentDictionary<AccountId, int> Sent { get; } = new();

    public ValueTask HandleAsync(AccountOpened @event, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        Sent.AddOrUpdate(@event.AccountId, 1, (_, n) => n + 1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>The rebuild scenario's module: version 2 of the summary and the side-effect handler.</summary>
[SourceFlowModule(typeof(RebuildJsonContext))]
public sealed partial class RebuildTestModule;

[JsonSerializable(typeof(AccountSummaryV2))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class RebuildJsonContext : JsonSerializerContext;

/// <summary>E2E-08 and UT-SUB-28: a blue/green rebuild over the Bank sample, in memory (plan 06 §4).</summary>
[Trait("Category", "E2E")]
public sealed class RebuildScenarios
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "E2E-08")]
    [Trait("Id", "UT-SUB-28")]
    [Trait("Covers", "ADR-016;ADR-032;GAP-06;doc 06 6")]
    public async Task VersionBump_TheNewGenerationCatchesUp_TheAliasSwitches_AndNoSideEffectRunsAgain()
    {
        var store = new InMemoryStore();
        AccountId ada = AccountId.New();
        AccountId bob = AccountId.New();
        await using (SourceFlowTestHost v1 = await StartAsync(store, version: 1))
        {
            await v1.SendAsync(new OpenAccount(ada, "Ada", 10m), cancellationToken: Ct);
            await v1.SendAsync(new Deposit(ada, 5m), cancellationToken: Ct);
            await v1.SendAsync(new Withdraw(ada, 3m), cancellationToken: Ct);
            await v1.SendAsync(new OpenAccount(bob, "Bob", 1m), cancellationToken: Ct);
            await v1.WaitForIdleAsync();
            await AliasAsync(store, v1, "account-summary@1");
        }

        GlobalPosition handlerCheckpoint = await store.GetAsync("handler:WelcomeCounter", 0, Ct);
        int outbox = await store.CountAsync(Ct);

        // Deploy version 2: it replays the log into account-summary@2 and takes over reads once caught up.
        await using SourceFlowTestHost v2 = await StartAsync(store, version: 2);
        await v2.WaitForIdleAsync();
        await AliasAsync(store, v2, "account-summary@2");

        AccountSummaryV2 summary = (await v2.DocumentAsync<AccountSummaryV2Projection, AccountSummaryV2>(ada.ToStreamSegment())).ShouldNotBeNull();
        summary.ShouldBe(new AccountSummaryV2(ada, "Ada", 12m, AccountStatus.Open, 2, 3));
        IReadOnlyList<LowBalanceAccount> low = await v2.Services.GetRequiredService<IQueryService>().ExecuteAsync(new LowBalanceAccounts(5m), cancellationToken: Ct);
        low.ShouldHaveSingleItem().AccountId.ShouldBe(bob);   // domain queries read the new generation through the alias

        // No side effects: the handler ran once per account and its checkpoint did not move; nothing was sent.
        WelcomeCounter.Sent[ada].ShouldBe(1);
        WelcomeCounter.Sent[bob].ShouldBe(1);
        (await store.GetAsync("handler:WelcomeCounter", 0, Ct)).ShouldBe(handlerCheckpoint);
        (await store.CountAsync(Ct)).ShouldBe(outbox);
        CollectionAlias alias = (await store.ListAliasesAsync(Ct)).ShouldHaveSingleItem();
        alias.Previous.ShouldBe("account-summary@1");
    }

    private static Task<SourceFlowTestHost> StartAsync(InMemoryStore store, int version)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.AddModule<RebuildTestModule>();
            sf.Projections(p =>
            {
                if (version == 1)
                {
                    p.Remove<AccountSummaryV2Projection>();
                    p.Add<AccountSummaryProjection>().Async();
                }
                else
                {
                    p.Remove<AccountSummaryProjection>();
                    p.Add<AccountSummaryV2Projection>().Async();
                }
            });
            sf.Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(20));
        }, store: store);
    }

    private static async Task AliasAsync(InMemoryStore store, SourceFlowTestHost host, string target)
    {
        for (int i = 0; i < 300; i++)
        {
            if ((await store.ListAliasesAsync(Ct)).FirstOrDefault()?.Target == target)
            {
                return;
            }

            await host.WaitForIdleAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }

        throw new TimeoutException($"account-summary did not switch to {target}.");
    }
}
