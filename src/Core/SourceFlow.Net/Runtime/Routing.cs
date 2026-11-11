using System.Collections.Frozen;
using SourceFlow.Messaging;

namespace SourceFlow.Runtime;

/// <summary>
/// Declares routing (doc 04 §3): remote command owners, publications through the outbox, and subscriptions.
/// Names are logical; transports map them to physical resources (RT-R5).
/// </summary>
public sealed class RoutingBuilder
{
    private readonly Dictionary<Type, CommandRoute> _commands = [];
    private readonly Dictionary<Type, List<PublicationRoute>> _publications = [];
    private readonly Dictionary<string, SubscriptionRoute> _subscriptions = new(StringComparer.Ordinal);
    private string? _endpoint;

    /// <summary>Sets the service's input endpoint (default: the service name).</summary>
    public RoutingBuilder Endpoint(string name)
    {
        _endpoint = ValidateName(name, nameof(name));
        return this;
    }

    /// <summary>Routes a command type to a remote owner.</summary>
    public CommandRouteBuilder Command<TCommand>()
        where TCommand : class, ICommand
    {
        return new CommandRouteBuilder(this, typeof(TCommand));
    }

    /// <summary>Publishes a domain event through the outbox (RT-R3).</summary>
    public PublicationBuilder<TEvent> Publish<TEvent>()
        where TEvent : class, IEvent
    {
        return new PublicationBuilder<TEvent>(this);
    }

    /// <summary>Subscribes the service to a topic; each service gets its own subscription (RT-R4).</summary>
    public RoutingBuilder Subscribe(string topic, Action<SubscriptionBuilder>? configure = null)
    {
        string name = ValidateName(topic, nameof(topic));
        var builder = new SubscriptionBuilder();
        configure?.Invoke(builder);
        _subscriptions[name] = new SubscriptionRoute(name, [.. builder.Types]) { Where = builder.Filter };
        return this;
    }

    internal void AddCommand(Type commandType, CommandRoute route)
    {
        if (_commands.TryGetValue(commandType, out CommandRoute? existing) && existing.Endpoint != route.Endpoint)
        {
            throw new InvalidOperationException($"Command {commandType.Name} is routed to both '{existing.Endpoint}' and '{route.Endpoint}' (ADR-007).");
        }

        _commands[commandType] = route;
    }

    internal void AddPublication(Type eventType, PublicationRoute route)
    {
        if (!_publications.TryGetValue(eventType, out List<PublicationRoute>? routes))
        {
            _publications[eventType] = routes = [];
        }

        routes.Add(route);
    }

    internal void ReplacePublication(Type eventType, PublicationRoute previous, PublicationRoute replacement)
    {
        List<PublicationRoute> routes = _publications[eventType];
        routes[routes.IndexOf(previous)] = replacement;
    }

    /// <summary>Validates the routes against the runtime model and freezes them (RT-R1, RT-R6).</summary>
    public RoutingTable Build(string serviceName, RuntimeModel model)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
        ArgumentNullException.ThrowIfNull(model);
        foreach (Type command in _commands.Keys)
        {
            if (model.CommandOwners.TryGetValue(command, out AggregateDescriptor? owner))
            {
                throw new InvalidOperationException(
                    $"Command {command.Name} is decided locally by {owner.Name} and also routed to '{_commands[command].Endpoint}'. " +
                    "A command is owned or routed, never both (RT-R1, ADR-007, SF0014).");
            }

            model.Registry.Get(command);
        }

        foreach (AggregateDescriptor aggregate in model.Aggregates.Values)
        {
            foreach (Type command in aggregate.TargetingCommands)
            {
                if (!model.CommandOwners.ContainsKey(command) && !_commands.ContainsKey(command))
                {
                    throw new InvalidOperationException(
                        $"Command {command.Name} targets aggregate {aggregate.Name} but has no Decide method, no ICommandHandler and no route (RT-R1, SF0006).");
                }
            }
        }

        foreach ((Type eventType, List<PublicationRoute> routes) in _publications)
        {
            model.Registry.Get(eventType);
            foreach (PublicationRoute route in routes)
            {
                model.Registry.Get(route.ContractType);
            }
        }

