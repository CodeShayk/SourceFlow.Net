using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>Operator actions on parked messages (doc 04 §9.3): list, replay, discard. Every action is audited with a note.</summary>
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

        await runtime.Storage.DeadLetters.SetStatusAsync(id, DeadLetterStatus.Replayed, note, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DiscardAsync(Guid id, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return runtime.Storage.DeadLetters.SetStatusAsync(id, DeadLetterStatus.Discarded, reason, cancellationToken);
    }
}
