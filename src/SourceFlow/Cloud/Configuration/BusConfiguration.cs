using System;
using System.Collections.Generic;
using System.Linq;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Configuration;

/// <summary>
/// Code-first bus configuration. Stores short queue/topic <em>names</em> at build time;
/// full SQS queue URLs and SNS topic ARNs are resolved and injected by
/// <see cref="AwsBusBootstrapper"/> during application startup before any message is sent.
/// </summary>
public sealed class BusConfiguration : ICommandRoutingConfiguration, IEventRoutingConfiguration, IBusBootstrapConfiguration
{
    // ── Short names set once at builder time ────────────────────────────────

    private readonly Dictionary<Type, string> _commandTypeToQueueName;
    private readonly Dictionary<Type, string> _eventTypeToTopicName;
    private readonly List<string> _commandListeningQueueNames;
    private readonly List<string> _subscribedTopicNames;

    // ── Resolved full paths – populated by the bootstrapper ─────────────────

    private Dictionary<Type, string>? _resolvedCommandRoutes;        // type → full queue URL
    private Dictionary<Type, string>? _resolvedEventRoutes;          // type → full topic ARN
    private List<string>? _resolvedCommandListeningUrls;             // full queue URLs
    private List<string>? _resolvedSubscribedTopicArns;              // full topic ARNs
    private List<string>? _resolvedEventListeningUrls;               // full queue URLs for event listening

    internal BusConfiguration(
        Dictionary<Type, string> commandTypeToQueueName,
        Dictionary<Type, string> eventTypeToTopicName,
        List<string> commandListeningQueueNames,
        List<string> subscribedTopicNames)
    {
        _commandTypeToQueueName = commandTypeToQueueName;
        _eventTypeToTopicName = eventTypeToTopicName;
        _commandListeningQueueNames = commandListeningQueueNames;
        _subscribedTopicNames = subscribedTopicNames;
    }

    // ── IBusBootstrapConfiguration ───────────────────────────────────────────

    IReadOnlyDictionary<Type, string> IBusBootstrapConfiguration.CommandTypeToQueueName => _commandTypeToQueueName;
    IReadOnlyDictionary<Type, string> IBusBootstrapConfiguration.EventTypeToTopicName => _eventTypeToTopicName;
    IReadOnlyList<string> IBusBootstrapConfiguration.CommandListeningQueueNames => _commandListeningQueueNames;
    IReadOnlyList<string> IBusBootstrapConfiguration.SubscribedTopicNames => _subscribedTopicNames;

    void IBusBootstrapConfiguration.Resolve(
        Dictionary<Type, string> commandRoutes,
        Dictionary<Type, string> eventRoutes,
        List<string> commandListeningUrls,
        List<string> subscribedTopicArns,
        List<string> eventListeningUrls)
    {
        _resolvedCommandRoutes = commandRoutes;
        _resolvedEventRoutes = eventRoutes;
        _resolvedCommandListeningUrls = commandListeningUrls;
        _resolvedSubscribedTopicArns = subscribedTopicArns;
        _resolvedEventListeningUrls = eventListeningUrls;
    }

    private void EnsureResolved()
    {
        if (_resolvedCommandRoutes is null)
            throw new InvalidOperationException(
                "BusConfiguration has not been bootstrapped yet. " +
                "Ensure the bus bootstrapper (registered as IHostedService) completes " +
                "before dispatching commands or events.");
    }

    // ── ICommandRoutingConfiguration ─────────────────────────────────────────

    bool ICommandRoutingConfiguration.ShouldRoute<TCommand>()
    {
        EnsureResolved();
        return _resolvedCommandRoutes!.ContainsKey(typeof(TCommand));
    }

    string ICommandRoutingConfiguration.GetQueueName<TCommand>()
    {
        EnsureResolved();
        if (_resolvedCommandRoutes!.TryGetValue(typeof(TCommand), out var name))
            return name;

        throw new InvalidOperationException(
            $"No queue registered for command '{typeof(TCommand).Name}'. " +
            $"Use .Send.Command<{typeof(TCommand).Name}>(q => q.Queue(\"queue-name\")) in BusConfigurationBuilder.");
    }

