using SourceFlow.Hosting;
using System.Collections.Frozen;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Storage;
using SourceFlow.Transport;

namespace SourceFlow.Runtime;

/// <summary>Who the current actor is, for audit (doc 10 §1). Register your own; the default returns null.</summary>
public interface IActorAccessor
{
    /// <summary>The current actor, for example <c>user:42</c>.</summary>
    string? Actor { get; }
}

/// <summary>Resolves the tenant of the current operation (ADR-022). Register your own; the default uses the options.</summary>
public interface ITenantResolver
{
    /// <summary>The current tenant, or null for the default tenant.</summary>
    string? TenantId { get; }
}

internal sealed class NullActorAccessor : IActorAccessor
{
    public string? Actor => null;
}

internal sealed class NullTenantResolver : ITenantResolver
{
    public string? TenantId => null;
}

/// <summary>
/// The storage ports one adapter provides. Registered by <c>UseInMemoryStorage()</c> or a storage package's <c>Use*</c>.
/// </summary>
/// <param name="Events">Stream reads.</param>
/// <param name="Log">Global log reads.</param>
/// <param name="Sessions">Sessions.</param>
/// <param name="Checkpoints">Checkpoint reads.</param>
/// <param name="Leases">Leases.</param>
/// <param name="Outbox">Outbox relay operations.</param>
/// <param name="DeadLetters">Dead letters.</param>
/// <param name="Snapshots">Snapshots.</param>
/// <param name="Documents">Document reads.</param>
/// <param name="Notifications">Commit notifications.</param>
public sealed record StoragePorts(
    IEventStore Events,
    IEventLogReader Log,
    IStorageSessionFactory Sessions,
    ICheckpointStore Checkpoints,
    ILeaseManager Leases,
    IOutboxStore Outbox,
    IDeadLetterStore DeadLetters,
    ISnapshotStore Snapshots,
    IDocumentReader Documents,
    IStoreNotifications Notifications)
{
    /// <summary>Retention cleanup, when the adapter supports it (doc 05 §9).</summary>
    public IStoreRetention? Retention { get; init; }

    /// <summary>Operational readings for health checks and gauges, when the adapter supports it (doc 08 §5).</summary>
    public IStoreMonitor? Monitor { get; init; }

    /// <summary>Subjects' data keys and streams for crypto-shredding, when the adapter supports it (doc 10 §4).</summary>
    public Security.IPersonalDataKeyStore? PersonalData { get; init; }

    /// <summary>Projection data administration, when the adapter supports it (doc 06 §6).</summary>
    public IDocumentAdmin? DocumentAdmin { get; init; }

    /// <summary>
    /// Read-model aliases for blue/green rebuilds, when the adapter supports them (doc 06 §6); without them every projection
    /// reads and writes its first generation.
    /// </summary>
    public ICollectionAliases? Aliases { get; init; }

    /// <summary>The schema registry, when the adapter supports it (doc 14 §2.2); startup registers this service's schemas.</summary>
    public ISchemaStore? Schemas { get; init; }

    /// <summary>Node liveness for lease fair-share, when the adapter supports it (doc 06 §4); without it a node takes every free lease.</summary>
    public INodeRegistry? Nodes { get; init; }
}

/// <summary>Everything the engine components share; one per host.</summary>
public sealed class SourceFlowRuntime
{
    /// <summary>Creates the runtime context.</summary>
    public SourceFlowRuntime(
        RuntimeModel model,
        RoutingTable routing,
        IOptions<SourceFlowOptions> options,
        StoragePorts storage,
        ITransport transport,
        IMessageSerializer serializer,
        MessagePipeline pipeline,
        ErrorClassifier classifier,
        SourceFlowMetrics metrics,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        IActorAccessor actor,
        ITenantResolver tenants)
    {
        Model = model;
        Routing = routing;
        Options = options.Value;
        Storage = storage;
        Transport = transport;
        Serializer = serializer;
        Pipeline = pipeline;
        Classifier = classifier;
        Metrics = metrics;
        Time = time;
        LoggerFactory = loggerFactory;
        Actor = actor;
        Tenants = tenants;
        InlineProjections = [];
        AsyncProjections = [];
        AggregateCache = new AggregateCache(Options);
        Snapshots = new SnapshotWriter(this);
        Generations = new ReadModelGenerations(this);
    }

