using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using SourceFlow.Messaging;
using SourceFlow.Transport;

namespace SourceFlow.Cloud.Azure;

/// <summary>
/// What every Service Bus receiver shares: decoding, client-side filters, the messages it holds unsettled, and lock
/// housekeeping. A message stays locked for the receiver's lock duration: when that is longer than the queue's lock, the
/// broker lock is renewed until it passes (a process that dies still loses its locks after the queue's lock duration).
/// </summary>
internal abstract class AzureReceiverBase : ITransportReceiver
{
    /// <summary>The reason Service Bus gives a message it dead-lettered after the delivery limit.</summary>
    public const string MaxDeliveriesReason = "MaxDeliveryCountExceeded";

    /// <summary>The reason of a message that is not a SourceFlow CloudEvent.</summary>
    public const string MalformedReason = "MalformedMessage";

    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyDictionary<string, CloudEventFilter> _clientFilters;
    private readonly ConcurrentDictionary<string, AzureDelivery> _unsettled = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _housekeeping;
    private int _disposed;

    protected AzureReceiverBase(AzureTransport transport, string endpoint, bool deadLetters, TimeSpan lockDuration, IReadOnlyDictionary<string, CloudEventFilter> clientFilters)
    {
        Transport = transport;
        Endpoint = endpoint;
        IsDeadLetterReceiver = deadLetters;
        LockDuration = lockDuration;
        _clientFilters = clientFilters;
        _housekeeping = Task.Run(HousekeepAsync);
    }

    public AzureTransport Transport { get; }

    public string Endpoint { get; }

    public bool IsDeadLetterReceiver { get; }

    public TimeSpan LockDuration { get; }

    public string Destination => IsDeadLetterReceiver ? Endpoint + " (dead-letter queue)" : Endpoint;

    protected CancellationToken Stopping => _stopping.Token;

