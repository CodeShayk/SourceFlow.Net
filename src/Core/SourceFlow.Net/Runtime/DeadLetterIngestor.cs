using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Storage;
using SourceFlow.Transport;

namespace SourceFlow.Runtime;

/// <summary>
/// Drains the endpoint's native dead-letter queue (the broker backstop, doc 07 §3, TCK-24) into the dead-letter store,
/// with the envelope and headers intact and the endpoint as destination, so <c>dotnet sourceflow dlq replay</c> can
/// re-send it. A message is completed in the broker only after it is parked.
/// </summary>
internal sealed class DeadLetterIngestor(SourceFlowRuntime runtime) : IAsyncDisposable
{
    private readonly ILogger _logger = runtime.Logger<DeadLetterIngestor>();
    private ITransportReceiver? _receiver;

    /// <summary>Moves what the dead-letter queue holds now; returns how many messages were parked.</summary>
    public async ValueTask<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!runtime.Transport.Capabilities.NativeDeadLetter)
        {
            return 0;
        }

        _receiver ??= await runtime.Transport.CreateDeadLetterReceiverAsync(runtime.Routing.Endpoint, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ReceivedMessage> messages = await _receiver.ReceiveAsync(10, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        foreach (ReceivedMessage message in messages)
        {
            DateTimeOffset now = runtime.Time.GetUtcNow();
            string reason = message.DeadLetterReason ?? "BrokerDeadLetter";
            await runtime.Storage.DeadLetters.ParkAsync(new DeadLetter
            {
                Id = Guid.CreateVersion7(now),
                Consumer = runtime.Routing.Endpoint,
                Source = "broker-dlq",
                Envelope = message.Envelope,
                Destination = runtime.Routing.Endpoint,
                Reason = reason,
                Attempts = message.DeliveryCount,
                FirstFailedAt = now,
                ParkedAt = now,
            }, cancellationToken).ConfigureAwait(false);
            runtime.Metrics.DeadLettersParked.Add(1,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Consumer, runtime.Routing.Endpoint),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Reason, reason),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.MessageType, message.Envelope.Type));
            Log.BrokerDeadLetterIngested(_logger, message.Envelope.MessageId.ToString(), runtime.Routing.Endpoint, reason);
            await message.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return messages.Count;
    }

    public async ValueTask DisposeAsync()
    {
        if (_receiver is not null)
        {
            await _receiver.DisposeAsync().ConfigureAwait(false);
        }
    }
}
