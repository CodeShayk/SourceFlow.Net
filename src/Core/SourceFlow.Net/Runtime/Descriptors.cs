using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SourceFlow.Runtime;

/// <summary>
/// A registered aggregate, exposing the generated static dispatch through non-generic members so the engine needs no
/// reflection (ADR-004, HP-1). Created by generated module code through closed generics.
/// </summary>
public abstract class AggregateDescriptor
{
    /// <summary>The aggregate (stream type) name.</summary>
    public abstract string Name { get; }

    /// <summary>The state type.</summary>
    public abstract Type StateType { get; }

    /// <summary>The id type.</summary>
    public abstract Type IdType { get; }

    /// <summary>The snapshot interval; 0 disables snapshots.</summary>
    public abstract int SnapshotEvery { get; }

    /// <summary>The snapshot schema version.</summary>
    public abstract int SnapshotVersion { get; }

    /// <summary>Commands with a generated <c>Decide</c>.</summary>
    public abstract IReadOnlyList<Type> DecidedCommands { get; }

    /// <summary>Every command that targets this aggregate.</summary>
    public abstract IReadOnlyList<Type> TargetingCommands { get; }

    /// <summary>The initial state.</summary>
    public abstract object Initial { get; }

    /// <summary>Applies one event.</summary>
    public abstract object Evolve(object state, IEvent @event);

    /// <summary>Runs the generated <c>Decide</c>.</summary>
    public abstract Decision Decide(object state, ICommand command, in DecisionContext context);

    /// <summary>The target stream of a command.</summary>
    /// <exception cref="InvalidCommandException">The target id is the default value (GAP-20).</exception>
    public abstract StreamId StreamOf(ICommand command);
}

internal sealed class AggregateDescriptor<TAggregate, TId> : AggregateDescriptor
    where TAggregate : class, IAggregateRuntime<TAggregate, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    public override string Name => TAggregate.AggregateName;

    public override Type StateType => typeof(TAggregate);

    public override Type IdType => typeof(TId);

    public override int SnapshotEvery => TAggregate.SnapshotEvery;

    public override int SnapshotVersion => TAggregate.SnapshotVersion;

    public override IReadOnlyList<Type> DecidedCommands => TAggregate.DecidedCommands;

    public override IReadOnlyList<Type> TargetingCommands => TAggregate.TargetingCommands;

    public override object Initial => TAggregate.Initial;

    public override object Evolve(object state, IEvent @event)
    {
        return ((TAggregate)state).Evolve(@event);
    }

    public override Decision Decide(object state, ICommand command, in DecisionContext context)
    {
        return TAggregate.Decide((TAggregate)state, command, context);
    }

    public override StreamId StreamOf(ICommand command)
    {
        TId id = TAggregate.TargetOf(command);
        if (id.IsDefault)
        {
            throw new InvalidCommandException(command.GetType(), $"The target {typeof(TId).Name} of {command.GetType().Name} is the default value (GAP-20).");
        }

        // ADR-015 (CR-16): a malformed id is a malformed command, never a stream that can't be read back.
        if (!id.IsValid)
        {
            throw new InvalidCommandException(command.GetType(), $"The target {typeof(TId).Name} '{id}' of {command.GetType().Name} is not a valid id.");
        }

        return StreamId.For(TAggregate.AggregateName, id.ToStreamSegment());
    }
}

/// <summary>A command handler with dependencies (doc 03 §5.3).</summary>
public abstract class CommandHandlerDescriptor
{
    /// <summary>The handler type.</summary>
    public abstract Type HandlerType { get; }

    /// <summary>The handled command type.</summary>
    public abstract Type CommandType { get; }

    /// <summary>The target aggregate type.</summary>
    public abstract Type AggregateType { get; }

    /// <summary>Registers the handler as a scoped service.</summary>
    public abstract void RegisterServices(IServiceCollection services);

    /// <summary>Resolves the handler from <paramref name="scope"/> and decides.</summary>
    public abstract ValueTask<Decision> HandleAsync(IServiceProvider scope, ICommand command, object state, DecisionContext context, CancellationToken cancellationToken);
}

internal sealed class CommandHandlerDescriptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TCommand, TAggregate> : CommandHandlerDescriptor
    where THandler : class, ICommandHandler<TCommand, TAggregate>
    where TCommand : class, ICommand<TAggregate>
    where TAggregate : class
{
    public override Type HandlerType => typeof(THandler);

    public override Type CommandType => typeof(TCommand);

    public override Type AggregateType => typeof(TAggregate);

    public override void RegisterServices(IServiceCollection services)
    {
        services.TryAddScoped<THandler>();
    }

    public override ValueTask<Decision> HandleAsync(IServiceProvider scope, ICommand command, object state, DecisionContext context, CancellationToken cancellationToken)
    {
        return scope.GetRequiredService<THandler>().HandleAsync((TCommand)command, (TAggregate)state, context, cancellationToken);
    }
}

/// <summary>A registered process manager (ADR-020).</summary>
public abstract class ProcessManagerDescriptor
{
    /// <summary>The process type name.</summary>
    public abstract string Name { get; }

