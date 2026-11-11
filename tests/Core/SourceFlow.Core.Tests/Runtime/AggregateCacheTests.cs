using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Diagnostics;
using SourceFlow.Hosting;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-SNAP-03..10: background snapshots and the aggregate state cache (doc 05 §8, WP-2.5).</summary>
[Trait("Category", "Unit")]
public sealed class AggregateCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-SNAP-03")]
    [Trait("Covers", "doc 05 8.1")]
    public async Task Snapshots_AreQueued_AndAFullQueueDropsTheOldest_WithoutBlockingCommands()
    {
        var store = new InMemoryStore();
        var gated = new GatedSnapshots(store);
        await using SourceFlowTestHost host = await StartAsync(store, configure: sf =>
        {
            sf.UseStorage(_ => Ports(store, gated));
            sf.Configure(o => o.SnapshotQueueCapacity = 1);
        });
        var id = CounterId.New();

        // SnapshotEvery = 3: snapshots at 3 (taken by the writer, which blocks), 6 (queued) and 9 (drops 6).
        for (int i = 0; i < 9; i++)
        {
            (await host.SendAsync(new Increment(id))).Status.ShouldBe(CommandStatus.Accepted);
        }

        await gated.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        gated.Release();
        await host.WaitForIdleAsync();

        gated.Saved.Select(s => s.Version.Value).ShouldBe([3L, 9L]);
    }

    [Fact]
    [Trait("Id", "UT-SNAP-04")]
    [Trait("Covers", "doc 05 8.1")]
    public async Task FailingSnapshotStore_IsHarmless()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost host = await StartAsync(store, configure: sf => sf.UseStorage(_ => Ports(store, new FailingSnapshots())));
        var id = CounterId.New();
        long dropped = 0;
        using MeterListener listener = Listen(host, "sourceflow.snapshot.dropped", (value, _) => Interlocked.Add(ref dropped, value));

        for (int i = 0; i < 4; i++)
        {
            await host.SendAsync(new Increment(id));
        }

        await host.WaitForIdleAsync();
        (await host.EventsAsync(StreamId.For(Counter.AggregateName, id))).Last().ShouldBe(new Incremented(id, 4));
        Interlocked.Read(ref dropped).ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-SNAP-05")]
    [Trait("Covers", "doc 05 8.2")]
    public async Task ReadTail_StartsFromTheCache_AndCatchesUpWithAnotherNode()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost a = await StartAsync(store, AggregateCacheMode.ReadTail);
        await using SourceFlowTestHost b = await StartAsync(store, AggregateCacheMode.ReadTail);
        var id = CounterId.New();
        var results = new List<string>();
        using MeterListener listener = Listen(a, "sourceflow.aggregate.cache.hits", (_, result) => { lock (results) { results.Add(result!); } });

        await a.SendAsync(new Increment(id));   // miss, then cached at v1
        await a.SendAsync(new Increment(id));   // hit
        await b.SendAsync(new Increment(id));   // another node: v3
        CommandResult fourth = await a.SendAsync(new Increment(id));   // hit, but stale: the tail read catches up

        fourth.Version.ShouldBe(new StreamVersion(4));
        (await a.EventsAsync(StreamId.For(Counter.AggregateName, id))).Last().ShouldBe(new Incremented(id, 4));
        results.ShouldBe(["miss", "hit", "hit", "stale"]);
    }

    [Fact]
    [Trait("Id", "UT-SNAP-06")]
    [Trait("Covers", "doc 05 8.2;ADR-008")]
    public async Task Optimistic_StaleEntry_ConflictsReloadsAndSucceeds()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost a = await StartAsync(store, AggregateCacheMode.Optimistic);
        await using SourceFlowTestHost b = await StartAsync(store, AggregateCacheMode.Optimistic);
        var id = CounterId.New();
        long conflicts = 0;
        using MeterListener listener = Listen(a, "sourceflow.eventstore.concurrency_conflicts", (value, _) => Interlocked.Add(ref conflicts, value));

        await a.SendAsync(new Increment(id));   // a caches v1
        await b.SendAsync(new Increment(id));   // v2 elsewhere
        CommandResult third = await a.SendAsync(new Increment(id));   // decides on stale v1, conflicts, reloads

        third.Status.ShouldBe(CommandStatus.Accepted);
        third.Version.ShouldBe(new StreamVersion(3));
        (await a.EventsAsync(StreamId.For(Counter.AggregateName, id))).Cast<Incremented>().Select(e => e.Count).ShouldBe([1, 2, 3]);
        Interlocked.Read(ref conflicts).ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-SNAP-07")]
    [Trait("Covers", "doc 05 8.2")]
    public async Task Optimistic_RejectionOnStaleState_IsConfirmedAgainstTheTail()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost a = await StartAsync(store, AggregateCacheMode.Optimistic);
        await using SourceFlowTestHost b = await StartAsync(store, AggregateCacheMode.Optimistic);
        var id = CounterId.New();
        await a.SendAsync(new Increment(id));
        await a.SendAsync(new Decrement(id));   // a caches count 0 at v2
        await b.SendAsync(new Increment(id));   // count 1 at v3, elsewhere

        CommandResult decrement = await a.SendAsync(new Decrement(id));

        decrement.Status.ShouldBe(CommandStatus.Accepted);   // not a stale "counter.empty" rejection
        decrement.Version.ShouldBe(new StreamVersion(4));
        (await a.SendAsync(new Decrement(id))).Error!.Code.ShouldBe("counter.empty");   // a genuine rejection still stands
    }

    [Fact]
    [Trait("Id", "UT-SNAP-08")]
    public async Task Optimistic_IsNotUsed_ForAnExpectedVersion()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost a = await StartAsync(store, AggregateCacheMode.Optimistic);
        await using SourceFlowTestHost b = await StartAsync(store, AggregateCacheMode.Optimistic);
        var id = CounterId.New();
        await a.SendAsync(new Increment(id));   // a caches v1
        await b.SendAsync(new Increment(id));   // v2

        CommandResult matching = await a.SendAsync(new Increment(id), new SendOptions { ExpectedVersion = new StreamVersion(2) });

        matching.Status.ShouldBe(CommandStatus.Accepted);
        matching.Version.ShouldBe(new StreamVersion(3));
    }

    [Fact]
    [Trait("Id", "UT-SNAP-09")]
    public async Task CacheOff_NeverCaches()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost host = await StartAsync(store);
        var id = CounterId.New();
        int lookups = 0;
        using MeterListener listener = Listen(host, "sourceflow.aggregate.cache.hits", (_, _) => Interlocked.Increment(ref lookups));

        await host.SendAsync(new Increment(id));
        await host.SendAsync(new Increment(id));

        lookups.ShouldBe(0);
        host.Runtime.AggregateCache.TryGet("-", StreamId.For(Counter.AggregateName, id), typeof(Counter), out _, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-SNAP-10")]
    public void CacheKeepsTheNewerVersion_AndChecksTheStateType()
    {
        using var cache = new AggregateCache(new SourceFlowOptions { AggregateCache = AggregateCacheMode.ReadTail });
        var stream = new StreamId("test-counter-1");

        cache.Set("-", stream, new Counter { Count = 5 }, new StreamVersion(5));
        cache.Set("-", stream, new Counter { Count = 4 }, new StreamVersion(4));

        cache.TryGet("-", stream, typeof(Counter), out object state, out StreamVersion version).ShouldBeTrue();
        ((Counter)state).Count.ShouldBe(5);
        version.ShouldBe(new StreamVersion(5));
        cache.TryGet("-", stream, typeof(string), out _, out _).ShouldBeFalse();
        cache.TryGet("eu", stream, typeof(Counter), out _, out _).ShouldBeFalse();
    }

    private static Task<SourceFlowTestHost> StartAsync(InMemoryStore store, AggregateCacheMode mode = AggregateCacheMode.Off, Action<SourceFlowBuilder>? configure = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
            sf.Configure(o => o.AggregateCache = mode);
            configure?.Invoke(sf);
        }, store: store);
    }

    private static StoragePorts Ports(InMemoryStore store, ISnapshotStore snapshots)
    {
        return new StoragePorts(store, store, store, store, store, store, store, snapshots, store, store);
    }

    private static MeterListener Listen(SourceFlowTestHost host, string instrumentName, Action<long, string?> onMeasurement)
    {
        Meter meter = host.Services.GetRequiredService<SourceFlowMetrics>().Meter;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? result = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "result")
                {
                    result = tag.Value as string;
                }
            }

            onMeasurement(value, result);
        });
        listener.Start();
        return listener;
    }

    private sealed class GatedSnapshots(ISnapshotStore inner) : ISnapshotStore
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Snapshot> Saved { get; } = [];

        public void Release()
        {
            _gate.TrySetResult();
        }

        public ValueTask<Snapshot?> GetAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
        {
            return inner.GetAsync(tenantId, stream, cancellationToken);
        }

        public async ValueTask SaveAsync(string tenantId, Snapshot snapshot, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await _gate.Task;
            lock (Saved)
            {
                Saved.Add(snapshot);
            }

            await inner.SaveAsync(tenantId, snapshot, cancellationToken);
        }
    }

    private sealed class FailingSnapshots : ISnapshotStore
    {
        public ValueTask<Snapshot?> GetAsync(string tenantId, StreamId stream, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<Snapshot?>(null);
        }

        public ValueTask SaveAsync(string tenantId, Snapshot snapshot, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("snapshot store down");
        }
    }
}
