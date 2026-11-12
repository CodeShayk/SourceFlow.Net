using SourceFlow.Messaging;

namespace SourceFlow.Transport;

/// <summary>
/// The broker SPI (ADR-014, ADR-033). Cloud packages implement it; the transport conformance kit (TCK) verifies parity.
/// Endpoints and topics are logical names; the transport maps them to physical resources (RT-R5). A transport only moves
/// bytes and headers, settles messages and provisions resources: retries, idempotency, dead-lettering policy and
/// telemetry live in the core (doc 07 §1).
/// </summary>
public interface ITransport
{
    /// <summary>The <c>messaging.system</c> name, for example <c>aws_sqs</c>.</summary>
    string SystemName { get; }

    /// <summary>What the broker supports; drives size limits, retry fallbacks and client-side filtering.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Sends commands (or delayed messages) to an endpoint, in order, in batches of at most
    /// <see cref="TransportCapabilities.MaxBatchSize"/>. Oversized messages are refused before anything is sent for them.
    /// </summary>
    /// <exception cref="TransportSendException">Some messages were not sent; the others were.</exception>
    /// <exception cref="DestinationNotFoundException">The endpoint does not exist.</exception>
    ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken);

    /// <summary>Publishes events to a topic, in order; each subscribed endpoint receives one copy (RT-R4).</summary>
    /// <exception cref="TransportSendException">Some messages were not published; the others were.</exception>
    /// <exception cref="DestinationNotFoundException">The topic does not exist.</exception>
    ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken);

    /// <summary>Creates a receiver of a service endpoint, including its topic subscriptions (RX-R5, RT-R4).</summary>
    ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a receiver of the endpoint's native dead-letter queue (the backstop, doc 07 §3); the core's ingestor moves
    /// its messages to the dead-letter store.
    /// </summary>
    ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken);

    /// <summary>Creates or validates the physical resources (ADR-017).</summary>
    /// <exception cref="TopologyValidationException">Under <see cref="ProvisioningMode.Validate"/>: what is missing or misconfigured.</exception>
    ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken);
}

/// <summary>
/// A transport that can check the broker cheaply for the <c>sourceflow.transport</c> health check (doc 08 §5): a describe
/// call on the service's endpoint. The core caches the result (<see cref="Runtime.HealthThresholds.TransportCacheDuration"/>).
/// </summary>
public interface ITransportProbe
{
    /// <summary>Returns when the broker answers and the endpoint exists.</summary>
    /// <exception cref="TransportThrottledException">The broker throttled the call (degraded).</exception>
    /// <exception cref="TransportException">The broker is unreachable, or the endpoint does not exist (unhealthy).</exception>
    ValueTask ProbeAsync(string endpoint, CancellationToken cancellationToken);
}

/// <summary>Receives messages for one endpoint. Disposing stops receiving; unsettled messages are redelivered.</summary>
public interface ITransportReceiver : IAsyncDisposable
{
    /// <summary>Receives up to <paramref name="maxMessages"/>, waiting at most <paramref name="wait"/> for the first.</summary>
    ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>A delivered message; settle it exactly once, after the handler commits (RX-R3).</summary>
public abstract class ReceivedMessage
{
    /// <summary>The envelope.</summary>
    public abstract MessageEnvelope Envelope { get; }

    /// <summary>How many times the broker has delivered this message (1 on first delivery).</summary>
    public abstract int DeliveryCount { get; }

    /// <summary>For a message read from a dead-letter queue: why the broker dead-lettered it.</summary>
    public virtual string? DeadLetterReason => null;

    /// <summary>Acknowledges the message.</summary>
    public abstract ValueTask CompleteAsync(CancellationToken cancellationToken);

    /// <summary>Returns the message for redelivery after <paramref name="delay"/> (native delayed retry, doc 04 §9.2).</summary>
    public abstract ValueTask AbandonAsync(TimeSpan delay, CancellationToken cancellationToken);

    /// <summary>Extends the lock (visibility timeout, lease) by the receiver's lock duration, for long handlers.</summary>
    public abstract ValueTask RenewAsync(CancellationToken cancellationToken);

