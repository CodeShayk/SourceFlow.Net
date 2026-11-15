using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// The in-process aggregate state cache (doc 05 §8.2): a size-limited <see cref="MemoryCache"/> of
/// <c>(state, version)</c> per stream, set after each commit. It is correct by construction: optimistic concurrency
/// never lets a decision on a stale state commit. Aggregate states are immutable records (SF0008), so entries are shared safely.
/// </summary>
internal sealed class AggregateCache : IDisposable
{
    private readonly MemoryCache? _cache;
    private readonly MemoryCacheEntryOptions _entry = new() { Size = 1 };

    public AggregateCache(SourceFlowOptions options)
    {
        Mode = options.AggregateCache;
        if (Mode != AggregateCacheMode.Off)
        {
            _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.AggregateCacheSize, CompactionPercentage = 0.1 });
        }
    }

    public AggregateCacheMode Mode { get; }

    /// <summary>
    /// The longest an entry lives; null for no limit. Set with personal data encryption, so a state folded from decrypted
    /// data on this node can't outlive an erasure on another node by more than the key cache does.
    /// </summary>
    public TimeSpan? MaxAge { get; set; }

    public bool TryGet(string tenant, StreamId stream, Type stateType, out object state, out StreamVersion version)
    {
        if (_cache is not null && _cache.TryGetValue(new Key(tenant, stream.Value), out Entry? entry) && entry is not null && entry.State.GetType() == stateType)
        {
            state = entry.State;
            version = entry.Version;
            return true;
        }

        state = null!;
        version = StreamVersion.None;
        return false;
    }

    /// <summary>Caches a committed state; an older version never replaces a newer one.</summary>
    public void Set(string tenant, StreamId stream, object state, StreamVersion version)
    {
        if (_cache is null)
        {
            return;
        }

        var key = new Key(tenant, stream.Value);
        if (_cache.TryGetValue(key, out Entry? current) && current is not null && current.Version >= version)
        {
            return;
        }

        _cache.Set(key, new Entry(state, version), MaxAge is { } maxAge ? new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = maxAge } : _entry);
    }

    public void Remove(string tenant, StreamId stream)
    {
        _cache?.Remove(new Key(tenant, stream.Value));
    }

    public void Dispose()
    {
        _cache?.Dispose();
    }

    private readonly record struct Key(string Tenant, string Stream);

    private sealed record Entry(object State, StreamVersion Version);
}

/// <summary>A snapshot to write: the state after <see cref="Version"/>.</summary>
internal sealed record SnapshotWork(AggregateDescriptor Aggregate, StreamId Stream, string Tenant, object State, StreamVersion Version);

/// <summary>
/// Writes snapshots outside the command transaction (doc 05 §8.1): a bounded queue drained by one background loop
/// while the host runs; when full, the oldest entry is dropped. Without a running host, snapshots are written inline.
/// Best effort throughout: a lost snapshot only makes a later load longer.
/// </summary>
internal sealed class SnapshotWriter
{
    private readonly SourceFlowRuntime _runtime;
    private readonly ConcurrentDictionary<Type, string> _schemaHashes = new();
    private Channel<SnapshotWork>? _channel;
    private Task? _loop;
    private int _pending;

    public SnapshotWriter(SourceFlowRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>Snapshots queued and not yet written (for idle detection).</summary>
    public int Pending => Volatile.Read(ref _pending);

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        Channel<SnapshotWork> channel = Channel.CreateBounded<SnapshotWork>(
            new BoundedChannelOptions(_runtime.Options.SnapshotQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Dropped());
        _channel = channel;
        _loop = Task.Run(() => DrainAsync(channel));
    }

    /// <summary>Stops accepting work and drains the queue within <paramref name="timeout"/>.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        Channel<SnapshotWork>? channel = _channel;
        Task? loop = _loop;
        if (channel is null || loop is null)
        {
            return;
        }

        channel.Writer.TryComplete();
        try
        {
            await loop.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Unwritten snapshots are just cache misses later.
        }

        _channel = null;
        _loop = null;
    }

    public async ValueTask EnqueueAsync(SnapshotWork work, CancellationToken cancellationToken)
    {
        Channel<SnapshotWork>? channel = _channel;
        if (channel is null)
        {
            await WriteAsync(work, cancellationToken).ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref _pending);
        if (!channel.Writer.TryWrite(work))
        {
            // Completed while stopping.
            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>
    /// The snapshot schema hash: the snapshot version plus the state's JSON shape (property names and types), so a shape
    /// change or an explicit version bump invalidates old snapshots automatically.
    /// </summary>
    public string SchemaHash(AggregateDescriptor aggregate)
    {
        return _schemaHashes.GetOrAdd(aggregate.StateType, _ =>
        {
            JsonTypeInfo info = _runtime.Model.JsonTypeInfoFor(aggregate.StateType);
            var shape = new StringBuilder().Append(aggregate.SnapshotVersion).Append('|').Append(aggregate.StateType.FullName);
            foreach (JsonPropertyInfo property in info.Properties.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                shape.Append('|').Append(property.Name).Append(':').Append(property.PropertyType.FullName);
            }

            return Convert.ToHexStringLower(XxHash64.Hash(Encoding.UTF8.GetBytes(shape.ToString())));
        });
    }

    private async Task DrainAsync(Channel<SnapshotWork> channel)
    {
        await foreach (SnapshotWork work in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await WriteAsync(work, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private async ValueTask WriteAsync(SnapshotWork work, CancellationToken cancellationToken)
    {
        try
        {
            JsonTypeInfo typeInfo = _runtime.Model.JsonTypeInfoFor(work.Aggregate.StateType);
            ReadOnlyMemory<byte> data = JsonSerializer.SerializeToUtf8Bytes(work.State, typeInfo);
            if (_runtime.PersonalData is { } personal && personal.Protects(work.Aggregate.StateType))
            {
                data = (await personal.ProtectAsync(work.Aggregate.StateType, work.Tenant, work.Stream.Value, data, cancellationToken).ConfigureAwait(false)).Body;   // doc 10 §4
            }

            var snapshot = new Snapshot(work.Stream, work.Version, work.Aggregate.StateType.FullName ?? work.Aggregate.StateType.Name,
                SchemaHash(work.Aggregate), data);
            await _runtime.Storage.Snapshots.SaveAsync(work.Tenant, snapshot, cancellationToken).ConfigureAwait(false);
            _runtime.Metrics.SnapshotsWritten.Add(1, new KeyValuePair<string, object?>(Diagnostics.SourceFlowTelemetry.Tags.AggregateType, work.Aggregate.Name));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A lost snapshot is harmless: it is only a cache (doc 05 §8.1).
            _runtime.Metrics.SnapshotsDropped.Add(1, new KeyValuePair<string, object?>(Diagnostics.SourceFlowTelemetry.Tags.AggregateType, work.Aggregate.Name));
        }
    }

    private void Dropped()
    {
        Interlocked.Decrement(ref _pending);
        _runtime.Metrics.SnapshotsDropped.Add(1);
    }
}