    IEnumerable<string> ICommandRoutingConfiguration.GetListeningQueues()
    {
        EnsureResolved();
        return _resolvedCommandListeningUrls!;
    }

    // ── IEventRoutingConfiguration ───────────────────────────────────────────

    bool IEventRoutingConfiguration.ShouldRoute<TEvent>()
    {
        EnsureResolved();
        return _resolvedEventRoutes!.ContainsKey(typeof(TEvent));
    }

    string IEventRoutingConfiguration.GetTopicName<TEvent>()
    {
        EnsureResolved();
        if (_resolvedEventRoutes!.TryGetValue(typeof(TEvent), out var name))
            return name;

        throw new InvalidOperationException(
            $"No topic registered for event '{typeof(TEvent).Name}'. " +
            $"Use .Raise.Event<{typeof(TEvent).Name}>(t => t.Topic(\"topic-name\")) in BusConfigurationBuilder.");
    }

    IEnumerable<string> IEventRoutingConfiguration.GetListeningQueues()
    {
        EnsureResolved();
        return _resolvedEventListeningUrls!;
    }

    IEnumerable<string> IEventRoutingConfiguration.GetSubscribedTopics()
    {
        EnsureResolved();
        return _resolvedSubscribedTopicArns!;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  ROOT BUILDER
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Entry point for building a <see cref="BusConfiguration"/> using a fluent API.
/// Provide only short queue/topic <em>names</em>; full URLs and ARNs are resolved
/// automatically by <see cref="AwsBusBootstrapper"/> at startup (creating missing
/// resources in AWS when needed).
/// </summary>
/// <example>
/// <code>
/// services.UseSourceFlowAws(
///     options => { options.Region = RegionEndpoint.USEast1; },
///     bus => bus
///         .Send
///             .Command&lt;CreateOrderCommand&gt;(q =&gt; q.Queue("orders.fifo"))
///             .Command&lt;UpdateOrderCommand&gt;(q =&gt; q.Queue("orders.fifo"))
///             .Command&lt;AdjustInventoryCommand&gt;(q =&gt; q.Queue("inventory.fifo"))
///         .Raise.Event&lt;OrderCreatedEvent&gt;(t =&gt; t.Topic("order-events"))
///         .Raise.Event&lt;OrderUpdatedEvent&gt;(t =&gt; t.Topic("order-events"))
///         .Listen.To
///             .CommandQueue("orders.fifo")
///             .CommandQueue("inventory.fifo")
///         .Subscribe.To
///             .Topic("order-events")
///             .Topic("payment-events"));
/// </code>
/// </example>
public sealed class BusConfigurationBuilder
{
    internal Dictionary<Type, string> CommandRoutes { get; } = new();   // type → queue name
    internal Dictionary<Type, string> EventRoutes { get; } = new();     // type → topic name
    internal List<string> CommandListeningQueues { get; } = new();      // queue names
    internal List<string> SubscribedTopics { get; } = new();            // topic names

    /// <summary>Opens the <b>Send</b> section for mapping outbound commands to SQS queue names.</summary>
    public SendConfigurationBuilder Send => new(this);

    /// <summary>Opens the <b>Raise</b> section for mapping outbound events to SNS topic names.</summary>
    public RaiseConfigurationBuilder Raise => new(this);

    /// <summary>Opens the <b>Listen</b> section for declaring queue names this service polls for commands.</summary>
    public ListenConfigurationBuilder Listen => new(this);

    /// <summary>Opens the <b>Subscribe</b> section for declaring topic names this service subscribes to for events.</summary>
    public SubscribeConfigurationBuilder Subscribe => new(this);

    /// <summary>
    /// Builds the <see cref="BusConfiguration"/> containing short names.
    /// Full URLs/ARNs are resolved later by <see cref="AwsBusBootstrapper"/>.
    /// </summary>
    public BusConfiguration Build()
        => new(
            new Dictionary<Type, string>(CommandRoutes),
            new Dictionary<Type, string>(EventRoutes),
            new List<string>(CommandListeningQueues),
            new List<string>(SubscribedTopics));
}

// ════════════════════════════════════════════════════════════════════════════
//  SEND  ─  outbound command → SQS queue name
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Fluent context for registering outbound commands.
/// Chain <see cref="Command{TCommand}"/> calls, then transition to another section.
/// </summary>
public sealed class SendConfigurationBuilder
{
    private readonly BusConfigurationBuilder _root;

    internal SendConfigurationBuilder(BusConfigurationBuilder root) => _root = root;

    /// <summary>
    /// Maps <typeparamref name="TCommand"/> to the SQS queue name specified in <paramref name="configure"/>.
    /// </summary>
    public SendConfigurationBuilder Command<TCommand>(Action<CommandEndpointBuilder> configure)
        where TCommand : ICommand
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        var endpoint = new CommandEndpointBuilder();
        configure(endpoint);
        endpoint.Validate(typeof(TCommand));
        _root.CommandRoutes[typeof(TCommand)] = endpoint.QueueName!;
        return this;
    }

    /// <summary>Transitions to the <b>Raise</b> section.</summary>
    public RaiseConfigurationBuilder Raise => new(_root);

    /// <summary>Transitions to the <b>Listen</b> section.</summary>
    public ListenConfigurationBuilder Listen => new(_root);

    /// <summary>Transitions to the <b>Subscribe</b> section.</summary>
    public SubscribeConfigurationBuilder Subscribe => new(_root);
}

/// <summary>
/// Callback builder used inside <c>Command&lt;T&gt;</c> to specify the target SQS queue name.
/// </summary>
public sealed class CommandEndpointBuilder
{
    internal string? QueueName { get; private set; }

