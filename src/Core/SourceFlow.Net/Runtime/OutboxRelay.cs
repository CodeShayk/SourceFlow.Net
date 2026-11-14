using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Storage;
using SourceFlow.Transport;

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
        string name = $"outbox:{partition}";
        LeaseToken? lease = await _runtime.Storage.Leases.TryAcquireAsync(name, _runtime.Options.NodeId, _runtime.Options.LeaseTtl, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _leases.TryRemove(partition, out _);
            return 0;
        }

        _leases[partition] = lease;

        int work = 0;
        var renewal = new RenewalClock(_runtime);
        try
        {
            while (true)
            {
                // A long drain renews the lease as it goes (CR-43), so it never lapses mid-drain and lets a second relay in.
                if (renewal.Due)
                {
                    LeaseToken? renewed = await _runtime.Storage.Leases.TryAcquireAsync(name, _runtime.Options.NodeId, _runtime.Options.LeaseTtl, cancellationToken).ConfigureAwait(false);
                    if (renewed is null || renewed.Token != lease.Token)
                    {
                        _leases.TryRemove(partition, out _);
                        return work;   // it lapsed meanwhile: whoever holds it now relays this partition
                    }

                    renewal.Restart();
                }

                IReadOnlyList<OutboxRecord> rows = await _runtime.Storage.Outbox.ClaimAsync(partition, lease, _runtime.Options.OutboxBatchSize, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    return work;
                }

                // Claimed rows are the heads of their keys, so one batch call per destination keeps per-key order (doc 04 §7).
                var delivered = new List<long>(rows.Count);
                foreach (IGrouping<(OutboxKind Kind, string Destination), OutboxRecord> group in rows.GroupBy(r => (r.Message.Kind == OutboxKind.Publish ? OutboxKind.Publish : OutboxKind.Send, r.Message.Destination)))
                {
                    OutboxRecord[] batch = [.. group];
                    Dictionary<long, Exception> failures = await DispatchAsync(group.Key.Kind, group.Key.Destination, batch, cancellationToken).ConfigureAwait(false);
                    foreach (OutboxRecord row in batch)
                    {
                        if (failures.TryGetValue(row.Sequence, out Exception? failure))
                        {
                            await HandleFailureAsync(lease, row, failure, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            delivered.Add(row.Sequence);
                        }

                        work++;
                    }
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

    // Sends one destination's rows in one transport call (the transport splits by its batch limits, doc 04 §7) and returns
    // the failure of each row that was not sent. A partial batch (TransportSendException) fails only the rows it lists,
    // each as a one-message send failure, so classification is the same as for a single send.
    private async ValueTask<Dictionary<long, Exception>> DispatchAsync(OutboxKind kind, string destination, OutboxRecord[] rows, CancellationToken cancellationToken)
    {
        // A send span per message, in its own trace; with several, none is made current, since they overlap.
        Activity? current = Activity.Current;
        var activities = new Activity?[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            MessageEnvelope envelope = rows[i].Message.Envelope;
            Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("send " + destination, ActivityKind.Producer,
                ActivityContext.TryParse(envelope.TraceParent, envelope.TraceState, out ActivityContext parent) ? parent : default);
            activity?.SetTag(SourceFlowTelemetry.Tags.MessagingSystem, _runtime.Transport.SystemName);
            SemanticConventions.SetOperationType(activity, "send");
            activity?.SetTag(SourceFlowTelemetry.Tags.Destination, destination);
            activity?.SetTag(SourceFlowTelemetry.Tags.MessageId, envelope.MessageId.ToString());
            activities[i] = activity;
            if (rows.Length > 1)
            {
                Activity.Current = current;
            }
        }

        var failures = new Dictionary<long, Exception>();
        long started = Stopwatch.GetTimestamp();
        try
        {
            MessageEnvelope[] envelopes = [.. rows.Select(r => r.Message.Envelope)];
            if (kind == OutboxKind.Publish)
            {
                await _runtime.Transport.PublishAsync(destination, envelopes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _runtime.Transport.SendAsync(destination, envelopes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TransportSendException partial) when (!cancellationToken.IsCancellationRequested)
        {
            ILookup<MessageId, SendFailure> failed = partial.Failures.ToLookup(f => f.MessageId);
            foreach (OutboxRecord row in rows)
            {
                if (failed[row.Message.Envelope.MessageId].FirstOrDefault() is { } failure)
                {
                    failures[row.Sequence] = new TransportSendException(destination, [failure], 0);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (OutboxRecord row in rows)
            {
                failures[row.Sequence] = exception;
            }
        }
        finally
        {
            _runtime.Metrics.OutboxDispatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.MessagingSystem, _runtime.Transport.SystemName));
            for (int i = activities.Length - 1; i >= 0; i--)
            {
                if (failures.TryGetValue(rows[i].Sequence, out Exception? failure))
                {
                    activities[i]?.SetStatus(ActivityStatusCode.Error, failure.GetType().Name);
                }

                activities[i]?.Dispose();
            }

            if (rows.Length == 1)
            {
                Activity.Current = current;
            }
        }

        if (rows.Length > failures.Count)
        {
            _runtime.Metrics.SentMessages.Add(rows.Length - failures.Count,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.MessagingSystem, _runtime.Transport.SystemName),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Destination, destination));
        }

        return failures;
    }

    // When to renew a partition lease during a drain: a third of the TTL on either the business clock or the system clock.
    private sealed class RenewalClock(SourceFlowRuntime runtime)
    {
        private DateTimeOffset _business = runtime.Time.GetUtcNow();
        private long _system = Stopwatch.GetTimestamp();

        public bool Due
        {
            get
            {
                TimeSpan third = runtime.Options.LeaseTtl / 3;
                return runtime.Time.GetUtcNow() - _business >= third || Stopwatch.GetElapsedTime(_system) >= third;
            }
        }

        public void Restart()
        {
            _business = runtime.Time.GetUtcNow();
            _system = Stopwatch.GetTimestamp();
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
