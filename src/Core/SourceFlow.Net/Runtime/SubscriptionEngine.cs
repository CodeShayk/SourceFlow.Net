using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>A durable subscription over the global log: async projections, local process managers, local event handlers.</summary>
/// <param name="Name">The subscription name (checkpoint and lease key).</param>
/// <param name="Handles">Whether a deserialized event is relevant.</param>
/// <param name="Apply">Applies one event inside the session.</param>
internal sealed record SubscriptionDefinition(string Name, Func<Type, bool> Handles, Func<SubscriptionContext, ValueTask> Apply)
{
    /// <summary>What happens to an event that keeps failing (doc 06 §7).</summary>
    public Hosting.SubscriptionFailurePolicy Policy { get; init; } = Hosting.SubscriptionFailurePolicy.Park;

    /// <summary>Loads what a batch needs before it is applied (document projections read their documents in one query).</summary>
    public Func<ProjectionScope, IReadOnlyList<(IEvent Event, EventContext Context)>, CancellationToken, ValueTask>? Prefetch { get; init; }

    /// <summary>The read-model generation a projection subscription writes (doc 06 §6); null for other subscriptions.</summary>
    public ReadModelGeneration? Generation { get; init; }
}

/// <summary>Inputs to one subscription apply.</summary>
internal sealed record SubscriptionContext(IStorageSession Session, IEvent Event, StoredEvent Stored, IServiceProvider Services, ProjectionScope Projections, CancellationToken CancellationToken);

/// <summary>
/// Runs subscriptions (doc 06). Each <c>(subscription, partition)</c> holds a fenced lease, reads its bucket range
/// of the commit-ordered log after its checkpoint, and applies a batch plus the checkpoint in one transaction
/// (exactly-once effect in the same database). A failed batch is retried event by event; a poison event is parked and
/// skipped so the partition never stalls silently.
/// </summary>
internal sealed class SubscriptionEngine
{
    private readonly SourceFlowRuntime _runtime;
    private readonly IServiceScopeFactory _scopes;
    private readonly AggregateStore _decoder;
    private readonly ILogger _logger;

