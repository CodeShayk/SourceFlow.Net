using System.Text.Json;
using System.Text.Json.Serialization;
using CsCheck;
using SourceFlow.Core.Tests.Messaging;
using SourceFlow.Storage;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

[StronglyTypedId]
public readonly partial record struct CounterId(Guid Value);

[MessageType("test.counter.increment", 1)]
public sealed record Increment(CounterId CounterId) : ICommand<Counter>;

[MessageType("test.counter.decrement", 1)]
public sealed record Decrement(CounterId CounterId) : ICommand<Counter>;

[MessageType("test.counter.incremented", 1)]
public sealed record Incremented(CounterId CounterId, int Count) : IEvent;

[Aggregate("test-counter", SnapshotEvery = 3)]
public sealed partial record Counter : IAggregate<Counter, CounterId>
{
    public static Counter Initial { get; } = new();

    public int Count { get; init; }

    public Decision Decide(Increment c, DecisionContext ctx)
    {
        return Decision.Accept(new Incremented(c.CounterId, Count + 1));
    }

    public Decision Decide(Decrement c, DecisionContext ctx)
    {
        return Count == 0
            ? Decision.Reject(new Error("counter.empty", "The counter is zero."))
            : Decision.Accept(new Incremented(c.CounterId, Count - 1));
    }

    private Counter When(Incremented e)
    {
        return this with { Count = e.Count };
    }
}

[SourceFlowModule(typeof(CoreTestJsonContext))]
public sealed partial class CoreTestModule;

[JsonSerializable(typeof(Increment))]
[JsonSerializable(typeof(Decrement))]
[JsonSerializable(typeof(Incremented))]
[JsonSerializable(typeof(Counter))]
[JsonSerializable(typeof(RenamedV1))]
[JsonSerializable(typeof(RenamedV2))]
[JsonSerializable(typeof(RenamedV3))]
[JsonSerializable(typeof(Drip))]
[JsonSerializable(typeof(Dripped))]
[JsonSerializable(typeof(Dripper))]
[JsonSerializable(typeof(Drips))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class CoreTestJsonContext : JsonSerializerContext;

/// <summary>Snapshots (doc 05 §8.1, SCK-51..55 runtime side).</summary>
[Trait("Category", "Unit")]
public sealed class SnapshotTests
{
    [Fact]
    [Trait("Id", "UT-SNAP-01")]
    [Trait("Covers", "doc 05 8.1")]
    public async Task Snapshot_IsWrittenAtTheInterval_AndUsedOnLoad()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
        });
        var id = CounterId.New();
        StreamId stream = StreamId.For(Counter.AggregateName, id);
        ISnapshotStore snapshots = host.Store;

        for (int i = 0; i < 7; i++)
        {
            await host.SendAsync(new Increment(id));
        }

        await host.WaitForIdleAsync();   // snapshots are written in the background

        Snapshot snapshot = (await snapshots.GetAsync("-", stream, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        snapshot.Version.ShouldBe(new StreamVersion(6));
        JsonDocument.Parse(snapshot.Data).RootElement.GetProperty("count").GetInt32().ShouldBe(6);

        // Prove the load starts from the snapshot: a tampered snapshot changes the next decision.
        await snapshots.SaveAsync("-", snapshot with { Version = new StreamVersion(7), Data = "{\"count\":1000}"u8.ToArray() }, TestContext.Current.CancellationToken);
        await host.SendAsync(new Increment(id));
        (await host.EventsAsync(stream)).Last().ShouldBe(new Incremented(id, 1001));
    }

    [Fact]
    [Trait("Id", "SCK-54")]
    [Trait("Covers", "doc 05 8.1")]
    public async Task LoadFromSnapshotPlusTail_EqualsTheFullFold_ForRandomHistories()
    {
        // Each history runs against SnapshotEvery = 3 with snapshots written in the background at varying lag; every
        // decision depends on the loaded state, so a wrong snapshot + tail fold shows up as a diverging decision.
        await Gen.Bool.Array[1, 30].SampleAsync(async increments =>
        {
            await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
            {
                sf.ServiceName = "counters";
                sf.AddModule<CoreTestModule>();
            });
            var id = CounterId.New();
            int model = 0;
            foreach (bool increment in increments)
            {
                CommandResult result = increment ? await host.SendAsync(new Increment(id)) : await host.SendAsync(new Decrement(id));
                if (increment || model > 0)
                {
                    model += increment ? 1 : -1;
                    result.Status.ShouldBe(CommandStatus.Accepted);
                }
                else
                {
                    result.Status.ShouldBe(CommandStatus.Rejected);
                }
            }

            await host.WaitForIdleAsync();
            IReadOnlyList<IEvent> events = await host.EventsAsync(StreamId.For(Counter.AggregateName, id));
            (events.Count == 0 ? 0 : ((Incremented)events[^1]).Count).ShouldBe(model);
        }, iter: 40);
    }

    [Fact]
    [Trait("Id", "UT-SNAP-02")]
    public async Task Snapshot_WithAnotherSchemaHash_IsIgnored()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
        });
        var id = CounterId.New();
        StreamId stream = StreamId.For(Counter.AggregateName, id);
        await host.SendAsync(new Increment(id));

        await ((ISnapshotStore)host.Store).SaveAsync("-", new Snapshot(stream, new StreamVersion(1), "Counter", "stale-hash", "{\"count\":500}"u8.ToArray()), TestContext.Current.CancellationToken);
        await host.SendAsync(new Increment(id));

        (await host.EventsAsync(stream)).Last().ShouldBe(new Incremented(id, 2));
    }
}
