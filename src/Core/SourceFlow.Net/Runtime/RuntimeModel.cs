using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;

namespace SourceFlow.Runtime;

/// <summary>
/// Collects generated module registrations (<see cref="ISourceFlowModuleBuilder"/>) and builds the validated,
/// frozen <see cref="RuntimeModel"/>. No assembly scanning (GAP-16).
/// </summary>
public sealed class SourceFlowModelBuilder : ISourceFlowModuleBuilder
{
    private readonly List<IJsonTypeInfoResolver> _resolvers = [];
    private readonly List<JsonConverter> _converters = [];
    private readonly HashSet<Type> _converterTypes = [];
    private readonly MessageTypeRegistryBuilder _messages = new();
    private readonly Dictionary<Type, AggregateDescriptor> _aggregates = [];
    private readonly Dictionary<Type, CommandHandlerDescriptor> _commandHandlers = [];
    private readonly Dictionary<Type, ProcessManagerDescriptor> _processManagers = [];
    private readonly Dictionary<Type, ProjectionDescriptor> _projections = [];
    private readonly Dictionary<(Type, Type), EventHandlerDescriptor> _eventHandlers = [];
    private readonly List<string> _modules = [];

    /// <summary>The names of the registered modules.</summary>
    public IReadOnlyList<string> Modules => _modules;

    /// <summary>Registers a generated module once.</summary>
    public SourceFlowModelBuilder AddModule<TModule>()
        where TModule : ISourceFlowModule
    {
        if (!_modules.Contains(TModule.ModuleName))
        {
            _modules.Add(TModule.ModuleName);
            TModule.Register(this);
        }

        return this;
    }

    /// <inheritdoc />
    public void AddJsonTypeInfoResolver(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (!_resolvers.Contains(resolver))
        {
            _resolvers.Add(resolver);
        }
    }

    /// <inheritdoc />
    public void AddJsonConverter(JsonConverter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        if (_converterTypes.Add(converter.GetType()))
        {
            _converters.Add(converter);
        }
    }

    /// <inheritdoc />
    public void AddMessage<TMessage>(string name, int version)
        where TMessage : class, IMessage
    {
        _messages.Add(typeof(TMessage), name, version);
    }

    /// <inheritdoc />
    public void AddUpcaster<TFrom, TTo, TUpcaster>()
        where TFrom : class, IMessage
        where TTo : class, IMessage
        where TUpcaster : class, IUpcaster<TFrom, TTo>, new()
    {
        var upcaster = new TUpcaster();
        _messages.AddUpcaster(typeof(TFrom), typeof(TTo), old => upcaster.Upcast((TFrom)old));
    }

    /// <inheritdoc />
    public void AddAggregate<TAggregate, TId>()
        where TAggregate : class, IAggregateRuntime<TAggregate, TId>
        where TId : struct, IStronglyTypedId<TId>
    {
        _aggregates.TryAdd(typeof(TAggregate), new AggregateDescriptor<TAggregate, TId>());
    }

    /// <inheritdoc />
    public void AddCommandHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TCommand, TAggregate>()
        where THandler : class, ICommandHandler<TCommand, TAggregate>
        where TCommand : class, ICommand<TAggregate>
        where TAggregate : class
    {
        if (_commandHandlers.TryGetValue(typeof(TCommand), out CommandHandlerDescriptor? existing) && existing.HandlerType != typeof(THandler))
        {
            throw new InvalidOperationException($"Command {typeof(TCommand).Name} has two handlers: {existing.HandlerType.Name} and {typeof(THandler).Name}.");
        }

        _commandHandlers[typeof(TCommand)] = new CommandHandlerDescriptor<THandler, TCommand, TAggregate>();
    }

    /// <inheritdoc />
    public void AddProcessManager<TProcess, TId>()
        where TProcess : class, IProcessManagerRuntime<TProcess, TId>
        where TId : struct, IStronglyTypedId<TId>
    {
        _processManagers.TryAdd(typeof(TProcess), new ProcessManagerDescriptor<TProcess, TId>());
    }

    /// <inheritdoc />
    public void AddProjection<TProjection, TDocument, TKey>()
        where TProjection : class, IDocumentProjectionRuntime<TProjection, TDocument, TKey>, new()
        where TDocument : class
        where TKey : notnull
    {
        _projections.TryAdd(typeof(TProjection), new ProjectionDescriptor<TProjection, TDocument, TKey>());
    }

    /// <inheritdoc />
    public void AddSessionProjection<TProjection, TUnitOfWork>()
        where TProjection : class, ISessionProjectionRuntime<TProjection, TUnitOfWork>, new()
        where TUnitOfWork : class
    {
        _projections.TryAdd(typeof(TProjection), new SessionProjectionDescriptor<TProjection, TUnitOfWork>());
    }

    /// <inheritdoc />
    public void AddEventHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TEvent>()
        where THandler : class, IEventHandler<TEvent>
        where TEvent : class, IMessage
    {
        _eventHandlers.TryAdd((typeof(THandler), typeof(TEvent)), new EventHandlerDescriptor<THandler, TEvent>());
    }

    /// <summary>Registers the handlers' DI services.</summary>
    public void RegisterServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (CommandHandlerDescriptor handler in _commandHandlers.Values)
        {
            handler.RegisterServices(services);
        }

