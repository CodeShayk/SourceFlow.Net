using SourceFlow.Diagnostics;
using SourceFlow.Messaging;

namespace SourceFlow.Transport.InMemory;

/// <summary>
/// An in-process broker shared by the services of a test or a single-process deployment, and the reference transport
/// for the conformance kit (ADR-033). Messages cross it in CloudEvents binary mode with a 10-attribute budget (the
/// strictest broker), so the header overflow is exercised. Each endpoint is a queue with competing consumers, locks,
/// a maximum delivery count and a dead-letter queue; ordered endpoints deliver one message per partition key at a time.
/// A topic fans out one copy to every subscribed endpoint whose CloudEvents SQL filter matches (RT-R4). Delays and
/// locks use the broker's <see cref="TimeProvider"/>. Faults: duplicates, dropped acknowledgements, outages,
/// throttling and credential expiry.
/// </summary>
public sealed class InMemoryBroker
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, EndpointQueue> _endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Topic> _topics = new(StringComparer.Ordinal);
    private long _tokens;
    private long _receivers;

    /// <summary>Creates a broker.</summary>
    public InMemoryBroker(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Capabilities that mirror the strictest supported broker: 1 MiB bodies, 10 native attributes, batches of 10, native
    /// delay up to 12 hours, ordered delivery, a native dead-letter queue and native subscription filters.
    /// </summary>
    public static TransportCapabilities Capabilities { get; } = new(1024 * 1024, 10, TimeSpan.FromDays(14), NativeDelay: true, TimeSpan.FromHours(12))
    {
        OrderedDelivery = true,
        NativeDeadLetter = true,
        SubscriptionFilters = true,
        MaxBatchSize = 10,
        MaxLockDuration = TimeSpan.FromHours(12),
    };

    /// <summary>
    /// Real-broker semantics: sending to an endpoint or publishing to a topic that was never provisioned fails with
    /// <see cref="DestinationNotFoundException"/>. Off by default, so tests can send to services that start later.
    /// </summary>
    public bool StrictDestinations { get; set; }

    /// <summary>Fault injection.</summary>
    public InMemoryBrokerFaults Faults { get; } = new();

    /// <summary>Messages queued, in flight or waiting for a delayed redelivery (dead-letter queues excluded).</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _endpoints.Values.Sum(e => e.Count + e.InFlightCount);
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
                return _endpoints.Values.Sum(e => e.VisibleCount(now) + e.InFlightCount);
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

    /// <summary>Completed message count per endpoint, for assertions.</summary>
    public int DeliveredCount(string endpoint)
    {
        lock (_gate)
        {
            return _endpoints.TryGetValue(endpoint, out EndpointQueue? queue) ? queue.Delivered : 0;
        }
    }

    /// <summary>Messages in the endpoint's dead-letter queue.</summary>
    public int DeadLetterCount(string endpoint)
    {
        lock (_gate)
        {
            return _endpoints.TryGetValue(endpoint, out EndpointQueue? queue) ? queue.DeadLetters.Count : 0;
        }
    }

    // ================================================================ administration (provisioning, and defects for the TCK)

    /// <summary>Declares an endpoint (as provisioning would); an existing endpoint keeps its settings.</summary>
    public void DeclareEndpoint(string endpoint, bool ordered = false, int maxDeliveries = 10)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(endpoint, out EndpointQueue? queue))
            {
                _endpoints[endpoint] = new EndpointQueue(ordered, maxDeliveries);
            }
            else if (queue.Implicit)
            {
                queue.Ordered = ordered;
                queue.MaxDeliveries = maxDeliveries;
                queue.Implicit = false;
            }
        }
    }

    /// <summary>Declares a topic (as provisioning would); an existing topic keeps its settings.</summary>
    public void DeclareTopic(string topic, bool ordered = false)
    {
        lock (_gate)
        {
            Declare(topic, ordered);
        }
    }

    /// <summary>Subscribes an endpoint to a topic, optionally with a CloudEvents SQL filter (replacing an existing one).</summary>
    public void Subscribe(string topic, string endpoint, string? filter = null)
    {
        CloudEventFilter? parsed = filter is null ? null : CloudEventFilter.Parse(filter);
        lock (_gate)
        {
            if (!_topics.TryGetValue(topic, out Topic? declared))
            {
                _topics[topic] = declared = new Topic(Queue(endpoint).Ordered) { Implicit = true };
            }

            Queue(endpoint);
            declared.Subscribers[endpoint] = parsed;
        }
    }

    /// <summary>Removes a subscription.</summary>
    public void Unsubscribe(string topic, string endpoint)
    {
        lock (_gate)
        {
            if (_topics.TryGetValue(topic, out Topic? declared))
            {
                declared.Subscribers.Remove(endpoint);
            }
        }
    }

    /// <summary>Deletes a topic and its subscriptions.</summary>
    public void DeleteTopic(string topic)
    {
        lock (_gate)
        {
            _topics.Remove(topic);
        }
    }

    /// <summary>Deletes an endpoint, its messages and its dead-letter queue.</summary>
    public void DeleteEndpoint(string endpoint)
    {
        lock (_gate)
        {
            _endpoints.Remove(endpoint);
        }
    }

    /// <summary>Changes the ordering of an endpoint or a topic (a misconfiguration, for validation tests).</summary>
    public void SetOrdered(string resource, bool ordered)
    {
        lock (_gate)
        {
            if (_endpoints.TryGetValue(resource, out EndpointQueue? queue))
            {
                queue.Ordered = ordered;
            }

            if (_topics.TryGetValue(resource, out Topic? topic))
            {
                topic.Ordered = ordered;
            }
        }
    }

    /// <summary>Detaches the endpoint's dead-letter queue: messages are then redelivered forever (a misconfiguration).</summary>
    public void DetachDeadLetterQueue(string endpoint)
    {
        lock (_gate)
        {
            if (_endpoints.TryGetValue(endpoint, out EndpointQueue? queue))
            {
                queue.DeadLetterAttached = false;
            }
        }
    }

    /// <summary>True when the topic exists.</summary>
    public bool TopicExists(string topic)
    {
        lock (_gate)
        {
            return _topics.ContainsKey(topic);
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

    // ================================================================ messaging

    /// <summary>Puts a message on an endpoint queue.</summary>
    public void Send(string endpoint, MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var wire = WireMessage.From(envelope);
        lock (_gate)
        {
            EndpointQueue queue = StrictDestinations
                ? _endpoints.GetValueOrDefault(endpoint) ?? throw new DestinationNotFoundException(endpoint)
                : Queue(endpoint);
            Enqueue(queue, wire);
        }
    }

    /// <summary>Publishes a message to every subscribed endpoint whose filter matches.</summary>
    public void Publish(string topic, MessageEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var wire = WireMessage.From(envelope);
        lock (_gate)
        {
            if (!_topics.TryGetValue(topic, out Topic? declared))
            {
                if (StrictDestinations)
                {
                    throw new DestinationNotFoundException(topic);
                }

                _topics[topic] = declared = new Topic(false) { Implicit = true };
            }

            foreach ((string endpoint, CloudEventFilter? filter) in declared.Subscribers)
            {
                if (filter is null || filter.Matches(wire.Attributes))
                {
                    Enqueue(Queue(endpoint), wire);
                }
            }
        }
    }

    /// <summary>Creates a transport bound to this broker.</summary>
    public InMemoryTransport CreateTransport()
    {
        return new InMemoryTransport(this);
    }

    /// <summary>Wakes every receiver, for example after the clock moved past a delayed redelivery.</summary>
    public void Pulse()
    {
        lock (_gate)
        {
            foreach (EndpointQueue queue in _endpoints.Values)
            {
                queue.Signal();
                queue.DeadLetters.Signal();
            }
        }
    }

    internal long NewReceiverId()
    {
        return Interlocked.Increment(ref _receivers);
    }

    internal void Guard()
    {
        Faults.Check();
    }

    internal IReadOnlyList<TopologyProblem> Validate(TransportTopology topology)
    {
        var problems = new List<TopologyProblem>();
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(topology.Endpoint, out EndpointQueue? queue))
            {
                problems.Add(new TopologyProblem(TopologyProblemKind.MissingEndpoint, topology.Endpoint, "the endpoint does not exist"));
            }
            else
            {
                if (queue.Ordered != topology.OrderedEndpoint)
                {
                    problems.Add(new TopologyProblem(TopologyProblemKind.OrderingMismatch, topology.Endpoint, $"expected ordered={topology.OrderedEndpoint}, found ordered={queue.Ordered}"));
                }

                if (!queue.DeadLetterAttached || queue.MaxDeliveries != topology.MaxDeliveries)
                {
                    problems.Add(new TopologyProblem(TopologyProblemKind.MissingDeadLetter, topology.Endpoint,
                        queue.DeadLetterAttached ? $"expected max deliveries {topology.MaxDeliveries}, found {queue.MaxDeliveries}" : "no dead-letter queue is attached"));
                }
            }

            foreach (string topic in topology.PublishedTopics.Distinct(StringComparer.Ordinal))
            {
                bool ordered = topology.OrderedTopics.Contains(topic, StringComparer.Ordinal);
                if (!_topics.TryGetValue(topic, out Topic? declared))
                {
                    problems.Add(new TopologyProblem(TopologyProblemKind.MissingTopic, topic, "the topic does not exist"));
                }
                else if (declared.Ordered != ordered)
                {
                    problems.Add(new TopologyProblem(TopologyProblemKind.OrderingMismatch, topic, $"expected ordered={ordered}, found ordered={declared.Ordered}"));
                }
            }

            foreach (string topic in topology.SubscribedTopics.Distinct(StringComparer.Ordinal))
            {
                string resource = $"{topic} → {topology.Endpoint}";
                if (!_topics.TryGetValue(topic, out Topic? declared))
                {
                    problems.Add(new TopologyProblem(TopologyProblemKind.MissingTopic, topic, "the subscribed topic does not exist"));
                    continue;
                }

                if (!declared.Subscribers.TryGetValue(topology.Endpoint, out CloudEventFilter? filter))
                {
                    problems.Add(new TopologyProblem(TopologyProblemKind.MissingSubscription, resource, "the endpoint is not subscribed"));
                    continue;
                }

                if (_endpoints.TryGetValue(topology.Endpoint, out EndpointQueue? subscriber) && declared.Ordered != subscriber.Ordered)
                {
                    problems.Add(new TopologyProblem(TopologyProblemKind.OrderingMismatch, resource, $"topic ordered={declared.Ordered}, endpoint ordered={subscriber.Ordered}"));
                }

                string? expected = topology.Filters.GetValueOrDefault(topic);
                if (!string.Equals(expected, filter?.Expression, StringComparison.Ordinal))
                {
                    problems.Add(new TopologyProblem(TopologyProblemKind.FilterDrift, resource, $"expected filter '{expected ?? "(none)"}', found '{filter?.Expression ?? "(none)"}'"));
                }
            }
        }

        return problems;
    }

    internal void Create(TransportTopology topology)
    {
        lock (_gate)
        {
            // Resources created implicitly (a send before the owner started) take the owner's settings.
            if (!_endpoints.TryGetValue(topology.Endpoint, out EndpointQueue? endpoint))
            {
                _endpoints[topology.Endpoint] = new EndpointQueue(topology.OrderedEndpoint, topology.MaxDeliveries);
            }
            else if (endpoint.Implicit)
            {
                endpoint.Ordered = topology.OrderedEndpoint;
                endpoint.MaxDeliveries = topology.MaxDeliveries;
                endpoint.Implicit = false;
            }

            foreach (string topic in topology.PublishedTopics)
            {
                Declare(topic, topology.OrderedTopics.Contains(topic, StringComparer.Ordinal));
            }

            foreach (string topic in topology.SubscribedTopics)
            {
                Topic declared = _topics.TryGetValue(topic, out Topic? existing) ? existing : Declare(topic, topology.OrderedEndpoint);
                if (!declared.Subscribers.ContainsKey(topology.Endpoint))
                {
                    declared.Subscribers[topology.Endpoint] = topology.Filters.TryGetValue(topic, out string? filter) ? CloudEventFilter.Parse(filter) : null;
                }
            }
        }
    }

    internal void EnsureReceiver(ReceiverOptions options)
    {
        lock (_gate)
        {
            Queue(options.Endpoint);
        }

        foreach (string topic in options.SubscribedTopics)
        {
            lock (_gate)
            {
                if (_topics.TryGetValue(topic, out Topic? declared) && declared.Subscribers.ContainsKey(options.Endpoint))
                {
                    continue;   // provisioned (with its filter)
                }
            }

            Subscribe(topic, options.Endpoint, options.Filters.GetValueOrDefault(topic));
        }
    }

    internal async ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(string endpoint, bool deadLetters, long receiver, TimeSpan lockDuration, int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        Guard();
        EndpointQueue queue;
        Task signal;
        lock (_gate)
        {
            EndpointQueue owner = Queue(endpoint);
            queue = deadLetters ? owner.DeadLetters : owner;
            signal = queue.SignalTask; // captured before taking, so a message that arrives in between wakes us
        }

        IReadOnlyList<ReceivedMessage> messages = Take(queue, receiver, lockDuration, maxMessages);
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

        return Take(queue, receiver, lockDuration, maxMessages);
    }

    // A receiver stops: what it holds and has not settled becomes visible again (redelivered, never lost).
    internal void Release(long receiver)
    {
        lock (_gate)
        {
            foreach (EndpointQueue queue in _endpoints.Values.SelectMany(e => new[] { e, e.DeadLetters }))
            {
                foreach (InFlight held in queue.InFlight.Values.Where(i => i.Receiver == receiver).ToArray())
                {
                    queue.InFlight.Remove(held.Token);
                    queue.Items.Add(held.Item with { VisibleAt = _time.GetUtcNow() });
                }

                queue.Signal();
            }
        }
    }

    private IReadOnlyList<ReceivedMessage> Take(EndpointQueue queue, long receiver, TimeSpan lockDuration, int maxMessages)
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            queue.ReclaimExpired(now);
            var taken = new List<ReceivedMessage>();
            var blocked = new HashSet<string>(queue.Ordered ? queue.InFlight.Values.Select(i => i.Item.Key).OfType<string>() : [], StringComparer.Ordinal);
            foreach (Item item in queue.Items.OrderBy(i => i.Order).ToArray())
            {
                if (taken.Count >= maxMessages)
                {
                    break;
                }

                // An ordered endpoint delivers a key's messages one at a time and in order: the head blocks the rest.
                bool keyed = queue.Ordered && item.Key is not null;
                if (keyed && blocked.Contains(item.Key!))
                {
                    continue;
                }

                if (item.VisibleAt > now)
                {
                    if (keyed)
                    {
                        blocked.Add(item.Key!);
                    }

                    continue;
                }

                queue.Items.Remove(item);
                if (queue.DeadLetterAttached && item.Deliveries >= queue.MaxDeliveries && !queue.IsDeadLetterQueue)
                {
                    queue.DeadLetters.Items.Add(item with { VisibleAt = now, Deliveries = 0, Reason = "MaxDeliveryCountExceeded", Order = queue.DeadLetters.NextOrder() });
                    queue.DeadLetters.Signal();
                    continue;
                }

                long token = Interlocked.Increment(ref _tokens);
                Item delivered = item with { Deliveries = item.Deliveries + 1 };
                queue.InFlight[token] = new InFlight(token, delivered, receiver, now + lockDuration);
                if (keyed)
                {
                    blocked.Add(item.Key!);
                }

                taken.Add(new Delivery(this, queue, token, delivered, lockDuration));
            }

            return taken;
        }
    }

    private void Enqueue(EndpointQueue queue, WireMessage wire)
    {
        DateTimeOffset now = _time.GetUtcNow();
        queue.Items.Add(new Item(wire, now, 0, queue.NextOrder(), wire.Key, null));
        if (Faults.ConsumeDuplicate())
        {
            queue.Items.Add(new Item(wire, now, 0, queue.NextOrder(), wire.Key, null));
        }

        queue.Signal();
    }

    private bool Settle(EndpointQueue queue, long token, Settlement settlement, TimeSpan delay = default, string? reason = null)
    {
        lock (_gate)
        {
            if (!queue.InFlight.Remove(token, out InFlight? held))
            {
                return false;   // the lock expired and the message was redelivered: a late settlement is ignored
            }

            DateTimeOffset now = _time.GetUtcNow();
            switch (settlement)
            {
                case Settlement.Complete:
                    queue.Delivered++;
                    break;
                case Settlement.Abandon:
                    queue.Items.Add(held.Item with { VisibleAt = now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay) });
                    break;
                case Settlement.DeadLetter:
                    if (!queue.DeadLetterAttached || queue.IsDeadLetterQueue)
                    {
                        queue.InFlight[token] = held;
                        throw new TransportException("The endpoint has no dead-letter queue.", transient: false);
                    }

                    queue.DeadLetters.Items.Add(held.Item with { VisibleAt = now, Deliveries = 0, Reason = reason, Order = queue.DeadLetters.NextOrder() });
                    queue.DeadLetters.Signal();
                    break;
            }

            queue.Signal();
            return true;
        }
    }

    private void Renew(EndpointQueue queue, long token, TimeSpan lockDuration)
    {
        lock (_gate)
        {
            if (!queue.InFlight.TryGetValue(token, out InFlight? held))
            {
                throw new TransportException("The message lock was lost (it expired and the message was redelivered).", transient: false);
            }

            queue.InFlight[token] = held with { LockedUntil = _time.GetUtcNow() + lockDuration };
        }
    }

    private EndpointQueue Queue(string endpoint)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        if (!_endpoints.TryGetValue(endpoint, out EndpointQueue? queue))
        {
            _endpoints[endpoint] = queue = new EndpointQueue(ordered: false, maxDeliveries: 10) { Implicit = true };
        }

        return queue;
    }

    private Topic Declare(string topic, bool ordered)
    {
        if (!_topics.TryGetValue(topic, out Topic? declared))
        {
            _topics[topic] = declared = new Topic(ordered);
        }
        else if (declared.Implicit)
        {
            declared.Ordered = ordered;
            declared.Implicit = false;
        }

        return declared;
    }

    private enum Settlement
    {
        Complete,
        Abandon,
        DeadLetter,
    }

    // The bytes on the wire: CloudEvents binary-mode attributes (with the sfext overflow) and the body.
    private sealed record WireMessage(Dictionary<string, string> Attributes, byte[] Body, string? Key)
    {
        public static WireMessage From(MessageEnvelope envelope)
        {
            return new WireMessage(CloudEvents.ToAttributes(envelope, Capabilities.MaxAttributes), envelope.Body.ToArray(), envelope.EffectivePartitionKey);
        }

        public MessageEnvelope ToEnvelope()
        {
            return CloudEvents.FromAttributes(Attributes, Body);
        }
    }

    private sealed record Item(WireMessage Wire, DateTimeOffset VisibleAt, int Deliveries, long Order, string? Key, string? Reason);

    private sealed record InFlight(long Token, Item Item, long Receiver, DateTimeOffset LockedUntil);

    private sealed class Topic(bool ordered)
    {
        public bool Ordered { get; set; } = ordered;

        public bool Implicit { get; set; }

        public Dictionary<string, CloudEventFilter?> Subscribers { get; } = new(StringComparer.Ordinal);
    }

    private sealed class EndpointQueue
    {
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _order;
        private EndpointQueue? _deadLetters;

        public EndpointQueue(bool ordered, int maxDeliveries, bool isDeadLetterQueue = false)
        {
            Ordered = ordered;
            MaxDeliveries = maxDeliveries;
            IsDeadLetterQueue = isDeadLetterQueue;
        }

        public bool Ordered { get; set; }

        public int MaxDeliveries { get; set; }

        public bool Implicit { get; set; }

        public bool IsDeadLetterQueue { get; }

        public bool DeadLetterAttached { get; set; } = true;

        public EndpointQueue DeadLetters => _deadLetters ??= new EndpointQueue(false, int.MaxValue, isDeadLetterQueue: true);

        public List<Item> Items { get; } = [];

        public Dictionary<long, InFlight> InFlight { get; } = [];

        public int Count => Items.Count;

        public int InFlightCount => InFlight.Count;

        public int Delivered { get; set; }

        public DateTimeOffset? NextVisibleAt => Items.Count == 0 ? null : Items.Min(i => i.VisibleAt);

        public Task SignalTask => _signal.Task;

        public long NextOrder()
        {
            return _order++;
        }

        public int VisibleCount(DateTimeOffset now)
        {
            return Items.Count(i => i.VisibleAt <= now);
        }

        public void ReclaimExpired(DateTimeOffset now)
        {
            foreach (InFlight expired in InFlight.Values.Where(i => i.LockedUntil <= now).ToArray())
            {
                InFlight.Remove(expired.Token);
                Items.Add(expired.Item with { VisibleAt = now });
            }
        }

        public void Signal()
        {
            TaskCompletionSource previous = _signal;
            _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    private sealed class Delivery(InMemoryBroker broker, EndpointQueue queue, long token, Item item, TimeSpan lockDuration) : ReceivedMessage
    {
        private readonly MessageEnvelope _envelope = item.Wire.ToEnvelope();

        public override MessageEnvelope Envelope => _envelope;

        public override int DeliveryCount => item.Deliveries;

        public override string? DeadLetterReason => item.Reason;

        public override ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            broker.Guard();
            bool dropAck = !queue.IsDeadLetterQueue && broker.Faults.ConsumeDroppedAck();
            broker.Settle(queue, token, dropAck ? Settlement.Abandon : Settlement.Complete);
            return ValueTask.CompletedTask;
        }

        public override ValueTask AbandonAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            broker.Guard();
            broker.Settle(queue, token, Settlement.Abandon, delay);
            return ValueTask.CompletedTask;
        }

        public override ValueTask RenewAsync(CancellationToken cancellationToken)
        {
            broker.Guard();
            broker.Renew(queue, token, lockDuration);
            return ValueTask.CompletedTask;
        }

        public override ValueTask DeadLetterAsync(string reason, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            broker.Guard();
            broker.Settle(queue, token, Settlement.DeadLetter, reason: reason);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Fault injection for <see cref="InMemoryBroker"/>.</summary>
public sealed class InMemoryBrokerFaults
{
    private int _duplicates;
    private int _droppedAcks;
    private int _throttled;
    private int _credentialsExpired;
    private int _refreshes;
    private long _retryAfterTicks;

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

    /// <summary>While true, every broker call fails with <see cref="TransportUnavailableException"/> (an outage).</summary>
    public bool Unavailable { get; set; }

    /// <summary>Throttles the next <paramref name="count"/> broker calls with <see cref="TransportThrottledException"/>.</summary>
    public void ThrottleNext(int count, TimeSpan retryAfter)
    {
        Interlocked.Exchange(ref _retryAfterTicks, retryAfter.Ticks);
        Interlocked.Exchange(ref _throttled, count);
    }

    /// <summary>Expires the credentials: the next call refreshes them (as the SDK credential chain does) and succeeds.</summary>
    public void ExpireCredentials()
    {
        Interlocked.Exchange(ref _credentialsExpired, 1);
    }

    /// <summary>How many times expired credentials were refreshed.</summary>
    public int CredentialRefreshes => Volatile.Read(ref _refreshes);

    internal void Check()
    {
        if (Unavailable)
        {
            throw new TransportUnavailableException("The in-memory broker is unavailable (injected outage).");
        }

        if (Volatile.Read(ref _throttled) > 0 && Interlocked.Decrement(ref _throttled) >= 0)
        {
            throw new TransportThrottledException("The in-memory broker throttled the call (injected).") { RetryAfter = TimeSpan.FromTicks(Interlocked.Read(ref _retryAfterTicks)) };
        }

        if (Interlocked.Exchange(ref _credentialsExpired, 0) == 1)
        {
            Interlocked.Increment(ref _refreshes);
        }
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
        return DeliverAsync(endpoint, messages, _broker.Send);
    }

    /// <inheritdoc />
    public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        return DeliverAsync(topic, messages, _broker.Publish);
    }

    /// <inheritdoc />
    public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _broker.Guard();
        _broker.EnsureReceiver(options);
        return ValueTask.FromResult<ITransportReceiver>(new Receiver(_broker, options.Endpoint, deadLetters: false, options.LockDuration));
    }

    /// <inheritdoc />
    public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        _broker.Guard();
        return ValueTask.FromResult<ITransportReceiver>(new Receiver(_broker, endpoint, deadLetters: true, TimeSpan.FromSeconds(30)));
    }

    /// <inheritdoc />
    public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        foreach ((string topic, string filter) in topology.Filters)
        {
            CloudEventFilter.Parse(filter);   // a malformed filter fails provisioning in every mode
            _ = topic;
        }

        if (mode == ProvisioningMode.None)
        {
            return ValueTask.CompletedTask;
        }

        _broker.Guard();
        if (mode == ProvisioningMode.CreateIfMissing)
        {
            _broker.Create(topology);
        }

        IReadOnlyList<TopologyProblem> problems = _broker.Validate(topology);
        return problems.Count == 0 ? ValueTask.CompletedTask : throw new TopologyValidationException(problems);
    }

    // Validates every message, sends the valid ones in batches of MaxBatchSize, then reports the ones not sent.
    private ValueTask DeliverAsync(string destination, IReadOnlyList<MessageEnvelope> messages, Action<string, MessageEnvelope> deliver)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(messages);
        _broker.Guard();
        var failures = new List<SendFailure>();
        int sent = 0;
        foreach (MessageEnvelope[] batch in messages.Chunk(Capabilities.MaxBatchSize))
        {
            foreach (MessageEnvelope message in batch)
            {
                try
                {
                    EnvelopeLimits.ValidateBody(message, Capabilities.MaxBodyBytes);
                    deliver(destination, message);
                    sent++;
                }
                catch (DestinationNotFoundException)
                {
                    throw;
                }
                catch (MessageTooLargeException e)
                {
                    failures.Add(new SendFailure(message.MessageId, e));
                }
            }
        }

        return failures.Count == 0 ? ValueTask.CompletedTask : throw new TransportSendException(destination, failures, sent);
    }

    private sealed class Receiver(InMemoryBroker broker, string endpoint, bool deadLetters, TimeSpan lockDuration) : ITransportReceiver
    {
        private readonly long _id = broker.NewReceiverId();

        public ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
        {
            return broker.ReceiveAsync(endpoint, deadLetters, _id, lockDuration, maxMessages, wait, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            broker.Release(_id);
            return ValueTask.CompletedTask;
        }
    }
}
