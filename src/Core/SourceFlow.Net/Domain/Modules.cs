using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SourceFlow;

/// <summary>
/// A module: the generated registration of one assembly's messages, aggregates, process managers, projections and
/// handlers. Mark a <c>partial class</c> with <see cref="SourceFlowModuleAttribute"/>; the generator implements this.
/// </summary>
public interface ISourceFlowModule
{
    /// <summary>The module name (the class name).</summary>
    static abstract string ModuleName { get; }

    /// <summary>Registers everything the generator found at compile time.</summary>
    static abstract void Register(ISourceFlowModuleBuilder builder);
}

/// <summary>The registration surface generated modules call. All registrations are closed generics, so they are AOT-safe.</summary>
public interface ISourceFlowModuleBuilder
{
    /// <summary>Adds the module's source-generated JSON metadata (DC-1).</summary>
    void AddJsonTypeInfoResolver(IJsonTypeInfoResolver resolver);

    /// <summary>Adds a JSON converter, for example for a strongly typed id.</summary>
    void AddJsonConverter(JsonConverter converter);

    /// <summary>Registers a message type under its logical name and version (ADR-005).</summary>
    void AddMessage<TMessage>(string name, int version)
        where TMessage : class, IMessage;

    /// <summary>Registers an upcaster from a stored older version (doc 03 §2.1).</summary>
    void AddUpcaster<TFrom, TTo, TUpcaster>()
        where TFrom : class, IMessage
        where TTo : class, IMessage
        where TUpcaster : class, IUpcaster<TFrom, TTo>, new();

    /// <summary>Registers an aggregate.</summary>
    void AddAggregate<TAggregate, TId>()
        where TAggregate : class, IAggregateRuntime<TAggregate, TId>
        where TId : struct, IStronglyTypedId<TId>;

    /// <summary>Registers a command handler with dependencies (resolved from a per-message DI scope).</summary>
    void AddCommandHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TCommand, TAggregate>()
        where THandler : class, ICommandHandler<TCommand, TAggregate>
        where TCommand : class, ICommand<TAggregate>
        where TAggregate : class;

    /// <summary>Registers a process manager.</summary>
    void AddProcessManager<TProcess, TId>()
        where TProcess : class, IProcessManagerRuntime<TProcess, TId>
        where TId : struct, IStronglyTypedId<TId>;

    /// <summary>Registers a document projection.</summary>
    void AddProjection<TProjection, TDocument, TKey>()
        where TProjection : class, IDocumentProjectionRuntime<TProjection, TDocument, TKey>, new()
        where TDocument : class
        where TKey : notnull;

    /// <summary>Registers a session projection (writes through a unit of work bound to the storage session).</summary>
    void AddSessionProjection<TProjection, TUnitOfWork>()
        where TProjection : class, ISessionProjectionRuntime<TProjection, TUnitOfWork>, new()
        where TUnitOfWork : class;

    /// <summary>Registers an event handler (resolved from a per-message DI scope).</summary>
    void AddEventHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TEvent>()
        where THandler : class, IEventHandler<TEvent>
        where TEvent : class, IMessage;

    /// <summary>Registers a named domain query (doc 14 Q-B4).</summary>
    void AddDomainQuery<TQuery, TResult>()
        where TQuery : class, IDomainQueryRuntime<TQuery, TResult>
        where TResult : class;
}
