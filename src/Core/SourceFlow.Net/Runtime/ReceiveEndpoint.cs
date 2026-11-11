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
/// exhausted retries are parked (doc 04 §9.3).
/// </summary>
public sealed class ReceiveEndpoint : IAsyncDisposable
{
    private readonly SourceFlowRuntime _runtime;
    private readonly MessageDispatcher _dispatcher;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;
    private readonly Channel<ReceivedMessage>[] _channels;
    private readonly IMessageMiddleware[] _builtIns;
    private ITransportReceiver? _receiver;
    private Transport.CloudEventFilter[]? _clientFilters;
    private Task[] _workers = [];
    private int _inFlight;

    /// <summary>Creates the endpoint.</summary>
    public ReceiveEndpoint(SourceFlowRuntime runtime, CommandProcessor commands, IServiceScopeFactory scopes)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _dispatcher = new MessageDispatcher(runtime, commands);
        _logger = runtime.Logger<ReceiveEndpoint>();
        _channels = [.. Enumerable.Range(0, runtime.Options.ReceiveConcurrency).Select(_ =>
            Channel.CreateBounded<ReceivedMessage>(new BoundedChannelOptions(runtime.Options.PrefetchPerWorker) { SingleReader = true, SingleWriter = true }))];
        _builtIns =
        [
            new TelemetryMiddleware(runtime.Metrics, runtime.Routing.Endpoint),
            new LoggingScopeMiddleware(_logger),
            new ErrorClassificationMiddleware(runtime.Classifier),
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
        _workers = [.. _channels.Select(channel => Task.Run(() => WorkAsync(channel.Reader, cancellationToken), CancellationToken.None))];
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
                        await _channels[WorkerOf(message)].Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
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
            foreach (Channel<ReceivedMessage> channel in _channels)
            {
                channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>Waits for the workers to finish in-flight messages (RX-R6).</summary>
    public async ValueTask DrainAsync(TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(_workers).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Unfinished messages are redelivered by the broker.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_receiver is not null)
        {
            await _receiver.DisposeAsync().ConfigureAwait(false);
        }
    }

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

    private async Task WorkAsync(ChannelReader<ReceivedMessage> reader, CancellationToken cancellationToken)
    {
        await foreach (ReceivedMessage message in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await HandleAsync(message, cancellationToken).ConfigureAwait(false);
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

    private async Task HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        // Keep the lock while the handler runs (visibility heartbeat, doc 07 §4).
        using var renewing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task renewal = RenewAsync(message, renewing.Token);
        try
        {
            await HandleLockedAsync(message, cancellationToken).ConfigureAwait(false);
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

    private async Task HandleLockedAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        MessageContext context;
        int immediate = 0;
        while (true)
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            context = new MessageContext(PipelineStage.Receive, message.Envelope, scope.ServiceProvider, cancellationToken);
            await InvokeAsync(context).ConfigureAwait(false);
            if (context.Outcome != HandlingOutcome.Transient || immediate >= _runtime.Options.ImmediateRetries || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            immediate++;
            _runtime.Metrics.RetryAttempts.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Stage, "receive"),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.RetryKind, "immediate"));
        }

        try
        {
            await SettleAsync(message, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Settling failed (for example the broker connection dropped): the message is redelivered and the inbox absorbs it.
            Log.ReceiveLoopFailed(_logger, exception, _runtime.Routing.Endpoint);
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

    private async ValueTask SettleAsync(ReceivedMessage message, MessageContext context, CancellationToken cancellationToken)
    {
        switch (context.Outcome)
        {
            case HandlingOutcome.Transient when message.DeliveryCount <= _runtime.Options.DelayedRetries.Count:
                TimeSpan delay = _runtime.Options.DelayedRetries[message.DeliveryCount - 1];
                Log.ReceiveRetry(_logger, context.Exception!, message.Envelope.MessageId.ToString(), message.Envelope.Type, message.DeliveryCount, delay);
                _runtime.Metrics.RetryAttempts.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Stage, "receive"),
                    new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.RetryKind, "delayed"));
                await message.AbandonAsync(delay, cancellationToken).ConfigureAwait(false);
                return;
            case HandlingOutcome.Transient:
            case HandlingOutcome.Permanent:
                await ParkAsync(message, context, context.Outcome == HandlingOutcome.Transient ? "Exhausted" : ReasonOf(context.Exception), cancellationToken).ConfigureAwait(false);
                await message.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return;
            default:
                await message.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return;
        }
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
            Attempts = message.DeliveryCount,
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
            null => "Permanent",
            _ => "PermanentFailure",
        };
    }
}
