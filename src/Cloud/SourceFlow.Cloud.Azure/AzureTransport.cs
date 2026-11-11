using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using SourceFlow.Messaging;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Azure;

/// <summary>
/// The Azure Service Bus transport (doc 07 §4): an endpoint is a queue (sessions on when ordered) with the built-in
/// dead-letter queue, a topic is a topic, and a subscription is a subscription named after the subscribing endpoint that
/// forwards to its queue (<c>ForwardTo</c>), with correlation or SQL rules on the CloudEvents type. Each service has one
/// queue and one receiver for commands and events alike.
/// </summary>
public sealed class AzureTransport : ITransport, IAsyncDisposable
{
    /// <summary>The <c>messaging.system</c> of Service Bus (OpenTelemetry semantic conventions).</summary>
    public const string System = "servicebus";

    /// <summary>The longest native delay: delayed retries are scheduled re-sends (or, on sessions, held locks).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    private readonly bool _ownsClients;
    private readonly AzureProvisioner _provisioner;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _ordered = new(StringComparer.Ordinal);

    /// <summary>Creates the transport with clients built from <paramref name="options"/>.</summary>
    public AzureTransport(AzureTransportOptions options)
        : this(options, client: null, administration: null)
    {
    }

    /// <summary>
    /// Creates the transport over existing clients; the caller owns and disposes them. Only the naming, entity and session
    /// options apply.
    /// </summary>
    public AzureTransport(ServiceBusClient client, ServiceBusAdministrationClient administration, AzureTransportOptions options)
        : this(options, client ?? throw new ArgumentNullException(nameof(client)), administration ?? throw new ArgumentNullException(nameof(administration)))
    {
    }

    private AzureTransport(AzureTransportOptions options, ServiceBusClient? client, ServiceBusAdministrationClient? administration)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (client is null)
        {
            options.Validate();
        }
        else
        {
            options.ValidateSettings();
        }

