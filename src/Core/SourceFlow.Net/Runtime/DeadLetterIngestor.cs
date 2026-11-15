using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Storage;
using SourceFlow.Transport;

namespace SourceFlow.Runtime;

/// <summary>
/// Drains the endpoint's native dead-letter queue (the broker backstop, doc 07 §3, TCK-24) into the dead-letter store,
/// with the envelope and headers intact and the endpoint as destination, so <c>dotnet sourceflow dlq replay</c> can
/// re-send it. A message is completed in the broker only after it is parked, so a crash in between parks it twice at
/// worst, never loses it.
/// </summary>
/// <remarks>
/// It runs on receiver instances as its own background loop (<see cref="RunAsync"/>): it drains without waiting while the
/// queue has messages, then checks every <see cref="SourceFlowOptions.DeadLetterIngestInterval"/>. Commits do not wake it;
/// dead letters are rare and every check is a broker call. After a broker error it recreates its receiver and backs off,
/// doubling from one second up to the interval.
/// </remarks>
internal sealed class DeadLetterIngestor(SourceFlowRuntime runtime) : IAsyncDisposable
{
    /// <summary>The most messages taken from the dead-letter queue per check.</summary>
    public const int BatchSize = 10;

    private readonly ILogger _logger = runtime.Logger<DeadLetterIngestor>();
    private ITransportReceiver? _receiver;

    /// <summary>Ingests until <paramref name="stopping"/> is cancelled; never throws otherwise.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        TimeSpan interval = runtime.Options.DeadLetterIngestInterval;
        Polly.ResilienceContext context = Polly.ResilienceContextPool.Shared.Get(stopping);
        try
        {
            // A failed poll is retried through the ingest pipeline (exponential from 1 s up to the interval, ADR-013, CR-39),
            // after the receiver is reset; the system clock, so a fake business clock doesn't stop the loop.
            context.Properties.Set(SourceFlowPipelines.OnIngestRetry, async (error, delay) =>
            {
                Log.BrokerDeadLetterIngestFailed(_logger, error!, runtime.Routing.Endpoint, delay);
                await ResetAsync().ConfigureAwait(false);
            });
            while (!stopping.IsCancellationRequested)
            {
                int moved;
                try
                {
                    moved = await runtime.Resilience.Ingest.ExecuteAsync(async (ctx, _) => await RunOnceAsync(ctx.CancellationToken).ConfigureAwait(false), context, 0).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }

                if (moved == 0)
                {
                    try
                    {
                        await Task.Delay(interval, TimeProvider.System, stopping).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            Polly.ResilienceContextPool.Shared.Return(context);
        }
    }

    /// <summary>Moves what the dead-letter queue holds now (up to <see cref="BatchSize"/>); returns how many were parked.</summary>
    public async ValueTask<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!runtime.Transport.Capabilities.NativeDeadLetter)
        {
            return 0;
        }

        _receiver ??= await runtime.Transport.CreateDeadLetterReceiverAsync(runtime.Routing.Endpoint, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ReceivedMessage> messages = await _receiver.ReceiveAsync(BatchSize, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
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

    public ValueTask DisposeAsync()
    {
        return ResetAsync();
    }

    private async ValueTask ResetAsync()
    {
        ITransportReceiver? receiver = _receiver;
        _receiver = null;
        if (receiver is null)
        {
            return;
        }

        try
        {
            await receiver.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken receiver may fail to close; a new one is created on the next check.
        }
    }
}