    public abstract ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _housekeeping.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped
        }

        // Release what this receiver still holds, so a graceful stop redelivers at once instead of after the lock.
        foreach (AzureDelivery delivery in _unsettled.Values)
        {
            await TryAsync(() => delivery.Receiver.AbandonMessageAsync(delivery.Message)).ConfigureAwait(false);
        }

        _unsettled.Clear();
        await DisposeReceiversAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    internal void Settled(AzureDelivery delivery)
    {
        if (_unsettled.TryRemove(delivery.Message.LockToken, out _))
        {
            delivery.Slot?.Release();
        }
    }

    /// <summary>
    /// A delivery whose settlement failed, or that a failed receive never handed out (CR-09). It is forgotten and its
    /// session hold released, so the key is not blocked until a restart. A session is closed, which ends its lock and
    /// lets the broker hand the session (with this message) out again; a queue message is abandoned, best effort.
    /// </summary>
    internal async Task SettleFailedAsync(AzureDelivery delivery)
    {
        if (!_unsettled.TryRemove(delivery.Message.LockToken, out _))
        {
            return;
        }

        if (delivery.Slot is { } slot)
        {
            slot.Release();
            await DropSessionAsync(slot).ConfigureAwait(false);
        }
        else
        {
            await TryAsync(() => delivery.Receiver.AbandonMessageAsync(delivery.Message)).ConfigureAwait(false);
        }
    }

    protected abstract ValueTask DisposeReceiversAsync();

    /// <summary>Closes a session the receiver can no longer settle reliably.</summary>
    protected virtual Task DropSessionAsync(SessionSlot slot)
    {
        return Task.CompletedTask;
    }

    /// <summary>Settles, as <see cref="SettleFailedAsync"/> does, the deliveries a receive accepted before it failed.</summary>
    protected async Task ReleaseAcceptedAsync(IEnumerable<ReceivedMessage> accepted)
    {
        foreach (AzureDelivery delivery in accepted.Cast<AzureDelivery>())
        {
            await SettleFailedAsync(delivery).ConfigureAwait(false);
        }
    }

    /// <summary>Renews the session locks of sessions with messages in flight.</summary>
    protected virtual Task RenewSessionsAsync(DateTimeOffset now)
    {
        return Task.CompletedTask;
    }

    /// <summary>Turns a received message into a delivery, or settles it here (not a CloudEvent, filtered out).</summary>
    protected async Task<AzureDelivery?> AcceptAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage message, SessionSlot? slot, CancellationToken cancellationToken)
    {
        MessageEnvelope envelope;
        try
        {
            envelope = AzureWire.Decode(message);
        }
        catch (FormatException e)
        {
            // Not a SourceFlow CloudEvent, and never will be. An endpoint dead-letters it; the dead-letter queue defers it,
            // so the ingestor moves on (deferred messages stay for inspection by sequence number).
            await AzureErrors.CallAsync(Destination, () => IsDeadLetterReceiver
                ? receiver.DeferMessageAsync(message, cancellationToken: cancellationToken)
                : receiver.DeadLetterMessageAsync(message, MalformedReason, e.Message, cancellationToken)).ConfigureAwait(false);
            return null;
        }

        if (message.ApplicationProperties.TryGetValue(AzureWire.TopicProperty, out object? topic) && topic is string name
            && _clientFilters.TryGetValue(name, out CloudEventFilter? filter) && !filter.Matches(envelope))
        {
            await AzureErrors.CallAsync(Destination, () => receiver.CompleteMessageAsync(message, cancellationToken)).ConfigureAwait(false);   // filtered out: acknowledged unseen
            return null;
        }

        int deliveries = message.DeliveryCount + AzureWire.PreviousDeliveries(message.ApplicationProperties);
        string? reason = IsDeadLetterReceiver ? message.DeadLetterReason ?? MaxDeliveriesReason : null;
        var delivery = new AzureDelivery(this, receiver, message, envelope, deliveries, reason, slot) { RequestedUntil = TimeProvider.System.GetUtcNow() + LockDuration };
        _unsettled[message.LockToken] = delivery;
        slot?.Hold();
        return delivery;
    }

    protected static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception e) when (e is ServiceBusException or TransportException or ObjectDisposedException or InvalidOperationException)
        {
            // Best effort: locks expire on their own.
        }
    }

    // Renews broker locks while a receiver's own lock lasts, and releases messages held for a delayed retry when due.
    private async Task HousekeepAsync()
    {
        using var timer = new PeriodicTimer(HousekeepingInterval, TimeProvider.System);
        while (await timer.WaitForNextTickAsync(Stopping).ConfigureAwait(false))
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            foreach (AzureDelivery delivery in _unsettled.Values)
            {
                if (delivery.DelayedUntil is { } due && now >= due)
                {
                    await TryAsync(() => delivery.Receiver.AbandonMessageAsync(delivery.Message)).ConfigureAwait(false);
                    Settled(delivery);
                }
                else if ((delivery.DelayedUntil is not null || now < delivery.RequestedUntil) && delivery.Message.LockedUntil - now < RenewMargin)
                {
                    await TryAsync(() => delivery.Receiver.RenewMessageLockAsync(delivery.Message)).ConfigureAwait(false);
                }
            }

            await RenewSessionsAsync(now).ConfigureAwait(false);
        }
    }
}

/// <summary>A received Service Bus message.</summary>
internal sealed class AzureDelivery(AzureReceiverBase owner, ServiceBusReceiver receiver, ServiceBusReceivedMessage message, MessageEnvelope envelope, int deliveries, string? reason, SessionSlot? slot) : ReceivedMessage
{
    public ServiceBusReceiver Receiver => receiver;

    public ServiceBusReceivedMessage Message => message;

    public SessionSlot? Slot => slot;

    /// <summary>Until when the receiver's own lock lasts (the broker lock is renewed until then).</summary>
    public DateTimeOffset RequestedUntil { get; set; }

    /// <summary>For a delayed retry on a session: when to return the message (until then it holds the session).</summary>
    public DateTimeOffset? DelayedUntil { get; private set; }

    public override MessageEnvelope Envelope => envelope;

    public override int DeliveryCount => deliveries;

    public override string? DeadLetterReason => reason;

