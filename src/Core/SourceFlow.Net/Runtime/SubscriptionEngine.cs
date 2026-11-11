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
internal sealed record SubscriptionDefinition(string Name, Func<Type, bool> Handles, Func<SubscriptionContext, ValueTask> Apply);

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
        Definitions = BuildDefinitions();
    }

    public IReadOnlyList<SubscriptionDefinition> Definitions { get; }

    /// <summary>Runs one pass over every subscription partition; returns the number of events processed.</summary>
    public async ValueTask<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        int processed = 0;
        int partitions = _runtime.Options.SubscriptionPartitions;
        foreach (SubscriptionDefinition definition in Definitions)
        {
            for (int partition = 0; partition < partitions; partition++)
            {
                processed += await RunPartitionAsync(definition, partition, partitions, cancellationToken).ConfigureAwait(false);
            }
        }

        return processed;
    }

    /// <summary>True when every partition's checkpoint has reached the log horizon.</summary>
    public async ValueTask<bool> IsCaughtUpAsync(CancellationToken cancellationToken)
    {
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
            return 0;
        }

        GlobalPosition checkpoint = await _runtime.Storage.Checkpoints.GetAsync(definition.Name, partition, cancellationToken).ConfigureAwait(false);
        LogBatch batch = await _runtime.Storage.Log.ReadAsync(checkpoint, Buckets.Range(partition, partitions), _runtime.Options.SubscriptionBatchSize, cancellationToken).ConfigureAwait(false);
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
                    // Isolate the poison event: apply one by one, park what still fails.
                    foreach (StoredEvent single in events)
                    {
                        await ApplyIsolatedAsync(definition, partition, lease, single, cancellationToken).ConfigureAwait(false);
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
        await using var projections = new ProjectionScope(_runtime, session, scope.ServiceProvider);
        foreach (StoredEvent stored in events)
        {
            if (!_runtime.Model.Registry.TryGet(stored.Type, stored.TypeVersion, out MessageTypeInfo? type) || !Relevant(definition, type))
            {
                continue;
            }

            var @event = (IEvent)_decoder.Deserialize(stored, upcast: true);
            await definition.Apply(new SubscriptionContext(session, @event, stored, scope.ServiceProvider, projections, cancellationToken)).ConfigureAwait(false);
        }

        // Session projections (EF Core) save before the fenced checkpoint, in the same transaction (doc 05 §10).
        await projections.CompleteAsync(cancellationToken).ConfigureAwait(false);
        session.SaveCheckpoint(definition.Name, partition, events[^1].Position, lease);
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ApplyIsolatedAsync(SubscriptionDefinition definition, int partition, LeaseToken lease, StoredEvent stored, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= _runtime.Options.ImmediateRetries; attempt++)
        {
            try
            {
                await ApplyAsync(definition, partition, lease, stored.TenantId, [stored], cancellationToken).ConfigureAwait(false);
                return;
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

    private IReadOnlyList<SubscriptionDefinition> BuildDefinitions()
    {
        var definitions = new List<SubscriptionDefinition>();
        foreach (ProjectionDescriptor projection in _runtime.AsyncProjections)
        {
            definitions.Add(new SubscriptionDefinition(
                "projection:" + projection.Collection,
                projection.Handles,
                ctx => ctx.Projections.ApplyAsync(projection, ctx.Event, ctx.Stored.ToContext(), ctx.CancellationToken)));
        }

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
