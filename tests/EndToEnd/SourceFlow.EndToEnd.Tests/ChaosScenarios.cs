using Bank.Accounts;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using SourceFlow.Runtime;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Testing;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// CH-01..10 (plan 06 §5, WP-4.7): crashes at every fault point, a broker outage, a database restart, a stalled node and a
/// held write transaction, on PostgreSQL with Accounts and Notifications. Each scenario checks the same outcome: no
/// command or event is lost, every duplicate is absorbed (one welcome per account, balances applied once), and the read
/// models converge. Durations are scaled down from the plan (10 min outage → 8 s, 2 min transaction → 8 s); the
/// mechanisms do not depend on the length.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Chaos")]
[Trait("Requires", "Docker")]
public sealed class ChaosScenarios(PostgreSqlServer postgreSql) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await postgreSql.EnsureStartedAsync();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    [Fact]
    [Trait("Id", "CH-01")]
    [Trait("Covers", "ADR-006;ADR-008")]
    public async Task CrashBeforeCommit_TheRetryIsTheOnlyCommit()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        ChaosNode a = await system.StartAccountsAsync("a", new ChaosInjector().CrashAt(FaultPoints.SessionBeforeCommit));
        AccountId id = AccountId.New();
        var once = new SendOptions { IdempotencyKey = "ch01-open" };

        await Should.ThrowAsync<NodeCrashedException>(async () => await a.Commands.SendAsync(new OpenAccount(id, "Ada", 100m), once, Ct));
        await system.KillAsync(a);
        ChaosNode b = await system.StartAccountsAsync("b");
        CommandResult retried = await b.Commands.SendAsync(new OpenAccount(id, "Ada", 100m), once, Ct);

        retried.Status.ShouldBe(CommandStatus.Accepted, "the crashed attempt never committed");
        await ConvergedAsync(system, b, notifications, id, 100m);
    }

    [Fact]
    [Trait("Id", "CH-02")]
    [Trait("Covers", "ADR-006")]
    public async Task CrashAfterCommitBeforeAck_TheRetryIsADuplicate()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        var faults = new ChaosInjector();
        ChaosNode a = await system.StartAccountsAsync("a", faults);
        AccountId id = AccountId.New();
        await a.Commands.SendAsync(new OpenAccount(id, "Ada", 100m), new SendOptions { IdempotencyKey = "ch02-open" }, Ct);
        var deposit = new SendOptions { IdempotencyKey = "ch02-deposit" };

        faults.CrashAt(FaultPoints.SessionAfterCommitBeforeAck);
        await Should.ThrowAsync<NodeCrashedException>(async () => await a.Commands.SendAsync(new Deposit(id, 25m), deposit, Ct));
        await system.KillAsync(a);
        ChaosNode b = await system.StartAccountsAsync("b");
        CommandResult retried = await b.Commands.SendAsync(new Deposit(id, 25m), deposit, Ct);

        retried.Status.ShouldBe(CommandStatus.Duplicate, "the deposit committed before the crash");
        await ConvergedAsync(system, b, notifications, id, 125m, version: 2);
    }

    [Fact]
    [Trait("Id", "CH-03")]
    [Trait("Covers", "ADR-006;doc 04 6")]
    public async Task CrashAfterPublishBeforeDelete_TheRepublishIsAbsorbed()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        var faults = new ChaosInjector().CrashAt(FaultPoints.RelayAfterPublishBeforeDelete);
        ChaosNode a = await system.StartAccountsAsync("a", faults);
        AccountId id = AccountId.New();

        (await a.Commands.SendAsync(new OpenAccount(id, "Ada", 100m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        (await faults.Triggered.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBe(FaultPoints.RelayAfterPublishBeforeDelete);
        await system.KillAsync(a);
        (await system.PendingOutboxAsync()).ShouldBeGreaterThan(0, "the published message is still in the outbox");
        ChaosNode b = await system.StartAccountsAsync("b");

        await ChaosSystem.EventuallyAsync(async () => await system.PendingOutboxAsync() == 0, "the outbox drained (published again)");
        await ConvergedAsync(system, b, notifications, id, 100m);
    }

    [Fact]
    [Trait("Id", "CH-04")]
    [Trait("Covers", "ADR-016;doc 06 2")]
    public async Task CrashAfterApplyBeforeCommit_TheBatchIsAppliedAgain_Once()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        var faults = new ChaosInjector().CrashAt(FaultPoints.SubscriptionAfterApplyBeforeCommit);
        ChaosNode a = await system.StartAccountsAsync("a", faults);
        AccountId id = AccountId.New();

        await a.Commands.SendAsync(new OpenAccount(id, "Ada", 100m), null, Ct);
        await faults.Triggered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await system.KillAsync(a);
        ChaosNode b = await system.StartAccountsAsync("b");
        await b.Commands.SendAsync(new Deposit(id, 10m), null, Ct);

        await ConvergedAsync(system, b, notifications, id, 110m, version: 2);
    }

    [Fact]
    [Trait("Id", "CH-05")]
    [Trait("Covers", "ADR-016")]
    public async Task CrashBeforeLeaseRenewal_AnotherNodeTakesOver()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        var faults = new ChaosInjector();
        ChaosNode a = await system.StartAccountsAsync("a", faults);
        AccountId first = AccountId.New();
        await a.Commands.SendAsync(new OpenAccount(first, "Ada", 1m), null, Ct);
        await ChaosSystem.EventuallyAsync(async () => await system.SummaryAsync(a, first) is not null, "node a projects");

        faults.CrashAt(FaultPoints.LeaseBeforeRenew);
        await faults.Triggered.WaitAsync(TimeSpan.FromSeconds(30), Ct);   // a holds the subscription lease and is dead
        ChaosNode b = await system.StartAccountsAsync("b");
        AccountId second = AccountId.New();
        await b.Commands.SendAsync(new OpenAccount(second, "Grace", 2m), null, Ct);

        await ConvergedAsync(system, b, notifications, second, 2m);
        await system.KillAsync(a);
    }

    [Fact]
    [Trait("Id", "CH-06")]
    [Trait("Covers", "ADR-006;doc 04 7")]
    public async Task CrashBeforeCompletingAReceivedMessage_TheRedeliveryIsAbsorbed()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        var faults = new ChaosInjector().CrashAt(FaultPoints.ReceiverBeforeComplete);
        ChaosNode n1 = await system.StartNotificationsAsync("n1", faults);
        ChaosNode a = await system.StartAccountsAsync("a");
        AccountId id = AccountId.New();

        await a.Commands.SendAsync(new OpenAccount(id, "Ada", 100m), null, Ct);
        await faults.Triggered.WaitAsync(TimeSpan.FromSeconds(30), Ct);   // handled and committed, never completed
        ChaosNode n2 = await system.StartNotificationsAsync("n2");
        await system.KillAsync(n1);

        await ConvergedAsync(system, a, n2, id, 100m);
    }

    [Fact]
    [Trait("Id", "CH-07")]
    [Trait("Covers", "ADR-006;doc 08 6")]
    public async Task BrokerOutage_CommandsKeepSucceeding_AndTheOutboxDrainsAfterwards()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        ChaosNode a = await system.StartAccountsAsync("a", options: o => o.Health.OutboxAgeDegraded = TimeSpan.FromSeconds(2));
        AccountId[] ids = [.. Enumerable.Range(0, 20).Select(_ => AccountId.New())];

        system.Broker.Faults.Unavailable = true;
        DateTimeOffset outageEnds = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(8);
        foreach (AccountId id in ids)
        {
            (await a.Commands.SendAsync(new OpenAccount(id, "Ada", 5m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted, "commands do not need the broker");
        }

        await ChaosSystem.EventuallyAsync(async () => (await system.HealthAsync(a, "outbox")).Status == HealthStatus.Degraded, "the outbox age alert");
        (await system.PendingOutboxAsync()).ShouldBe(ids.Length);
        TimeSpan rest = outageEnds - TimeProvider.System.GetUtcNow();
        if (rest > TimeSpan.Zero)
        {
            await Task.Delay(rest, TimeProvider.System, Ct);
        }

        system.Broker.Faults.Unavailable = false;
        await ChaosSystem.EventuallyAsync(async () => await system.PendingOutboxAsync() == 0, "the outbox drained", TimeSpan.FromSeconds(90));
        foreach (AccountId id in ids)
        {
            await ConvergedAsync(system, a, notifications, id, 5m);
        }
    }

    [Fact]
    [Trait("Id", "CH-08")]
    [Trait("Covers", "ADR-008;ADR-016")]
    public async Task DatabaseRestart_FailsFast_ThenResumesWithoutDuplicates()
    {
        string database = "chaos_" + Guid.CreateVersion7().ToString("N")[..12];
        await using ChaosSystem system = await ChaosSystem.OnOwnDatabaseAsync(postgreSql, database);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        ChaosNode a = await system.StartAccountsAsync("a");
        AccountId before = AccountId.New();
        await a.Commands.SendAsync(new OpenAccount(before, "Ada", 10m), null, Ct);

        // A primary restart, as the application sees it: every connection dropped, and none accepted for a while.
        await using (NpgsqlCommand block = postgreSql.DataSource.CreateCommand(
            $"ALTER DATABASE \"{database}\" ALLOW_CONNECTIONS false; SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}'"))
        {
            await block.ExecuteNonQueryAsync(Ct);
        }

        NpgsqlConnection.ClearAllPools();
        Exception? failure = await Record.ExceptionAsync(async () => await a.Commands.SendAsync(new Deposit(before, 1m), new SendOptions { IdempotencyKey = "ch08-deposit" }, Ct));
        failure.ShouldNotBeNull("commands fail fast while the database is down");
        await Task.Delay(TimeSpan.FromSeconds(3), TimeProvider.System, Ct);
        await using (NpgsqlCommand allow = postgreSql.DataSource.CreateCommand($"ALTER DATABASE \"{database}\" ALLOW_CONNECTIONS true"))
        {
            await allow.ExecuteNonQueryAsync(Ct);
        }

        CommandResult retried = await RetryAsync(() => a.Commands.SendAsync(new Deposit(before, 1m), new SendOptions { IdempotencyKey = "ch08-deposit" }, Ct));
        retried.Status.ShouldBeOneOf(CommandStatus.Accepted, CommandStatus.Duplicate);
        AccountId after = AccountId.New();
        await a.Commands.SendAsync(new OpenAccount(after, "Grace", 20m), null, Ct);

        await ConvergedAsync(system, a, notifications, before, 11m, version: 2);
        await ConvergedAsync(system, a, notifications, after, 20m);
    }

    [Fact]
    [Trait("Id", "CH-09")]
    [Trait("Covers", "ADR-016;doc 06 3")]
    public async Task StalledNode_LosesItsLease_AndItsLateCommitIsFenced()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        var faults = new ChaosInjector();
        ChaosNode a = await system.StartAccountsAsync("a", faults);
        AccountId id = AccountId.New();

        faults.FreezeAt(FaultPoints.SubscriptionAfterApplyBeforeCommit);
        await a.Commands.SendAsync(new OpenAccount(id, "Ada", 100m), null, Ct);
        await faults.Triggered.WaitAsync(TimeSpan.FromSeconds(30), Ct);   // a holds the lease with a batch applied, frozen
        ChaosNode b = await system.StartAccountsAsync("b");
        await Task.Delay(ChaosSystem.LeaseTtl * 2, TimeProvider.System, Ct);   // longer than the lease TTL
        await b.Commands.SendAsync(new Deposit(id, 50m), null, Ct);
        await ChaosSystem.EventuallyAsync(async () => (await system.SummaryAsync(b, id))?.Balance == 150m, "node b took the subscription over");

        faults.Resume();   // a wakes up and commits its stale batch: the fenced checkpoint rejects it
        await Task.Delay(TimeSpan.FromSeconds(1), TimeProvider.System, Ct);

        await ConvergedAsync(system, b, notifications, id, 150m, version: 2);
        await system.KillAsync(a);
    }

    [Fact]
    [Trait("Id", "CH-10")]
    [Trait("Covers", "ADR-010;doc 08 5")]
    public async Task LongWriteTransaction_HoldsTheHorizon_AndNothingIsSkippedAfterwards()
    {
        await using ChaosSystem system = ChaosSystem.On(postgreSql);
        ChaosNode notifications = await system.StartNotificationsAsync("n1");
        ChaosNode a = await system.StartAccountsAsync("a", options: o => o.Health.HorizonAgeDegraded = TimeSpan.FromSeconds(2));
        AccountId early = AccountId.New();
        await a.Commands.SendAsync(new OpenAccount(early, "Ada", 1m), null, Ct);
        await ChaosSystem.EventuallyAsync(async () => await system.SummaryAsync(a, early) is not null, "the first account projected");

        // A writing transaction (it has an xid) that stays open: every commit after it waits behind the horizon.
        await using NpgsqlConnection holder = await system.DataSource.OpenConnectionAsync(Ct);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Ct);
        await using (var write = new NpgsqlCommand($"CREATE TABLE \"{system.AccountsSchema}\".chaos_hold (id int)", holder, held))
        {
            await write.ExecuteNonQueryAsync(Ct);
        }

        AccountId[] during = [.. Enumerable.Range(0, 5).Select(_ => AccountId.New())];
        foreach (AccountId id in during)
        {
            (await a.Commands.SendAsync(new OpenAccount(id, "Grace", 3m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        }

        await ChaosSystem.EventuallyAsync(async () => (await system.HealthAsync(a, "subscriptions")).Status == HealthStatus.Degraded, "the horizon age alert");
        await Task.Delay(TimeSpan.FromSeconds(8), TimeProvider.System, Ct);
        foreach (AccountId id in during)
        {
            (await system.SummaryAsync(a, id)).ShouldBeNull("subscriptions pause behind the held horizon");
        }

        await held.RollbackAsync(Ct);
        foreach (AccountId id in during)
        {
            await ConvergedAsync(system, a, notifications, id, 3m);
        }

        await ChaosSystem.EventuallyAsync(async () => (await system.HealthAsync(a, "subscriptions")).Status == HealthStatus.Healthy, "the alert clears");
    }

    // ================================================================ helpers

    // The account's summary reached the balance (and version), and Notifications welcomed its customer exactly once.
    private static async Task ConvergedAsync(ChaosSystem system, ChaosNode accounts, ChaosNode notifications, AccountId id, decimal balance, long version = 1)
    {
        await ChaosSystem.EventuallyAsync(async () => await system.SummaryAsync(accounts, id) is { } s && s.Balance == balance && s.Version == version,
            $"summary of {id} at {balance} (version {version})");
        await ChaosSystem.EventuallyAsync(async () => await system.WelcomesAsync(notifications, id) >= 1, $"welcome for {id}");
        await Task.Delay(TimeSpan.FromMilliseconds(500), TimeProvider.System, Ct);   // room for a duplicate to show up
        (await system.WelcomesAsync(notifications, id)).ShouldBe(1, "duplicates are absorbed");
        (await system.SummaryAsync(accounts, id))!.Balance.ShouldBe(balance);
    }

    private static async Task<CommandResult> RetryAsync(Func<ValueTask<CommandResult>> send)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                return await send();
            }
            catch (Exception) when (TimeProvider.System.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), TimeProvider.System, Ct);
            }
        }
    }
}
