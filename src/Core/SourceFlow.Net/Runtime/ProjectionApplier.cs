using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// Applies document projections inside a storage session; shared by inline projections (command transaction) and
/// async projections (subscription transaction). Document versions follow the source stream when the document is keyed
/// by the stream's aggregate id, otherwise they count writes (DC-2, doc 06 §5).
/// </summary>
internal sealed class ProjectionApplier(SourceFlowRuntime runtime)
{
    public async ValueTask ApplyAsync(IStorageSession session, ProjectionDescriptor projection, IEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        if (!Handles(projection, @event) || !projection.TryGetDocumentId(@event, context, out string? id))
        {
            return;
        }

        JsonTypeInfo typeInfo = runtime.Model.JsonTypeInfoFor(projection.DocumentType);
        StoredDocument? stored = await session.Documents.GetAsync(projection.Collection, id, cancellationToken).ConfigureAwait(false);
        object? current = stored is null ? null : JsonSerializer.Deserialize(stored.Data.Span, typeInfo);
        object? next = projection.Apply(@event, current, context);
        if (next is null)
        {
            if (stored is not null)
            {
                session.Documents.Delete(projection.Collection, id);
            }

            return;
        }

        long version = id == context.AggregateKey ? context.StreamVersion.Value : (stored?.Version ?? 0) + 1;
        session.Documents.Upsert(projection.Collection, id, version, JsonSerializer.SerializeToUtf8Bytes(next, typeInfo));
    }

    public static bool Handles(ProjectionDescriptor projection, IEvent @event)
    {
        Type type = @event.GetType();
        foreach (Type handled in projection.HandledEvents)
        {
            if (handled == type)
            {
                return true;
            }
        }

        return false;
    }
}
