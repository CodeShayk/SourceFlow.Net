namespace SourceFlow;

/// <summary>Where and when an event happened; passed to projections and event handlers.</summary>
/// <param name="EventId">The event's message id (the natural idempotency key).</param>
/// <param name="StreamId">The source stream.</param>
/// <param name="StreamVersion">The event's version in its stream.</param>
/// <param name="Position">The event's global position.</param>
/// <param name="Timestamp">The decision time recorded with the event.</param>
/// <param name="CorrelationId">The conversation the event belongs to.</param>
/// <param name="CausationId">The message that caused the event.</param>
/// <param name="TenantId">The tenant, when multi-tenant.</param>
public readonly record struct EventContext(
    MessageId EventId,
    StreamId StreamId,
    StreamVersion StreamVersion,
    GlobalPosition Position,
    DateTimeOffset Timestamp,
    CorrelationId CorrelationId,
    MessageId? CausationId,
    string? TenantId)
{
    /// <summary>The aggregate id segment of the source stream: the text after the last dash (UT-ABS-13).</summary>
    public string AggregateKey => StreamId.AggregateKey;
}

/// <summary>A projection that maintains one document per key (doc 03 §6.3).</summary>
/// <typeparam name="TDocument">The read-model document.</typeparam>
/// <typeparam name="TKey">The document key.</typeparam>
public interface IDocumentProjection<TDocument, TKey>
    where TDocument : class
    where TKey : notnull;

/// <summary>The dispatch members the generator adds to a document projection. Not implemented by hand.</summary>
/// <typeparam name="TSelf">The projection type.</typeparam>
/// <typeparam name="TDocument">The read-model document.</typeparam>
/// <typeparam name="TKey">The document key.</typeparam>
public interface IDocumentProjectionRuntime<TSelf, TDocument, TKey> : IDocumentProjection<TDocument, TKey>
    where TSelf : class, IDocumentProjectionRuntime<TSelf, TDocument, TKey>
    where TDocument : class
    where TKey : notnull
{
    /// <summary>The projection name (also the document collection).</summary>
    static abstract string ProjectionName { get; }

    /// <summary>The projection version; a bump triggers a rebuild.</summary>
    static abstract int ProjectionVersion { get; }

    /// <summary>Every event type with a <c>When</c> method.</summary>
    static abstract IReadOnlyList<Type> HandledEvents { get; }

    /// <summary>Finds the document key for an event: <c>KeyOf</c>, a member of the key type, or the stream's aggregate key.</summary>
    static abstract bool TryGetKey(IEvent @event, in EventContext context, out TKey key);

    /// <summary>Formats a key as the stored document id.</summary>
    static abstract string FormatKey(TKey key);

    /// <summary>Applies an event to the current document. Returns the new document, or null to delete it.</summary>
    TDocument? Apply(IEvent @event, TDocument? current, in EventContext context);
}

/// <summary>Handles an event with a side effect, at least once (doc 03 §6.2). Make external effects idempotent with <see cref="EventContext.EventId"/>.</summary>
/// <typeparam name="TEvent">The event or integration event type.</typeparam>
public interface IEventHandler<in TEvent>
    where TEvent : class, IMessage
{
    /// <summary>Handles one event.</summary>
    ValueTask HandleAsync(TEvent @event, EventContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A projection that writes read models through a unit of work bound to the storage session, such as an EF Core
/// <c>DbContext</c> (doc 03 §6.3, doc 05 §10). It runs in the same transaction as the events it projects (inline) or as
/// the subscription checkpoint (async). Mark the class <c>[Projection("name")]</c> and declare
/// <c>When(TEvent e, TUnitOfWork unitOfWork, EventContext context, CancellationToken cancellationToken)</c> methods returning
/// <see cref="ValueTask"/> or <see cref="Task"/>; the generator emits the rest.
/// </summary>
/// <typeparam name="TUnitOfWork">The unit of work type, for example a <c>DbContext</c>.</typeparam>
public interface ISessionProjection<TUnitOfWork>
    where TUnitOfWork : class;

/// <summary>Generated half of <see cref="ISessionProjection{TUnitOfWork}"/>.</summary>
public interface ISessionProjectionRuntime<TSelf, TUnitOfWork> : ISessionProjection<TUnitOfWork>
    where TSelf : class, ISessionProjectionRuntime<TSelf, TUnitOfWork>
    where TUnitOfWork : class
{
    /// <summary>The projection name.</summary>
    static abstract string ProjectionName { get; }

    /// <summary>The projection version; a bump starts a new generation.</summary>
    static abstract int ProjectionVersion { get; }

    /// <summary>The event types the projection handles.</summary>
    static abstract IReadOnlyList<Type> HandledEvents { get; }

    /// <summary>Dispatches an event to its <c>When</c> method.</summary>
    ValueTask ApplyAsync(IEvent @event, TUnitOfWork unitOfWork, EventContext context, CancellationToken cancellationToken);
}
