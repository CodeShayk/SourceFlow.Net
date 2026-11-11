using System.Diagnostics;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>Loads aggregates by folding their streams (doc 05 §4). Unknown stored types fail loudly.</summary>
internal sealed class AggregateStore(SourceFlowRuntime runtime)
{
    private const int PageSize = 512;

    public async ValueTask<(object State, StreamVersion Version)> LoadAsync(
        AggregateDescriptor aggregate, StreamId stream, Func<StreamVersion, int, CancellationToken, ValueTask<StreamSlice>> read, CancellationToken cancellationToken)
    {
        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("sourceflow.eventstore.load", ActivityKind.Client);
        object state = aggregate.Initial;
        StreamVersion version = StreamVersion.None;
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
            new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.LoadSource, "events"));
        activity?.SetTag(SourceFlowTelemetry.Tags.LoadSource, "events");
        activity?.SetTag("sourceflow.load.events", folded);
        return (state, version);
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