    public override ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        return SettleAsync(() => LateSettlementIgnoredAsync(() => receiver.CompleteMessageAsync(message, cancellationToken)));
    }

    public override async ValueTask AbandonAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero || owner.IsDeadLetterReceiver)
        {
            await SettleAsync(() => LateSettlementIgnoredAsync(() => receiver.AbandonMessageAsync(message, cancellationToken: cancellationToken))).ConfigureAwait(false);
            return;
        }

        TimeSpan wait = delay < AzureTransport.MaxDelay ? delay : AzureTransport.MaxDelay;
        if (slot is not null)
        {
            // An ordered message keeps its place: it holds its session (and its lock) until the delay passes.
            DelayedUntil = TimeProvider.System.GetUtcNow() + wait;
            return;
        }

        // A scheduled copy carries the deliveries so far; the original is completed (a crash in between duplicates it,
        // which the inbox absorbs).
        ServiceBusSender sender = owner.Transport.Sender(AzureNaming.Queue(owner.Transport.Prefix, owner.Endpoint));
        await SettleAsync(async () =>
        {
            await AzureErrors.CallAsync(owner.Destination, () => sender.ScheduleMessageAsync(AzureWire.Retry(message, deliveries), TimeProvider.System.GetUtcNow() + wait, cancellationToken)).ConfigureAwait(false);
            await LateSettlementIgnoredAsync(() => receiver.CompleteMessageAsync(message, cancellationToken)).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public override async ValueTask RenewAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AzureErrors.CallAsync(owner.Destination, () => receiver.RenewMessageLockAsync(message, cancellationToken)).ConfigureAwait(false);
        }
        catch (TransportException e) when (AzureErrors.IsLockLost(e))
        {
            throw new TransportException("The message lock was lost (it expired and the message was redelivered).", transient: false, e);
        }

        RequestedUntil = TimeProvider.System.GetUtcNow() + owner.LockDuration;
    }

    public override async ValueTask DeadLetterAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (owner.IsDeadLetterReceiver)
        {
            throw new TransportException("A message on a dead-letter queue cannot be dead-lettered again.", transient: false);
        }

        await SettleAsync(() => AzureErrors.CallAsync(owner.Destination, () => receiver.DeadLetterMessageAsync(message, reason, deadLetterErrorDescription: null, cancellationToken))).ConfigureAwait(false);
    }

    // Settles, and on failure releases the hold this delivery has on its session before the error goes on (CR-09).
    private async ValueTask SettleAsync(Func<Task> settle)
    {
        try
        {
            await settle().ConfigureAwait(false);
        }
        catch (Exception)
        {
            await owner.SettleFailedAsync(this).ConfigureAwait(false);
            throw;
        }

        owner.Settled(this);
    }

    // A settlement after the lock expired is ignored: the message was, or will be, redelivered.
    private async Task LateSettlementIgnoredAsync(Func<Task> settle)
    {
        try
        {
            await AzureErrors.CallAsync(owner.Destination, settle).ConfigureAwait(false);
        }
        catch (TransportException e) when (AzureErrors.IsLockLost(e))
        {
        }
        catch (ObjectDisposedException) when (slot is not null)
        {
            // The session was lost and its receiver closed: the message comes back with the session.
        }
    }
}

/// <summary>Receives from a queue without sessions (an unordered endpoint, or any endpoint's dead-letter queue).</summary>
internal sealed class AzureQueueReceiver(AzureTransport transport, string endpoint, ServiceBusReceiver receiver, bool deadLetters, TimeSpan lockDuration, IReadOnlyDictionary<string, CloudEventFilter> clientFilters)
    : AzureReceiverBase(transport, endpoint, deadLetters, lockDuration, clientFilters)
{
    // A zero wait still gives the broker a moment to answer.
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(100);

    public ServiceBusReceiver Receiver => receiver;

    public override async ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        IReadOnlyList<ServiceBusReceivedMessage> messages = await AzureErrors.CallAsync(Destination, () =>
            receiver.ReceiveMessagesAsync(Math.Clamp(maxMessages, 1, 100), wait < MinimumWait ? MinimumWait : wait, cancellationToken)).ConfigureAwait(false);
        var received = new List<ReceivedMessage>(messages.Count);
        int next = 0;
        try
        {
            for (; next < messages.Count; next++)
            {
                if (await AcceptAsync(receiver, messages[next], slot: null, cancellationToken).ConfigureAwait(false) is { } delivery)
                {
                    received.Add(delivery);
                }
            }
        }
        catch (Exception)
        {
            // Nothing this receive took stays locked unseen (CR-42): the accepted ones, the failing one and the rest.
            await ReleaseAcceptedAsync(received).ConfigureAwait(false);
            foreach (ServiceBusReceivedMessage left in messages.Skip(next))
            {
                await TryAsync(() => receiver.AbandonMessageAsync(left)).ConfigureAwait(false);
            }

            throw;
        }

        return received;
    }

    protected override ValueTask DisposeReceiversAsync()
    {
        return receiver.DisposeAsync();
    }
}

