using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// Operator actions on parked messages (doc 04 §9.3): list and filter, show, replay, discard and purge. Replays and discards
/// are audited with a note (prefixed with the actor: <c>by {actor}: {note}</c>) and counted (<c>sourceflow.deadletter.replayed</c>, <c>sourceflow.deadletter.discarded</c>).
/// </summary>
public interface IDeadLetterManager
{
    /// <summary>Lists dead letters.</summary>
    ValueTask<IReadOnlyList<DeadLetter>> ListAsync(DeadLetterQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-sends the original envelope to its original destination with a new delivery attempt. The message id is kept,
    /// so the inbox still protects against duplicates.
    /// </summary>
    ValueTask ReplayAsync(Guid id, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>Discards a dead letter with a reason.</summary>
    ValueTask DiscardAsync(Guid id, string reason, CancellationToken cancellationToken = default);

    /// <summary>Shows one dead letter with its full envelope and error, or null.</summary>
    ValueTask<DeadLetter?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes replayed and discarded dead letters resolved before <paramref name="resolvedBefore"/>; parked ones are kept.</summary>
    /// <returns>The dead letters deleted.</returns>
    ValueTask<int> PurgeAsync(DateTimeOffset resolvedBefore, CancellationToken cancellationToken = default);
}

internal sealed class DeadLetterManager(SourceFlowRuntime runtime) : IDeadLetterManager
{
    public ValueTask<IReadOnlyList<DeadLetter>> ListAsync(DeadLetterQuery query, CancellationToken cancellationToken = default)
    {
        return runtime.Storage.DeadLetters.QueryAsync(query, cancellationToken);
    }

    public async ValueTask ReplayAsync(Guid id, string? note = null, CancellationToken cancellationToken = default)
    {
        DeadLetter deadLetter = await runtime.Storage.DeadLetters.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Dead letter {id} does not exist.");
        if (deadLetter.Status != DeadLetterStatus.Parked)
        {
            throw new InvalidOperationException($"Dead letter {id} is {deadLetter.Status}; only parked messages can be replayed.");
        }

        if (deadLetter.Destination is null)
        {
            throw new NotSupportedException(
                $"Dead letter {id} came from '{deadLetter.Source}' and has no destination to replay to. Fix the cause and rebuild the subscription instead.");
        }

        if (deadLetter.DestinationIsTopic)
        {
            await runtime.Transport.PublishAsync(deadLetter.Destination, [deadLetter.Envelope], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await runtime.Transport.SendAsync(deadLetter.Destination, [deadLetter.Envelope], cancellationToken).ConfigureAwait(false);
        }

        await runtime.Storage.DeadLetters.SetStatusAsync(id, DeadLetterStatus.Replayed, Audited(note), cancellationToken).ConfigureAwait(false);
        runtime.Metrics.DeadLettersReplayed.Add(1, new KeyValuePair<string, object?>(Diagnostics.SourceFlowTelemetry.Tags.Consumer, deadLetter.Consumer));
    }

    public async ValueTask DiscardAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DeadLetter deadLetter = await runtime.Storage.DeadLetters.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Dead letter {id} does not exist.");
        await runtime.Storage.DeadLetters.SetStatusAsync(id, DeadLetterStatus.Discarded, Audited(reason), cancellationToken).ConfigureAwait(false);
        runtime.Metrics.DeadLettersDiscarded.Add(1, new KeyValuePair<string, object?>(Diagnostics.SourceFlowTelemetry.Tags.Consumer, deadLetter.Consumer));
    }

    // Repudiation (doc 10 §1): the note records who replayed or discarded, from IActorAccessor (the CLI adds the operator).
    private string? Audited(string? note)
    {
        if (runtime.Actor.Actor is not { Length: > 0 } actor || note?.StartsWith("by ", StringComparison.Ordinal) == true)
        {
            return note;
        }

        return string.IsNullOrWhiteSpace(note) ? "by " + actor : "by " + actor + ": " + note;
    }

    public ValueTask<DeadLetter?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return runtime.Storage.DeadLetters.GetAsync(id, cancellationToken);
    }

    public async ValueTask<int> PurgeAsync(DateTimeOffset resolvedBefore, CancellationToken cancellationToken = default)
    {
        IStoreRetention retention = runtime.Storage.Retention
            ?? throw new NotSupportedException("The storage adapter has no retention support (doc 05 §9).");
        int total = 0;
        int deleted;
        do
        {
            deleted = await retention.PurgeDeadLettersAsync(resolvedBefore, runtime.Options.RetentionBatchSize, cancellationToken).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted > 0);

        return total;
    }
}