        Options = options;
        _ownsClients = client is null;
        Client = client ?? options.CreateClient();
        Administration = administration ?? options.CreateAdministrationClient();
        Capabilities = new TransportCapabilities(AzureWire.MaxBodyBytes, MaxAttributes: 64, MaxRetention: options.MessageTimeToLive, NativeDelay: true, MaxDelay)
        {
            OrderedDelivery = true,
            NativeDeadLetter = true,
            SubscriptionFilters = true,
            MaxBatchSize = 100,
            Deduplication = options.DuplicateDetectionWindow is not null,
            DeduplicationWindow = options.DuplicateDetectionWindow ?? TimeSpan.Zero,
            MaxLockDuration = TimeSpan.FromHours(12),
        };
        _provisioner = new AzureProvisioner(this);
    }

    /// <summary>The options.</summary>
    public AzureTransportOptions Options { get; }

    /// <summary>The messaging client.</summary>
    public ServiceBusClient Client { get; }

    /// <summary>The administration client (provisioning, validation, and the ordering of endpoints).</summary>
    public ServiceBusAdministrationClient Administration { get; }

    /// <inheritdoc />
    public string SystemName => System;

    /// <summary>
    /// Service Bus limits: 192 KiB bodies (256 KiB messages on the standard tier), retention as configured, native delay
    /// up to an hour, sessions, the built-in dead-letter queue, subscription rules, batches of 100, and broker duplicate
    /// detection when <see cref="AzureTransportOptions.DuplicateDetectionWindow"/> is set. Locks longer than the queue's
    /// are kept renewed by the receiver.
    /// </summary>
    public TransportCapabilities Capabilities { get; }

    internal string Prefix => Options.NamePrefix;

    /// <inheritdoc />
    public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        ArgumentNullException.ThrowIfNull(messages);
        return new ValueTask(DeliverAsync(endpoint, AzureNaming.Queue(Prefix, endpoint), messages, topic: null, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(messages);
        return new ValueTask(DeliverAsync(topic, AzureNaming.Topic(Prefix, topic), messages, topic, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LockDuration <= TimeSpan.Zero || options.LockDuration > Capabilities.MaxLockDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.LockDuration, "The lock duration must be between 0 and 12 hours.");
        }

        bool ordered = await IsOrderedAsync(options.Endpoint, cancellationToken).ConfigureAwait(false);

        // Filters Service Bus rules cannot express are evaluated here, per topic (published events carry sf-topic).
        var clientFilters = new Dictionary<string, CloudEventFilter>(StringComparer.Ordinal);
        foreach ((string topic, string expression) in options.Filters)
        {
            CloudEventFilter filter = CloudEventFilter.Parse(expression);
            if (!AzureFilter.IsNative(filter))
            {
                clientFilters[topic] = filter;
            }
        }

        string queue = AzureNaming.Queue(Prefix, options.Endpoint);
        return ordered
            ? new AzureSessionReceiver(this, options.Endpoint, queue, options.LockDuration, clientFilters)
            : new AzureQueueReceiver(this, options.Endpoint, Client.CreateReceiver(queue, new ServiceBusReceiverOptions { PrefetchCount = 0 }), deadLetters: false, options.LockDuration, clientFilters);
    }

    /// <inheritdoc />
    public async ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        _ = await IsOrderedAsync(endpoint, cancellationToken).ConfigureAwait(false);   // the queue exists
        ServiceBusReceiver receiver = Client.CreateReceiver(AzureNaming.Queue(Prefix, endpoint), new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, PrefetchCount = 0 });
        return new AzureQueueReceiver(this, endpoint, receiver, deadLetters: true, TimeSpan.FromSeconds(30), new Dictionary<string, CloudEventFilter>());
    }

    /// <inheritdoc />
    public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
    {
        return _provisioner.ProvisionAsync(topology, mode, cancellationToken);
    }

    /// <summary>Checks that the endpoint's queue is reachable (the health check).</summary>
    public async Task<bool> IsReachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        return await AzureErrors.CallAsync(endpoint, async () =>
            (await Administration.QueueExistsAsync(AzureNaming.Queue(Prefix, endpoint), cancellationToken).ConfigureAwait(false)).Value).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (ServiceBusSender sender in _senders.Values)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }

        _senders.Clear();
        if (_ownsClients)
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal ServiceBusSender Sender(string entity)
    {
        return _senders.GetOrAdd(entity, name => Client.CreateSender(name));
    }

    internal void Remember(string endpoint, bool ordered)
    {
        _ordered[endpoint] = ordered;
    }

    // Whether the endpoint's queue has sessions: from provisioning in this process, else from the queue's description.
    internal async Task<bool> IsOrderedAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (_ordered.TryGetValue(endpoint, out bool ordered))
        {
            return ordered;
        }

        QueueProperties queue = await AzureErrors.CallAsync(endpoint, async () =>
            (await Administration.GetQueueAsync(AzureNaming.Queue(Prefix, endpoint), cancellationToken).ConfigureAwait(false)).Value).ConfigureAwait(false);
        Remember(endpoint, queue.RequiresSession);
        return queue.RequiresSession;
    }

    // Batches of at most 100 messages that fit the broker's size limit, in order; a failed call fails the rest.
    private async Task DeliverAsync(string destination, string entity, IReadOnlyList<MessageEnvelope> messages, string? topic, CancellationToken cancellationToken)
    {
        var encoded = new List<(MessageEnvelope Envelope, ServiceBusMessage Message)>(messages.Count);
        var failures = new List<SendFailure>();
        foreach (MessageEnvelope message in messages)
        {
            try
            {
                EnvelopeLimits.ValidateBody(message, AzureWire.MaxBodyBytes);
                encoded.Add((message, AzureWire.Encode(message, topic)));
            }
            catch (MessageTooLargeException e)
            {
                failures.Add(new SendFailure(message.MessageId, e));
            }
        }

        if (encoded.Count == 0)
        {
            ThrowIfFailed(destination, failures, sent: 0);
            return;
        }

        ServiceBusSender sender = Sender(entity);
        int sent = 0;
        int next = 0;
        while (next < encoded.Count)
        {
            int start = next;
            try
            {
                using ServiceBusMessageBatch batch = await AzureErrors.CallAsync(destination, () => sender.CreateMessageBatchAsync(cancellationToken).AsTask()).ConfigureAwait(false);
                while (next < encoded.Count && batch.Count < Capabilities.MaxBatchSize)
                {
                    if (batch.TryAddMessage(encoded[next].Message))
                    {
                        next++;
                    }
                    else if (batch.Count == 0)
                    {
                        // Refused before sending: even alone it exceeds the broker's limit (properties count too).
                        failures.Add(new SendFailure(encoded[next].Envelope.MessageId, AzureErrors.TooLarge(encoded[next].Envelope, batch.MaxSizeInBytes)));
                        next++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    await AzureErrors.CallAsync(destination, () => sender.SendMessagesAsync(batch, cancellationToken)).ConfigureAwait(false);
                    sent += batch.Count;
                }
            }
            catch (TransportException e)
            {
                if (e is DestinationNotFoundException && _senders.TryRemove(entity, out ServiceBusSender? stale))
                {
                    await stale.DisposeAsync().ConfigureAwait(false);
                }

                if (sent == 0 && failures.Count == 0)
                {
                    throw;   // nothing was sent: the call's own error
                }

                failures.AddRange(encoded.Skip(start).Select(m => new SendFailure(m.Envelope.MessageId, e)).Where(f => failures.All(x => x.MessageId != f.MessageId)));
                break;
            }
        }

        ThrowIfFailed(destination, failures, sent);
    }

    private static void ThrowIfFailed(string destination, List<SendFailure> failures, int sent)
    {
        if (failures.Count > 0)
        {
            throw new TransportSendException(destination, failures, sent);
        }
    }
}
