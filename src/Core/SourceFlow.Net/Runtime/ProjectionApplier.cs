using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// The projections of one storage session: inline projections in the command transaction, or async projections in a
/// subscription transaction. Session projections share one unit of work per type, which is completed (for example
/// <c>SaveChangesAsync</c>) by <see cref="CompleteAsync"/> before the session commits, so read models, events and
/// checkpoints commit or roll back together (doc 05 §10).
/// </summary>
internal sealed class ProjectionScope : IAsyncDisposable
{
    private readonly List<UnitOfWork> _units = [];

    public ProjectionScope(SourceFlowRuntime runtime, IStorageSession session, IServiceProvider services)
    {
        Runtime = runtime;
        Session = session;
        Services = services;
    }

    public SourceFlowRuntime Runtime { get; }

    public IStorageSession Session { get; }

    public IServiceProvider Services { get; }

    public ValueTask ApplyAsync(ProjectionDescriptor projection, IEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        return projection.ApplyAsync(this, @event, context, cancellationToken);
    }

    public async ValueTask<TUnitOfWork> UnitOfWorkAsync<TUnitOfWork>(Type projectionType, CancellationToken cancellationToken)
        where TUnitOfWork : class
    {
        foreach (UnitOfWork unit in _units)
        {
            if (unit.Instance is TUnitOfWork existing)
            {
                return existing;
            }
        }

        IProjectionUnitOfWork<TUnitOfWork> factory = Services.GetService<IProjectionUnitOfWork<TUnitOfWork>>()
            ?? throw new InvalidOperationException(
                $"Projection {projectionType.Name} writes through {typeof(TUnitOfWork).Name}, but no IProjectionUnitOfWork<{typeof(TUnitOfWork).Name}> is registered. "
                + $"Register it with the storage package, for example UseEntityFrameworkProjections<{typeof(TUnitOfWork).Name}>(…).");
        if (_units.Count == 0)
        {
            // The unit of work writes through the session's connection: send the queued append first (ES-R1).
            await Session.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        TUnitOfWork created = await factory.BeginAsync(Session, Services, cancellationToken).ConfigureAwait(false);
        _units.Add(new UnitOfWork(created, ct => factory.CompleteAsync(created, ct)));
        return created;
    }

    /// <summary>Flushes every unit of work; call before committing the session.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
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
    }

    private sealed record UnitOfWork(object Instance, Func<CancellationToken, ValueTask> Complete);
}

/// <summary>
/// Applies document projections inside a storage session. Document versions follow the source stream when the
/// document is keyed by the stream's aggregate id, otherwise they count writes (DC-2, doc 06 §5).
/// </summary>
internal static class ProjectionApplier
{
    public static async ValueTask ApplyDocumentAsync(ProjectionScope scope, DocumentProjectionDescriptor projection, IEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        if (!projection.Handles(@event.GetType()) || !projection.TryGetDocumentId(@event, context, out string? id))
        {
            return;
        }

        IStorageSession session = scope.Session;
        JsonTypeInfo typeInfo = scope.Runtime.Model.JsonTypeInfoFor(projection.DocumentType);
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
}
