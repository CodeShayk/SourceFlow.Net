using SourceFlow.Messaging;

namespace SourceFlow.Transport;

/// <summary>
/// The broker SPI (ADR-014). Cloud packages implement it; the transport conformance kit verifies parity.
/// Endpoints and topics are logical names; the transport maps them to physical resources (RT-R5).
/// </summary>
public interface ITransport
{
    /// <summary>The <c>messaging.system</c> name, for example <c>aws_sqs</c>.</summary>
    string SystemName { get; }

    /// <summary>What the broker supports; drives size limits and retry fallbacks.</summary>
    TransportCapabilities Capabilities { get; }

    /// <summary>Sends commands (or delayed messages) to an endpoint, in order.</summary>
    ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken);

    /// <summary>Publishes events to a topic, in order.</summary>
    ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken);

    /// <summary>Creates the single receiver of a service endpoint, including its topic subscriptions (RX-R5, RT-R4).</summary>
    ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken);

    /// <summary>Creates or validates the physical resources (ADR-017).</summary>
    ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken);
}

/// <summary>Receives messages for one endpoint.</summary>
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

    /// <summary>Acknowledges the message.</summary>
    public abstract ValueTask CompleteAsync(CancellationToken cancellationToken);

    /// <summary>Returns the message for redelivery after <paramref name="delay"/> (native delayed retry, doc 04 §9.2).</summary>
    public abstract ValueTask AbandonAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Receiver options.</summary>
/// <param name="Endpoint">The service's input endpoint.</param>
/// <param name="SubscribedTopics">Topics whose events this service receives on its endpoint.</param>
public sealed record ReceiverOptions(string Endpoint, IReadOnlyList<string> SubscribedTopics);

/// <summary>Broker capabilities (doc 04 §2.3, doc 07).</summary>
/// <param name="MaxBodyBytes">The largest body the broker accepts.</param>
/// <param name="MaxAttributes">The most native attributes per message.</param>
/// <param name="MaxRetention">How long the broker keeps undelivered messages.</param>
/// <param name="NativeDelay">True when the broker can delay redelivery.</param>
/// <param name="MaxDelay">The longest native delay.</param>
public sealed record TransportCapabilities(int MaxBodyBytes, int MaxAttributes, TimeSpan MaxRetention, bool NativeDelay, TimeSpan MaxDelay);

/// <summary>The logical topology a service needs (ADR-017, topology as data).</summary>
/// <param name="Endpoint">The service's input endpoint.</param>
/// <param name="PublishedTopics">Topics the service publishes to.</param>
/// <param name="SubscribedTopics">Topics the service subscribes to.</param>
/// <param name="SendEndpoints">Remote endpoints the service sends commands to.</param>
public sealed record TransportTopology(string Endpoint, IReadOnlyList<string> PublishedTopics, IReadOnlyList<string> SubscribedTopics, IReadOnlyList<string> SendEndpoints);

/// <summary>How a transport treats physical resources at startup (ADR-017).</summary>
public enum ProvisioningMode
{
    /// <summary>Assume they exist.</summary>
    None = 0,

    /// <summary>Check they exist; fail startup otherwise.</summary>
    Validate = 1,

    /// <summary>Create missing resources.</summary>
    CreateIfMissing = 2,
}

/// <summary>A physical resource is missing under <see cref="ProvisioningMode.Validate"/>.</summary>
public sealed class TopologyValidationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TopologyValidationException(string message)
        : base(message)
    {
    }
}