        return new RoutingTable(
            _endpoint ?? serviceName,
            _commands.ToFrozenDictionary(),
            _publications.ToFrozenDictionary(p => p.Key, p => (IReadOnlyList<PublicationRoute>)[.. p.Value]),
            [.. _subscriptions.Values.Select(s => s with { Filter = EffectiveFilter(s, model) })]);
    }

    // Only<T>() becomes `type IN (…)` on the CloudEvents types; Where(…) is ANDed with it (doc 07 §7).
    private static string? EffectiveFilter(SubscriptionRoute subscription, RuntimeModel model)
    {
        string? types = subscription.OnlyTypes.Count == 0
            ? null
            : Transport.CloudEventFilter.ForTypes(subscription.OnlyTypes.Select(t =>
            {
                MessageTypeInfo info = model.Registry.Get(t);
                return CloudEvents.FormatType(info.Name, info.Version);
            })).Expression;
        return (types, subscription.Where) switch
        {
            (null, null) => null,
            (string t, null) => t,
            (null, string w) => w,
            (string t, string w) => $"({t}) AND ({w})",
        };
    }

    internal static string ValidateName(string name, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameter);
        if (name.Contains("://", StringComparison.Ordinal) || name.StartsWith("arn:", StringComparison.OrdinalIgnoreCase) || name.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' looks like a URL or ARN. Use logical names; the transport maps them (RT-R5).", parameter);
        }

        return name;
    }
}

/// <summary>Configures one command route.</summary>
public sealed class CommandRouteBuilder
{
    private readonly RoutingBuilder _routing;
    private readonly Type _commandType;
    private string? _endpoint;

    internal CommandRouteBuilder(RoutingBuilder routing, Type commandType)
    {
        _routing = routing;
        _commandType = commandType;
    }

    /// <summary>The remote owner's endpoint.</summary>
    public CommandRouteBuilder To(string endpoint)
    {
        _endpoint = RoutingBuilder.ValidateName(endpoint, nameof(endpoint));
        _routing.AddCommand(_commandType, new CommandRoute(_endpoint, ReplyOnReject: false));
        return this;
    }

    /// <summary>Asks the owner to reply with a rejection to the correlation (doc 03 §6.1).</summary>
    public CommandRouteBuilder ReplyOnReject()
    {
        if (_endpoint is null)
        {
            throw new InvalidOperationException("Call To(endpoint) before ReplyOnReject().");
        }

        _routing.AddCommand(_commandType, new CommandRoute(_endpoint, ReplyOnReject: true));
        return this;
    }
}

/// <summary>Configures a publication of <typeparamref name="TEvent"/>.</summary>
/// <typeparam name="TEvent">The domain event.</typeparam>
public sealed class PublicationBuilder<TEvent>
    where TEvent : class, IEvent
{
    private readonly RoutingBuilder _routing;

    internal PublicationBuilder(RoutingBuilder routing)
    {
        _routing = routing;
    }

    /// <summary>Maps the domain event to a public contract (runs inside the command transaction).</summary>
    public PublicationTarget<TEvent> As<TContract>(Func<TEvent, TContract> map)
        where TContract : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(map);
        return new PublicationTarget<TEvent>(_routing, typeof(TContract), e => map(e));
    }

    /// <summary>Publishes the domain event unchanged (SF0013 warns when it contains personal data).</summary>
    public PublicationTarget<TEvent> AsIs()
    {
        return new PublicationTarget<TEvent>(_routing, typeof(TEvent), e => e);
    }
}

