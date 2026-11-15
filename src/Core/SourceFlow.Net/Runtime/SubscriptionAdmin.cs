using SourceFlow.Diagnostics;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>Operator actions on subscriptions (doc 06 §7): see halted partitions and skip a poison event (audited).</summary>
public interface ISubscriptionAdmin
{
    /// <summary>Partitions this node holds that are halted at a failing event (Stop policy).</summary>
    IReadOnlyList<SubscriptionStall> Stalls { get; }

    /// <summary>
    /// Skips the next event of a partition (the poison event of a stopped projection): it is recorded in dead letters as
    /// discarded with <paramref name="reason"/> (the audit trail), and the checkpoint moves past it. Runs on the node
    /// that holds the partition's lease, or when no node does.
    /// </summary>
    /// <returns>The position skipped.</returns>
    ValueTask<GlobalPosition> SkipAsync(string subscription, int partition, string reason, CancellationToken cancellationToken = default);
}

internal sealed class SubscriptionAdmin(SourceFlowRuntime runtime, SubscriptionEngine engine) : ISubscriptionAdmin
{
    public IReadOnlyList<SubscriptionStall> Stalls => engine.Stalls;

    public async ValueTask<GlobalPosition> SkipAsync(string subscription, int partition, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        int partitions = await runtime.Storage.Checkpoints.GetPartitionsAsync(subscription, cancellationToken).ConfigureAwait(false) ?? runtime.PartitionsOf(subscription);
        ArgumentOutOfRangeException.ThrowIfNegative(partition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partition, partitions);
        LeaseToken lease = await runtime.Storage.Leases.TryAcquireAsync($"sub:{subscription}:{partition}", runtime.Options.NodeId, runtime.Options.LeaseTtl, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Another node holds {subscription} partition {partition}; run the skip there or stop that node.");
        GlobalPosition checkpoint = await runtime.Storage.Checkpoints.GetAsync(subscription, partition, cancellationToken).ConfigureAwait(false);
        LogBatch next = await runtime.Storage.Log.ReadAsync(checkpoint, Buckets.Range(partition, partitions), 1, cancellationToken).ConfigureAwait(false);
        StoredEvent stored = next.Events.Count > 0 ? next.Events[0] : throw new InvalidOperationException($"{subscription} partition {partition} has no event to skip.");

        SubscriptionStall? stall = engine.Stalls.FirstOrDefault(s => s.Subscription == subscription && s.Partition == partition);
        DateTimeOffset now = runtime.Time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        await runtime.Storage.DeadLetters.ParkAsync(new DeadLetter
        {
            Id = id,
            Consumer = subscription,
            Source = "subscription",
            Envelope = stored.ToEnvelope(),
            Reason = "Skipped",
            ErrorType = stall?.ErrorType,
            ErrorMessage = stall?.ErrorMessage,
            Attempts = stall?.Attempts ?? 0,
            FirstFailedAt = now,
            ParkedAt = now,
        }, cancellationToken).ConfigureAwait(false);
        await runtime.Storage.DeadLetters.SetStatusAsync(id, DeadLetterStatus.Discarded, "skipped: " + reason, cancellationToken).ConfigureAwait(false);

        await using (IStorageSession session = await runtime.Storage.Sessions.BeginAsync(stored.TenantId, cancellationToken).ConfigureAwait(false))
        {
            session.SaveCheckpoint(subscription, partition, stored.Position, lease);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        engine.ClearStall(subscription, partition);
        Log.SubscriptionSkipped(runtime.Logger<SubscriptionAdmin>(), subscription, partition, stored.EventId.ToString(), stored.Position.ToString(), reason);
        return stored.Position;
    }
}
