using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
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
    private readonly ConcurrentDictionary<Type, string> _schemaHashes = new();

    public async ValueTask<(object State, StreamVersion Version)> LoadAsync(
        AggregateDescriptor aggregate, StreamId stream, string tenant,
        Func<StreamVersion, int, CancellationToken, ValueTask<StreamSlice>> read, CancellationToken cancellationToken)
    {
        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("sourceflow.eventstore.load", ActivityKind.Client);
        object state = aggregate.Initial;
        StreamVersion version = StreamVersion.None;
        string source = "events";
        if (aggregate.SnapshotEvery > 0
            && await runtime.Storage.Snapshots.GetAsync(tenant, stream, cancellationToken).ConfigureAwait(false) is { } snapshot
            && snapshot.SchemaHash == SchemaHash(aggregate))
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

        runtime.Metrics.LoadEvents.Record(folded, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.AggregateType, aggregate.Name),
            new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.LoadSource, source));
        activity?.SetTag(SourceFlowTelemetry.Tags.LoadSource, source);
        activity?.SetTag("sourceflow.load.events", folded);
        return (state, version);
    }

    /// <summary>Writes a snapshot when an append crossed a multiple of <see cref="AggregateDescriptor.SnapshotEvery"/>. Best effort.</summary>
    public async ValueTask SnapshotIfDueAsync(
        AggregateDescriptor aggregate, StreamId stream, string tenant, object stateBefore, StreamVersion before, IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        int every = aggregate.SnapshotEvery;
        StreamVersion after = before + events.Count;
        if (every <= 0 || after.Value / every == before.Value / every)
        {
            return;
        }

        try
        {
            object state = stateBefore;
            foreach (IEvent e in events)
            {
                state = aggregate.Evolve(state, e);
            }

            JsonTypeInfo typeInfo = runtime.Model.JsonTypeInfoFor(aggregate.StateType);
            await runtime.Storage.Snapshots.SaveAsync(tenant,
                new Snapshot(stream, after, aggregate.StateType.FullName ?? aggregate.StateType.Name, SchemaHash(aggregate), JsonSerializer.SerializeToUtf8Bytes(state, typeInfo)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A lost snapshot is harmless: it is only a cache (doc 05 §8.1).
        }
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

    /// <summary>
    /// The snapshot schema hash: the snapshot version plus the state's JSON shape (property names and types), so a shape
    /// change or an explicit version bump invalidates old snapshots automatically.
    /// </summary>
    public string SchemaHash(AggregateDescriptor aggregate)
    {
        return _schemaHashes.GetOrAdd(aggregate.StateType, _ =>
        {
            JsonTypeInfo info = runtime.Model.JsonTypeInfoFor(aggregate.StateType);
            var shape = new StringBuilder().Append(aggregate.SnapshotVersion).Append('|').Append(aggregate.StateType.FullName);
            foreach (JsonPropertyInfo property in info.Properties.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                shape.Append('|').Append(property.Name).Append(':').Append(property.PropertyType.FullName);
            }

            return Convert.ToHexStringLower(XxHash64.Hash(Encoding.UTF8.GetBytes(shape.ToString())));
        });
    }
}