    /// <summary>
    /// The service's transport topology (ADR-017): its endpoint (ordered or not) with a dead-letter backstop after the retry
    /// budget plus two deliveries (doc 07 §4), published topics (ordered or not), subscribed topics with their CloudEvents
    /// SQL filters, and remote endpoints.
    /// </summary>
    public TransportTopology Topology()
    {
        return new TransportTopology(
            Routing.Endpoint,
            [.. Routing.PublishedTopics],
            [.. Routing.Subscriptions.Select(s => s.Topic)],
            [.. Routing.Commands.Values.Select(c => c.Endpoint).Distinct(StringComparer.Ordinal)])
        {
            OrderedEndpoint = Routing.OrderedEndpoint,
            OrderedTopics = [.. Routing.OrderedTopics.Order(StringComparer.Ordinal)],
            MaxDeliveries = 1 + Options.DelayedRetries.Count + 2,
            Filters = Routing.Subscriptions.Where(s => s.Filter is not null).ToDictionary(s => s.Topic, s => s.Filter!, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// The service's topology for export (ADR-017): <see cref="Topology"/>, the physical resources when the transport
    /// describes them, and the CloudEvents types the service owns, publishes per topic and sends per endpoint.
    /// </summary>
    public TopologyDocument TopologyDocument()
    {
        TransportTopology topology = Topology();
        return new TopologyDocument(ServiceName, topology)
        {
            Physical = (Transport as ITopologyDescriber)?.Describe(topology),
            OwnedCommands = [.. Model.CommandOwners.Keys.Select(CloudEventType)],
            PublishedMessages = Routing.Publications.Values.SelectMany(p => p)
                .GroupBy(p => p.Destination, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(p => CloudEventType(p.ContractType)).Distinct(StringComparer.Ordinal)], StringComparer.Ordinal),
            SentCommands = Routing.Commands
                .GroupBy(c => c.Value.Endpoint, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(c => CloudEventType(c.Key))], StringComparer.Ordinal),
        };
    }

    private string CloudEventType(Type type)
    {
        MessageTypeInfo info = Model.Registry.Get(type);
        return CloudEvents.FormatType(info.Name, info.Version);
    }

    /// <summary>Read-model generations and aliases (doc 06 §6).</summary>
    internal ReadModelGenerations Generations { get; }

    /// <summary>The aggregate state cache (doc 05 §8.2).</summary>
    internal AggregateCache AggregateCache { get; }

    /// <summary>The background snapshot writer (doc 05 §8.1).</summary>
    internal SnapshotWriter Snapshots { get; }

    /// <summary>The runtime model.</summary>
    public RuntimeModel Model { get; }

    /// <summary>The routing table.</summary>
    public RoutingTable Routing { get; }

    /// <summary>The options.</summary>
    public SourceFlowOptions Options { get; }

    /// <summary>The storage ports.</summary>
    public StoragePorts Storage { get; }

    /// <summary>The transport.</summary>
    public ITransport Transport { get; }

    /// <summary>The payload serializer.</summary>
    public IMessageSerializer Serializer { get; }

    /// <summary>The middleware pipeline.</summary>
    public MessagePipeline Pipeline { get; }

    /// <summary>The error classifier.</summary>
    public ErrorClassifier Classifier { get; }

    /// <summary>The instruments.</summary>
    public SourceFlowMetrics Metrics { get; }

    /// <summary>The clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>Loggers.</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>The actor accessor.</summary>
    public IActorAccessor Actor { get; }

    /// <summary>Personal data encryption (doc 10 §4); null when not configured.</summary>
    internal Security.PersonalDataProtector? PersonalData { get; set; }

    /// <summary>Decrypts the personal data in a stored event (no-op when encryption is off).</summary>
    internal ValueTask<StoredEvent> OpenAsync(StoredEvent stored, CancellationToken cancellationToken)
    {
        return PersonalData is { } personal ? personal.UnprotectAsync(stored, cancellationToken) : ValueTask.FromResult(stored);
    }

    /// <summary>Payload protection (doc 10 §3); null when not configured.</summary>
    internal Security.PayloadProtector? Protector { get; set; }

    /// <summary>The fault injector of a chaos test (plan 06 CH-01..06); null otherwise.</summary>
    internal IFaultInjector? Faults { get; set; }

    /// <summary>Asks the fault injector, if any, for a fault at <paramref name="point"/>.</summary>
    internal ValueTask FaultAsync(string point, CancellationToken cancellationToken)
    {
        return Faults is { } faults ? faults.InjectAsync(point, cancellationToken) : ValueTask.CompletedTask;
    }

    /// <summary>The tenant resolver.</summary>
    public ITenantResolver Tenants { get; }

    /// <summary>Projections applied inside the command transaction.</summary>
    public IReadOnlyList<ProjectionDescriptor> InlineProjections { get; internal set; }

    /// <summary>Projections applied by subscriptions.</summary>
    public IReadOnlyList<ProjectionDescriptor> AsyncProjections { get; internal set; }

    /// <summary>The service name.</summary>
    public string ServiceName => Options.ServiceName;

    /// <summary>Failure policies of async projections that override the default (<see cref="SubscriptionFailurePolicy.Stop"/>).</summary>
    internal IReadOnlyDictionary<Type, SubscriptionFailurePolicy> ProjectionPolicies { get; set; } = new Dictionary<Type, SubscriptionFailurePolicy>();

    /// <summary>Live nodes running subscriptions, from the last heartbeat (1 without a node registry).</summary>
    internal int LiveSubscriptionNodes { get; set; } = 1;

    /// <summary>The current tenant.</summary>
    public string CurrentTenant => Tenants.TenantId ?? Options.DefaultTenant;

    /// <summary>Serializes a registered message.</summary>
    public ReadOnlyMemory<byte> Serialize(object message, MessageTypeInfo type)
    {
        return Serializer.SerializeToArray(message, type);
    }

    /// <summary>Creates the envelope of a new message caused by <paramref name="cause"/> (or by nobody).</summary>
    public MessageEnvelope CreateEnvelope(
        object message,
        MessageEnvelope? cause = null,
        MessageId? messageId = null,
        CorrelationId? correlation = null,
        string? subject = null,
        string? partitionKey = null,
        string? tenant = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        MessageTypeInfo type = Model.Registry.Get(message.GetType());
        MessageId id = messageId ?? MessageIds.New(Time);
        using Activity? create = SourceFlowTelemetry.ActivitySource.StartActivity("create " + type.Name, ActivityKind.Producer);
        SemanticConventions.SetOperationType(create, "create");
        create?.SetTag(SourceFlowTelemetry.Tags.MessageType, type.Name);
        Activity? trace = create ?? Activity.Current;
        return new MessageEnvelope
        {
            MessageId = id,
            Type = type.Name,
            TypeVersion = type.Version,
            Source = ServiceName,
            Time = Time.GetUtcNow(),
            CorrelationId = correlation ?? cause?.CorrelationId ?? CorrelationId.From(id),
            CausationId = cause?.MessageId,
            Subject = subject,
            PartitionKey = partitionKey,
            TenantId = tenant ?? cause?.TenantId ?? CurrentTenant,
            Actor = cause?.Actor ?? Actor.Actor,
            TraceParent = trace?.Id ?? cause?.TraceParent,
            TraceState = trace?.TraceStateString ?? cause?.TraceState,
            Headers = headers is null ? Inherited(cause?.Headers) : headers.ToFrozenDictionary(StringComparer.Ordinal),   // never the caller's mutable instance
            Body = Serialize(message, type),
        };
    }

    /// <summary>Builds an outbox row for <paramref name="envelope"/>, checking the transport's size limit first (NFR-PERF-07).</summary>
    public OutboxMessage ToOutbox(MessageEnvelope envelope, string destination, OutboxKind kind, DateTimeOffset? deliverAt = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        EnvelopeLimits.ValidateBody(envelope, Transport.Capabilities.MaxBodyBytes);
        int partition = OutboxPartitioning.PartitionOf(envelope.EffectivePartitionKey, Options.OutboxPartitions);
        return new OutboxMessage(envelope, destination, kind, deliverAt ?? DateTimeOffset.MinValue, partition);
    }

    /// <summary>
    /// Queues a command for its owner through the outbox: the local endpoint when owned here, else the routed endpoint.
    /// With <paramref name="replyOnReject"/>, the owner replies <see cref="CommandRejected{TCommand}"/> to this
    /// service's endpoint when it rejects the command: always for local owners, and for routes with <c>ReplyOnReject()</c>.
    /// A <paramref name="messageId"/> makes the send deterministic (workflow re-sends, doc 15 §5.3); null gets a new id.
    /// </summary>
    public void EnqueueCommand(IStorageSession session, ICommand command, MessageEnvelope? cause, CorrelationId? correlation, bool replyOnReject = false, MessageId? messageId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        (MessageEnvelope envelope, string destination) = CommandEnvelope(command, cause, correlation, session.TenantId, replyOnReject, messageId, headers: null);
        session.EnqueueOutbox(ToOutbox(envelope, destination, OutboxKind.Send));
    }

    /// <summary>
    /// The envelope of a command for its owner, and the owner's endpoint (this service's when owned here). User
    /// <paramref name="headers"/> replace the ones inherited from <paramref name="cause"/>.
    /// </summary>
    internal (MessageEnvelope Envelope, string Destination) CommandEnvelope(ICommand command, MessageEnvelope? cause, CorrelationId? correlation, string? tenant,
        bool replyOnReject, MessageId? messageId, IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentNullException.ThrowIfNull(command);
        bool local = Model.CommandOwners.ContainsKey(command.GetType());
        CommandRoute? route = null;
        string destination = local
            ? Routing.Endpoint
            : Routing.Commands.TryGetValue(command.GetType(), out route)
                ? route.Endpoint
                : throw new NoRouteException(command.GetType());
        string? subject = TrySubjectOf(command);
        if (replyOnReject && (local || route is { ReplyOnReject: true }) && ExpectsRejection(command.GetType()))
        {
            headers = new Dictionary<string, string>(headers ?? Inherited(cause?.Headers), StringComparer.Ordinal) { [CommandReplies.ReplyToHeader] = Routing.Endpoint };
        }

        MessageEnvelope envelope = CreateEnvelope(command, cause, messageId: messageId, correlation: correlation, subject: subject, tenant: tenant, headers: headers);
        return (envelope, destination);
    }

    // A reply is asked for only when this service registered CommandRejected<T> (a process manager reacts to it);
    // otherwise the reply type would be unknown here and would be dead-lettered.
    private bool ExpectsRejection(Type commandType)
    {
        MessageTypeInfo command = Model.Registry.Get(commandType);
        return Model.Registry.TryGet(CommandReplies.RejectedTypeName(command.Name), command.Version, out _);
    }

    // Headers a caused message inherits: the cause's user headers, without reserved ones (sf-reply-to).
    private static IReadOnlyDictionary<string, string> Inherited(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return System.Collections.Frozen.FrozenDictionary<string, string>.Empty;
        }

        return headers.ContainsKey(CommandReplies.ReplyToHeader)
            ? System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(headers.Where(h => h.Key != CommandReplies.ReplyToHeader), StringComparer.Ordinal)
            : headers;
    }