    /// <summary>Moves the message to the endpoint's native dead-letter queue (the backstop path only).</summary>
    public abstract ValueTask DeadLetterAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>Receiver options.</summary>
/// <param name="Endpoint">The service's input endpoint.</param>
/// <param name="SubscribedTopics">Topics whose events this service receives on its endpoint.</param>
public sealed record ReceiverOptions(string Endpoint, IReadOnlyList<string> SubscribedTopics)
{
    /// <summary>
    /// CloudEvents SQL filters per subscribed topic (doc 07 §7); a topic without one receives every event. Transports
    /// without <see cref="TransportCapabilities.SubscriptionFilters"/> deliver everything and the core filters.
    /// </summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>();

    /// <summary>How long a received message stays locked before the broker redelivers it (visibility timeout).</summary>
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Broker capabilities (doc 04 §2.3, doc 07 §2).</summary>
/// <param name="MaxBodyBytes">The largest body the broker accepts.</param>
/// <param name="MaxAttributes">The most native attributes per message; extra CloudEvents extensions overflow into <c>sfext</c>.</param>
/// <param name="MaxRetention">How long the broker keeps undelivered messages.</param>
/// <param name="NativeDelay">True when the broker can delay redelivery.</param>
/// <param name="MaxDelay">The longest native delay.</param>
public sealed record TransportCapabilities(int MaxBodyBytes, int MaxAttributes, TimeSpan MaxRetention, bool NativeDelay, TimeSpan MaxDelay)
{
    /// <summary>True when an ordered endpoint keeps per-partition-key order across competing consumers (FIFO groups, sessions, ordering keys).</summary>
    public bool OrderedDelivery { get; init; }

    /// <summary>True when the broker moves messages to a dead-letter queue after the endpoint's maximum deliveries.</summary>
    public bool NativeDeadLetter { get; init; }

    /// <summary>True when subscriptions filter natively; otherwise the core evaluates the CloudEvents SQL filter on receipt.</summary>
    public bool SubscriptionFilters { get; init; }

    /// <summary>The most messages per send call to the broker (1 without batching).</summary>
    public int MaxBatchSize { get; init; } = 1;

    /// <summary>True when the broker drops a re-sent <see cref="MessageEnvelope.MessageId"/> within <see cref="DeduplicationWindow"/>.</summary>
    public bool Deduplication { get; init; }

    /// <summary>The broker deduplication window (zero without broker deduplication).</summary>
    public TimeSpan DeduplicationWindow { get; init; }

