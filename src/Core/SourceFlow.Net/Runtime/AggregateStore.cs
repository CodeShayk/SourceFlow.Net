using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// Loads aggregates (doc 05 §4): the latest matching snapshot (when the aggregate opts in), then the stream tail.
/// Unknown stored types fail loudly. Snapshots are written after commit, outside the command transaction (doc 05 §8.1).
/// </summary>
internal sealed class AggregateStore(SourceFlowRuntime runtime)
{
    private const int PageSize = 512;

    /// <summary>
    /// Loads an aggregate: from the cache (when enabled and <paramref name="useCache"/>), else the latest matching
    /// snapshot, then the stream tail. With <see cref="AggregateCacheMode.Optimistic"/> and <paramref name="optimistic"/>, a
    /// cache hit skips the tail read; <see cref="LoadResult.Optimistic"/> tells the caller to confirm non-appending decisions.
    /// </summary>
    public async ValueTask<LoadResult> LoadAsync(
        AggregateDescriptor aggregate, StreamId stream, string tenant,
        Func<StreamVersion, int, CancellationToken, ValueTask<StreamSlice>> read, CancellationToken cancellationToken,
        bool useCache = false, bool optimistic = false)
    {
        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("sourceflow.eventstore.load", ActivityKind.Client);
        object state = aggregate.Initial;
        StreamVersion version = StreamVersion.None;
        string source = "events";
        AggregateCache cache = runtime.AggregateCache;
        if (useCache && cache.Mode != AggregateCacheMode.Off)
        {
            if (cache.TryGet(tenant, stream, aggregate.StateType, out object cached, out StreamVersion cachedVersion))
            {
                CacheResult(aggregate, "hit");
                if (optimistic && cache.Mode == AggregateCacheMode.Optimistic)
                {
                    Record(activity, aggregate, "cache", 0);
                    return new LoadResult(cached, cachedVersion, Optimistic: true);
                }

                (state, version, source) = (cached, cachedVersion, "cache");
            }
            else
            {
                CacheResult(aggregate, "miss");
            }
        }

        if (source == "events"
            && aggregate.SnapshotEvery > 0
            && await runtime.Storage.Snapshots.GetAsync(tenant, stream, cancellationToken).ConfigureAwait(false) is { } snapshot
            && snapshot.SchemaHash == runtime.Snapshots.SchemaHash(aggregate))
        {
            state = JsonSerializer.Deserialize(snapshot.Data.Span, runtime.Model.JsonTypeInfoFor(aggregate.StateType))!;
            version = snapshot.Version;
            source = "snapshot";
        }

        long folded = 0;
        while (true)
        {
            StreamSlice slice = await read(version + 1, PageSize, cancellationToken).ConfigureAwait(false);
            foreach (StoredEvent stored in slice.Events)
            {
                state = aggregate.Evolve(state, (IEvent)Deserialize(stored, upcast: true));
                version = stored.StreamVersion;
                folded++;
            }

            if (slice.IsEndOfStream || slice.Events.Count == 0)
            {
                break;
            }
        }

        if (source == "cache" && folded > 0)
        {
            // Another instance appended since this one cached the stream.
            CacheResult(aggregate, "stale");
        }

        Record(activity, aggregate, source, folded);
        return new LoadResult(state, version, Optimistic: false);
    }

    /// <summary>
    /// After a successful commit: caches the new state and queues a snapshot when the append crossed a multiple of
    /// <see cref="AggregateDescriptor.SnapshotEvery"/> (written in the background, outside the command transaction).
    /// </summary>
    public async ValueTask CommittedAsync(
        AggregateDescriptor aggregate, StreamId stream, string tenant, object stateBefore, StreamVersion before, IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        int every = aggregate.SnapshotEvery;
        StreamVersion after = before + events.Count;
        bool snapshotDue = every > 0 && after.Value / every != before.Value / every;
        bool cached = runtime.AggregateCache.Mode != AggregateCacheMode.Off;
        if (!snapshotDue && !cached)
        {
            return;
        }

        object state = stateBefore;
        foreach (IEvent e in events)
        {
            state = aggregate.Evolve(state, e);
        }

        runtime.AggregateCache.Set(tenant, stream, state, after);
        if (snapshotDue)
        {
            await runtime.Snapshots.EnqueueAsync(new SnapshotWork(aggregate, stream, tenant, state, after), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Drops a cached state (after a conflict).</summary>
    public void Invalidate(string tenant, StreamId stream)
    {
        runtime.AggregateCache.Remove(tenant, stream);
    }

    private void CacheResult(AggregateDescriptor aggregate, string result)
    {
        runtime.Metrics.AggregateCacheHits.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.AggregateType, aggregate.Name),
            new KeyValuePair<string, object?>("result", result));
    }

    private void Record(Activity? activity, AggregateDescriptor aggregate, string source, long folded)
    {
        runtime.Metrics.LoadEvents.Record(folded, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.AggregateType, aggregate.Name),
            new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.LoadSource, source));
        activity?.SetTag(SourceFlowTelemetry.Tags.LoadSource, source);
        activity?.SetTag("sourceflow.load.events", folded);
    }

    /// <summary>Deserializes a stored event through the allow-list; storage reads are upcast to the latest version.</summary>
    public object Deserialize(StoredEvent stored, bool upcast)
    {
        MessageTypeInfo type = runtime.Model.Registry.TryGet(stored.Type, stored.TypeVersion, out MessageTypeInfo? info)
            ? info
            : throw new UnknownMessageTypeException(stored.Type, stored.TypeVersion,
                $"Stream '{stored.StreamId}' contains ('{stored.Type}', {stored.TypeVersion}), which is not registered. Register the type (or its upcaster) before loading (fail loud).");
        object message = runtime.Serializer.Deserialize(stored.Data, type);
        return upcast ? runtime.Model.Registry.UpcastToLatest(message, type, out _) : message;
    }
}

/// <summary>An aggregate load: the state, its version, and whether it came unverified from the optimistic cache.</summary>
internal readonly record struct LoadResult(object State, StreamVersion Version, bool Optimistic);