/// <summary>A session (partition key) a session receiver works on.</summary>
internal sealed class SessionSlot(ServiceBusSessionReceiver receiver)
{
    private int _held;

    public ServiceBusSessionReceiver Receiver => receiver;

    /// <summary>True while a message of the session is unsettled: the next one waits (per-key order, head of line).</summary>
    public bool Busy => Volatile.Read(ref _held) > 0;

    public void Hold()
    {
        Interlocked.Increment(ref _held);
    }

    public void Release()
    {
        Interlocked.Decrement(ref _held);
    }
}

/// <summary>
/// Receives from a session queue (an ordered endpoint). It works on up to
/// <see cref="AzureTransportOptions.MaxConcurrentSessions"/> sessions, accepting the next free one in the background, and
/// takes messages from a session only while none of that session's messages is unsettled, so a key's next message waits
/// for its head across every instance. An idle session is released.
/// </summary>
internal sealed class AzureSessionReceiver(AzureTransport transport, string endpoint, string queue, TimeSpan lockDuration, IReadOnlyDictionary<string, CloudEventFilter> clientFilters)
    : AzureReceiverBase(transport, endpoint, deadLetters: false, lockDuration, clientFilters)
{
    private static readonly TimeSpan SessionReceiveWait = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(2);

    private readonly List<SessionSlot> _slots = [];
    private readonly object _gate = new();
    private Task<ServiceBusSessionReceiver?>? _accepting;
    private Exception? _acceptError;

    public override async ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
    {
        int max = Math.Clamp(maxMessages, 1, 100);
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + (wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
        while (true)
        {
            Harvest();
            var received = new List<ReceivedMessage>();
            SessionSlot[] slots;
            lock (_gate)
            {
                slots = [.. _slots];
            }

            foreach (SessionSlot slot in slots)
            {
                if (received.Count >= max)
                {
                    break;
                }

                if (slot.Busy)
                {
                    continue;
                }

                IReadOnlyList<ServiceBusReceivedMessage> messages;
                bool lost = false;
                try
                {
                    messages = await AzureErrors.CallAsync(Destination, () => slot.Receiver.ReceiveMessagesAsync(max - received.Count, SessionReceiveWait, cancellationToken)).ConfigureAwait(false);
                }
                catch (Exception e) when ((e is TransportException && AzureErrors.IsLockLost(e)) || e is ObjectDisposedException)
                {
                    messages = [];   // the session lock lapsed and the client closed the session: release it
                    lost = true;
                }

                if (messages.Count == 0)
                {
                    if (lost || slot.Receiver.IsClosed || await IsDrainedAsync(slot, cancellationToken).ConfigureAwait(false))
                    {
                        await ReleaseAsync(slot).ConfigureAwait(false);   // empty: let another instance (or a later accept) have it
                    }

                    continue;
                }

                try
                {
                    foreach (ServiceBusReceivedMessage message in messages)
                    {
                        if (await AcceptAsync(slot.Receiver, message, slot, cancellationToken).ConfigureAwait(false) is { } delivery)
                        {
                            received.Add(delivery);
                        }
                    }
                }
                catch (Exception)
                {
                    // Nothing this receive took stays held (CR-42): the sessions of the accepted messages are closed, and
                    // so is this one, which returns the failing message and the rest of its batch.
                    await ReleaseAcceptedAsync(received).ConfigureAwait(false);
                    await DropSessionAsync(slot).ConfigureAwait(false);
                    throw;
                }
            }

            if (received.Count > 0)
            {
                return received;
            }

            if (Interlocked.Exchange(ref _acceptError, null) is { } error)
            {
                throw error;
            }

            TimeSpan remaining = deadline - TimeProvider.System.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return received;
            }

            Task pause = Task.Delay(remaining < Poll ? remaining : Poll, TimeProvider.System, cancellationToken);
            await (_accepting is { } accepting ? Task.WhenAny(pause, accepting) : pause).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    protected override async Task RenewSessionsAsync(DateTimeOffset now)
    {
        SessionSlot[] slots;
        lock (_gate)
        {
            slots = [.. _slots];
        }

        // Only sessions with deliveries in flight: an idle one is not kept alive here, so it lapses (and goes to another
        // instance) even when the receive loop has stopped calling (CR-09).
        foreach (SessionSlot slot in slots.Where(s => s.Busy && !s.Receiver.IsClosed && s.Receiver.SessionLockedUntil - now < RenewMargin))
        {
            await TryAsync(() => slot.Receiver.RenewSessionLockAsync()).ConfigureAwait(false);
        }
    }

    protected override async Task DropSessionAsync(SessionSlot slot)
    {
        lock (_gate)
        {
            _slots.Remove(slot);
        }

        await TryAsync(() => slot.Receiver.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    protected override async ValueTask DisposeReceiversAsync()
    {
        Task<ServiceBusSessionReceiver?>? accepting = _accepting;
        if (accepting is not null)
        {
            try
            {
                if (await accepting.ConfigureAwait(false) is { } late)
                {
                    await late.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ServiceBusException or TransportException)
            {
                // cancelled by the stop
            }
        }

        SessionSlot[] slots;
        lock (_gate)
        {
            slots = [.. _slots];
            _slots.Clear();
        }

        foreach (SessionSlot slot in slots)
        {
            await slot.Receiver.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Collects a finished accept and starts the next one while there is room for another session.
    private void Harvest()
    {
        if (_accepting is { IsCompleted: true } done)
        {
            _accepting = null;
            if (done.IsCompletedSuccessfully && done.Result is { } session)
            {
                lock (_gate)
                {
                    _slots.Add(new SessionSlot(session));
                }
            }
        }

        int held;
        lock (_gate)
        {
            held = _slots.Count;
        }

        if (_accepting is null && held < Transport.Options.MaxConcurrentSessions && !Stopping.IsCancellationRequested)
        {
            _accepting = AcceptNextAsync();
        }
    }

    // Waits (up to the client's try timeout) for a free session in the background; none is not an error.
    private async Task<ServiceBusSessionReceiver?> AcceptNextAsync()
    {
        try
        {
            return await AzureErrors.CallAsync(Destination, () => Transport.Client.AcceptNextSessionAsync(queue, new ServiceBusSessionReceiverOptions { PrefetchCount = 0 }, Stopping)).ConfigureAwait(false);
        }
        catch (TransportUnavailableException e) when (e.InnerException is ServiceBusException { Reason: ServiceBusFailureReason.ServiceTimeout })
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TransportException e)
        {
            _acceptError = e;
            return null;
        }
    }

    // True when the session holds no message, locked or not. An empty receive alone is not enough: under load a short
    // receive can come back empty while the session still has messages, and a message already on its way to a link that
    // is then closed stays locked until its lock expires, while another instance takes the session and handles the key's
    // later messages first (seen on the Service Bus emulator, TCK-13). Peeking sees locked messages too.
    private async Task<bool> IsDrainedAsync(SessionSlot slot, CancellationToken cancellationToken)
    {
        try
        {
            return await AzureErrors.CallAsync(Destination, () => slot.Receiver.PeekMessageAsync(cancellationToken: cancellationToken)).ConfigureAwait(false) is null;
        }
        catch (Exception e) when ((e is TransportException && AzureErrors.IsLockLost(e)) || e is ObjectDisposedException)
        {
            return true;   // the session is no longer ours: release the slot
        }
    }

    private async Task ReleaseAsync(SessionSlot slot)
    {
        lock (_gate)
        {
            if (slot.Busy || !_slots.Remove(slot))
            {
                return;
            }
        }

        await TryAsync(() => slot.Receiver.DisposeAsync().AsTask()).ConfigureAwait(false);
    }
}
