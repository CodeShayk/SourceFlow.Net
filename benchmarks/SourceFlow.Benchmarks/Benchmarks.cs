using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Storage;
using SourceFlow.Storage.PostgreSQL;

namespace SourceFlow.Benchmarks;

/// <summary>PERF-01: an in-process command on the in-memory store, one event, a new stream each time.</summary>
[MemoryDiagnoser]
public class CommandBenchmarks
{
    private IHost _host = null!;
    private ICommandSender _commands = null!;

    [GlobalSetup]
    public void Setup()
    {
        _host = BenchEnvironment.InMemoryHost();
        _commands = _host.Services.GetRequiredService<ICommandSender>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _host.Dispose();
    }

    [Benchmark(Description = "PERF-01 in-memory command, 1 event")]
    public ValueTask<CommandResult> Command()
    {
        return _commands.SendAsync(new Increment(BenchId.New()), null, CancellationToken.None);
    }
}

/// <summary>PERF-02: the cost of three pass-through middleware over the bare pipeline.</summary>
[MemoryDiagnoser]
public class MiddlewareBenchmarks
{
    private static readonly MessageDelegate Terminal = static _ => default;
    private MessagePipeline _bare = null!;
    private MessagePipeline _three = null!;
    private MessageContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        _bare = new MessagePipeline([]);
        _three = new MessagePipeline(Enumerable.Range(0, 3).Select(_ => new MiddlewareRegistration(_ => PassThrough.Instance, PipelineStage.All)));
        var envelope = new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "bench.increment",
            TypeVersion = 1,
            Source = "bench",
            Time = DateTimeOffset.UnixEpoch,
            Body = Encoding.UTF8.GetBytes("{}"),
        };
        _context = new MessageContext(PipelineStage.Handle, envelope, new ServiceCollection().BuildServiceProvider(), CancellationToken.None);
    }

    [Benchmark(Baseline = true, Description = "PERF-02 pipeline, no middleware")]
    public ValueTask Bare()
    {
        return _bare.InvokeAsync(_context, Terminal);
    }

    [Benchmark(Description = "PERF-02 pipeline, 3 middleware")]
    public ValueTask Three()
    {
        return _three.InvokeAsync(_context, Terminal);
    }

    private sealed class PassThrough : IMessageMiddleware
    {
        public static readonly PassThrough Instance = new();

        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            return next(context);
        }
    }
}

/// <summary>PERF-03: a PostgreSQL commit of 1, 3 or 10 events plus an outbox row (one round trip, ADR-028).</summary>
[MemoryDiagnoser]
public class PostgreSqlCommitBenchmarks
{
    private PostgreSqlStore _store = null!;
    private EventToAppend[] _events = null!;

    [Params(1, 3, 10)]
    public int Events { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _store = await BenchEnvironment.StoreAsync();
        _events = [.. Enumerable.Range(0, Events).Select(_ => Event())];
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _store.DisposeAsync();
        await BenchEnvironment.StopAsync();
    }

    [Benchmark(Description = "PERF-03 PostgreSQL commit + outbox")]
    public async Task<CommitResult> Commit()
    {
        await using IStorageSession session = await _store.BeginAsync(MessageEnvelope.DefaultTenant, CancellationToken.None);
        session.Append(new StreamId("bench-" + Guid.CreateVersion7().ToString("N")), "bench", ExpectedVersion.NoStream,
            [.. _events.Select(e => e with { EventId = new MessageId(Guid.CreateVersion7()) })]);
        session.EnqueueOutbox(Outbox());
        return await session.CommitAsync(CancellationToken.None);
    }

    internal static EventToAppend Event()
    {
        return new EventToAppend(new MessageId(Guid.CreateVersion7()), "bench.incremented", 1, Encoding.UTF8.GetBytes("{\"id\":\"x\",\"count\":1}"),
            new EventMetadata { Source = "bench", CorrelationId = new CorrelationId("bench") }, DateTimeOffset.UtcNow, []);
    }

    private static OutboxMessage Outbox()
    {
        var envelope = new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "bench.incremented",
            TypeVersion = 1,
            Source = "bench",
            Time = DateTimeOffset.UnixEpoch,
            PartitionKey = "bench",
            Body = Encoding.UTF8.GetBytes("{}"),
        };
        return new OutboxMessage(envelope, "bench.events", OutboxKind.Publish, DateTimeOffset.MinValue, 0);
    }
}

/// <summary>PERF-04: command-path load of a stream with 0, 100 or 1,000 events, with and without snapshots (no append).</summary>
[MemoryDiagnoser]
public class LoadBenchmarks
{
    private IHost _host = null!;
    private ICommandSender _commands = null!;
    private BenchId _id;

    [Params(0, 100, 1000)]
    public int Events { get; set; }

    [Params(false, true)]
    public bool Snapshots { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _host = await BenchEnvironment.PostgreSqlHostAsync();
        _commands = _host.Services.GetRequiredService<ICommandSender>();
        _id = BenchId.New();
        for (int i = 0; i < Events; i++)
        {
            _ = Snapshots
                ? await _commands.SendAsync(new SnapIncrement(_id), null, CancellationToken.None)
                : await _commands.SendAsync(new Increment(_id), null, CancellationToken.None);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _host.Dispose();
        await BenchEnvironment.StopAsync();
    }

    [Benchmark(Description = "PERF-04 load + decide (PostgreSQL)")]
    public ValueTask<CommandResult> Load()
    {
        return Snapshots
            ? _commands.SendAsync(new SnapProbe(_id), null, CancellationToken.None)
            : _commands.SendAsync(new Probe(_id), null, CancellationToken.None);
    }
}

/// <summary>PERF-06: global log read throughput (reported per event).</summary>
[MemoryDiagnoser]
public class LogReadBenchmarks
{
    private const int TotalEvents = 20_000;
    private PostgreSqlStore _store = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _store = await BenchEnvironment.StoreAsync();
        for (int s = 0; s < TotalEvents / 100; s++)
        {
            await using IStorageSession session = await _store.BeginAsync(MessageEnvelope.DefaultTenant, CancellationToken.None);
            session.Append(new StreamId($"bench-log-{s}"), "bench", ExpectedVersion.NoStream, [.. Enumerable.Range(0, 100).Select(_ => PostgreSqlCommitBenchmarks.Event())]);
            await session.CommitAsync(CancellationToken.None);
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _store.DisposeAsync();
        await BenchEnvironment.StopAsync();
    }

    [Benchmark(Description = "PERF-06 global log read (per event)", OperationsPerInvoke = TotalEvents)]
    public async Task<int> ReadAll()
    {
        int read = 0;
        GlobalPosition after = GlobalPosition.Start;
        while (true)
        {
            LogBatch batch = await _store.ReadAsync(after, BucketRange.All, 1000, CancellationToken.None);
            if (batch.Events.Count == 0)
            {
                return read;
            }

            read += batch.Events.Count;
            after = batch.Events[^1].Position;
        }
    }
}