/// <summary>The destination of a publication.</summary>
/// <typeparam name="TEvent">The domain event.</typeparam>
public sealed class PublicationTarget<TEvent>
    where TEvent : class, IEvent
{
    private readonly RoutingBuilder _routing;
    private readonly Type _contractType;
    private readonly Func<TEvent, IMessage> _map;
    private PublicationRoute? _route;

    internal PublicationTarget(RoutingBuilder routing, Type contractType, Func<TEvent, IMessage> map)
    {
        _routing = routing;
        _contractType = contractType;
        _map = map;
    }

    /// <summary>The topic to publish to.</summary>
    public PublicationTarget<TEvent> To(string topic)
    {
        _route = new PublicationRoute(RoutingBuilder.ValidateName(topic, nameof(topic)), _contractType, e => _map((TEvent)e), null);
        _routing.AddPublication(typeof(TEvent), _route);
        return this;
    }

    /// <summary>The ordering key (default: the stream id).</summary>
    public PublicationTarget<TEvent> OrderedBy(Func<TEvent, object> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_route is null)
        {
            throw new InvalidOperationException("Call To(topic) before OrderedBy(key).");
        }

        PublicationRoute ordered = _route with { PartitionKey = e => key((TEvent)e)?.ToString() };
        _routing.ReplacePublication(typeof(TEvent), _route, ordered);
        _route = ordered;
        return this;
    }
}

/// <summary>Configures a subscription.</summary>
public sealed class SubscriptionBuilder
{
    internal List<Type> Types { get; } = [];

    internal string? Filter { get; private set; }

    /// <summary>
    /// Filters the subscription with a CloudEvents SQL expression over the event's attributes, for example
    /// <c>type LIKE 'payments.%' AND sftenant = 'eu'</c> (doc 07 §7). Translated to the broker's native filter where
    /// it can be, otherwise evaluated on receipt, with identical results.
    /// </summary>
    /// <exception cref="FormatException">The expression is not valid CESQL.</exception>
    public SubscriptionBuilder Where(string filter)
    {
        Filter = Transport.CloudEventFilter.Parse(filter).Expression;
        return this;
    }

    /// <summary>Restricts the subscription to <typeparamref name="T"/> (broker-side filter where supported).</summary>
    public SubscriptionBuilder Only<T>()
        where T : class, IMessage
    {
        Types.Add(typeof(T));
        return this;
    }
}

/// <summary>A remote command owner.</summary>
public sealed record CommandRoute(string Endpoint, bool ReplyOnReject);

/// <summary>A publication of a domain event.</summary>
/// <param name="Destination">The topic.</param>
/// <param name="ContractType">The published message type.</param>
/// <param name="Map">Maps the domain event to the published message.</param>
/// <param name="PartitionKey">The ordering key; null means the stream id.</param>
public sealed record PublicationRoute(string Destination, Type ContractType, Func<IEvent, IMessage> Map, Func<IEvent, string?>? PartitionKey);

/// <summary>A topic subscription.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="OnlyTypes">The accepted message types (<c>Only&lt;T&gt;()</c>); empty accepts all.</param>
public sealed record SubscriptionRoute(string Topic, IReadOnlyList<Type> OnlyTypes)
{
    /// <summary>The <c>Where(…)</c> CESQL expression, if any.</summary>
    public string? Where { get; init; }

    /// <summary>The effective CESQL filter (the types and the <c>Where</c> expression), or null for every event.</summary>
    public string? Filter { get; init; }
}

/// <summary>The frozen routing table.</summary>
public sealed class RoutingTable
{
    internal RoutingTable(
        string endpoint,
        FrozenDictionary<Type, CommandRoute> commands,
        FrozenDictionary<Type, IReadOnlyList<PublicationRoute>> publications,
        IReadOnlyList<SubscriptionRoute> subscriptions)
    {
        Endpoint = endpoint;
        Commands = commands;
        Publications = publications;
        Subscriptions = subscriptions;
    }

    /// <summary>The service's input endpoint.</summary>
    public string Endpoint { get; }

    /// <summary>Remote command routes.</summary>
    public FrozenDictionary<Type, CommandRoute> Commands { get; }

    /// <summary>Publications by domain event type.</summary>
    public FrozenDictionary<Type, IReadOnlyList<PublicationRoute>> Publications { get; }

    /// <summary>Topic subscriptions.</summary>
    public IReadOnlyList<SubscriptionRoute> Subscriptions { get; }

    /// <summary>Every topic the service publishes to.</summary>
    public IEnumerable<string> PublishedTopics => Publications.Values.SelectMany(r => r).Select(r => r.Destination).Distinct(StringComparer.Ordinal);
}
