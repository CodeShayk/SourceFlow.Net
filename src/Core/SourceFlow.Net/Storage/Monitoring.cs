namespace SourceFlow.Storage;

/// <summary>
/// Operational readings of a store, for health checks and observable gauges (doc 08 §3.2, §5). An optional adapter port
/// (<see cref="Runtime.StoragePorts.Monitor"/>): without it the store check only reads the log horizon and the gauges
/// that need it are not reported. Every method is a cheap aggregate query; the core samples them on an interval
/// (<see cref="Runtime.SourceFlowOptions.MonitorInterval"/>), never per message.
/// </summary>
public interface IStoreMonitor
{
    /// <summary>Checks the store answers and its schema is the version this adapter expects.</summary>
    /// <exception cref="Exception">The store is unreachable.</exception>
    ValueTask<StoreProbe> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>The undelivered outbox rows per partition, and since when the oldest due one has waited.</summary>
    ValueTask<IReadOnlyList<OutboxBacklog>> GetOutboxBacklogAsync(CancellationToken cancellationToken);

    /// <summary>Parked dead letters per consumer.</summary>
    ValueTask<IReadOnlyDictionary<string, int>> CountParkedDeadLettersAsync(CancellationToken cancellationToken);

    /// <summary>Committed events after <paramref name="after"/> in <paramref name="buckets"/>, counting at most <paramref name="cap"/>.</summary>
    ValueTask<long> CountEventsAfterAsync(GlobalPosition after, BucketRange buckets, long cap, CancellationToken cancellationToken);

    /// <summary>
    /// How long the oldest in-progress writing transaction has run, which holds the subscription horizon back (doc 05 §5.4);
    /// zero for stores without such a horizon.
    /// </summary>
    ValueTask<TimeSpan> GetHorizonAgeAsync(CancellationToken cancellationToken);

    /// <summary>Process-manager instances not completed, per process type.</summary>
    ValueTask<IReadOnlyDictionary<string, int>> CountActiveProcessesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unfinished DSL workflow instances per workflow, version and status, across tenants: the documents of
    /// <see cref="Workflows.WorkflowInstanceSummary.Collection"/> whose status is one of
    /// <see cref="Workflows.WorkflowInstanceSummary.ActiveStatuses"/> (the <c>sourceflow.workflow.active</c> gauge, doc 15 §7).
    /// Adapters without it report no workflow gauge.
    /// </summary>
    ValueTask<IReadOnlyList<Workflows.WorkflowStatusCount>> CountActiveWorkflowsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IReadOnlyList<Workflows.WorkflowStatusCount>>([]);
    }
}

/// <summary>The result of <see cref="IStoreMonitor.ProbeAsync"/>.</summary>
/// <param name="SchemaCurrent">True when the schema is the version the adapter expects (no pending or unknown migrations).</param>
/// <param name="Detail">What was checked, or what is wrong.</param>
public sealed record StoreProbe(bool SchemaCurrent, string Detail);

/// <summary>The outbox backlog of one partition.</summary>
/// <param name="Partition">The partition.</param>
/// <param name="Pending">Undelivered rows (including scheduled ones).</param>
/// <param name="OldestDueAt">
/// When the longest-waiting due row became due (the later of its creation and its scheduled delivery), or null when none is due.
/// </param>
public sealed record OutboxBacklog(int Partition, int Pending, DateTimeOffset? OldestDueAt);
