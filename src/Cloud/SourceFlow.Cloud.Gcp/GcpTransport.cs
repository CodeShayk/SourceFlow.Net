using System.Globalization;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using SourceFlow.Messaging;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Gcp;

/// <summary>
/// The Google Cloud Pub/Sub transport (doc 07 §4). Pub/Sub has no forwarding, so an endpoint is a set of subscriptions
/// that one receiver pulls from: the endpoint's command subscription (on its own topic) and one subscription per
/// subscribed topic, named after the subscribing service so services fan out and instances compete (GAP-13). Ordered
/// endpoints enable message ordering on their subscriptions and the publisher sets the ordering key; every subscription
/// dead-letters to the endpoint's dead-letter topic.
/// </summary>
public sealed class GcpTransport : ITransport, ITopologyDescriber
{
    /// <summary>The <c>messaging.system</c> of Pub/Sub (OpenTelemetry semantic conventions).</summary>
    public const string System = "gcp_pubsub";

    /// <summary>The subscription label holding the endpoint's delivery limit (Pub/Sub's own limit starts at 5).</summary>
    public const string MaxDeliveriesLabel = "sf-max-deliveries";

    private readonly GcpProvisioner _provisioner;

    /// <summary>Creates the transport with clients built from <paramref name="options"/> (Application Default Credentials or the emulator).</summary>
    public GcpTransport(GcpTransportOptions options)
        : this(options, publisher: null, subscriber: null)
    {
    }

    /// <summary>
    /// Creates the transport over existing clients (for example with a custom call invoker); only the project, naming,
    /// call, receive and retention options apply.
    /// </summary>
    public GcpTransport(PublisherServiceApiClient publisher, SubscriberServiceApiClient subscriber, GcpTransportOptions options)
        : this(options, publisher ?? throw new ArgumentNullException(nameof(publisher)), subscriber ?? throw new ArgumentNullException(nameof(subscriber)))
    {
    }

