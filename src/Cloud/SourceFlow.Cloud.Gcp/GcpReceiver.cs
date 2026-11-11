using System.Collections.Concurrent;
using Google.Cloud.PubSub.V1;
using SourceFlow.Messaging;
using SourceFlow.Transport;
using PubSubReceivedMessage = Google.Cloud.PubSub.V1.ReceivedMessage;
using ReceivedMessage = SourceFlow.Transport.ReceivedMessage;

namespace SourceFlow.Cloud.Gcp;

/// <summary>
/// Receives from the subscriptions of one endpoint (or from its dead-letter subscription) with synchronous pulls, taking
/// them in turn so none starves. A received message is leased for the receiver's lock duration (its ack deadline);
/// completing acknowledges it, abandoning sets its ack deadline to the delay (native delay up to 10 minutes), renewing
/// extends it. A message delivered more often than the endpoint's limit, or that is not a SourceFlow CloudEvent, is moved
/// to the dead-letter topic by the receiver. Disposing releases what the receiver holds unsettled at once.
/// </summary>
/// <remarks>
/// Synchronous pull, not streaming pull with automatic lease extension: the lease must lapse when a process dies holding
/// a message (TCK-22), and each receiver has its own lock duration. Pulls return immediately; while nothing arrives the
/// receiver pulls again every <see cref="GcpTransportOptions.PollInterval"/> until the wait is over.
/// </remarks>
internal sealed class GcpReceiver : ITransportReceiver
{
    /// <summary>The reason of a message the delivery limit moved to the dead-letter topic.</summary>
    public const string MaxDeliveriesReason = "MaxDeliveryAttemptsExceeded";

    private const int MaxAckDeadlineSeconds = 600;
    private const int AckIdsPerCall = 1000;

    private readonly GcpTransport _transport;
    private readonly string _endpoint;
    private readonly IReadOnlyList<GcpSubscription> _subscriptions;
    private readonly TopicName? _deadLetters;
    private readonly ConcurrentDictionary<string, GcpSubscription> _unsettled = new(StringComparer.Ordinal);
    private int _turn = -1;

    public GcpReceiver(GcpTransport transport, string endpoint, IReadOnlyList<GcpSubscription> subscriptions, TopicName? deadLetters, bool isDeadLetterReceiver, TimeSpan lockDuration)
    {
        _transport = transport;
        _endpoint = endpoint;
        _subscriptions = subscriptions;
        _deadLetters = deadLetters;
        IsDeadLetterReceiver = isDeadLetterReceiver;
        LockDuration = TimeSpan.FromSeconds(Seconds(lockDuration, minimum: 1));
    }

    public bool IsDeadLetterReceiver { get; }

    public TimeSpan LockDuration { get; }

    public IReadOnlyList<GcpSubscription> Subscriptions => _subscriptions;

    private string Destination => IsDeadLetterReceiver ? _endpoint + " (dead-letter subscription)" : _endpoint;