    /// <summary>The longest lock (visibility timeout) a receiver can hold before renewing.</summary>
    public TimeSpan MaxLockDuration { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>The logical topology a service needs (ADR-017, topology as data).</summary>
/// <param name="Endpoint">The service's input endpoint.</param>
/// <param name="PublishedTopics">Topics the service publishes to.</param>
/// <param name="SubscribedTopics">Topics the service subscribes to.</param>
/// <param name="SendEndpoints">Remote endpoints the service sends commands to.</param>
public sealed record TransportTopology(string Endpoint, IReadOnlyList<string> PublishedTopics, IReadOnlyList<string> SubscribedTopics, IReadOnlyList<string> SendEndpoints)
{
    /// <summary>True when the endpoint keeps per-partition-key order across instances (FIFO, sessions, ordering keys).</summary>
    public bool OrderedEndpoint { get; init; }

    /// <summary>Published topics that keep per-partition-key order.</summary>
    public IReadOnlyList<string> OrderedTopics { get; init; } = [];

    /// <summary>Deliveries before the broker dead-letters a message (the runtime retry budget plus two, doc 07 §4).</summary>
    public int MaxDeliveries { get; init; } = 10;

    /// <summary>CloudEvents SQL filters per subscribed topic.</summary>
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>();
}

/// <summary>How a transport treats physical resources at startup (ADR-017).</summary>
public enum ProvisioningMode
{
    /// <summary>Assume they exist.</summary>
    None = 0,

    /// <summary>Check they exist and are configured as the topology requires; fail startup otherwise.</summary>
    Validate = 1,

    /// <summary>Create missing resources, then validate.</summary>
    CreateIfMissing = 2,
}

/// <summary>What is wrong with a physical resource.</summary>
public enum TopologyProblemKind
{
    /// <summary>The endpoint (queue) does not exist.</summary>
    MissingEndpoint,

    /// <summary>The topic does not exist.</summary>
    MissingTopic,

    /// <summary>The endpoint is not subscribed to the topic.</summary>
    MissingSubscription,

    /// <summary>Ordering differs from the topology (for example a FIFO queue on a standard topic, ordering disabled).</summary>
    OrderingMismatch,

    /// <summary>The endpoint has no dead-letter queue, or a different maximum delivery count.</summary>
    MissingDeadLetter,

    /// <summary>The subscription's filter differs from the topology.</summary>
    FilterDrift,

    /// <summary>A provider-specific setting differs (for example SNS raw message delivery).</summary>
    Misconfigured,
}

/// <summary>One difference between the topology and the broker.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Resource">The logical resource (endpoint, topic or <c>topic → endpoint</c>).</param>
/// <param name="Detail">Expected and actual values.</param>
public sealed record TopologyProblem(TopologyProblemKind Kind, string Resource, string Detail);

/// <summary>Physical resources are missing or misconfigured under <see cref="ProvisioningMode.Validate"/>; lists every difference.</summary>
public sealed class TopologyValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TopologyValidationException(string message)
        : base(message)
    {
        Problems = [];
    }

    /// <summary>Creates the exception from the full diff.</summary>
    public TopologyValidationException(IReadOnlyList<TopologyProblem> problems)
        : base("The broker topology differs from the service's (ProvisioningMode.Validate):" + Environment.NewLine
            + string.Join(Environment.NewLine, (problems ?? throw new ArgumentNullException(nameof(problems))).Select(p => $"  {p.Kind} {p.Resource}: {p.Detail}")))
    {
        Problems = problems;
    }

    /// <summary>Every difference found.</summary>
    public IReadOnlyList<TopologyProblem> Problems { get; }
}

/// <summary>A broker error. Transient ones (outage, throttling) are retried by the core; the others are not.</summary>
public class TransportException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TransportException(string message, bool transient, Exception? inner = null)
        : base(message, inner)
    {
        IsTransient = transient;
    }

    /// <summary>True when retrying later can succeed.</summary>
    public bool IsTransient { get; }

    /// <summary>When the broker said to retry (throttling), if it did.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>The broker is unreachable (transient).</summary>
public sealed class TransportUnavailableException(string message, Exception? inner = null) : TransportException(message, transient: true, inner);

/// <summary>The broker throttled the call (transient; honour <see cref="TransportException.RetryAfter"/>).</summary>
public sealed class TransportThrottledException(string message, Exception? inner = null) : TransportException(message, transient: true, inner);

/// <summary>The endpoint or topic does not exist (not transient: provision it).</summary>
public sealed class DestinationNotFoundException(string destination) : TransportException($"Destination '{destination}' does not exist; provision it (ProvisioningMode.CreateIfMissing) or check the routing.", transient: false)
{
    /// <summary>The logical destination.</summary>
    public string Destination { get; } = destination;
}

/// <summary>One message a batch send could not deliver.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="Error">Why.</param>
public sealed record SendFailure(MessageId MessageId, Exception Error);

/// <summary>A batch send delivered some messages and not others; <see cref="Failures"/> lists the ones not sent.</summary>
public sealed class TransportSendException : TransportException
{
    /// <summary>Creates the exception.</summary>
    public TransportSendException(string destination, IReadOnlyList<SendFailure> failures, int sent)
        : base($"{(failures ?? throw new ArgumentNullException(nameof(failures))).Count} message(s) to '{destination}' were not sent ({sent} were): "
            + string.Join("; ", failures.Select(f => $"{f.MessageId}: {f.Error.Message}")), transient: failures.All(f => f.Error is TransportException { IsTransient: true }))
    {
        Destination = destination;
        Failures = failures;
        Sent = sent;
    }

    /// <summary>The destination.</summary>
    public string Destination { get; }

    /// <summary>The messages not sent.</summary>
    public IReadOnlyList<SendFailure> Failures { get; }

    /// <summary>How many messages were sent.</summary>
    public int Sent { get; }
}