    /// <summary>Queues a delayed message to this service's own endpoint (durable timer, doc 04 §8).</summary>
    public void EnqueueScheduled(IStorageSession session, IMessage message, DateTimeOffset deliverAt, MessageEnvelope? cause, CorrelationId? correlation)
    {
        MessageEnvelope envelope = CreateEnvelope(message, cause, correlation: correlation, tenant: session.TenantId);
        session.EnqueueOutbox(ToOutbox(envelope, Routing.Endpoint, OutboxKind.Scheduled, deliverAt));
    }

    /// <summary>The stream id a command targets, when it is owned locally.</summary>
    public string? TrySubjectOf(ICommand command)
    {
        if (!Model.CommandOwners.TryGetValue(command.GetType(), out AggregateDescriptor? owner))
        {
            return null;
        }

        try
        {
            return owner.StreamOf(command).Value;
        }
        catch (InvalidCommandException)
        {
            return null; // rejected by the command processor with a stable error
        }
    }

    /// <summary>Creates a logger.</summary>
    public ILogger Logger<T>()
    {
        return LoggerFactory.CreateLogger<T>();
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IStorageSession, List<Action>> _afterCommit = [];

    /// <summary>
    /// Runs <paramref name="action"/> after <paramref name="session"/> commits (see <see cref="Committed"/>), so what it
    /// records happens once per committed transaction, not once per attempt. An uncommitted session drops it.
    /// </summary>
    internal void AfterCommit(IStorageSession session, Action action)
    {
        _afterCommit.GetOrCreateValue(session).Add(action);
    }

    /// <summary>Runs the actions queued for <paramref name="session"/>; its committer calls it after a successful commit.</summary>
    internal void Committed(IStorageSession session)
    {
        if (!_afterCommit.TryGetValue(session, out List<Action>? actions))
        {
            return;
        }

        _afterCommit.Remove(session);
        foreach (Action action in actions)
        {
            action();
        }
    }
}

/// <summary>A command type is neither owned locally nor routed (RT-R1).</summary>
public sealed class NoRouteException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public NoRouteException(Type commandType)
        : base($"Command {commandType.Name} has no local owner and no route. Add a Decide/handler or r.Command<{commandType.Name}>().To(endpoint) (RT-R1).")
    {
        CommandType = commandType;
    }

    /// <summary>The command type.</summary>
    public Type CommandType { get; }
}