    /// <summary>
    /// Sets the short SQS queue name (e.g. <c>"orders.fifo"</c>).
    /// Do not provide a full URL — the bootstrapper resolves that automatically.
    /// </summary>
    public CommandEndpointBuilder Queue(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            throw new ArgumentException("Queue name cannot be null or whitespace.", nameof(queueName));

        if (queueName.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            queueName.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Provide only the queue name (e.g. \"orders.fifo\"), not a full URL. Got: \"{queueName}\".",
                nameof(queueName));

        QueueName = queueName;
        return this;
    }

    internal void Validate(Type commandType)
    {
        if (string.IsNullOrWhiteSpace(QueueName))
            throw new InvalidOperationException(
                $"No queue name provided for command '{commandType.Name}'. " +
                $"Call .Queue(\"queue-name\") inside the configure callback.");
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  RAISE  ─  outbound event → SNS topic name
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Fluent context for registering outbound events.
/// Re-accessing <see cref="Raise"/> returns the same context so consecutive
/// <c>.Raise.Event&lt;T&gt;(...)</c> calls read naturally.
/// </summary>
public sealed class RaiseConfigurationBuilder
{
    private readonly BusConfigurationBuilder _root;

    internal RaiseConfigurationBuilder(BusConfigurationBuilder root) => _root = root;

    /// <summary>Returns this context (self-reference for chaining repeated <c>.Raise.Event&lt;T&gt;</c> calls).</summary>
    public RaiseConfigurationBuilder Raise => this;

    /// <summary>
    /// Maps <typeparamref name="TEvent"/> to the SNS topic name specified in <paramref name="configure"/>.
    /// </summary>
    public RaiseConfigurationBuilder Event<TEvent>(Action<EventEndpointBuilder> configure)
        where TEvent : IEvent
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        var endpoint = new EventEndpointBuilder();
        configure(endpoint);
        endpoint.Validate(typeof(TEvent));
        _root.EventRoutes[typeof(TEvent)] = endpoint.TopicName!;
        return this;
    }

    /// <summary>Transitions to the <b>Listen</b> section.</summary>
    public ListenConfigurationBuilder Listen => new(_root);

    /// <summary>Transitions to the <b>Subscribe</b> section.</summary>
    public SubscribeConfigurationBuilder Subscribe => new(_root);
}

/// <summary>
/// Callback builder used inside <c>Event&lt;T&gt;</c> to specify the target SNS topic name.
/// </summary>
public sealed class EventEndpointBuilder
{
    internal string? TopicName { get; private set; }

    /// <summary>
    /// Sets the short SNS topic name (e.g. <c>"order-events"</c>).
    /// Do not provide a full ARN — the bootstrapper resolves that automatically.
    /// </summary>
    public EventEndpointBuilder Topic(string topicName)
    {
        if (string.IsNullOrWhiteSpace(topicName))
            throw new ArgumentException("Topic name cannot be null or whitespace.", nameof(topicName));

        if (topicName.StartsWith("arn:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Provide only the topic name (e.g. \"order-events\"), not a full ARN. Got: \"{topicName}\".",
                nameof(topicName));

        TopicName = topicName;
        return this;
    }

    internal void Validate(Type eventType)
    {
        if (string.IsNullOrWhiteSpace(TopicName))
            throw new InvalidOperationException(
                $"No topic name provided for event '{eventType.Name}'. " +
                $"Call .Topic(\"topic-name\") inside the configure callback.");
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  LISTEN  ─  inbound commands from SQS queue names
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Gateway to the <b>Listen</b> section. Access <see cref="To"/> to start registering queues.</summary>
public sealed class ListenConfigurationBuilder
{
    private readonly BusConfigurationBuilder _root;

    internal ListenConfigurationBuilder(BusConfigurationBuilder root) => _root = root;

    /// <summary>Opens the queue name registration context.</summary>
    public ListenToConfigurationBuilder To => new(_root);
}

/// <summary>Fluent context for declaring SQS queue names this service polls for inbound commands.</summary>
public sealed class ListenToConfigurationBuilder
{
    private readonly BusConfigurationBuilder _root;

    internal ListenToConfigurationBuilder(BusConfigurationBuilder root) => _root = root;

    /// <summary>
    /// Registers a short SQS queue name (e.g. <c>"orders.fifo"</c>) that the command listener will poll.
    /// </summary>
    public ListenToConfigurationBuilder CommandQueue(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            throw new ArgumentException("Queue name cannot be null or whitespace.", nameof(queueName));

        if (queueName.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            queueName.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Provide only the queue name (e.g. \"orders.fifo\"), not a full URL. Got: \"{queueName}\".",
                nameof(queueName));

        _root.CommandListeningQueues.Add(queueName);
        return this;
    }

    /// <summary>Transitions to the <b>Subscribe</b> section.</summary>
    public SubscribeConfigurationBuilder Subscribe => new(_root);
}

// ════════════════════════════════════════════════════════════════════════════
//  SUBSCRIBE  ─  inbound events from SNS topic names
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Gateway to the <b>Subscribe</b> section. Access <see cref="To"/> to start registering topics.</summary>
public sealed class SubscribeConfigurationBuilder
{
    private readonly BusConfigurationBuilder _root;

    internal SubscribeConfigurationBuilder(BusConfigurationBuilder root) => _root = root;

    /// <summary>Opens the topic name registration context.</summary>
    public SubscribeToConfigurationBuilder To => new(_root);
}

/// <summary>Fluent context for declaring SNS topic names this service subscribes to for inbound events.</summary>
public sealed class SubscribeToConfigurationBuilder
{
    private readonly BusConfigurationBuilder _root;

    internal SubscribeToConfigurationBuilder(BusConfigurationBuilder root) => _root = root;

    /// <summary>
    /// Registers a short SNS topic name (e.g. <c>"order-events"</c>) to subscribe to.
    /// </summary>
    public SubscribeToConfigurationBuilder Topic(string topicName)
    {
        if (string.IsNullOrWhiteSpace(topicName))
            throw new ArgumentException("Topic name cannot be null or whitespace.", nameof(topicName));

        if (topicName.StartsWith("arn:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Provide only the topic name (e.g. \"order-events\"), not a full ARN. Got: \"{topicName}\".",
                nameof(topicName));

        _root.SubscribedTopics.Add(topicName);
        return this;
    }
}