    /// <summary>The state type.</summary>
    public abstract Type StateType { get; }

    /// <summary>Every message type with a <c>React</c>.</summary>
    public abstract IReadOnlyList<Type> HandledMessages { get; }

    /// <summary>The initial state.</summary>
    public abstract object Initial { get; }

    /// <summary>True for a generated DSL workflow (doc 15 §5.5): its state is a <see cref="Workflows.IWorkflowInstance"/>.</summary>
    public bool IsWorkflow => typeof(Workflows.IWorkflowInstance).IsAssignableFrom(StateType);

    /// <summary>
    /// Correlates a message to an instance id (its stream segment form): <c>CorrelateBy</c> first, then the envelope
    /// correlation id when it parses as the id type.
    /// </summary>
    public abstract bool TryCorrelate(IMessage message, CorrelationId envelopeCorrelation, [NotNullWhen(true)] out string? instanceId);

    /// <summary>Runs the generated <c>React</c>.</summary>
    public abstract Reaction React(object state, IMessage message, string instanceId, DateTimeOffset now, MessageId messageId, bool isNew, System.Text.Json.JsonSerializerOptions? serializer = null, MessageId? causationId = null);
}

internal sealed class ProcessManagerDescriptor<TProcess, TId> : ProcessManagerDescriptor
    where TProcess : class, IProcessManagerRuntime<TProcess, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    public override string Name => TProcess.ProcessName;

    public override Type StateType => typeof(TProcess);

    public override IReadOnlyList<Type> HandledMessages => TProcess.HandledMessages;

    public override object Initial => TProcess.Initial;

    public override bool TryCorrelate(IMessage message, CorrelationId envelopeCorrelation, [NotNullWhen(true)] out string? instanceId)
    {
        if (TProcess.TryCorrelate(message, out TId id) && !id.IsDefault)
        {
            instanceId = id.ToStreamSegment();
            return true;
        }

        if (!envelopeCorrelation.IsNone && TId.TryParse(envelopeCorrelation.Value, out TId fromEnvelope) && !fromEnvelope.IsDefault)
        {
            instanceId = fromEnvelope.ToStreamSegment();
            return true;
        }

        instanceId = null;
        return false;
    }

    public override Reaction React(object state, IMessage message, string instanceId, DateTimeOffset now, MessageId messageId, bool isNew, System.Text.Json.JsonSerializerOptions? serializer = null, MessageId? causationId = null)
    {
        TId id = TId.Parse(instanceId);
        return ((TProcess)state).React(message, new ReactionContext<TId>(id, now, messageId, isNew) { Serializer = serializer, CausationId = causationId });
    }
}

/// <summary>
/// A registered projection (ADR-016). It runs inline in the command transaction or async in a subscription transaction,
/// and is either a <see cref="DocumentProjectionDescriptor"/> or a session projection writing through a unit of work.
/// </summary>
public abstract class ProjectionDescriptor
{
    private protected ProjectionDescriptor()
    {
    }

    /// <summary>The projection name.</summary>
    public abstract string Name { get; }

    /// <summary>The projection class.</summary>
    public abstract Type ProjectionType { get; }

    /// <summary>The projection version.</summary>
    public abstract int Version { get; }

    /// <summary>The generation id <c>{name}@{version}</c>: the document collection and the checkpoint name (doc 06 §6).</summary>
    public string Collection => $"{Name}@{Version}";

    /// <summary>Every event type with a <c>When</c>.</summary>
    public abstract IReadOnlyList<Type> HandledEvents { get; }

    /// <summary>True when the projection handles <paramref name="eventType"/>.</summary>
    public bool Handles(Type eventType)
    {
        foreach (Type handled in HandledEvents)
        {
            if (handled == eventType)
            {
                return true;
            }
        }

        return false;
    }

    internal abstract ValueTask ApplyAsync(ProjectionScope scope, IEvent @event, EventContext context, CancellationToken cancellationToken);

    // Describes a missing service the projection needs at run time, or null when it can run.
    internal virtual string? MissingDependency(IServiceProvider services)
    {
        return null;
    }

    // The store that keeps a session projection's generations apart (blue/green rebuilds), or null.
    internal virtual Storage.IProjectionGenerationStore? GenerationStore(IServiceProvider services)
    {
        return null;
    }
}

/// <summary>A registered document projection: JSON documents in the store's document collection (doc 05 §10).</summary>
public abstract class DocumentProjectionDescriptor : ProjectionDescriptor
{
    private protected DocumentProjectionDescriptor()
    {
    }

    /// <summary>The document type.</summary>
    public abstract Type DocumentType { get; }

    /// <summary>Finds the stored document id of an event.</summary>
    public abstract bool TryGetDocumentId(IEvent @event, in EventContext context, [NotNullWhen(true)] out string? documentId);

    /// <summary>Applies an event; returns the new document or null to delete it.</summary>
    public abstract object? Apply(IEvent @event, object? current, in EventContext context);

