using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Storage;
using SourceFlow.Transport;

namespace SourceFlow.Runtime;

/// <summary>
/// Samples the service's operational state on an interval (<see cref="SourceFlowOptions.MonitorInterval"/>) for the
/// observable gauges and the health checks (doc 08 §3.2, §5): the outbox backlog, parked dead letters, subscription lag
/// per partition, the log horizon age, active process managers and subscription leases. Gauges and checks read the latest
/// sample, so a scrape or a probe never queries the store; a check asks for a fresh sample when the last one is stale.
/// </summary>
internal sealed class SourceFlowMonitor
{
    /// <summary>Lag is counted up to this many events per partition (beyond it the gauge reports the cap).</summary>
    public const long LagCap = 100_000;

    private readonly SourceFlowRuntime _runtime;
    private readonly SubscriptionEngine _subscriptions;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sampling = new(1, 1);
    private readonly SemaphoreSlim _probing = new(1, 1);
    private volatile MonitorSnapshot? _latest;
    private DateTimeOffset? _underShareSince;
    private (DateTimeOffset At, TransportProbeResult Result)? _transport;

    private readonly bool _subscribing;

    public SourceFlowMonitor(SourceFlowRuntime runtime, SubscriptionEngine subscriptions, Hosting.HostSettings settings)
    {
        _runtime = runtime;
        _subscribing = settings.Roles.HasFlag(Hosting.SourceFlowRoles.Subscriptions);
        _subscriptions = subscriptions;
        _logger = runtime.Logger<SourceFlowMonitor>();
        Meter meter = runtime.Metrics.Meter;
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.OutboxPending, () => Read<int>(s => s.Outbox?.Select(o => Measure(o.Pending, (SourceFlowTelemetry.Tags.Partition, o.Partition)))),
            "{message}", "Undelivered outbox messages per partition.");
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.OutboxAge, () => Read<double>(s => s.Outbox?.Select(o => Measure(OutboxAge(s, o).TotalSeconds, (SourceFlowTelemetry.Tags.Partition, o.Partition)))),
            "s", "How long the oldest due outbox message has waited (durability SLO).");
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.DeadLetterCount, () => Read<int>(s => s.DeadLetters?.Select(d => Measure(d.Value, (SourceFlowTelemetry.Tags.Consumer, d.Key)))),
            "{message}", "Parked dead letters per consumer.");
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.SubscriptionLag, () => Read<long>(s => s.Partitions.Where(p => p.Lag is not null).Select(p => Measure(p.Lag!.Value, Subscription(p)))),
            "{event}", "Events not yet processed per subscription partition.");
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.SubscriptionLagTime, () => Read<double>(s => s.Partitions.Select(p => Measure(p.LagTime.TotalSeconds, Subscription(p)))),
            "s", "Age of the oldest unprocessed event per subscription partition (freshness SLO).");
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.HorizonAge, () => Read<double>(s => s.HorizonAge is { } age ? [Measure(age.TotalSeconds)] : null),
            "s", "How long the oldest writing transaction has held the subscription horizon back.");
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.ProcessActive, () => Read<int>(s => s.Processes?.Select(p => Measure(p.Value, (SourceFlowTelemetry.Tags.ProcessType, p.Key)))),
            "{instance}", "Process-manager instances not completed, per process type.");
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.WorkflowActive, () => Read<int>(s => s.Workflows?.Select(w => Measure(w.Count,
                (SourceFlowTelemetry.Tags.Workflow, w.Workflow), (SourceFlowTelemetry.Tags.WorkflowVersion, w.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)), (SourceFlowTelemetry.Tags.WorkflowStatus, w.Status)))),
            "{instance}", "Workflow instances not finished, per workflow version and status.");
        meter.CreateObservableGauge(SourceFlowTelemetry.Instruments.LeaseOwned, () => Read<int>(s => [Measure(s.Leases.Owned, (SourceFlowTelemetry.Tags.LeaseKind, "subscription"))]),
            "{lease}", "Subscription partition leases this node owns.");
    }

    /// <summary>The latest sample, or null before the first.</summary>
    public MonitorSnapshot? Latest => _latest;

    /// <summary>Samples until <paramref name="stopping"/> is cancelled; never throws otherwise.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stopping).ConfigureAwait(false);
                await Task.Delay(_runtime.Options.MonitorInterval, TimeProvider.System, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>The latest sample when it is recent (two intervals), otherwise a fresh one.</summary>
    public async ValueTask<MonitorSnapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        MonitorSnapshot? latest = _latest;
        return latest is not null && _runtime.Time.GetUtcNow() - latest.At <= _runtime.Options.MonitorInterval * 2
            ? latest
            : await SampleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes a sample now. Each reading fails on its own: the others are still taken, and the error is reported.</summary>
    public async ValueTask<MonitorSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        await _sampling.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var errors = new List<string>();
            IStoreMonitor? store = _runtime.Storage.Monitor;
            IReadOnlyList<OutboxBacklog>? outbox = await TryAsync<IReadOnlyList<OutboxBacklog>>(store is null ? null : store.GetOutboxBacklogAsync, "outbox", errors, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, int>? deadLetters = await TryAsync<IReadOnlyDictionary<string, int>>(store is null ? null : store.CountParkedDeadLettersAsync, "dead letters", errors, cancellationToken).ConfigureAwait(false);
            TimeSpan? horizon = store is null ? null : await TryAsync<TimeSpan?>(async ct => await store.GetHorizonAgeAsync(ct).ConfigureAwait(false), "horizon", errors, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, int>? processes = await TryAsync<IReadOnlyDictionary<string, int>>(store is null ? null : store.CountActiveProcessesAsync, "process managers", errors, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PartitionReading> partitions = await TryAsync<IReadOnlyList<PartitionReading>>(ReadPartitionsAsync, "subscriptions", errors, cancellationToken).ConfigureAwait(false) ?? [];
            IReadOnlyList<Workflows.WorkflowStatusCount>? workflows = store is null || !_runtime.Model.ProcessManagers.Any(p => p.IsWorkflow) ? null
                : await TryAsync<IReadOnlyList<Workflows.WorkflowStatusCount>>(store.CountActiveWorkflowsAsync, "workflows", errors, cancellationToken).ConfigureAwait(false);
            var snapshot = new MonitorSnapshot(_runtime.Time.GetUtcNow(), outbox, deadLetters, partitions, horizon, processes, ReadLeases(), errors) { Workflows = workflows };
            _latest = snapshot;
            return snapshot;
        }
        finally
        {
            _sampling.Release();
        }
    }

    /// <summary>Probes the broker, reusing a result younger than <see cref="HealthThresholds.TransportCacheDuration"/>.</summary>
    public async ValueTask<TransportProbeResult> ProbeTransportAsync(CancellationToken cancellationToken)
    {
        if (_runtime.Transport is not ITransportProbe probe)
        {
            return new TransportProbeResult(TransportProbeStatus.NotProbed, $"{_runtime.Transport.SystemName} does not support probing.");
        }

        await _probing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _runtime.Time.GetUtcNow();
            if (_transport is { } cached && now - cached.At < _runtime.Options.Health.TransportCacheDuration)
            {
                return cached.Result;
            }

            TransportProbeResult result;
            try
            {
                await probe.ProbeAsync(_runtime.Routing.Endpoint, cancellationToken).ConfigureAwait(false);
                result = new TransportProbeResult(TransportProbeStatus.Reachable, $"{_runtime.Transport.SystemName} answers for '{_runtime.Routing.Endpoint}'.");
            }
            catch (TransportThrottledException e)
            {
                result = new TransportProbeResult(TransportProbeStatus.Throttled, e.Message, e);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                result = new TransportProbeResult(TransportProbeStatus.Unreachable, e.Message, e);
            }

            _transport = (now, result);
            return result;
        }
        finally
        {
            _probing.Release();
        }
    }

    private async ValueTask<IReadOnlyList<PartitionReading>> ReadPartitionsAsync(CancellationToken ct)
    {
        IReadOnlyList<SubscriptionStall> stalls = _subscriptions.Stalls;
        DateTimeOffset now = _runtime.Time.GetUtcNow();
        var readings = new List<PartitionReading>();
        foreach (SubscriptionDefinition definition in _subscriptions.Definitions)
        {
            int partitions = _runtime.PartitionsOf(definition.Name);
            for (int partition = 0; partition < partitions; partition++)
            {
                BucketRange buckets = Buckets.Range(partition, partitions);
                GlobalPosition checkpoint = await _runtime.Storage.Checkpoints.GetAsync(definition.Name, partition, ct).ConfigureAwait(false);
                LogBatch next = await _runtime.Storage.Log.ReadAsync(checkpoint, buckets, 1, ct).ConfigureAwait(false);
                TimeSpan lagTime = next.Events.Count == 0 ? TimeSpan.Zero : Max(TimeSpan.Zero, now - next.Events[0].CreatedAt);
                long? lag = next.Events.Count == 0 ? 0
                    : _runtime.Storage.Monitor is { } monitor ? await monitor.CountEventsAfterAsync(checkpoint, buckets, LagCap, ct).ConfigureAwait(false)
                    : null;
                readings.Add(new PartitionReading(definition.Name, partition, lag, lagTime, stalls.FirstOrDefault(s => s.Subscription == definition.Name && s.Partition == partition)));
            }
        }

        return readings;
    }

    private LeaseReading ReadLeases()
    {
        bool subscribing = _subscribing && _subscriptions.HasSubscriptions && _subscriptions.Definitions.Count > 0;
        int owned = _subscriptions.OwnedLeases;
        int share = subscribing ? _subscriptions.FairShare : 0;
        DateTimeOffset now = _runtime.Time.GetUtcNow();
        _underShareSince = owned < share ? _underShareSince ?? now : null;
        return new LeaseReading(owned, share, _underShareSince, subscribing);
    }

    private async Task<T?> TryAsync<T>(Func<CancellationToken, ValueTask<T>>? read, string what, List<string> errors, CancellationToken ct)
    {
        if (read is null)
        {
            return default;
        }

        try
        {
            return await read(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            errors.Add($"{what}: {e.Message}");
            Log.MonitorReadingFailed(_logger, e, what);
            return default;
        }
    }

    private IEnumerable<Measurement<T>> Read<T>(Func<MonitorSnapshot, IEnumerable<Measurement<T>>?> select)
        where T : struct
    {
        return _latest is { } snapshot ? select(snapshot) ?? [] : [];
    }

    private static TimeSpan OutboxAge(MonitorSnapshot snapshot, OutboxBacklog backlog)
    {
        return backlog.OldestDueAt is { } due ? Max(TimeSpan.Zero, snapshot.At - due) : TimeSpan.Zero;
    }

    private static (string, object?)[] Subscription(PartitionReading p)
    {
        return [(SourceFlowTelemetry.Tags.Subscription, p.Subscription), (SourceFlowTelemetry.Tags.Partition, p.Partition)];
    }

    private static Measurement<T> Measure<T>(T value, params (string Key, object? Value)[] tags)
        where T : struct
    {
        return new Measurement<T>(value, tags.Select(t => new KeyValuePair<string, object?>(t.Key, t.Value)).ToArray());
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b)
    {
        return a > b ? a : b;
    }

    /// <summary>The age of the oldest due outbox message over every partition.</summary>
    public static TimeSpan OldestOutboxAge(MonitorSnapshot snapshot)
    {
        return snapshot.Outbox is null || snapshot.Outbox.Count == 0 ? TimeSpan.Zero : snapshot.Outbox.Max(o => OutboxAge(snapshot, o));
    }
}

/// <summary>One monitor sample; null readings were not taken (no adapter support) or failed (see <see cref="Errors"/>).</summary>
internal sealed record MonitorSnapshot(
    DateTimeOffset At,
    IReadOnlyList<OutboxBacklog>? Outbox,
    IReadOnlyDictionary<string, int>? DeadLetters,
    IReadOnlyList<PartitionReading> Partitions,
    TimeSpan? HorizonAge,
    IReadOnlyDictionary<string, int>? Processes,
    LeaseReading Leases,
    IReadOnlyList<string> Errors)
{
    /// <summary>Unfinished workflow instances per workflow, version and status; null without workflows or a store monitor.</summary>
    public IReadOnlyList<Workflows.WorkflowStatusCount>? Workflows { get; init; }
}

/// <summary>A subscription partition: events behind (null when the store cannot count them), the oldest one's age, and its stall.</summary>
internal sealed record PartitionReading(string Subscription, int Partition, long? Lag, TimeSpan LagTime, SubscriptionStall? Stall);

/// <summary>Subscription leases this node owns, its fair share, and since when it has been under that share.</summary>
internal sealed record LeaseReading(int Owned, int FairShare, DateTimeOffset? UnderShareSince, bool Subscribing);

/// <summary>What a transport probe found.</summary>
internal enum TransportProbeStatus
{
    NotProbed,
    Reachable,
    Throttled,
    Unreachable,
}

/// <summary>A transport probe result.</summary>
internal sealed record TransportProbeResult(TransportProbeStatus Status, string Detail, Exception? Error = null);
