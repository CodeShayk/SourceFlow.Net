using System.Text.Json;
using Bank.Accounts;
using Bank.Contracts;
using Bank.Transfers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

// A projection that fails on one value, for the park policy (doc 06 §7). Only these tests produce its events.
[StronglyTypedId]
public readonly partial record struct DripId(Guid Value);

[MessageType("test.drip.drip", 1)]
public sealed record Drip(DripId DripId, string Value) : ICommand<Dripper>;

[MessageType("test.drip.dripped", 1)]
public sealed record Dripped(DripId DripId, string Value) : IEvent;

[Aggregate("test-dripper")]
public sealed partial record Dripper : IAggregate<Dripper, DripId>
{
    public static Dripper Initial { get; } = new();

    public Decision Decide(Drip c, DecisionContext ctx)
    {
        return Decision.Accept(new Dripped(c.DripId, c.Value));
    }

    private Dripper When(Dripped e)
    {
        return this;
    }
}

public sealed record Drips(DripId Id, int Count, string Last);

[Projection("test-drips")]
public sealed partial class DripsProjection : IDocumentProjection<Drips, DripId>
{
    private static Drips When(Dripped e, Drips? current, EventContext ctx)
    {
        return e.Value == "poison"
            ? throw new InvalidOperationException("This drip is poison.")
            : new Drips(e.DripId, (current?.Count ?? 0) + 1, e.Value);
    }
}

