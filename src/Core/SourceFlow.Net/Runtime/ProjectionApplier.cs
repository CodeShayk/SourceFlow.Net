using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// The projections of one storage session: inline projections in the command transaction, or async projections in a
/// subscription transaction.
/// <list type="bullet">
/// <item>Document projections work on a per-session document cache. A batch can prefetch its documents in one read
/// (<see cref="PrefetchAsync"/>), events apply to the deserialized documents in memory, and each changed document is
/// written once by <see cref="CompleteAsync"/> (PERF-05).</item>
/// <item>Session projections share one unit of work per type, completed (for example <c>SaveChangesAsync</c>) by
/// <see cref="CompleteAsync"/>.</item>
/// </list>
/// Call <see cref="CompleteAsync"/> before committing the session, so read models, events and checkpoints commit or roll
/// back together (doc 05 §10).
/// </summary>
internal sealed class ProjectionScope : IAsyncDisposable
{
    private readonly List<UnitOfWork> _units = [];
    private readonly Dictionary<(string Collection, string Id), CachedDocument> _documents = [];

    public ProjectionScope(SourceFlowRuntime runtime, IStorageSession session, IServiceProvider services)
    {
        Runtime = runtime;
        Session = session;
        Services = services;
    }

    public SourceFlowRuntime Runtime { get; }

    public IStorageSession Session { get; }

    public IServiceProvider Services { get; }

    /// <summary>
    /// The read-model generation this scope writes (a subscription building or serving one generation, doc 06 §6), or null
    /// for each projection's first generation (inline projections).
    /// </summary>
    public ReadModelGeneration? Generation { get; init; }

    /// <summary>The collection a document projection writes in this scope.</summary>
    public string CollectionOf(DocumentProjectionDescriptor projection)
    {
        return Generation is { } generation && generation.Name == projection.Name ? generation.Collection : projection.Collection;
    }

    public ValueTask ApplyAsync(ProjectionDescriptor projection, IEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        return projection.ApplyAsync(this, @event, context, cancellationToken);
    }

    /// <summary>Loads, in one read, every document the batch will touch that is not cached yet.</summary>
    public async ValueTask PrefetchAsync(DocumentProjectionDescriptor projection, IReadOnlyList<(IEvent Event, EventContext Context)> batch, CancellationToken cancellationToken)
    {
        string collection = CollectionOf(projection);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach ((IEvent @event, EventContext context) in batch)
        {
            if (projection.Handles(@event.GetType()) && projection.TryGetDocumentId(@event, context, out string? id) && !_documents.ContainsKey((collection, id)))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            return;
        }

        foreach (StoredDocument stored in await Session.Documents.GetManyAsync(collection, ids, cancellationToken).ConfigureAwait(false))
        {
            _documents[(collection, stored.Id)] = Cached(projection, stored);
            ids.Remove(stored.Id);
        }

        foreach (string missing in ids)
        {
            _documents[(collection, missing)] = new CachedDocument(projection);
        }
    }

    /// <summary>The cached document, read from the session on a miss.</summary>
    public async ValueTask<CachedDocument> DocumentAsync(DocumentProjectionDescriptor projection, string id, CancellationToken cancellationToken)
    {
        string collection = CollectionOf(projection);
        if (_documents.TryGetValue((collection, id), out CachedDocument? cached))
        {
            return cached;
        }

        StoredDocument? stored = await Session.Documents.GetAsync(collection, id, cancellationToken).ConfigureAwait(false);
        cached = stored is null ? new CachedDocument(projection) : Cached(projection, stored);
        _documents[(collection, id)] = cached;
        return cached;
    }

    public async ValueTask<TUnitOfWork> UnitOfWorkAsync<TUnitOfWork>(ProjectionDescriptor projection, CancellationToken cancellationToken)
        where TUnitOfWork : class
    {
        Type projectionType = projection.ProjectionType;
        IProjectionUnitOfWork<TUnitOfWork>? factory = Services.GetService<IProjectionUnitOfWork<TUnitOfWork>>();

        // A store that keeps generations apart (an EF Core schema per generation) gets one unit of work per generation.
        ReadModelGeneration generation = Generation is { } g && g.Name == projection.Name ? g : new ReadModelGeneration(projection.Name, projection.Version, 1);
        string? key = factory is IProjectionGenerationStore { SeparatesGenerations: true } ? generation.Collection : null;
        foreach (UnitOfWork unit in _units)
        {
            if (unit.Instance is TUnitOfWork existing && unit.Generation == key)
            {
                return existing;
            }
        }

        factory = factory
            ?? throw new InvalidOperationException(
                $"Projection {projectionType.Name} writes through {typeof(TUnitOfWork).Name}, but no IProjectionUnitOfWork<{typeof(TUnitOfWork).Name}> is registered. "
                + $"Register it with the storage package, for example UseEntityFrameworkProjections<{typeof(TUnitOfWork).Name}>(…).");
        if (_units.Count == 0)
        {
            // The unit of work writes through the session's connection: send the queued append first (ES-R1).
            await Session.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Services.GetService<ProjectionTarget>() is { } target)
        {
            target.Generation = generation;
        }

        TUnitOfWork created = await factory.BeginAsync(Session, Services, cancellationToken).ConfigureAwait(false);
        _units.Add(new UnitOfWork(created, key, ct => factory.CompleteAsync(created, ct)));
        return created;
    }

