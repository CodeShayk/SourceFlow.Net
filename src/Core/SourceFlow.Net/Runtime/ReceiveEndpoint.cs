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
        var options = new ReceiverOptions(_runtime.Routing.Endpoint, [.. _runtime.Routing.Subscriptions.Select(s => s.Topic)]);
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
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private async Task HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
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
