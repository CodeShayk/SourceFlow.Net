using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// The outbox relay (doc 04 §7). For each partition it holds a fenced lease, claims the head row of each key, publishes
/// in sequence order, deletes delivered rows, and backs off on failure without letting a later row of the same key
/// overtake. After <see cref="SourceFlowOptions.OutboxMaxAttempts"/> a row is parked.
/// </summary>
public sealed class OutboxRelay
{
    private readonly SourceFlowRuntime _runtime;
    private readonly ILogger _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, LeaseToken> _leases = new();

    /// <summary>Creates the relay.</summary>
    public OutboxRelay(SourceFlowRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _logger = runtime.Logger<OutboxRelay>();
    }

    /// <summary>
    /// Releases the outbox partition leases this relay holds, so another node (a rolling deploy's next instance) takes
    /// them over at once instead of after <see cref="SourceFlowOptions.LeaseTtl"/>. The host calls it on stop, after the
    /// relay loop ended; a failure only leaves the lease to expire.
    /// </summary>
    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        foreach (KeyValuePair<int, LeaseToken> held in _leases)
        {
            try
            {
                await _runtime.Storage.Leases.ReleaseAsync(held.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.OutboxLeaseNotReleased(_logger, exception, held.Key);
            }

            _leases.TryRemove(held.Key, out _);
        }
    }

    /// <summary>Runs one pass over every partition; returns the number of rows dispatched or rescheduled.</summary>
    public async ValueTask<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        int work = 0;
        for (int partition = 0; partition < _runtime.Options.OutboxPartitions; partition++)
        {
            work += await RelayPartitionAsync(partition, cancellationToken).ConfigureAwait(false);
        }

        return work;
    }

    private async ValueTask<int> RelayPartitionAsync(int partition, CancellationToken cancellationToken)
    {
        LeaseToken? lease = await _runtime.Storage.Leases.TryAcquireAsync($"outbox:{partition}", _runtime.Options.NodeId, _runtime.Options.LeaseTtl, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _leases.TryRemove(partition, out _);
            return 0;
        }

        _leases[partition] = lease;

        int work = 0;
        try
        {
            while (true)
            {
                IReadOnlyList<OutboxRecord> rows = await _runtime.Storage.Outbox.ClaimAsync(partition, lease, _runtime.Options.OutboxBatchSize, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    return work;
                }

                var delivered = new List<long>(rows.Count);
                foreach (OutboxRecord row in rows)
                {
                    Exception? failure = await DispatchAsync(row, cancellationToken).ConfigureAwait(false);
                    if (failure is null)
                    {
                        delivered.Add(row.Sequence);
                    }
                    else
                    {
                        await HandleFailureAsync(lease, row, failure, cancellationToken).ConfigureAwait(false);
                    }

                    work++;
                }

                if (delivered.Count > 0)
                {
                    await _runtime.FaultAsync(FaultPoints.RelayAfterPublishBeforeDelete, cancellationToken).ConfigureAwait(false);
                    await _runtime.Storage.Outbox.CompleteAsync(lease, delivered, cancellationToken).ConfigureAwait(false);
                }

                if (delivered.Count < rows.Count)
                {
                    return work; // a failed head blocks its key until its retry time
                }
            }
        }
        catch (LeaseLostException)
        {
            return work;
        }
    }

    private async ValueTask<Exception?> DispatchAsync(OutboxRecord row, CancellationToken cancellationToken)
    {
        OutboxMessage message = row.Message;
        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("send " + message.Destination, ActivityKind.Producer,
            ActivityContext.TryParse(message.Envelope.TraceParent, message.Envelope.TraceState, out ActivityContext parent) ? parent : default);
        activity?.SetTag(SourceFlowTelemetry.Tags.MessagingSystem, _runtime.Transport.SystemName);
        SemanticConventions.SetOperationType(activity, "send");
        activity?.SetTag(SourceFlowTelemetry.Tags.Destination, message.Destination);
        activity?.SetTag(SourceFlowTelemetry.Tags.MessageId, message.Envelope.MessageId.ToString());
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (message.Kind == OutboxKind.Publish)
            {
                await _runtime.Transport.PublishAsync(message.Destination, [message.Envelope], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _runtime.Transport.SendAsync(message.Destination, [message.Envelope], cancellationToken).ConfigureAwait(false);
            }

            _runtime.Metrics.SentMessages.Add(1,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.MessagingSystem, _runtime.Transport.SystemName),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Destination, message.Destination));
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            return exception;
        }
        finally
        {
            _runtime.Metrics.OutboxDispatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.MessagingSystem, _runtime.Transport.SystemName));
        }
    }

    private async ValueTask HandleFailureAsync(LeaseToken lease, OutboxRecord row, Exception error, CancellationToken cancellationToken)
    {
        int attempt = row.Attempts + 1;
        bool permanent = _runtime.Classifier.Classify(error) == Pipeline.HandlingOutcome.Permanent;
        if (permanent || attempt >= _runtime.Options.OutboxMaxAttempts)
        {
            Log.OutboxParked(_logger, error, row.Message.Envelope.MessageId.ToString(), row.Message.Destination, attempt);
            await _runtime.Storage.DeadLetters.ParkAsync(new DeadLetter
            {
                Id = Guid.CreateVersion7(_runtime.Time.GetUtcNow()),
                Consumer = "outbox",
                Source = "outbox",
                Envelope = row.Message.Envelope,
                Destination = row.Message.Destination,
                DestinationIsTopic = row.Message.Kind == OutboxKind.Publish,
                Reason = permanent ? "OutboxPublishFailed" : "Exhausted",
                ErrorType = error.GetType().FullName,
                ErrorMessage = error.Message,
                ErrorDetail = error.ToString(),
                Attempts = attempt,
                FirstFailedAt = row.CreatedAt,
                ParkedAt = _runtime.Time.GetUtcNow(),
            }, cancellationToken).ConfigureAwait(false);
            _runtime.Metrics.DeadLettersParked.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Consumer, "outbox"),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Reason, permanent ? "OutboxPublishFailed" : "Exhausted"));
            await _runtime.Storage.Outbox.CompleteAsync(lease, [row.Sequence], cancellationToken).ConfigureAwait(false);
            return;
        }

        TimeSpan delay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, attempt)));
        DateTimeOffset retryAt = _runtime.Time.GetUtcNow() + delay;
        Log.OutboxRetry(_logger, error, row.Message.Destination, attempt, retryAt);
        _runtime.Metrics.RetryAttempts.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Stage, "publish"),
            new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.RetryKind, "delayed"));
        await _runtime.Storage.Outbox.RetryAsync(lease, row.Sequence, retryAt, error.Message, cancellationToken).ConfigureAwait(false);
    }
}
