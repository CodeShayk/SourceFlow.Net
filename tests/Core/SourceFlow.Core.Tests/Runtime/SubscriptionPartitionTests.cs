using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Runtime;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;
using SourceFlow.Testing.Conformance;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-SUB-31..32 (WP-C.9, CR-18): a subscription's partition count is stored, and every node must match it.</summary>
[Trait("Category", "Unit")]
public sealed class SubscriptionPartitionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-SUB-31")]
    [Trait("Covers", "doc 06 3")]
    public async Task ChangingTheGlobalDefault_DoesNotChangeAnExistingSubscription()
    {
        var store = new InMemoryStore();
        var broker = new InMemoryBroker();
        string subscription;
        await using (SourceFlowTestHost first = await NodeAsync(store, broker, partitions: 1, new TckObservations()))
        {
            await first.WaitForIdleAsync();
            subscription = HandlerOf(first);
        }

        (await store.GetPartitionsAsync(subscription, Ct)).ShouldBe(1, "the first node stored its count");
        await using SourceFlowTestHost later = await NodeAsync(store, broker, partitions: 4, new TckObservations());
        await later.WaitForIdleAsync();

        (await store.GetPartitionsAsync(subscription, Ct)).ShouldBe(1, "the stored count stays; only a repartition changes it");
        Engine(later).PartitionMismatches.ShouldContainKey(subscription, "a node configured otherwise refuses it");
    }

    [Fact]
    [Trait("Id", "UT-SUB-32")]
    [Trait("Covers", "doc 06 3;doc 06 4")]
    public async Task ANodeWithAMismatchedCount_RefusesTheSubscription_AndEachEventIsHandledOnce()
    {
        var store = new InMemoryStore();
        var broker = new InMemoryBroker();
        var observations = new TckObservations();
        await using SourceFlowTestHost current = await NodeAsync(store, broker, partitions: 2, observations);
        await current.WaitForIdleAsync();   // claims 2 for the handler subscription
        await using SourceFlowTestHost mismatched = await NodeAsync(store, broker, partitions: 1, observations);

        for (int i = 0; i < 6; i++)
        {
            await current.SendAsync(new TckAdd(TckTallyId.New(), 1), cancellationToken: Ct);
        }

        await current.WaitForIdleAsync();
        await mismatched.WaitForIdleAsync();   // idle: it doesn't wait on a subscription it refuses

        Engine(mismatched).PartitionMismatches[HandlerOf(current)].ShouldBe(2);
        Engine(mismatched).OwnedLeases.ShouldBe(0, "it takes no lease of the refused subscription");
        observations.Handled.Count.ShouldBe(6);
        observations.Handled.Select(h => h.EventId).Distinct().Count().ShouldBe(6, "each event once, all by the node with the stored count");
    }

    private static Task<SourceFlowTestHost> NodeAsync(InMemoryStore store, InMemoryBroker broker, int partitions, TckObservations observations)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "tck";
            sf.AddModule<TckModule>();
            sf.Configure(o =>
            {
                o.SubscriptionPartitions = partitions;
                o.NodeId = "node-" + partitions.ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
        }, store: store, broker: broker, services: s => s.AddSingleton(observations), cancellationToken: Ct);
    }

    // The kit's event handler subscription (TckObserver).
    private static string HandlerOf(SourceFlowTestHost host)
    {
        return Engine(host).Definitions.Single(d => d.Name.StartsWith("handler:", StringComparison.Ordinal)).Name;
    }

    private static SubscriptionEngine Engine(SourceFlowTestHost host)
    {
        return host.Services.GetRequiredService<SubscriptionEngine>();
    }
}