    public SubscriptionEngine(SourceFlowRuntime runtime, IServiceScopeFactory scopes)
    {
        _runtime = runtime;
        _scopes = scopes;
        _decoder = new AggregateStore(runtime);
        _logger = runtime.Logger<SubscriptionEngine>();
        _static = BuildStaticDefinitions();
        _definitions = Compose();
        runtime.Metrics.Meter.CreateObservableGauge(
            SourceFlowTelemetry.Instruments.SubscriptionStalled,
            () => _stalls.Values.Select(s => new System.Diagnostics.Metrics.Measurement<int>(1,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Subscription, s.Subscription),
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Partition, s.Partition))),
            "{partition}",
            "Subscription partitions halted at a failing event (Stop policy).");
    }

    private readonly IReadOnlyList<SubscriptionDefinition> _static;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SubscriptionDefinition> _generations = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<SubscriptionDefinition> _definitions;
    private DateTimeOffset _nextRetirementCheck = DateTimeOffset.MinValue;
    private volatile bool _building;
    private volatile int _composedRevision = -1;
    private volatile bool _rebuilding;

    /// <summary>The subscriptions this node runs now: projection generations (they change with the aliases), process managers and handlers.</summary>
    public IReadOnlyList<SubscriptionDefinition> Definitions => _definitions;

    /// <summary>True when this service has anything to subscribe (async projections, local process managers or handlers).</summary>
    public bool HasSubscriptions => _static.Count > 0 || _runtime.AsyncProjections.Count > 0;

    /// <summary>Runs one pass over every subscription partition; returns the number of events processed.</summary>
    private const int InitialBatchSize = 100;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Subscription, int Partition), int> _batchSizes = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Subscription, int Partition), LeaseToken> _owned = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Subscription, int Partition), SubscriptionStall> _stalls = new();

    /// <summary>Partitions halted at a failing event (Stop policy).</summary>
    internal IReadOnlyList<SubscriptionStall> Stalls => [.. _stalls.Values.OrderBy(s => s.Subscription, StringComparer.Ordinal).ThenBy(s => s.Partition)];

    /// <summary>Clears a stall after an operator skipped the event.</summary>
    internal void ClearStall(string subscription, int partition)
    {
        _stalls.TryRemove((subscription, partition), out _);
    }

    /// <summary>The (subscription, partition) leases this node holds.</summary>
    internal int OwnedLeases => _owned.Count;

    /// <summary>
    /// The most leases this node takes: <c>ceil(total / live nodes)</c> (doc 06 §4). Without a node registry, or with one
    /// live node, every lease.
    /// </summary>
    internal int FairShare
    {
        get
        {
            int total = Definitions.Count * _runtime.Options.SubscriptionPartitions;
            int live = Math.Max(1, _runtime.LiveSubscriptionNodes);
            return (total + live - 1) / live;
        }
    }

    public async ValueTask<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        bool refreshed = await _runtime.Generations.RefreshAsync(force: false, cancellationToken).ConfigureAwait(false);
        if (_runtime.Generations.Revision != _composedRevision)
        {
            await RecomposeAsync(cancellationToken).ConfigureAwait(false);   // reloaded here, or by a query
        }

        int processed = 0;
        int partitions = _runtime.Options.SubscriptionPartitions;
        int share = FairShare;
        foreach (SubscriptionDefinition definition in Definitions)
        {
            for (int partition = 0; partition < partitions; partition++)
            {
                // Up to the fair share: keep (renew) what this node holds, take free or expired leases only below it.
                if (!_owned.ContainsKey((definition.Name, partition)) && _owned.Count >= share)
                {
                    continue;
                }

                processed += await RunPartitionAsync(definition, partition, partitions, cancellationToken).ConfigureAwait(false);
            }
        }

        await ReleaseExcessAsync(share, cancellationToken).ConfigureAwait(false);
        if (refreshed || (processed == 0 && _building))
        {
            await SwitchCaughtUpGenerationsAsync(cancellationToken).ConfigureAwait(false);
        }

        await DropRetiredGenerationsAsync(force: false, cancellationToken).ConfigureAwait(false);
        return processed;
    }

    // ================================================================ blue/green generations (doc 06 §6, ADR-032)

    // Rebuilds the subscription list from the aliases and releases the leases of generations this node no longer runs.
    private async ValueTask RecomposeAsync(CancellationToken cancellationToken)
    {
        _definitions = Compose();
        var running = new HashSet<string>(_definitions.Select(d => d.Name), StringComparer.Ordinal);
        foreach ((string subscription, int partition) in _owned.Keys.Where(k => !running.Contains(k.Subscription)).ToArray())
        {
            _stalls.TryRemove((subscription, partition), out _);
            _batchSizes.TryRemove((subscription, partition), out _);
            if (_owned.TryRemove((subscription, partition), out LeaseToken? lease))
            {
                await _runtime.Storage.Leases.ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private List<SubscriptionDefinition> Compose()
    {
        _composedRevision = _runtime.Generations.Revision;
        var definitions = new List<SubscriptionDefinition>();
        bool anyBuilding = false;
        bool anyRebuilding = false;
        foreach (ProjectionDescriptor projection in _runtime.AsyncProjections)
        {
            (ReadModelGeneration? serving, ReadModelGeneration? building) = _runtime.Generations.Plan(projection);
            anyBuilding |= building is not null;
            anyRebuilding |= building is not null && _runtime.Generations.Aliases.ContainsKey(projection.Name);
            foreach (ReadModelGeneration generation in new[] { serving, building }.OfType<ReadModelGeneration>())
            {
                definitions.Add(_generations.GetOrAdd(generation.Collection, _ => ProjectionDefinition(projection, generation)));
            }
        }

        definitions.AddRange(_static);
        _building = anyBuilding;
        _rebuilding = anyRebuilding;
        return definitions;
    }

    /// <summary>
    /// Points each alias at the generation this node builds once every partition is within
    /// <see cref="SourceFlowOptions.SwitchLagThreshold"/> events of the log (compare-and-set: one node wins).
    /// </summary>
    internal async ValueTask<int> SwitchCaughtUpGenerationsAsync(CancellationToken cancellationToken)
    {
        if (_runtime.Storage.Aliases is not { } aliases)
        {
            return 0;
        }

        int switched = 0;
        foreach (ProjectionDescriptor projection in _runtime.AsyncProjections)
        {
            if (_runtime.Generations.Plan(projection).Building is not { } building
                || !await WithinLagAsync(building.Subscription, _runtime.Options.SwitchLagThreshold, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            string? current = _runtime.Generations.Aliases.TryGetValue(projection.Name, out CollectionAlias? alias) ? alias.Target : null;
            if (await aliases.SwitchAsync(projection.Name, current, building.Collection, cancellationToken).ConfigureAwait(false))
            {
                _runtime.Generations.Switched(projection.Name, current, building.Collection, _runtime.Time.GetUtcNow());
                Log.ReadModelSwitched(_logger, projection.Name, current ?? "(none)", building.Collection);
                switched++;
            }
        }

        if (switched > 0)
        {
            await _runtime.Generations.RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);
            await RecomposeAsync(cancellationToken).ConfigureAwait(false);
        }

        return switched;
    }

    // True when every partition of the subscription is at most `threshold` events behind the log.
    private async ValueTask<bool> WithinLagAsync(string subscription, int threshold, CancellationToken cancellationToken)
    {
        int partitions = _runtime.Options.SubscriptionPartitions;
        for (int partition = 0; partition < partitions; partition++)
        {
            GlobalPosition checkpoint = await _runtime.Storage.Checkpoints.GetAsync(subscription, partition, cancellationToken).ConfigureAwait(false);
            LogBatch behind = await _runtime.Storage.Log.ReadAsync(checkpoint, Buckets.Range(partition, partitions), threshold + 1, cancellationToken).ConfigureAwait(false);
            if (behind.Events.Count > threshold)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Drops the data of generations retired more than <see cref="SourceFlowOptions.RetainPreviousFor"/> ago
    /// (checked once per retention interval unless forced); returns how many generations were dropped.
    /// </summary>
    internal async ValueTask<int> DropRetiredGenerationsAsync(bool force, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _runtime.Time.GetUtcNow();
        if (_runtime.Storage.Aliases is not { } aliases || (!force && now < _nextRetirementCheck))
        {
            return 0;
        }

        _nextRetirementCheck = now + _runtime.Options.RetentionInterval;
        int dropped = 0;
        foreach (CollectionAlias alias in await aliases.ListAliasesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (alias.Previous is not { } previous
                || now - alias.SwitchedAt < _runtime.Options.RetainPreviousFor
                || !ReadModelGeneration.TryParse(previous, out ReadModelGeneration? generation)
                || _runtime.AsyncProjections.FirstOrDefault(p => p.Name == alias.Alias) is not { } projection
                || !_runtime.Generations.Participates(projection))
            {
                continue;
            }

            if (projection is DocumentProjectionDescriptor)
            {
                if (_runtime.Storage.DocumentAdmin is not { } admin)
                {
                    continue;
                }

                await admin.DropCollectionAsync(previous, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                if (projection.GenerationStore(scope.ServiceProvider) is not { } store)
                {
                    continue;
                }

                await using IStorageSession session = await _runtime.Storage.Sessions.BeginAsync(_runtime.Options.DefaultTenant, cancellationToken).ConfigureAwait(false);
                await store.DropGenerationAsync(generation.Value, session, cancellationToken).ConfigureAwait(false);
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await aliases.ClearPreviousAsync(alias.Alias, previous, cancellationToken).ConfigureAwait(false);
            Log.ReadModelDropped(_logger, previous);
            dropped++;
        }

        return dropped;
    }

    // Above the fair share (a node joined): release one lease per pass, so rebalancing is gradual (no thundering herd).
    private async ValueTask ReleaseExcessAsync(int share, CancellationToken cancellationToken)
    {
        if (_owned.Count <= share)
        {
            return;
        }

        (string Subscription, int Partition) victim = _owned.Keys.OrderByDescending(k => k.Partition).ThenByDescending(k => k.Subscription, StringComparer.Ordinal).First();
        if (_owned.TryRemove(victim, out LeaseToken? lease))
        {
            await _runtime.Storage.Leases.ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
            _batchSizes.TryRemove(victim, out _);
        }
    }

    /// <summary>Releases every lease this node holds (graceful shutdown: others take over without waiting for the TTL).</summary>
    public async ValueTask ReleaseAllAsync(CancellationToken cancellationToken)
    {
        foreach ((string, int) key in _owned.Keys.ToArray())
        {
            if (_owned.TryRemove(key, out LeaseToken? lease))
            {
                await _runtime.Storage.Leases.ReleaseAsync(lease, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>True when every partition's checkpoint has reached the log horizon.</summary>
    public async ValueTask<bool> IsCaughtUpAsync(CancellationToken cancellationToken)
    {
        if (_rebuilding)
        {
            return false;   // a rebuild is pending its alias switch (the first build of a projection reads its own collection anyway)
        }

        int partitions = _runtime.Options.SubscriptionPartitions;
        foreach (SubscriptionDefinition definition in Definitions)
        {
            for (int partition = 0; partition < partitions; partition++)
            {
                GlobalPosition checkpoint = await _runtime.Storage.Checkpoints.GetAsync(definition.Name, partition, cancellationToken).ConfigureAwait(false);
                LogBatch next = await _runtime.Storage.Log.ReadAsync(checkpoint, Buckets.Range(partition, partitions), 1, cancellationToken).ConfigureAwait(false);
                if (next.Events.Count > 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async ValueTask<int> RunPartitionAsync(SubscriptionDefinition definition, int partition, int partitions, CancellationToken cancellationToken)
    {
        LeaseToken? lease = await _runtime.Storage.Leases.TryAcquireAsync($"sub:{definition.Name}:{partition}", _runtime.Options.NodeId, _runtime.Options.LeaseTtl, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _owned.TryRemove((definition.Name, partition), out _);
            return 0;
        }

        _owned[(definition.Name, partition)] = lease;

        if (_stalls.TryGetValue((definition.Name, partition), out SubscriptionStall? stall) && _runtime.Time.GetUtcNow() < stall.RetryAt)
        {
            return 0;   // halted at the poison event until the backoff elapses (Stop policy)
        }

        GlobalPosition checkpoint = await _runtime.Storage.Checkpoints.GetAsync(definition.Name, partition, cancellationToken).ConfigureAwait(false);
        int max = _runtime.Options.SubscriptionBatchSize;
        int size = _batchSizes.GetOrAdd((definition.Name, partition), Math.Min(InitialBatchSize, max));
        LogBatch batch = await _runtime.Storage.Log.ReadAsync(checkpoint, Buckets.Range(partition, partitions), size, cancellationToken).ConfigureAwait(false);

        // Adaptive batches (doc 06 §2): a full batch means the partition is behind, so read more next time.
        _batchSizes[(definition.Name, partition)] = batch.Events.Count == size ? Math.Min(size * 2, max) : Math.Min(InitialBatchSize, max);
        if (batch.Events.Count == 0)
        {
            return 0;
        }

        using Activity? activity = SourceFlowTelemetry.ActivitySource.StartActivity("project " + definition.Name, ActivityKind.Consumer);
        activity?.SetTag(SourceFlowTelemetry.Tags.Subscription, definition.Name);
        activity?.SetTag(SourceFlowTelemetry.Tags.Partition, partition);
        activity?.SetTag(SourceFlowTelemetry.Tags.BatchCount, batch.Events.Count);
        long started = Stopwatch.GetTimestamp();
        try
        {
            foreach (IGrouping<string, StoredEvent> group in GroupByTenant(batch.Events))
            {
                StoredEvent[] events = [.. group];
                try
                {
                    await ApplyAsync(definition, partition, lease, group.Key, events, cancellationToken).ConfigureAwait(false);
                }
                catch (LeaseLostException)
                {
                    throw;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // Isolate the poison event: apply one by one; then park it, or stop the partition at it.
                    foreach (StoredEvent single in events)
                    {
                        if (!await ApplyIsolatedAsync(definition, partition, lease, single, cancellationToken).ConfigureAwait(false))
                        {
                            return 0;
                        }
                    }
                }
            }
        }
        catch (LeaseLostException)
        {
            Log.SubscriptionLeaseLost(_logger, definition.Name, partition);
            return 0;
        }
        finally
        {
            _runtime.Metrics.SubscriptionBatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Subscription, definition.Name));
        }

        return batch.Events.Count;
    }

    private async ValueTask ApplyAsync(SubscriptionDefinition definition, int partition, LeaseToken lease, string tenant, IReadOnlyList<StoredEvent> events, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        await using IStorageSession session = await _runtime.Storage.Sessions.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        await using var projections = new ProjectionScope(_runtime, session, scope.ServiceProvider) { Generation = definition.Generation };
        var relevant = new List<(IEvent Event, StoredEvent Stored)>(events.Count);
        foreach (StoredEvent stored in events)
        {
            if (_runtime.Model.Registry.TryGet(stored.Type, stored.TypeVersion, out MessageTypeInfo? type) && Relevant(definition, type))
            {
                relevant.Add(((IEvent)_decoder.Deserialize(stored, upcast: true), stored));
            }
        }

        if (definition.Prefetch is { } prefetch && relevant.Count > 1)
        {
            await prefetch(projections, [.. relevant.Select(r => (r.Event, r.Stored.ToContext()))], cancellationToken).ConfigureAwait(false);
        }

        foreach ((IEvent @event, StoredEvent stored) in relevant)
        {
            await definition.Apply(new SubscriptionContext(session, @event, stored, scope.ServiceProvider, projections, cancellationToken)).ConfigureAwait(false);
        }

        // Session projections (EF Core) save before the fenced checkpoint, in the same transaction (doc 05 §10).
        await projections.CompleteAsync(cancellationToken).ConfigureAwait(false);
        session.SaveCheckpoint(definition.Name, partition, events[^1].Position, lease);
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Returns false when the partition stops at this event (Stop policy).
    private async ValueTask<bool> ApplyIsolatedAsync(SubscriptionDefinition definition, int partition, LeaseToken lease, StoredEvent stored, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= _runtime.Options.ImmediateRetries; attempt++)
        {
            try
            {
                await ApplyAsync(definition, partition, lease, stored.TenantId, [stored], cancellationToken).ConfigureAwait(false);
                _stalls.TryRemove((definition.Name, partition), out _);
                return true;
            }
            catch (LeaseLostException)
            {
                throw;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                last = exception;
                if (_runtime.Classifier.Classify(exception) == Pipeline.HandlingOutcome.Permanent)
                {
                    break;
                }
            }
        }

        if (definition.Policy == Hosting.SubscriptionFailurePolicy.Stop)
        {
            // doc 06 §7: halt the partition at the event; retry with backoff (1 s doubling, capped at 5 min).
            SubscriptionStall stall = _stalls.AddOrUpdate((definition.Name, partition),
                _ => Stall(definition.Name, partition, stored, last!, 1, since: null),
                (_, previous) => previous.Position == stored.Position
                    ? Stall(definition.Name, partition, stored, last!, previous.Attempts + 1, previous.StalledSince)
                    : Stall(definition.Name, partition, stored, last!, 1, since: null));
            Log.SubscriptionFailed(_logger, last!, definition.Name, partition, stored.EventId.ToString(), $"stopped (attempt {stall.Attempts}, retry at {stall.RetryAt:O})");
            return false;
        }

        Log.SubscriptionFailed(_logger, last!, definition.Name, partition, stored.EventId.ToString(), "parked");
        DateTimeOffset now = _runtime.Time.GetUtcNow();
        await _runtime.Storage.DeadLetters.ParkAsync(new DeadLetter
        {
            Id = Guid.CreateVersion7(now),
            Consumer = definition.Name,
            Source = "subscription",
            Envelope = stored.ToEnvelope(),
            Reason = last is UnknownMessageTypeException ? "UnknownMessageType" : "SubscriptionFailed",
            ErrorType = last?.GetType().FullName,
            ErrorMessage = last?.Message,
            ErrorDetail = last?.ToString(),
            Attempts = _runtime.Options.ImmediateRetries + 1,
            FirstFailedAt = now,
            ParkedAt = now,
        }, cancellationToken).ConfigureAwait(false);
        _runtime.Metrics.DeadLettersParked.Add(1, new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Consumer, definition.Name),
            new KeyValuePair<string, object?>(SourceFlowTelemetry.Tags.Reason, "SubscriptionFailed"));

        // Advance past the parked event so the partition does not stall.
        await using IStorageSession skip = await _runtime.Storage.Sessions.BeginAsync(stored.TenantId, cancellationToken).ConfigureAwait(false);
        skip.SaveCheckpoint(definition.Name, partition, stored.Position, lease);
        await skip.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private SubscriptionStall Stall(string subscription, int partition, StoredEvent stored, Exception error, int attempts, DateTimeOffset? since)
    {
        TimeSpan backoff = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts - 1, 9))));
        DateTimeOffset now = _runtime.Time.GetUtcNow();
        return new SubscriptionStall(subscription, partition, stored.Position, stored.EventId, stored.Type, attempts, error.GetType().Name, error.Message, now + backoff)
        {
            StalledSince = since ?? now,
        };
    }

    private static bool Relevant(SubscriptionDefinition definition, MessageTypeInfo type)
    {
        Type target = type.UpcastsTo is null ? type.ClrType : Latest(type).ClrType;
        return type.Kind == MessageKind.Event && definition.Handles(target);
    }

    private static MessageTypeInfo Latest(MessageTypeInfo type)
    {
        while (type.UpcastsTo is not null)
        {
            type = type.UpcastsTo;
        }

        return type;
    }

    private static IEnumerable<IGrouping<string, StoredEvent>> GroupByTenant(IReadOnlyList<StoredEvent> events)
    {
        // Consecutive runs per tenant keep commit order inside each transaction.
        var groups = new List<IGrouping<string, StoredEvent>>();
        int start = 0;
        for (int i = 1; i <= events.Count; i++)
        {
            if (i == events.Count || events[i].TenantId != events[start].TenantId)
            {
                groups.Add(new Group(events[start].TenantId, events.Skip(start).Take(i - start).ToArray()));
                start = i;
            }
        }

        return groups;
    }

    // One subscription per read-model generation; process managers and handlers are never part of a rebuild (GAP-06).
    private SubscriptionDefinition ProjectionDefinition(ProjectionDescriptor projection, ReadModelGeneration generation)
    {
        return new SubscriptionDefinition(
            generation.Subscription,
            projection.Handles,
            ctx => ctx.Projections.ApplyAsync(projection, ctx.Event, ctx.Stored.ToContext(), ctx.CancellationToken))
        {
            Policy = _runtime.ProjectionPolicies.GetValueOrDefault(projection.ProjectionType, Hosting.SubscriptionFailurePolicy.Stop),
            Prefetch = projection is DocumentProjectionDescriptor document
                ? (scope, batch, ct) => scope.PrefetchAsync(document, batch, ct)
                : null,
            Generation = generation,
        };
    }

    private List<SubscriptionDefinition> BuildStaticDefinitions()
    {
        var definitions = new List<SubscriptionDefinition>();
        var processes = new ProcessManagerRunner(_runtime);
        foreach (ProcessManagerDescriptor process in _runtime.Model.ProcessManagers)
        {
            if (!process.HandledMessages.Any(t => typeof(IEvent).IsAssignableFrom(t)))
            {
                continue;
            }

            definitions.Add(new SubscriptionDefinition(
                "pm:" + process.Name,
                type => process.HandledMessages.Contains(type),
                async ctx => await processes.ReactAsync(ctx.Session, process, ctx.Event, ctx.Stored.ToEnvelope(), ctx.CancellationToken).ConfigureAwait(false)));
        }

        foreach (EventHandlerDescriptor handler in _runtime.Model.EventHandlers)
        {
            if (!typeof(IEvent).IsAssignableFrom(handler.EventType))
            {
                continue;
            }

            definitions.Add(new SubscriptionDefinition(
                "handler:" + handler.Name,
                type => type == handler.EventType,
                ctx => handler.HandleAsync(ctx.Services, ctx.Event, ctx.Stored.ToContext(), ctx.CancellationToken)));
        }

        return definitions;
    }

    private sealed class Group(string key, StoredEvent[] events) : IGrouping<string, StoredEvent>
    {
        public string Key => key;

        public IEnumerator<StoredEvent> GetEnumerator()
        {
            return ((IEnumerable<StoredEvent>)events).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}

/// <summary>A subscription partition halted at a failing event (doc 06 §7, Stop policy).</summary>
/// <param name="Subscription">The subscription.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Position">The failing event's position.</param>
/// <param name="EventId">The failing event's id.</param>
/// <param name="EventType">The failing event's type.</param>
/// <param name="Attempts">Failed attempts so far.</param>
/// <param name="ErrorType">The last error's type.</param>
/// <param name="ErrorMessage">The last error's message.</param>
/// <param name="RetryAt">When the next attempt runs.</param>
public sealed record SubscriptionStall(
    string Subscription,
    int Partition,
    GlobalPosition Position,
    MessageId EventId,
    string EventType,
    int Attempts,
    string ErrorType,
    string ErrorMessage,
    DateTimeOffset RetryAt)
{
    /// <summary>When the partition first stopped at this event (the <c>sourceflow.subscriptions</c> health check, doc 08 §5).</summary>
    public DateTimeOffset StalledSince { get; init; }
}
