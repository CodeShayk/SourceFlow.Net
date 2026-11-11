using SourceFlow.Diagnostics;
using SourceFlow.Messaging;

namespace SourceFlow.Transport.InMemory;

/// <summary>
/// An in-process broker shared by the services of a test or a single-process deployment. Each endpoint is a queue
/// with competing consumers; a topic fans out one copy to every subscribed endpoint (RT-R4). Redelivery delays use the
/// broker's <see cref="TimeProvider"/>. Fault injection: duplicate deliveries, dropped acknowledgements, raw messages.
/// </summary>
public sealed class InMemoryBroker
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, EndpointQueue> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _subscriptions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _topics = new(StringComparer.Ordinal);
    private int _inFlight;

    /// <summary>Creates a broker.</summary>
    public InMemoryBroker(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Capabilities that mirror a typical broker (1 MiB bodies, native delay).</summary>
    public static TransportCapabilities Capabilities { get; } = new(1024 * 1024, 64, TimeSpan.FromDays(14), NativeDelay: true, TimeSpan.FromHours(12));

    /// <summary>Fault injection.</summary>
    public InMemoryBrokerFaults Faults { get; } = new();

    /// <summary>Messages queued, in flight or waiting for a delayed redelivery.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _endpoints.Values.Sum(e => e.Count) + _inFlight;
            }
        }
    }

    /// <summary>Messages visible now or in flight (excludes delayed redeliveries), for idle detection in tests.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                DateTimeOffset now = _time.GetUtcNow();
                return _endpoints.Values.Sum(e => e.VisibleCount(now)) + _inFlight;
            }
        }
    }

    /// <summary>The time of the earliest delayed redelivery, if any.</summary>
    public DateTimeOffset? NextVisibleAt
    {
        get
        {
            lock (_gate)
            {
                return _endpoints.Values.Select(e => e.NextVisibleAt).Where(t => t is not null).Min();
            }
        }
    }

    /// <summary>Delivered message count per endpoint, for assertions.</summary>
    public int DeliveredCount(string endpoint)
    {
        lock (_gate)
        {
            return _endpoints.TryGetValue(endpoint, out EndpointQueue? queue) ? queue.Delivered : 0;
        }
    }

    /// <summary>Declares a topic (as provisioning would).</summary>
    public void DeclareTopic(string topic)
    {
        lock (_gate)
        {
            _topics.Add(topic);
        }
    }

    /// <summary>Subscribes an endpoint to a topic.</summary>
    public void Subscribe(string topic, string endpoint)
    {
        lock (_gate)
        {
            _topics.Add(topic);
            if (!_subscriptions.TryGetValue(topic, out HashSet<string>? endpoints))
            {
                _subscriptions[topic] = endpoints = new HashSet<string>(StringComparer.Ordinal);
            }

            endpoints.Add(endpoint);
            Queue(endpoint);
        }
    }

    /// <summary>Puts a message on an endpoint queue.</summary>
    public void Send(string endpoint, MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            EndpointQueue queue = Queue(endpoint);
            queue.Enqueue(envelope, _time.GetUtcNow(), deliveryCount: 0);
            if (Faults.ConsumeDuplicate())
            {
                queue.Enqueue(envelope, _time.GetUtcNow(), deliveryCount: 0);
            }

            queue.Signal();
        }
    }

    /// <summary>Publishes a message to every endpoint subscribed to the topic.</summary>
    public void Publish(string topic, MessageEnvelope envelope)
    {
        string[] endpoints;
        lock (_gate)
        {
            _topics.Add(topic);
            endpoints = _subscriptions.TryGetValue(topic, out HashSet<string>? subscribers) ? [.. subscribers] : [];
        }

        foreach (string endpoint in endpoints)
        {
            Send(endpoint, envelope);
        }
    }

    /// <summary>True when the topic exists.</summary>
    public bool TopicExists(string topic)
    {
        lock (_gate)
        {
            return _topics.Contains(topic);
        }
    }

    /// <summary>True when the endpoint exists.</summary>
    public bool EndpointExists(string endpoint)
    {
        lock (_gate)
        {
            return _endpoints.ContainsKey(endpoint);
        }
    }

    /// <summary>Creates a transport bound to this broker.</summary>
    public InMemoryTransport CreateTransport()
    {
        return new InMemoryTransport(this);
    }

    internal async ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(string endpoint, int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        EndpointQueue queue;
        Task signal;
        lock (_gate)
        {
            queue = Queue(endpoint);
            signal = queue.SignalTask; // captured before taking, so a message that arrives in between wakes us
        }

        IReadOnlyList<ReceivedMessage> messages = Take(queue, maxMessages);
        if (messages.Count > 0 || wait <= TimeSpan.Zero)
        {
            return messages;
        }

        try
        {
            // The long-poll timeout is infrastructure timing (system clock); visibility uses the broker clock.
            await signal.WaitAsync(wait, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Nothing arrived within the wait.
        }

        return Take(queue, maxMessages);
    }

    /// <summary>Wakes every receiver, for example after the clock moved past a delayed redelivery.</summary>
    public void Pulse()
    {
        lock (_gate)
        {
            foreach (EndpointQueue queue in _endpoints.Values)
            {
                queue.Signal();
            }
        }
    }

    private IReadOnlyList<ReceivedMessage> Take(EndpointQueue queue, int maxMessages)
    {
        lock (_gate)
        {
            List<(MessageEnvelope Envelope, int DeliveryCount)> taken = queue.Take(maxMessages, _time.GetUtcNow());
            _inFlight += taken.Count;
            return taken.Select(t => (ReceivedMessage)new Delivery(this, queue, t.Envelope, t.DeliveryCount + 1)).ToArray();
        }
    }

    private void Settle(EndpointQueue queue, MessageEnvelope envelope, int deliveryCount, TimeSpan? redeliverAfter)
    {
        lock (_gate)
        {
            _inFlight--;
            if (redeliverAfter is { } delay)
            {
                queue.Enqueue(envelope, _time.GetUtcNow() + delay, deliveryCount);
            }
            else
            {
                queue.Delivered++;
            }

            queue.Signal();
        }
    }

    private EndpointQueue Queue(string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        if (!_endpoints.TryGetValue(endpoint, out EndpointQueue? queue))
        {
            _endpoints[endpoint] = queue = new EndpointQueue();
        }

        return queue;
    }

    private sealed class EndpointQueue
    {
        private readonly List<(MessageEnvelope Envelope, DateTimeOffset VisibleAt, int DeliveryCount, long Order)> _items = [];
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _order;

        public int Count => _items.Count;

        public int VisibleCount(DateTimeOffset now)
        {
            return _items.Count(i => i.VisibleAt <= now);
        }

        public int Delivered { get; set; }

        public DateTimeOffset? NextVisibleAt => _items.Count == 0 ? null : _items.Min(i => i.VisibleAt);

        public void Enqueue(MessageEnvelope envelope, DateTimeOffset visibleAt, int deliveryCount)
        {
            _items.Add((envelope, visibleAt, deliveryCount, _order++));
        }

        public List<(MessageEnvelope, int)> Take(int max, DateTimeOffset now)
        {
            var taken = new List<(MessageEnvelope, int)>();
            foreach (var item in _items.Where(i => i.VisibleAt <= now).OrderBy(i => i.Order).Take(max).ToArray())
            {
                _items.Remove(item);
                taken.Add((item.Envelope, item.DeliveryCount));
            }

            return taken;
        }

        public Task SignalTask => _signal.Task;

        public void Signal()
        {
            TaskCompletionSource previous = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    private sealed class Delivery(InMemoryBroker broker, EndpointQueue queue, MessageEnvelope envelope, int deliveryCount) : ReceivedMessage
    {
        private int _settled;

        public override MessageEnvelope Envelope => envelope;

        public override int DeliveryCount => deliveryCount;

        public override ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _settled, 1) == 0)
            {
                bool dropAck = broker.Faults.ConsumeDroppedAck();
                broker.Settle(queue, envelope, deliveryCount, dropAck ? TimeSpan.Zero : null);
            }

            return ValueTask.CompletedTask;
        }

        public override ValueTask AbandonAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _settled, 1) == 0)
            {
                broker.Settle(queue, envelope, deliveryCount, delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Fault injection for <see cref="InMemoryBroker"/>.</summary>
public sealed class InMemoryBrokerFaults
{
    private int _duplicates;
    private int _droppedAcks;

    /// <summary>Delivers the next <paramref name="count"/> sent messages twice.</summary>
    public void DuplicateNext(int count = 1)
    {
        Interlocked.Exchange(ref _duplicates, count);
    }

    /// <summary>Loses the next <paramref name="count"/> acknowledgements, so those messages are redelivered.</summary>
    public void DropNextAcks(int count = 1)
    {
        Interlocked.Exchange(ref _droppedAcks, count);
    }

    internal bool ConsumeDuplicate()
    {
        return Volatile.Read(ref _duplicates) > 0 && Interlocked.Decrement(ref _duplicates) >= 0;
    }

    internal bool ConsumeDroppedAck()
    {
        return Volatile.Read(ref _droppedAcks) > 0 && Interlocked.Decrement(ref _droppedAcks) >= 0;
    }
}

/// <summary>The in-memory <see cref="ITransport"/>, bound to an <see cref="InMemoryBroker"/>.</summary>
public sealed class InMemoryTransport : ITransport
{
    private readonly InMemoryBroker _broker;

    internal InMemoryTransport(InMemoryBroker broker)
    {
        _broker = broker;
    }

    /// <inheritdoc />
    public string SystemName => SourceFlowTelemetry.InMemorySystem;

    /// <inheritdoc />
    public TransportCapabilities Capabilities => InMemoryBroker.Capabilities;

    /// <inheritdoc />
    public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (MessageEnvelope message in messages)
        {
            EnvelopeLimits.ValidateBody(message, Capabilities.MaxBodyBytes);
            _broker.Send(endpoint, message);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (MessageEnvelope message in messages)
        {
            EnvelopeLimits.ValidateBody(message, Capabilities.MaxBodyBytes);
            _broker.Publish(topic, message);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (string topic in options.SubscribedTopics)
        {
            _broker.Subscribe(topic, options.Endpoint);
        }

        return ValueTask.FromResult<ITransportReceiver>(new Receiver(_broker, options.Endpoint));
    }

    /// <inheritdoc />
    public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (mode == ProvisioningMode.Validate)
        {
            string? missing = topology.PublishedTopics.Concat(topology.SubscribedTopics).FirstOrDefault(t => !_broker.TopicExists(t));
            if (missing is not null)
            {
                throw new TopologyValidationException($"Topic '{missing}' does not exist (ProvisioningMode.Validate).");
            }
        }
        else if (mode == ProvisioningMode.CreateIfMissing)
        {
            foreach (string topic in topology.PublishedTopics.Concat(topology.SubscribedTopics))
            {
                _broker.DeclareTopic(topic);
            }
        }

        return ValueTask.CompletedTask;
    }

    private sealed class Receiver(InMemoryBroker broker, string endpoint) : ITransportReceiver
    {
        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
        {
            return broker.ReceiveAsync(endpoint, maxMessages, wait, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
