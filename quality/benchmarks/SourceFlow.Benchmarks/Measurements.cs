using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.PostgreSQL;

namespace SourceFlow.Benchmarks;

/// <summary>A measured result against its doc 09 §7.1 target.</summary>
internal sealed record PerfMeasurement(string Id, string Name, double Value, string Unit, string Target, bool Met, string Notes);

/// <summary>Throughput and latency measurements that are not per-operation benchmarks (PERF-05, PERF-08, PERF-10).</summary>
internal static class Measurements
{
    public static async Task<IReadOnlyList<PerfMeasurement>> RunAsync()
    {
        var results = new List<PerfMeasurement>();
        results.AddRange(await StartupAsync());
        results.Add(await RebuildAsync(batchSize: 1000));   // the default: adaptive 100 → 1,000
        results.Add(await RebuildAsync(batchSize: 256));
        results.Add(await ProjectionLatencyAsync());
        await BenchEnvironment.StopAsync();
        return results;
    }

    // PERF-10: build and start an in-memory host to ready; the first start in the process pays the JIT cost.
    private static async Task<IReadOnlyList<PerfMeasurement>> StartupAsync()
    {
        var times = new List<double>();
        for (int i = 0; i < 21; i++)
        {
            long started = Stopwatch.GetTimestamp();
            using IHost host = BenchEnvironment.InMemoryHost();
            await host.StartAsync();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await host.StopAsync();
        }

        double cold = times[0];
        double warm = Percentile(times.Skip(1).ToList(), 0.5);
        return
        [
            new PerfMeasurement("PERF-10", "Startup to ready, first start in process (JIT)", cold, "ms", "<= 150 ms", cold <= 150, "In-memory storage; excludes process start and provisioning"),
            new PerfMeasurement("PERF-10", "Startup to ready, warm (median of 20)", warm, "ms", "-", true, "Reference only"),
        ];
    }

    // PERF-05: an async document projection catching up on 20,000 events on one partition.
    private static async Task<PerfMeasurement> RebuildAsync(int batchSize)
    {
        const int streams = 400;
        const int perStream = 50;
        using IHost writer = await BenchEnvironment.PostgreSqlHostAsync(sf => sf.Roles = SourceFlowRoles.Api);
        ICommandSender commands = writer.Services.GetRequiredService<ICommandSender>();
        await Parallel.ForEachAsync(Enumerable.Range(0, streams), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (_, ct) =>
        {
            var id = BenchId.New();
            for (int i = 0; i < perStream; i++)
            {
                await commands.SendAsync(new Increment(id), null, ct);
            }
        });

        PostgreSqlStore store = writer.Services.GetRequiredService<PostgreSqlStore>();
        // The position of the last event (the horizon itself has a sequence no event reaches).
        GlobalPosition last = GlobalPosition.Start;
        while ((await store.ReadAsync(last, BucketRange.All, 5000, CancellationToken.None)).Events is { Count: > 0 } page)
        {
            last = page[^1].Position;
        }

        using IHost reader = BuildReader(store, batchSize);
        await reader.StartAsync();
        long started = Stopwatch.GetTimestamp();
        while (await store.GetAsync("projection:bench-totals@1", 0, CancellationToken.None) < last)
        {
            await Task.Delay(5);
        }

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        await reader.StopAsync();
        double rate = streams * perStream / seconds;
        return new PerfMeasurement("PERF-05", $"Document projection rebuild, one partition, max batch {batchSize}", rate, "events/s", ">= 20,000 events/s", rate >= 20_000,
            $"{streams * perStream} events, adaptive batches up to SubscriptionBatchSize {batchSize}, timed after host start");
    }

    // PERF-08: commit to async projection visibility, woken by LISTEN/NOTIFY.
    private static async Task<PerfMeasurement> ProjectionLatencyAsync()
    {
        using IHost host = await BenchEnvironment.PostgreSqlHostAsync(sf => sf.Projections(p => p.Add<TotalsProjection>().Async()));
        await host.StartAsync();
        ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
        PostgreSqlStore store = host.Services.GetRequiredService<PostgreSqlStore>();
        RuntimeModel model = host.Services.GetRequiredService<RuntimeModel>();
        string collection = model.Projections.Single().Collection;
        var latencies = new List<double>();
        for (int i = 0; i < 200; i++)
        {
            var id = BenchId.New();
            long started = Stopwatch.GetTimestamp();
            await commands.SendAsync(new Increment(id), null, CancellationToken.None);
            while (await ((IDocumentReader)store).GetAsync("-", collection, id.ToStreamSegment(), CancellationToken.None) is null)
            {
                await Task.Yield();
            }

            latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        await host.StopAsync();
        double p95 = Percentile(latencies.Skip(20).ToList(), 0.95);
        return new PerfMeasurement("PERF-08", "Event to async projection latency (p95)", p95, "ms", "<= 100 ms (p95)", p95 <= 100,
            $"p50 {Percentile(latencies.Skip(20).ToList(), 0.5):F1} ms over 180 commands after 20 warm-up; includes the command itself");
    }

    private static IHost BuildReader(PostgreSqlStore store, int batchSize)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "bench";
            sf.Roles = SourceFlowRoles.Subscriptions;
            sf.Configure(o => o.SubscriptionBatchSize = batchSize);
            sf.AddModule<BenchModule>();
            sf.Projections(p => p.Add<TotalsProjection>().Async());
            sf.UseStorage(_ => store.ToPorts());
            sf.UseInMemoryTransport();
        });
        return builder.Build();
    }

    private static double Percentile(List<double> values, double percentile)
    {
        values.Sort();
        return values[Math.Clamp((int)Math.Ceiling(percentile * values.Count) - 1, 0, values.Count - 1)];
    }
}