/// <summary>UT-SUB, UT-PRJ and UT-PM (plan 05 §3.7, WP-1.9): subscriptions, projections and process managers.</summary>
[Trait("Category", "Unit")]
public sealed class SubscriptionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ subscriptions

    [Fact]
    [Trait("Id", "UT-SUB-01")]
    [Trait("Covers", "ADR-016")]
    public async Task Worker_AppliesAfterTheCheckpoint_AndCheckpointsInTheSameCommit()
    {
        await using SourceFlowTestHost host = await BankAsync();
        var id = AccountId.New();

        await host.SendAsync(new OpenAccount(id, "Ada", 10m), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        GlobalPosition first = await host.Store.GetAsync(Subscription, 0, Ct);
        await host.SendAsync(new Deposit(id, 5m), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        first.ShouldNotBe(GlobalPosition.Start);
        (await host.Store.GetAsync(Subscription, 0, Ct)).CompareTo(first).ShouldBeGreaterThan(0);
        (await host.DocumentAsync<AccountSummaryProjection, AccountSummary>(id.ToStreamSegment()))!.Balance.ShouldBe(15m);
    }

    [Fact]
    [Trait("Id", "UT-SUB-05")]
    [Trait("Covers", "doc 06 3")]
    public async Task UnhandledEventTypes_StillAdvanceTheCheckpoint()
    {
        await using SourceFlowTestHost host = await BankAsync(transfers: true);
        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10m), cancellationToken: Ct);
        await host.SendAsync(new RequestTransfer(TransferId.New(), AccountId.New(), AccountId.New(), 1m), cancellationToken: Ct);   // transfer events: not handled by the summary
        await host.WaitForIdleAsync();

        LogBatch log = await host.Store.ReadAsync(GlobalPosition.Start, BucketRange.All, 10_000, Ct);
        log.Events[^1].Type.ShouldStartWith("bank.transfer.");
        (await host.Store.GetAsync(Subscription, 0, Ct)).ShouldBe(log.Events.Where(e => e.Type.StartsWith("bank.", StringComparison.Ordinal)).Max(e => e.Position));
    }

    [Fact]
    [Trait("Id", "UT-SUB-06")]
    [Trait("Covers", "doc 06 3")]
    public async Task EachPartition_ReadsOnlyItsBucketRange()
    {
        await using SourceFlowTestHost host = await BankAsync(partitions: 4);
        for (int i = 0; i < 24; i++)
        {
            await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), cancellationToken: Ct);
        }

        await host.WaitForIdleAsync();

        for (int partition = 0; partition < 4; partition++)
        {
            LogBatch own = await host.Store.ReadAsync(GlobalPosition.Start, Buckets.Range(partition, 4), 10_000, Ct);
            GlobalPosition expected = own.Events.Count == 0 ? GlobalPosition.Start : own.Events[^1].Position;
            (await host.Store.GetAsync(Subscription, partition, Ct)).ShouldBe(expected, $"partition {partition}");
        }
    }

    [Fact]
    [Trait("Id", "UT-SUB-08")]
    [Trait("Covers", "doc 06 4")]
    public async Task FairShare_OneNodeTakesEveryLease_TwoNodesSplitThem_ALeavingNodeHandsItsLeasesBack()
    {
        // Passes and heartbeats are driven by the test (no background loops), so the rebalancing is deterministic.
        var store = new InMemoryStore();
        await using SourceFlowTestHost a = await BankAsync(partitions: 4, store: store, node: "node-a", loops: false);
        await using SourceFlowTestHost b = await BankAsync(partitions: 4, store: store, node: "node-b", loops: false);
        SourceFlow.Runtime.SubscriptionEngine engineA = a.Services.GetRequiredService<SourceFlow.Runtime.SubscriptionEngine>();
        SourceFlow.Runtime.SubscriptionEngine engineB = b.Services.GetRequiredService<SourceFlow.Runtime.SubscriptionEngine>();
        TimeSpan window = TimeSpan.FromSeconds(30);
        int subscriptions = (int)SourceFlowRoles.Subscriptions;

        a.Runtime.LiveSubscriptionNodes = await store.HeartbeatAsync("node-a", subscriptions, window, Ct);
        await engineA.RunOnceAsync(Ct);
        a.Runtime.LiveSubscriptionNodes.ShouldBe(1);
        engineA.OwnedLeases.ShouldBe(4);

        // UT-SUB-09: B joins. A releases one lease per pass (no thundering herd) until both hold their share of 2.
        b.Runtime.LiveSubscriptionNodes = await store.HeartbeatAsync("node-b", subscriptions, window, Ct);
        a.Runtime.LiveSubscriptionNodes = await store.HeartbeatAsync("node-a", subscriptions, window, Ct);
        b.Runtime.LiveSubscriptionNodes.ShouldBe(2);
        engineB.FairShare.ShouldBe(2);
        await engineA.RunOnceAsync(Ct);
        engineA.OwnedLeases.ShouldBe(3);
        await engineB.RunOnceAsync(Ct);
        await engineA.RunOnceAsync(Ct);
        await engineB.RunOnceAsync(Ct);
        engineA.OwnedLeases.ShouldBe(2);
        engineB.OwnedLeases.ShouldBe(2);

        // UT-SUB-10: B leaves gracefully: releases its leases and leaves the registry; A takes everything again.
        await engineB.ReleaseAllAsync(Ct);
        await store.LeaveAsync("node-b", Ct);
        a.Runtime.LiveSubscriptionNodes = await store.HeartbeatAsync("node-a", subscriptions, window, Ct);
        await engineA.RunOnceAsync(Ct);
        engineA.OwnedLeases.ShouldBe(4);
        engineB.OwnedLeases.ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "UT-SUB-12")]
    [Trait("Covers", "doc 06 4")]
    public async Task StaleToken_CannotCommit_AfterANewOwnerTookOver()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemoryStore(time);
        LeaseToken paused = (await store.TryAcquireAsync("sub:test:0", "node-a", TimeSpan.FromSeconds(30), Ct)).ShouldNotBeNull();
        time.Advance(TimeSpan.FromSeconds(31));   // node A pauses past its TTL
        LeaseToken current = (await store.TryAcquireAsync("sub:test:0", "node-b", TimeSpan.FromSeconds(30), Ct)).ShouldNotBeNull();

        await using IStorageSession stale = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        stale.SaveCheckpoint("test", 0, new GlobalPosition(5, 1), paused);
        await Should.ThrowAsync<LeaseLostException>(async () => await stale.CommitAsync(Ct));

        await using IStorageSession fresh = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        fresh.SaveCheckpoint("test", 0, new GlobalPosition(5, 1), current);
        await fresh.CommitAsync(Ct);
        (await store.GetAsync("test", 0, Ct)).ShouldBe(new GlobalPosition(5, 1));
        current.Token.ShouldBeGreaterThan(paused.Token);
    }

    [Fact]
    [Trait("Id", "UT-SUB-17")]
    [Trait("Covers", "doc 06 7;GAP-07")]
    public async Task PoisonEvent_IsParked_AndThePartitionMovesPastIt()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "drips";
            sf.AddModule<CoreTestModule>();
        });
        var id = DripId.New();

        await host.SendAsync(new Drip(id, "one"), cancellationToken: Ct);
        await host.SendAsync(new Drip(id, "poison"), cancellationToken: Ct);
        await host.SendAsync(new Drip(id, "three"), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        DeadLetter parked = (await host.Store.QueryAsync(new DeadLetterQuery(Consumer: "projection:test-drips@1"), Ct)).ShouldHaveSingleItem();
        parked.Source.ShouldBe("subscription");
        parked.Reason.ShouldBe("SubscriptionFailed");
        parked.Envelope.Type.ShouldBe("test.drip.dripped");
        Drips drips = (await host.DocumentAsync<DripsProjection, Drips>(id.ToStreamSegment())).ShouldNotBeNull();
        drips.Count.ShouldBe(2);
        drips.Last.ShouldBe("three");
    }

    // ================================================================ projections

    [Fact]
    [Trait("Id", "UT-PRJ-01")]
    [Trait("Covers", "DC-2;ADR-016")]
    public async Task Applier_CreatesThenUpdates_AndTheCollectionIsNameAtVersion()
    {
        await using SourceFlowTestHost host = await BankAsync();
        var id = AccountId.New();

        await host.SendAsync(new OpenAccount(id, "Ada", 10m), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        StoredDocument created = (await ((IDocumentReader)host.Store).GetAsync(MessageEnvelope.DefaultTenant, "account-summary@1", id.ToStreamSegment(), Ct)).ShouldNotBeNull();
        await host.SendAsync(new Withdraw(id, 4m), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        StoredDocument updated = (await ((IDocumentReader)host.Store).GetAsync(MessageEnvelope.DefaultTenant, "account-summary@1", id.ToStreamSegment(), Ct)).ShouldNotBeNull();

        created.Version.ShouldBe(1);
        updated.Version.ShouldBe(2);
        JsonDocument.Parse(updated.Data).RootElement.GetProperty("balance").GetDecimal().ShouldBe(6m);
        host.Runtime.AsyncProjections.ShouldHaveSingleItem().Collection.ShouldBe("account-summary@1");
    }

    // ================================================================ process managers

    [Fact]
    [Trait("Id", "UT-PM-01")]
    [Trait("Covers", "ADR-020")]
    public async Task ProcessState_IsKeyedByCorrelateBy_PersistedWithAVersion_AndCompleted()
    {
        await using SourceFlowTestHost host = await BankAsync(transfers: true);
        var (from, to) = (AccountId.New(), AccountId.New());
        await host.SendAsync(new OpenAccount(from, "Ada", 100m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(to, "Grace", 0m + 1m), cancellationToken: Ct);
        var transfer = TransferId.New();

        await host.SendAsync(new RequestTransfer(transfer, from, to, 30m), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        await using IStorageSession session = await host.Store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        ProcessState state = (await session.LoadProcessStateAsync(MoneyTransfer.ProcessName, transfer.ToStreamSegment(), Ct)).ShouldNotBeNull();
        state.Completed.ShouldBeTrue();
        state.Version.ShouldBe(3);   // started (withdrawing), depositing, completed
    }

    [Fact]
    [Trait("Id", "UT-PM-10")]
    [Trait("Covers", "ADR-007;ADR-018")]
    public async Task ReactionCommands_CarryTheProcessCorrelation_TheEventCausation_AndAReplyAddressOnlyWhenExpected()
    {
        var received = new System.Collections.Concurrent.ConcurrentQueue<MessageEnvelope>();
        await using SourceFlowTestHost host = await BankAsync(transfers: true, middleware: received);
        var (from, to) = (AccountId.New(), AccountId.New());
        await host.SendAsync(new OpenAccount(from, "Ada", 100m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(to, "Grace", 1m), cancellationToken: Ct);
        var transfer = TransferId.New();

        await host.SendAsync(new RequestTransfer(transfer, from, to, 30m), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        StoredEvent requested = (await host.Store.ReadStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(Transfer.AggregateName, transfer), new StreamVersion(1), 10, Ct)).Events[0];
        MessageEnvelope withdraw = received.Single(e => e.Type == "bank.account.withdraw");
        MessageEnvelope deposit = received.Single(e => e.Type == "bank.account.deposit");

        withdraw.CorrelationId.Value.ShouldBe(transfer.ToStreamSegment());   // UT-PM-11: correlation = the process instance
        withdraw.CausationId.ShouldBe(requested.EventId);                    // UT-PM-12: causation = the reacted event
        withdraw.Headers[CommandReplies.ReplyToHeader].ShouldBe(host.Runtime.Routing.Endpoint);   // CommandRejected<Withdraw> is expected
        deposit.CorrelationId.Value.ShouldBe(transfer.ToStreamSegment());
        deposit.Headers.ShouldNotContainKey(CommandReplies.ReplyToHeader);   // no CommandRejected<Deposit> reaction: no reply asked

        // UT-PM-13: the reply address never propagates to messages the command causes.
        StoredEvent withdrawn = (await host.Store.ReadStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(BankAccount.AggregateName, from), new StreamVersion(1), 10, Ct)).Events
            .Single(e => e.Type == "bank.account.withdrawn");
        withdrawn.ToEnvelope().Headers.ShouldNotContainKey(CommandReplies.ReplyToHeader);
    }

    // ================================================================ helpers

    private const string Subscription = "projection:account-summary@1";

    private static Task<SourceFlowTestHost> BankAsync(
        bool transfers = false, int partitions = 1, InMemoryStore? store = null, string? node = null,
        System.Collections.Concurrent.ConcurrentQueue<MessageEnvelope>? middleware = null, bool loops = true)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            if (transfers)
            {
                sf.AddModule<TransfersModule>();
            }

            sf.Projections(p => p.Add<AccountSummaryProjection>().Async());
            if (!loops)
            {
                sf.Roles = SourceFlowRoles.Api;
            }

            sf.Configure(o =>
            {
                o.SubscriptionPartitions = partitions;
                o.NodeHeartbeatInterval = TimeSpan.FromMilliseconds(50);
                if (node is not null)
                {
                    o.NodeId = node;
                }
            });
            if (middleware is not null)
            {
                sf.UseMiddleware(_ => new Capture(middleware), PipelineStage.Receive);
            }
        }, store: store);
    }

    private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<MessageEnvelope> received) : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            received.Enqueue(context.Envelope);
            return next(context);
        }
    }
}
