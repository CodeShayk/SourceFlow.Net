using SourceFlow.Messaging;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// The storage session of the handler that is running, and the message it handles (ADR-006, doc 03 §5.2). While it is
/// set, <see cref="ICommandSender"/> writes commands to that session's outbox, so they commit or roll back with the
/// handler's own work and its inbox entry. The id of each command is derived from the cause's id and the send's ordinal,
/// so a redelivered message sends the same ids again and the owner's inbox drops the duplicates.
/// </summary>
internal sealed class AmbientSend
{
    private static readonly AsyncLocal<AmbientSend?> Holder = new();
    private int _ordinal;

    private AmbientSend(IStorageSession session, MessageEnvelope cause)
    {
        Session = session;
        Cause = cause;
    }

    /// <summary>The handler's session; only while the handler runs (a task it leaves behind sees it inactive).</summary>
    public static AmbientSend? Current => Holder.Value is { Active: true } current ? current : null;

    public IStorageSession Session { get; }

    public MessageEnvelope Cause { get; }

    private bool Active { get; set; } = true;

    /// <summary>Makes <paramref name="session"/> the ambient one until the returned scope is disposed.</summary>
    public static Scope Enter(IStorageSession session, MessageEnvelope cause)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(cause);
        AmbientSend? previous = Holder.Value;
        var current = new AmbientSend(session, cause);
        Holder.Value = current;
        return new Scope(current, previous);
    }

    /// <summary>The next command id of this handler run: the same on a redelivery, as long as the handler sends the same commands.</summary>
    public MessageId NextId()
    {
        int ordinal = Interlocked.Increment(ref _ordinal);
        return MessageIds.Derive(Cause.MessageId, "send:" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Restores the previous ambient session and deactivates this one.</summary>
    public readonly struct Scope(AmbientSend current, AmbientSend? previous) : IDisposable
    {
        public void Dispose()
        {
            current.Active = false;
            Holder.Value = previous;
        }
    }
}
