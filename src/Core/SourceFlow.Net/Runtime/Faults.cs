namespace SourceFlow.Runtime;

/// <summary>
/// Injects faults at the runtime's crash-sensitive points, for chaos tests (plan 06 CH-01..06, CH-09; doc 11). Register
/// one in the service collection to enable it; without one, the points cost a null check. An injector may throw (a
/// crash at that point), delay (a stalled process), or return (no fault).
/// </summary>
public interface IFaultInjector
{
    /// <summary>Called when the runtime reaches <paramref name="point"/> (one of <see cref="FaultPoints"/>).</summary>
    ValueTask InjectAsync(string point, CancellationToken cancellationToken);
}

/// <summary>The points where the runtime asks an <see cref="IFaultInjector"/> for a fault.</summary>
public static class FaultPoints
{
    /// <summary>A command's session is complete and about to commit: a crash here loses only the uncommitted attempt.</summary>
    public const string SessionBeforeCommit = "session.beforeCommit";

    /// <summary>A command committed, and the result has not reached the caller: a retry must be absorbed by the inbox.</summary>
    public const string SessionAfterCommitBeforeAck = "session.afterCommitBeforeAck";

    /// <summary>The relay published a message and has not yet deleted its outbox row: the message is published again.</summary>
    public const string RelayAfterPublishBeforeDelete = "relay.afterPublishBeforeDelete";

    /// <summary>A subscription applied a batch and has not committed it with the checkpoint: the batch is applied again.</summary>
    public const string SubscriptionAfterApplyBeforeCommit = "subscription.afterApplyBeforeCommit";

    /// <summary>A node is about to renew a subscription or outbox lease it owns: past the TTL, another node takes over.</summary>
    public const string LeaseBeforeRenew = "lease.beforeRenew";

    /// <summary>A received message was handled and committed, and is about to be completed at the broker: it is redelivered.</summary>
    public const string ReceiverBeforeComplete = "receiver.beforeComplete";

    /// <summary>Every point, in pipeline order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SessionBeforeCommit, SessionAfterCommitBeforeAck, RelayAfterPublishBeforeDelete,
        SubscriptionAfterApplyBeforeCommit, LeaseBeforeRenew, ReceiverBeforeComplete,
    ];
}
