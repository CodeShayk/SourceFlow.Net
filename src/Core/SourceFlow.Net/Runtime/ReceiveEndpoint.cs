using System.IO.Hashing;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Pipeline;
using SourceFlow.Storage;
using SourceFlow.Transport;

namespace SourceFlow.Runtime;

/// <summary>
/// The single receiver of the service endpoint (RX-R5). Messages are dispatched to workers by partition key
/// (RX-R1, per-key order), through bounded channels (RX-R2), and settled only after the handler commits (RX-R3).
/// Transient failures retry immediately, then with the delayed schedule (doc 04 §9.2); permanent failures and
/// exhausted retries are parked (doc 04 §9.3). On an ordered endpoint a key's next message never overtakes its head:
/// while the head waits for a delayed retry, the messages of its key already received are released, not handled
/// (ADR-033 §7). On stop it stops receiving, finishes the messages in progress and releases the queued ones (RX-R6).
/// </summary>
public sealed class ReceiveEndpoint : IAsyncDisposable
{
    private readonly SourceFlowRuntime _runtime;
    private readonly MessageDispatcher _dispatcher;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;
    private readonly Channel<Delivery>[] _channels;
    private readonly IMessageMiddleware[] _builtIns;
    private readonly CancellationTokenSource _abort = new();
    private ITransportReceiver? _receiver;
    private Transport.CloudEventFilter[]? _clientFilters;
    private Task[] _workers = [];
    private int _inFlight;
    private long _received;
    private volatile bool _draining;
    private int _disposed;