    public async ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        int max = Math.Clamp(maxMessages, 1, 1000);
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + (wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
        while (true)
        {
            List<ReceivedMessage> received = await PullRoundAsync(max, cancellationToken).ConfigureAwait(false);
            TimeSpan remaining = deadline - TimeProvider.System.GetUtcNow();
            if (received.Count > 0 || remaining <= TimeSpan.Zero)
            {
                return received;
            }

            TimeSpan pause = remaining < _transport.Options.PollInterval ? remaining : _transport.Options.PollInterval;
            await Task.Delay(pause, TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Release what this receiver still holds, so a graceful stop redelivers at once instead of after the lock.
        KeyValuePair<string, GcpSubscription>[] held = [.. _unsettled];
        _unsettled.Clear();
        foreach (IGrouping<GcpSubscription, string> group in held.GroupBy(h => h.Value, h => h.Key))
        {
            try
            {
                await SetDeadlineAsync(group.Key, [.. group], Release(group.Key), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TransportException)
            {
                // Best effort: the leases expire on their own.
            }
        }
    }

    internal static int Seconds(TimeSpan duration, int minimum)
    {
        return (int)Math.Clamp(Math.Ceiling(duration.TotalSeconds), minimum, MaxAckDeadlineSeconds);
    }

    // The deadline that returns a message at once. On an ordered subscription it is one second: the Pub/Sub emulator
    // redelivers a nacked (deadline 0) ordered message after its successor, while an expiring lease keeps the order.
    internal static int Release(GcpSubscription subscription)
    {
        return subscription.Ordered ? 1 : 0;
    }

    internal void Settled(string ackId)
    {
        _unsettled.TryRemove(ackId, out _);
    }

    internal Task AcknowledgeAsync(GcpSubscription subscription, IReadOnlyList<string> ackIds, CancellationToken cancellationToken)
    {
        var request = new AcknowledgeRequest { SubscriptionAsSubscriptionName = subscription.Name };
        request.AckIds.AddRange(ackIds);

        // An ack of an expired lease is ignored by Pub/Sub: a late settlement is harmless.
        return GcpErrors.CallAsync(Destination, () => _transport.Subscriber.AcknowledgeAsync(request, _transport.Call(cancellationToken)), cancellationToken);
    }

    internal async Task SetDeadlineAsync(GcpSubscription subscription, IReadOnlyList<string> ackIds, int seconds, CancellationToken cancellationToken)
    {
        foreach (string[] chunk in ackIds.Chunk(AckIdsPerCall))
        {
            var request = new ModifyAckDeadlineRequest { SubscriptionAsSubscriptionName = subscription.Name, AckDeadlineSeconds = seconds };
            request.AckIds.AddRange(chunk);
            await GcpErrors.CallAsync(Destination, () => _transport.Subscriber.ModifyAckDeadlineAsync(request, _transport.Call(cancellationToken)), cancellationToken).ConfigureAwait(false);
        }
    }

    // Publishes a copy with the reason to the dead-letter topic, then acknowledges the original.
    internal async Task DeadLetterAsync(GcpSubscription subscription, PubSubReceivedMessage message, string reason, CancellationToken cancellationToken)
    {
        if (IsDeadLetterReceiver || _deadLetters is null)
        {
            throw new TransportException("A message on a dead-letter subscription cannot be dead-lettered again.", transient: false);
        }

        var request = new PublishRequest { TopicAsTopicName = _deadLetters };
        request.Messages.Add(GcpWire.ForDeadLetter(message.Message, reason));
        await GcpErrors.CallAsync(Destination + " (dead-letter topic)", () => _transport.Publisher.PublishAsync(request, _transport.Call(cancellationToken)), cancellationToken).ConfigureAwait(false);
        await AcknowledgeAsync(subscription, [message.AckId], cancellationToken).ConfigureAwait(false);
    }

    // One pull from each subscription in turn (starting with the next one each round), up to the maximum in total.
    private async Task<List<ReceivedMessage>> PullRoundAsync(int max, CancellationToken cancellationToken)
    {
        var received = new List<ReceivedMessage>();
        int start = Interlocked.Increment(ref _turn);
        for (int i = 0; i < _subscriptions.Count && received.Count < max; i++)
        {
            GcpSubscription subscription = _subscriptions[(int)((uint)(start + i) % (uint)_subscriptions.Count)];
#pragma warning disable CS0612, CS0618 // ReturnImmediately: the receiver bounds the wait itself, and a cancelled long pull can strand leased messages
            var request = new PullRequest { SubscriptionAsSubscriptionName = subscription.Name, MaxMessages = max - received.Count, ReturnImmediately = true };
#pragma warning restore CS0612, CS0618
            PullResponse response = await GcpErrors.CallAsync(Destination, () => _transport.Subscriber.PullAsync(request, _transport.Call(cancellationToken)), cancellationToken).ConfigureAwait(false);

            var accepted = new List<GcpDelivery>();
            foreach (PubSubReceivedMessage message in response.ReceivedMessages)
            {
                if (await AcceptAsync(subscription, message, cancellationToken).ConfigureAwait(false) is { } delivery)
                {
                    accepted.Add(delivery);
                }
            }

            if (accepted.Count == 0)
            {
                continue;
            }

            // The subscription's ack deadline applies until the receiver sets its own lock.
            int lockSeconds = (int)LockDuration.TotalSeconds;
            if (lockSeconds != subscription.AckDeadlineSeconds)
            {
                await SetDeadlineAsync(subscription, [.. accepted.Select(d => d.AckId)], lockSeconds, cancellationToken).ConfigureAwait(false);
            }

            DateTimeOffset lockedUntil = TimeProvider.System.GetUtcNow() + LockDuration;
            foreach (GcpDelivery delivery in accepted)
            {
                delivery.LockedUntil = lockedUntil;
                _unsettled[delivery.AckId] = subscription;
            }

            received.AddRange(accepted);
        }

        return received;
    }

    private async Task<GcpDelivery?> AcceptAsync(GcpSubscription subscription, PubSubReceivedMessage message, CancellationToken cancellationToken)
    {
        // Delivery attempts are counted when the subscription has a dead-letter policy; otherwise each delivery is a first.
        int deliveries = Math.Max(1, message.DeliveryAttempt);
        if (!IsDeadLetterReceiver && deliveries > subscription.MaxDeliveries)
        {
            // Pub/Sub dead-letters after 5 deliveries at the least; smaller limits, and a dead-letter policy without the
            // publisher grant, are enforced here.
            await DeadLetterAsync(subscription, message, MaxDeliveriesReason, cancellationToken).ConfigureAwait(false);
            return null;
        }

        MessageEnvelope envelope;
        try
        {
            envelope = GcpWire.Decode(message.Message);
        }
        catch (FormatException e)
        {
            // Not a SourceFlow CloudEvent, and never will be. An endpoint moves it to the dead-letter topic; the
            // dead-letter subscription hides it for the longest lease so the ingestor moves on.
            if (IsDeadLetterReceiver)
            {
                await SetDeadlineAsync(subscription, [message.AckId], MaxAckDeadlineSeconds, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeadLetterAsync(subscription, message, "MalformedMessage: " + e.Message, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        if (subscription.ClientFilter is { } filter && !filter.Matches(envelope))
        {
            await AcknowledgeAsync(subscription, [message.AckId], cancellationToken).ConfigureAwait(false);   // filtered out: acknowledged unseen
            return null;
        }

        string? reason = IsDeadLetterReceiver
            ? message.Message.Attributes.TryGetValue(GcpWire.ReasonAttribute, out string? explicitReason) ? explicitReason : MaxDeliveriesReason
            : null;
        return new GcpDelivery(this, subscription, message, envelope, deliveries, reason);
    }

    private sealed class GcpDelivery(GcpReceiver receiver, GcpSubscription subscription, PubSubReceivedMessage message, MessageEnvelope envelope, int deliveries, string? reason) : ReceivedMessage
    {
        public string AckId => message.AckId;

        public DateTimeOffset LockedUntil { get; set; }

        public override MessageEnvelope Envelope => envelope;

        public override int DeliveryCount => deliveries;

        public override string? DeadLetterReason => reason;

        public override async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            await receiver.AcknowledgeAsync(subscription, [AckId], cancellationToken).ConfigureAwait(false);
            receiver.Settled(AckId);
        }

        public override async ValueTask AbandonAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            int seconds = delay <= TimeSpan.Zero ? Release(subscription) : Seconds(delay, Release(subscription));
            await receiver.SetDeadlineAsync(subscription, [AckId], seconds, cancellationToken).ConfigureAwait(false);
            receiver.Settled(AckId);
        }

        public override async ValueTask RenewAsync(CancellationToken cancellationToken)
        {
            // Pub/Sub accepts a deadline change on an expired lease silently, so a lapsed lock is detected here.
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            if (now >= LockedUntil)
            {
                throw new TransportException("The message lock was lost (its ack deadline passed and the message may have been redelivered).", transient: false);
            }

            await receiver.SetDeadlineAsync(subscription, [AckId], (int)receiver.LockDuration.TotalSeconds, cancellationToken).ConfigureAwait(false);
            LockedUntil = now + receiver.LockDuration;
        }

        public override async ValueTask DeadLetterAsync(string reason, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            await receiver.DeadLetterAsync(subscription, message, reason, cancellationToken).ConfigureAwait(false);
            receiver.Settled(AckId);
        }
    }
}