    internal override ValueTask ApplyAsync(ProjectionScope scope, IEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        return ProjectionApplier.ApplyDocumentAsync(scope, this, @event, context, cancellationToken);
    }
}

internal sealed class ProjectionDescriptor<TProjection, TDocument, TKey> : DocumentProjectionDescriptor
    where TProjection : class, IDocumentProjectionRuntime<TProjection, TDocument, TKey>, new()
    where TDocument : class
    where TKey : notnull
{
    private readonly TProjection _projection = new();

    public override string Name => TProjection.ProjectionName;

    public override Type ProjectionType => typeof(TProjection);

    public override int Version => TProjection.ProjectionVersion;

    public override Type DocumentType => typeof(TDocument);

    public override IReadOnlyList<Type> HandledEvents => TProjection.HandledEvents;

    public override bool TryGetDocumentId(IEvent @event, in EventContext context, [NotNullWhen(true)] out string? documentId)
    {
        if (TProjection.TryGetKey(@event, context, out TKey key))
        {
            documentId = TProjection.FormatKey(key);
            return documentId.Length > 0;
        }

        documentId = null;
        return false;
    }

    public override object? Apply(IEvent @event, object? current, in EventContext context)
    {
        return _projection.Apply(@event, (TDocument?)current, context);
    }
}

/// <summary>
/// A session projection (<see cref="ISessionProjection{TUnitOfWork}"/>): writes through a unit of work bound to the
/// storage session, created once per session by the registered <see cref="Storage.IProjectionUnitOfWork{TUnitOfWork}"/>.
/// </summary>
internal sealed class SessionProjectionDescriptor<TProjection, TUnitOfWork> : ProjectionDescriptor
    where TProjection : class, ISessionProjectionRuntime<TProjection, TUnitOfWork>, new()
    where TUnitOfWork : class
{
    private readonly TProjection _projection = new();

    public override string Name => TProjection.ProjectionName;

    public override Type ProjectionType => typeof(TProjection);

    public override int Version => TProjection.ProjectionVersion;

    public override IReadOnlyList<Type> HandledEvents => TProjection.HandledEvents;

    internal override async ValueTask ApplyAsync(ProjectionScope scope, IEvent @event, EventContext context, CancellationToken cancellationToken)
    {
        if (!Handles(@event.GetType()))
        {
            return;
        }

        TUnitOfWork unitOfWork = await scope.UnitOfWorkAsync<TUnitOfWork>(this, cancellationToken).ConfigureAwait(false);
        await _projection.ApplyAsync(@event, unitOfWork, context, cancellationToken).ConfigureAwait(false);
    }

    internal override Storage.IProjectionGenerationStore? GenerationStore(IServiceProvider services)
    {
        return services.GetService<Storage.IProjectionUnitOfWork<TUnitOfWork>>() is Storage.IProjectionGenerationStore { SeparatesGenerations: true } store ? store : null;
    }

    internal override string? MissingDependency(IServiceProvider services)
    {
        return services.GetService<Storage.IProjectionUnitOfWork<TUnitOfWork>>() is null
            ? $"Projection {ProjectionType.Name} writes through {typeof(TUnitOfWork).Name}, but no IProjectionUnitOfWork<{typeof(TUnitOfWork).Name}> is registered (for EF Core: UseEntityFrameworkProjections<{typeof(TUnitOfWork).Name}>(…))."
            : null;
    }
}

/// <summary>A registered event handler (at-least-once side effects, doc 03 §6.2).</summary>
public abstract class EventHandlerDescriptor
{
    /// <summary>The handler type.</summary>
    public abstract Type HandlerType { get; }

    /// <summary>The handled message type.</summary>
    public abstract Type EventType { get; }

    /// <summary>The consumer name used for checkpoints and the inbox.</summary>
    public string Name => HandlerType.Name;

    /// <summary>Registers the handler as a scoped service.</summary>
    public abstract void RegisterServices(IServiceCollection services);

    /// <summary>Resolves the handler from <paramref name="scope"/> and handles the message.</summary>
    public abstract ValueTask HandleAsync(IServiceProvider scope, IMessage message, EventContext context, CancellationToken cancellationToken);
}

internal sealed class EventHandlerDescriptor<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TEvent> : EventHandlerDescriptor
    where THandler : class, IEventHandler<TEvent>
    where TEvent : class, IMessage
{
    public override Type HandlerType => typeof(THandler);

    public override Type EventType => typeof(TEvent);

    public override void RegisterServices(IServiceCollection services)
    {
        services.TryAddScoped<THandler>();
    }

    public override ValueTask HandleAsync(IServiceProvider scope, IMessage message, EventContext context, CancellationToken cancellationToken)
    {
        return scope.GetRequiredService<THandler>().HandleAsync((TEvent)message, context, cancellationToken);
    }
}

/// <summary>A command is structurally invalid (for example a default target id); it is rejected, never retried.</summary>
public sealed class InvalidCommandException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    public InvalidCommandException(Type commandType, string message)
        : base(message)
    {
        CommandType = commandType;
    }

    /// <summary>The command type.</summary>
    public Type CommandType { get; }
}