    /// <summary>Writes each changed document once, then flushes every unit of work; call before committing the session.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        foreach (((string collection, string id), CachedDocument document) in _documents)
        {
            if (!document.Dirty)
            {
                continue;
            }

            if (document.Value is null)
            {
                if (document.Stored)
                {
                    Session.Documents.Delete(collection, id);
                }
            }
            else
            {
                JsonTypeInfo typeInfo = Runtime.Model.JsonTypeInfoFor(document.Projection.DocumentType);
                Session.Documents.Upsert(collection, id, document.Version, JsonSerializer.SerializeToUtf8Bytes(document.Value, typeInfo), document.Position);
            }

            document.Stored = document.Value is not null;
            document.Dirty = false;
        }

        foreach (UnitOfWork unit in _units)
        {
            await unit.Complete(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (UnitOfWork unit in _units)
        {
            switch (unit.Instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        _units.Clear();
        _documents.Clear();
    }

    private CachedDocument Cached(DocumentProjectionDescriptor projection, StoredDocument stored)
    {
        JsonTypeInfo typeInfo = Runtime.Model.JsonTypeInfoFor(projection.DocumentType);
        return new CachedDocument(projection) { Value = JsonSerializer.Deserialize(stored.Data.Span, typeInfo), Version = stored.Version, Position = stored.Position, Stored = true };
    }

    private sealed record UnitOfWork(object Instance, string? Generation, Func<CancellationToken, ValueTask> Complete);
}

/// <summary>A document in a <see cref="ProjectionScope"/>: its current value, version and whether it must be written.</summary>
internal sealed class CachedDocument(DocumentProjectionDescriptor projection)
{
    public DocumentProjectionDescriptor Projection { get; } = projection;

    public object? Value { get; set; }

    public long Version { get; set; }

    /// <summary>For a cross-stream document, the global position of the last event applied (CR-31).</summary>
    public GlobalPosition? Position { get; set; }

    /// <summary>True when the document exists in the store (so a null value means delete).</summary>
    public bool Stored { get; set; }

    public bool Dirty { get; set; }
}

/// <summary>
/// Applies document projections inside a <see cref="ProjectionScope"/>. Document versions follow the source stream when
/// the document is keyed by the stream's aggregate id, otherwise they count writes and the document records the global
/// position of the last event applied (DC-2, doc 06 §5, CR-31).
/// </summary>
internal static class ProjectionApplier
{
    public static async ValueTask ApplyDocumentAsync(ProjectionScope scope, DocumentProjectionDescriptor projection, IEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        if (!projection.Handles(@event.GetType()) || !projection.TryGetDocumentId(@event, context, out string? id))
        {
            return;
        }

        CachedDocument document = await scope.DocumentAsync(projection, id, cancellationToken).ConfigureAwait(false);

        // DC-2 (doc 06 §5): a document keyed by the event's own stream already reflects every event up to its version;
        // re-applying one (a redelivery, a repartition) would double-count incremental projections, so it is skipped.
        bool streamKeyed = id == context.AggregateKey;
        if (document.Value is not null && streamKeyed && document.Version >= context.StreamVersion.Value)
        {
            return;
        }

        // CR-31: a document fed by many streams has no stream version to compare, so it records the global position of
        // the last event applied; an event at or below it was applied already (a repartition, a replay) and is skipped.
        if (document.Value is not null && !streamKeyed && document.Position is { } applied && context.Position <= applied)
        {
            return;
        }

        object? next = projection.Apply(@event, document.Value, context);
        if (next is null)
        {
            if (document.Value is not null)
            {
                document.Value = null;
                document.Dirty = true;
            }

            return;
        }

        document.Version = streamKeyed ? context.StreamVersion.Value : document.Version + 1;
        document.Position = streamKeyed ? null : context.Position;
        document.Value = next;
        document.Dirty = true;
    }
}