        foreach (EventHandlerDescriptor handler in _eventHandlers.Values)
        {
            handler.RegisterServices(services);
        }
    }

    /// <summary>Validates every registration and freezes the model (fail fast at startup).</summary>
    public RuntimeModel Build()
    {
        JsonSerializerOptions options = SystemTextJsonMessageSerializer.CreateOptions(_resolvers, _converters);
        MessageTypeRegistry registry = _messages.Build(options);

        var owners = new Dictionary<Type, AggregateDescriptor>();
        foreach (AggregateDescriptor aggregate in _aggregates.Values)
        {
            foreach (Type command in aggregate.DecidedCommands)
            {
                if (owners.TryGetValue(command, out AggregateDescriptor? other))
                {
                    throw new InvalidOperationException($"Command {command.Name} is decided by two aggregates: {other.Name} and {aggregate.Name}.");
                }

                owners[command] = aggregate;
            }

            ResolveDocument(options, aggregate.StateType, required: aggregate.SnapshotEvery > 0);
        }

        foreach (CommandHandlerDescriptor handler in _commandHandlers.Values)
        {
            if (owners.ContainsKey(handler.CommandType))
            {
                throw new InvalidOperationException(
                    $"Command {handler.CommandType.Name} has both a Decide method and the handler {handler.HandlerType.Name}; keep exactly one.");
            }

            AggregateDescriptor aggregate = _aggregates.Values.FirstOrDefault(a => a.StateType == handler.AggregateType)
                ?? throw new InvalidOperationException($"Handler {handler.HandlerType.Name} targets {handler.AggregateType.Name}, which is not a registered aggregate.");
            owners[handler.CommandType] = aggregate;
        }

        foreach (Type command in owners.Keys)
        {
            registry.Get(command); // every owned command must be a registered message (SF0001)
        }

        foreach (ProcessManagerDescriptor process in _processManagers.Values)
        {
            ResolveDocument(options, process.StateType, required: true);
        }

        foreach (DocumentProjectionDescriptor projection in _projections.Values.OfType<DocumentProjectionDescriptor>())
        {
            ResolveDocument(options, projection.DocumentType, required: true);
        }

        string? duplicate = _projections.Values.GroupBy(p => p.Collection, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Two projections share the name and version '{duplicate}'.");
        }

        return new RuntimeModel(
            registry,
            [.. _modules],
            _aggregates.Values.ToFrozenDictionary(a => a.StateType),
            owners.ToFrozenDictionary(),
            _commandHandlers.ToFrozenDictionary(),
            [.. _processManagers.Values],
            [.. _projections.Values],
            [.. _eventHandlers.Values]);
    }

    private static void ResolveDocument(JsonSerializerOptions options, Type type, bool required)
    {
        JsonTypeInfo? info = null;
        try
        {
            info = options.TypeInfoResolver?.GetTypeInfo(type, options);
        }
        catch (NotSupportedException)
        {
            info = null;
        }

        if (info is null && required)
        {
            throw new InvalidOperationException(
                $"No JSON metadata for '{type.FullName}'. Add [JsonSerializable(typeof({type.Name}))] to the module's JsonSerializerContext (SF0010, DC-1).");
        }
    }
}

/// <summary>The frozen, validated runtime model: types, aggregates, owners and subscribers.</summary>
public sealed class RuntimeModel
{
    internal RuntimeModel(
        MessageTypeRegistry registry,
        IReadOnlyList<string> modules,
        FrozenDictionary<Type, AggregateDescriptor> aggregates,
        FrozenDictionary<Type, AggregateDescriptor> commandOwners,
        FrozenDictionary<Type, CommandHandlerDescriptor> commandHandlers,
        IReadOnlyList<ProcessManagerDescriptor> processManagers,
        IReadOnlyList<ProjectionDescriptor> projections,
        IReadOnlyList<EventHandlerDescriptor> eventHandlers)
    {
        Registry = registry;
        Modules = modules;
        Aggregates = aggregates;
        CommandOwners = commandOwners;
        CommandHandlers = commandHandlers;
        ProcessManagers = processManagers;
        Projections = projections;
        EventHandlers = eventHandlers;
    }

    /// <summary>The message type allow-list.</summary>
    public MessageTypeRegistry Registry { get; }

    /// <summary>The registered module names.</summary>
    public IReadOnlyList<string> Modules { get; }

    /// <summary>Aggregates by state type.</summary>
    public FrozenDictionary<Type, AggregateDescriptor> Aggregates { get; }

    /// <summary>The owning aggregate of every locally owned command (RT-R1).</summary>
    public FrozenDictionary<Type, AggregateDescriptor> CommandOwners { get; }

    /// <summary>Command handlers with dependencies, by command type.</summary>
    public FrozenDictionary<Type, CommandHandlerDescriptor> CommandHandlers { get; }

    /// <summary>The process managers.</summary>
    public IReadOnlyList<ProcessManagerDescriptor> ProcessManagers { get; }

    /// <summary>The document projections.</summary>
    public IReadOnlyList<ProjectionDescriptor> Projections { get; }

    /// <summary>The event handlers.</summary>
    public IReadOnlyList<EventHandlerDescriptor> EventHandlers { get; }

    /// <summary>The JSON metadata for a document or state type.</summary>
    public JsonTypeInfo JsonTypeInfoFor(Type type)
    {
        return Registry.Options.GetTypeInfo(type);
    }
}