    /// <summary>Creates the endpoint.</summary>
    public ReceiveEndpoint(SourceFlowRuntime runtime, CommandProcessor commands, IServiceScopeFactory scopes)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _dispatcher = new MessageDispatcher(runtime, commands);
        _logger = runtime.Logger<ReceiveEndpoint>();
        _channels = [.. Enumerable.Range(0, runtime.Options.ReceiveConcurrency).Select(_ =>
            Channel.CreateBounded<Delivery>(new BoundedChannelOptions(runtime.Options.PrefetchPerWorker) { SingleReader = true, SingleWriter = true }))];
        _builtIns =
        [
            new TelemetryMiddleware(runtime.Metrics, runtime.Routing.Endpoint, runtime.Options.Telemetry.ParentBasedOnMessage),
            new LoggingScopeMiddleware(_logger),
            new ErrorClassificationMiddleware(runtime.Classifier),
            .. runtime.Protector is { } protector ? [new Security.PayloadProtectionMiddleware(protector, runtime.Routing.Endpoint)] : Array.Empty<IMessageMiddleware>(),
        ];
    }

    /// <summary>Messages received and not yet settled.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Opens the receiver and starts the workers.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SubscriptionRoute> subscriptions = _runtime.Routing.Subscriptions;
        var options = new ReceiverOptions(_runtime.Routing.Endpoint, [.. subscriptions.Select(s => s.Topic)])
        {
            Filters = subscriptions.Where(s => s.Filter is not null).ToDictionary(s => s.Topic, s => s.Filter!, StringComparer.Ordinal),
            LockDuration = _runtime.Options.ReceiveLockDuration,
        };

        // Without native filters every subscribed event arrives; drop those no subscription's filter accepts (UT-RX-22).
        _clientFilters = !_runtime.Transport.Capabilities.SubscriptionFilters && subscriptions.Count > 0 && subscriptions.All(s => s.Filter is not null)
            ? [.. subscriptions.Select(s => Transport.CloudEventFilter.Parse(s.Filter!))]
            : null;
        _receiver = await _runtime.Transport.CreateReceiverAsync(options, cancellationToken).ConfigureAwait(false);

        // Handlers run under their own token: stopping the receive loop doesn't cancel the messages in progress (RX-R6).
        _workers = [.. _channels.Select(channel => Task.Run(() => WorkAsync(channel.Reader, _abort.Token), CancellationToken.None))];
    }

    /// <summary>Receives until cancelled (the hosted service's receive loop).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ITransportReceiver receiver = _receiver ?? throw new InvalidOperationException("StartAsync must be called first.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    IReadOnlyList<ReceivedMessage> batch = await receiver.ReceiveAsync(_runtime.Options.PrefetchPerWorker, _runtime.Options.PollInterval, cancellationToken).ConfigureAwait(false);
                    foreach (ReceivedMessage message in batch)
                    {
                        if (FilteredOut(message))
                        {
                            await message.CompleteAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        Interlocked.Increment(ref _inFlight);
                        _runtime.Metrics.ConsumedMessages.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.ConsumerGroup, _runtime.Routing.Endpoint));
                        await _channels[WorkerOf(message)].Writer.WriteAsync(new Delivery(message, Interlocked.Increment(ref _received)), cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Log.ReceiveLoopFailed(_logger, exception, _runtime.Routing.Endpoint);
                    await Task.Delay(_runtime.Options.PollInterval, TimeProvider.System, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach (Channel<Delivery> channel in _channels)
            {
                channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Starts draining (RX-R6): the messages in progress finish, and those still queued are released with no delay, so
    /// the broker redelivers them at once (to another instance). Stop the receive loop as well.
    /// </summary>
    public void BeginDrain()
    {
        _draining = true;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the workers to finish (RX-R6). After it, the handlers still running are
    /// cancelled; their messages are redelivered after their lock, and the inbox absorbs any that had committed.
    /// </summary>
    public async ValueTask DrainAsync(TimeSpan timeout)
    {
        _draining = true;
        try
        {
            await Task.WhenAll(_workers).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _abort.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;   // the host and the container both dispose the endpoint
        }

        await _abort.CancelAsync().ConfigureAwait(false);
        _abort.Dispose();
        if (_receiver is not null)
        {
            await _receiver.DisposeAsync().ConfigureAwait(false);
        }
    }

    // A received message and its receive sequence (per-key order on retries).
    private readonly record struct Delivery(ReceivedMessage Message, long Sequence);

    // An event no subscription filter accepts (client-side filtering, for brokers without native filters).
    private bool FilteredOut(ReceivedMessage message)
    {
        if (_clientFilters is null)
        {
            return false;
        }

        Messaging.MessageEnvelope envelope = message.Envelope;
        bool isEvent = _runtime.Model.Registry.TryGet(envelope.Type, envelope.TypeVersion, out Messaging.MessageTypeInfo? type) && type.Kind == MessageKind.Event;
        return isEvent && !Array.Exists(_clientFilters, f => f.Matches(envelope));
    }

    private int WorkerOf(ReceivedMessage message)
    {
        string key = message.Envelope.EffectivePartitionKey ?? message.Envelope.MessageId.ToString();
        return (int)(XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(key)) % (uint)_channels.Length);
    }

    private async Task WorkAsync(ChannelReader<Delivery> reader, CancellationToken cancellationToken)
    {
        // Keys whose head waits for a delayed retry: the receive sequence reached when it was released, and its delay. The
        // key's messages received up to then came after the head and must not overtake it (ADR-033 §7, CR-08), so they
        // are released with the same delay: they never become visible before the head, whether or not the broker holds
        // the key back while the head waits (an emulator may not).
        var held = new Dictionary<string, (long UpTo, TimeSpan Delay)>(StringComparer.Ordinal);
        await foreach ((ReceivedMessage message, long sequence) in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                string? key = _runtime.Routing.OrderedEndpoint ? message.Envelope.EffectivePartitionKey : null;
                if (_draining)
                {
                    await ReleaseAsync(message, TimeSpan.Zero).ConfigureAwait(false);
                    continue;
                }

                if (key is not null && held.TryGetValue(key, out (long UpTo, TimeSpan Delay) head) && sequence <= head.UpTo)
                {
                    await ReleaseAsync(message, head.Delay).ConfigureAwait(false);
                    continue;
                }

                if (key is not null)
                {
                    held.Remove(key);
                }

                if (await HandleAsync(message, cancellationToken).ConfigureAwait(false) is { } delay && key is not null)
                {
                    held[key] = (Interlocked.Read(ref _received), delay);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Stopping cancelled the handler or its settlement (a broker call): the message stays unsettled and is
                // redelivered after its lock; the inbox absorbs a handler that had committed (RX-R6).
            }
            catch (Exception exception)
            {
                // Never let one message stop the worker: the rest of its partition would wait forever.
                Log.ReceiveLoopFailed(_logger, exception, _runtime.Routing.Endpoint);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    // Releases a message unhandled, for redelivery after the delay; a failure only leaves it to its lock.
    private async ValueTask ReleaseAsync(ReceivedMessage message, TimeSpan delay)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await message.AbandonAsync(delay, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.ReceiveLoopFailed(_logger, exception, _runtime.Routing.Endpoint);
        }
    }

    // The delay when the message was released for a delayed retry; null otherwise.
    private async Task<TimeSpan?> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        // Keep the lock while the handler runs (visibility heartbeat, doc 07 §4).
        using var renewing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task renewal = RenewAsync(message, renewing.Token);
        try
        {
            return await HandleLockedAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await renewing.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
        }
    }

    private async Task RenewAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_runtime.Options.ReceiveLockDuration / 2, TimeProvider.System);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await message.RenewAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The handler finished.
        }
        catch (Exception exception)
        {
            // A lost lock means a redelivery; the inbox absorbs the duplicate.
            Log.ReceiveLoopFailed(_logger, exception, _runtime.Routing.Endpoint);
        }
    }

    private async Task<TimeSpan?> HandleLockedAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        MessageContext context;
        int immediate = 0;
        AsyncServiceScope? scope = null;
        try
        {
            while (true)
            {
                if (scope is { } previous)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);   // the previous attempt's scope
                }

                AsyncServiceScope current = _scopes.CreateAsyncScope();
                scope = current;
                context = new MessageContext(PipelineStage.Receive, message.Envelope, current.ServiceProvider, cancellationToken);
                await InvokeAsync(context).ConfigureAwait(false);
                if (context.Outcome != HandlingOutcome.Transient || immediate >= _runtime.Options.ImmediateRetries || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                immediate++;
                _runtime.Metrics.RetryAttempts.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Stage, "receive"),
                    new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.RetryKind, "immediate"));
            }

            // Settled inside the last attempt's scope: parking may resolve services from it (CR-10).
            try
            {
                return await SettleAsync(message, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Settling failed (for example the broker connection dropped): the message is redelivered and the inbox absorbs it.
                Log.ReceiveLoopFailed(_logger, exception, _runtime.Routing.Endpoint);
                return null;
            }
        }
        finally
        {
            if (scope is { } last)
            {
                await last.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private ValueTask InvokeAsync(MessageContext context)
    {
        return Invoke(0, context);

        ValueTask Invoke(int index, MessageContext ctx)
        {
            return index < _builtIns.Length
                ? _builtIns[index].InvokeAsync(ctx, next => Invoke(index + 1, next))
                : _runtime.Pipeline.InvokeAsync(ctx, _dispatcher.DispatchAsync);
        }
    }

    // The delay when the message was released for a delayed retry; null otherwise.
    private async ValueTask<TimeSpan?> SettleAsync(ReceivedMessage message, MessageContext context, CancellationToken cancellationToken)
    {
        int attempt = AttemptOf(message);
        switch (context.Outcome)
        {
            case HandlingOutcome.Transient when attempt <= _runtime.Options.DelayedRetries.Count:
                TimeSpan delay = _runtime.Options.DelayedRetries[attempt - 1];
                Log.ReceiveRetry(_logger, context.Exception!, message.Envelope.MessageId.ToString(), message.Envelope.Type, attempt, delay);
                _runtime.Metrics.RetryAttempts.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Stage, "receive"),
                    new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.RetryKind, "delayed"));
                TimeSpan maxDelay = _runtime.Transport.Capabilities.MaxDelay;
                if (delay <= maxDelay)
                {
                    await message.AbandonAsync(delay, cancellationToken).ConfigureAwait(false);
                    return delay;
                }

                if (_runtime.Routing.OrderedEndpoint)
                {
                    // Re-sending would let the key's next message overtake this one: wait as long as the broker can (CR-13).
                    await message.AbandonAsync(maxDelay, cancellationToken).ConfigureAwait(false);
                    return maxDelay;
                }

                await DeferAsync(message, attempt, delay, cancellationToken).ConfigureAwait(false);
                await message.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return null;
            case HandlingOutcome.Transient:
            case HandlingOutcome.Permanent:
                await ParkAsync(message, context, context.Outcome == HandlingOutcome.Transient ? "Exhausted" : ReasonOf(context.Exception), cancellationToken).ConfigureAwait(false);
                await message.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return null;
            default:
                await _runtime.FaultAsync(FaultPoints.ReceiverBeforeComplete, cancellationToken).ConfigureAwait(false);
                await message.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return null;
        }
    }

    // Attempts made before a fallback re-send (sfattempt) plus the broker's count for this copy.
    private static int AttemptOf(ReceivedMessage message)
    {
        return (message.Envelope.Attempts ?? 0) + message.DeliveryCount;
    }

    // The core fallback for a delay longer than the broker's maximum (doc 04 §7, CR-13): the message, as received, goes
    // back to this endpoint through the outbox, due after the delay and carrying the attempts so far. The inbox absorbs
    // a duplicate if completing the original fails after the commit.
    private async ValueTask DeferAsync(ReceivedMessage message, int attempt, TimeSpan delay, CancellationToken cancellationToken)
    {
        Messaging.MessageEnvelope envelope = message.Envelope with { Attempts = attempt };
        await using IStorageSession session = await _runtime.Storage.Sessions.BeginAsync(envelope.TenantId, cancellationToken).ConfigureAwait(false);
        session.EnqueueOutbox(_runtime.ToOutbox(envelope, _runtime.Routing.Endpoint, OutboxKind.Scheduled, _runtime.Time.GetUtcNow() + delay));
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        _runtime.Committed(session);
    }

    private async ValueTask ParkAsync(ReceivedMessage message, MessageContext context, string reason, CancellationToken cancellationToken)
    {
        Log.ReceiveParked(_logger, context.Exception, message.Envelope.MessageId.ToString(), message.Envelope.Type, reason);
        if (_runtime.Options.LogPayloads && context.Services.GetService(typeof(PayloadRedaction)) is PayloadRedaction payloads)
        {
            Log.ParkedPayload(_logger, message.Envelope.MessageId.ToString(), payloads.ForLog(message.Envelope));
        }
        DateTimeOffset now = _runtime.Time.GetUtcNow();
        await _runtime.Storage.DeadLetters.ParkAsync(new DeadLetter
        {
            Id = Guid.CreateVersion7(now),
            Consumer = _runtime.Routing.Endpoint,
            Source = "receive",
            Envelope = message.Envelope,
            Destination = _runtime.Routing.Endpoint,
            Reason = reason,
            ErrorType = context.Exception?.GetType().FullName,
            ErrorMessage = context.Exception?.Message,
            ErrorDetail = context.Exception?.ToString(),
            Attempts = AttemptOf(message),
            FirstFailedAt = now,
            ParkedAt = now,
        }, cancellationToken).ConfigureAwait(false);
        _runtime.Metrics.DeadLettersParked.Add(1,
            new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Consumer, _runtime.Routing.Endpoint),
            new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Reason, reason),
            new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.MessageType, message.Envelope.Type));
    }

    private static string ReasonOf(Exception? exception)
    {
        return exception switch
        {
            Messaging.UnknownMessageTypeException => "UnknownMessageType",
            System.Text.Json.JsonException => "DeserializationFailed",
            NoRouteException => "NoRoute",
            Security.DecryptionFailedException => "DecryptionFailed",
            Security.ProtectionRequiredException => "ProtectionRequired",
            null => "Permanent",
            _ => "PermanentFailure",
        };
    }
}
