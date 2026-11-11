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
    }

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

    /// <summary>The tenant resolver.</summary>
    public ITenantResolver Tenants { get; }

    /// <summary>Projections applied inside the command transaction.</summary>
    public IReadOnlyList<ProjectionDescriptor> InlineProjections { get; internal set; }

    /// <summary>Projections applied by subscriptions.</summary>
    public IReadOnlyList<ProjectionDescriptor> AsyncProjections { get; internal set; }

    /// <summary>The service name.</summary>
    public string ServiceName => Options.ServiceName;

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
        create?.SetTag(SourceFlowTelemetry.Tags.OperationType, "create");
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
            Headers = headers ?? cause?.Headers ?? System.Collections.Frozen.FrozenDictionary<string, string>.Empty,
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
    /// </summary>
    public void EnqueueCommand(IStorageSession session, ICommand command, MessageEnvelope? cause, CorrelationId? correlation)
    {
        string destination = Model.CommandOwners.ContainsKey(command.GetType())
            ? Routing.Endpoint
            : Routing.Commands.TryGetValue(command.GetType(), out CommandRoute? route)
                ? route.Endpoint
                : throw new NoRouteException(command.GetType());
        string? subject = TrySubjectOf(command);
        MessageEnvelope envelope = CreateEnvelope(command, cause, correlation: correlation, subject: subject, tenant: session.TenantId);
        session.EnqueueOutbox(ToOutbox(envelope, destination, OutboxKind.Send));
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