    private GcpTransport(GcpTransportOptions options, PublisherServiceApiClient? publisher, SubscriberServiceApiClient? subscriber)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        Publisher = publisher ?? options.ToPublisherBuilder().Build();
        Subscriber = subscriber ?? options.ToSubscriberBuilder().Build();
        CallSettings = options.ToCallSettings();
        _provisioner = new GcpProvisioner(this);
    }

    /// <summary>
    /// Pub/Sub limits: 9 MiB bodies (any bytes; 10 MB per request), 100 attributes, 7 days retention, native delay and lock
    /// extension up to 10 minutes (the ack deadline limit), ordering keys, dead-letter topics, filters, batches of 100.
    /// Broker deduplication is not declared: publishing has none, and the inbox deduplicates.
    /// </summary>
    public static TransportCapabilities GcpCapabilities { get; } = new(GcpWire.MaxBodyBytes, GcpWire.MaxAttributes, TimeSpan.FromDays(7), NativeDelay: true, TimeSpan.FromMinutes(10))
    {
        OrderedDelivery = true,
        NativeDeadLetter = true,
        SubscriptionFilters = true,
        MaxBatchSize = 100,
        MaxLockDuration = TimeSpan.FromMinutes(10),
    };

    /// <summary>The options.</summary>
    public GcpTransportOptions Options { get; }

    /// <summary>The publisher (topic administration and publishing) client.</summary>
    public PublisherServiceApiClient Publisher { get; }

    /// <summary>The subscriber (subscription administration, pull and settlement) client.</summary>
    public SubscriberServiceApiClient Subscriber { get; }

    /// <inheritdoc />
    public string SystemName => System;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => GcpCapabilities;

    internal CallSettings CallSettings { get; }

    internal string Prefix => Options.NamePrefix;

    /// <inheritdoc />
    public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentNullException.ThrowIfNull(messages);
        return new ValueTask(PublishAllAsync(endpoint, TopicName(GcpNaming.EndpointTopic(Prefix, endpoint)), messages, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(messages);
        return new ValueTask(PublishAllAsync(topic, TopicName(GcpNaming.Topic(Prefix, topic)), messages, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LockDuration <= TimeSpan.Zero || options.LockDuration > Capabilities.MaxLockDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.LockDuration, "The lock duration must be between 0 and 10 minutes (the Pub/Sub ack deadline limit).");
        }

        Subscription endpoint = await GetSubscriptionAsync(GcpNaming.EndpointSubscription(Prefix, options.Endpoint), options.Endpoint, cancellationToken).ConfigureAwait(false)
            ?? throw new DestinationNotFoundException(options.Endpoint);
        var subscriptions = new List<GcpSubscription> { Describe(endpoint, topic: null, clientFilter: null) };
        foreach (string topic in options.SubscribedTopics.Distinct(StringComparer.Ordinal))
        {
            string resource = $"{topic} → {options.Endpoint}";
            Subscription subscription = await GetSubscriptionAsync(GcpNaming.TopicSubscription(Prefix, options.Endpoint, topic), resource, cancellationToken).ConfigureAwait(false)
                ?? throw new DestinationNotFoundException(resource);

            // Filters Pub/Sub cannot express are evaluated here, per subscription.
            CloudEventFilter? clientFilter = null;
            if (options.Filters.TryGetValue(topic, out string? expression))
            {
                CloudEventFilter filter = CloudEventFilter.Parse(expression);
                clientFilter = GcpFilter.Translate(filter) is null ? filter : null;
            }

            subscriptions.Add(Describe(subscription, topic, clientFilter));
        }

        TopicName deadLetters = endpoint.DeadLetterPolicy is { DeadLetterTopic: { Length: > 0 } target } ? Google.Cloud.PubSub.V1.TopicName.Parse(target) : TopicName(GcpNaming.DeadLetter(Prefix, options.Endpoint));
        return new GcpReceiver(this, options.Endpoint, subscriptions, deadLetters, isDeadLetterReceiver: false, options.LockDuration);
    }

    /// <inheritdoc />
    public async ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        string destination = endpoint + " (dead-letter subscription)";
        Subscription subscription = await GetSubscriptionAsync(GcpNaming.DeadLetter(Prefix, endpoint), destination, cancellationToken).ConfigureAwait(false)
            ?? throw new DestinationNotFoundException(destination);
        return new GcpReceiver(this, endpoint, [Describe(subscription, topic: null, clientFilter: null)], deadLetters: null, isDeadLetterReceiver: true, TimeSpan.FromSeconds(30));
    }

    /// <inheritdoc />
    public PhysicalTopology Describe(TransportTopology topology)
    {
        return GcpTopology.Describe(Options, topology);
    }

    /// <inheritdoc />
    public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
    {
        return _provisioner.ProvisionAsync(topology, mode, cancellationToken);
    }

    /// <summary>Checks that the endpoint's subscription is reachable (the health check).</summary>
    public async Task<bool> IsReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        return await GetSubscriptionAsync(GcpNaming.EndpointSubscription(Prefix, endpoint), endpoint, cancellationToken).ConfigureAwait(false) is not null;
    }

    internal TopicName TopicName(string id)
    {
        return new TopicName(Options.ProjectId, id);
    }

    internal SubscriptionName SubscriptionName(string id)
    {
        return new SubscriptionName(Options.ProjectId, id);
    }

    internal CallSettings Call(CancellationToken cancellationToken)
    {
        return CallSettings.WithCancellationToken(cancellationToken);
    }

    internal Task<Subscription?> GetSubscriptionAsync(string id, string destination, CancellationToken cancellationToken)
    {
        return GcpErrors.CallAsync(destination, async () =>
        {
            try
            {
                return await Subscriber.GetSubscriptionAsync(new GetSubscriptionRequest { SubscriptionAsSubscriptionName = SubscriptionName(id) }, Call(cancellationToken)).ConfigureAwait(false);
            }
            catch (Grpc.Core.RpcException e) when (GcpErrors.IsNotFound(e))
            {
                return null;
            }
        }, cancellationToken);
    }

    internal Task<Topic?> GetTopicAsync(string id, string destination, CancellationToken cancellationToken)
    {
        return GcpErrors.CallAsync(destination, async () =>
        {
            try
            {
                return await Publisher.GetTopicAsync(new GetTopicRequest { TopicAsTopicName = TopicName(id) }, Call(cancellationToken)).ConfigureAwait(false);
            }
            catch (Grpc.Core.RpcException e) when (GcpErrors.IsNotFound(e))
            {
                return null;
            }
        }, cancellationToken);
    }

    /// <summary>The delivery limit of a subscription: its label, else its dead-letter policy, else unlimited.</summary>
    internal static int MaxDeliveries(Subscription subscription)
    {
        if (subscription.Labels.TryGetValue(MaxDeliveriesLabel, out string? label) && int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out int max) && max > 0)
        {
            return max;
        }

        return subscription.DeadLetterPolicy is { MaxDeliveryAttempts: > 0 } policy ? policy.MaxDeliveryAttempts : int.MaxValue;
    }

    private static GcpSubscription Describe(Subscription subscription, string? topic, CloudEventFilter? clientFilter)
    {
        return new GcpSubscription(subscription.SubscriptionName, topic, subscription.EnableMessageOrdering, MaxDeliveries(subscription), subscription.AckDeadlineSeconds, clientFilter);
    }

    // Refuses oversized messages before anything is sent for them (TCK-06, TCK-08), then publishes in order in batches.
    private async Task PublishAllAsync(string destination, TopicName topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        var encoded = new List<GcpOutgoingMessage>(messages.Count);
        var failures = new List<SendFailure>();
        foreach (MessageEnvelope message in messages)
        {
            try
            {
                EnvelopeLimits.ValidateBody(message, GcpWire.MaxBodyBytes);
                GcpOutgoingMessage wire = GcpWire.Encode(message);
                if (wire.Size > GcpWire.MaxRequestBytes - RequestOverhead)
                {
                    throw new MessageTooLargeException(message.CloudEventType, wire.Size, GcpWire.MaxRequestBytes - RequestOverhead);
                }

                encoded.Add(wire);
            }
            catch (MessageTooLargeException e)
            {
                failures.Add(new SendFailure(message.MessageId, e));
            }
        }

        int sent = 0;
        List<List<GcpOutgoingMessage>> batches = Batches(encoded);
        for (int b = 0; b < batches.Count; b++)
        {
            List<GcpOutgoingMessage> batch = batches[b];
            var request = new PublishRequest { TopicAsTopicName = topic };
            request.Messages.AddRange(batch.Select(m => m.Message));
            try
            {
                PublishResponse response = await GcpErrors.CallAsync(destination, () => Publisher.PublishAsync(request, Call(cancellationToken)), cancellationToken).ConfigureAwait(false);
                sent += response.MessageIds.Count;
            }
            catch (TransportException e)
            {
                if (sent == 0 && failures.Count == 0)
                {
                    throw;   // nothing was sent: the call's own error
                }

                failures.AddRange(batches.Skip(b).SelectMany(rest => rest).Select(m => new SendFailure(m.Envelope.MessageId, e)));
                break;
            }
        }

        if (failures.Count > 0)
        {
            throw new TransportSendException(destination, failures, sent);
        }
    }

    // The topic name and framing of a publish request.
    private const int RequestOverhead = 4096;

    private static List<List<GcpOutgoingMessage>> Batches(List<GcpOutgoingMessage> messages)
    {
        var batches = new List<List<GcpOutgoingMessage>>();
        var current = new List<GcpOutgoingMessage>();
        int size = RequestOverhead;
        foreach (GcpOutgoingMessage message in messages)
        {
            if (current.Count == GcpCapabilities.MaxBatchSize || (current.Count > 0 && size + message.Size > GcpWire.MaxRequestBytes))
            {
                batches.Add(current);
                current = [];
                size = RequestOverhead;
            }

            current.Add(message);
            size += message.Size;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }
}

/// <summary>A subscription a receiver pulls from.</summary>
/// <param name="Name">The subscription.</param>
/// <param name="Topic">The logical topic, for a topic subscription; null for the endpoint's command subscription.</param>
/// <param name="Ordered">True when message ordering is enabled.</param>
/// <param name="MaxDeliveries">Deliveries before the receiver dead-letters a message itself.</param>
/// <param name="AckDeadlineSeconds">The subscription's ack deadline, which applies until the receiver sets its lock.</param>
/// <param name="ClientFilter">The filter the receiver evaluates (one Pub/Sub cannot express), if any.</param>
internal sealed record GcpSubscription(SubscriptionName Name, string? Topic, bool Ordered, int MaxDeliveries, int AckDeadlineSeconds, CloudEventFilter? ClientFilter);
