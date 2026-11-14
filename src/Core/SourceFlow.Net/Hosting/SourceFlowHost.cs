using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Transport;

namespace SourceFlow.Hosting;

/// <summary>
/// The hosted lifecycle (doc 01 §9): provision → start receiver, relay and subscriptions → ready → drain on stop.
/// Background loops wake on commit notifications and fall back to <see cref="SourceFlowOptions.PollInterval"/>.
/// </summary>
public sealed class SourceFlowHost : IHostedLifecycleService, IHealthCheck, IAsyncDisposable
{
    private readonly SourceFlowRuntime _runtime;
    private readonly OutboxRelay _relay;
    private readonly ReceiveEndpoint _receiver;
    private readonly SubscriptionEngine _subscriptions;
    private readonly HostSettings _settings;
    private readonly ILogger _logger;
    private readonly AsyncSignal _commits = new();
    private readonly List<Task> _loops = [];
    private CancellationTokenSource? _stopping;
    private IDisposable? _notifications;
    private volatile bool _ready;

    private readonly Query.QueryService? _queries;
    private DeadLetterIngestor? _ingestor;
    private readonly SourceFlowMonitor _monitor;

    internal SourceFlowHost(SourceFlowRuntime runtime, OutboxRelay relay, ReceiveEndpoint receiver, SubscriptionEngine subscriptions, HostSettings settings, SourceFlowMonitor monitor, Query.QueryService? queries = null)
    {
        _monitor = monitor;
        _queries = queries;
        _runtime = runtime;
        _relay = relay;
        _receiver = receiver;
        _subscriptions = subscriptions;
        _settings = settings;
        _logger = runtime.Logger<SourceFlowHost>();
    }

    /// <summary>True after startup completed.</summary>
    public bool IsReady => _ready;

    /// <summary>The runtime context.</summary>
    public SourceFlowRuntime Runtime => _runtime;

    /// <summary>Messages received and not yet settled.</summary>
    public int ReceiveInFlight => _receiver.InFlight;

    /// <summary>True when every subscription partition has caught up with the log.</summary>
    public ValueTask<bool> SubscriptionsCaughtUpAsync(CancellationToken cancellationToken)
    {
        return _subscriptions.IsCaughtUpAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CheckTransportLimits();
        await _runtime.Transport.ProvisionAsync(_runtime.Topology(), _settings.Provisioning, cancellationToken).ConfigureAwait(false);

        await RegisterSchemasAsync(cancellationToken).ConfigureAwait(false);
        CheckDomainQueries();
        await CheckWorkflowVersionsAsync(cancellationToken).ConfigureAwait(false);
        _stopping = new CancellationTokenSource();
        CancellationToken stopping = _stopping.Token;
        _runtime.Snapshots.Start();
        _notifications = _runtime.Storage.Notifications.Subscribe(_commits.Set);

        if (_settings.Roles.HasFlag(SourceFlowRoles.Receiver))
        {
            await _receiver.StartAsync(stopping).ConfigureAwait(false);
            _loops.Add(Task.Run(() => _receiver.RunAsync(stopping), CancellationToken.None));
            if (_runtime.Options.IngestBrokerDeadLetters && _runtime.Transport.Capabilities.NativeDeadLetter)
            {
                _ingestor = new DeadLetterIngestor(_runtime);
                _loops.Add(Task.Run(() => _ingestor.RunAsync(stopping), CancellationToken.None));
            }
        }

        if (_settings.Roles.HasFlag(SourceFlowRoles.Relay))
        {
            _loops.Add(Task.Run(() => LoopAsync(ct => _relay.RunOnceAsync(ct), stopping), CancellationToken.None));
            if (_runtime.Storage.Retention is { } retention)
            {
                _loops.Add(Task.Run(() => RetentionLoopAsync(retention, stopping), CancellationToken.None));
            }
        }

        if (_settings.Roles.HasFlag(SourceFlowRoles.Subscriptions) && _subscriptions.HasSubscriptions)
        {
            if (_runtime.Storage.Nodes is { } nodes)
            {
                await HeartbeatAsync(nodes, stopping).ConfigureAwait(false);   // know the live nodes before taking leases
                _loops.Add(Task.Run(() => HeartbeatLoopAsync(nodes, stopping), CancellationToken.None));
            }

            _loops.Add(Task.Run(() => LoopAsync(ct => _subscriptions.RunOnceAsync(ct), stopping), CancellationToken.None));
        }

        _loops.Add(Task.Run(() => _monitor.RunAsync(stopping), CancellationToken.None));
        _ready = true;
        Log.Started(_logger, _runtime.ServiceName, _runtime.Model.Modules.Count, _runtime.Model.Aggregates.Count, _subscriptions.Definitions.Count);
    }

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// The host is about to stop (doc 01 §9): stop receiving and start draining before any hosted service stops, so the
    /// messages already queued here are released for another instance at once (RX-R6).
    /// </summary>
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _ready = false;
        _receiver.BeginDrain();
        if (_stopping is { } stopping)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        _commits.Set();
    }

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops within one <see cref="SourceFlowOptions.ShutdownTimeout"/> in all (RX-R6): the loops end, leases are handed
    /// back, the messages in progress finish (and are cancelled when the time is up), and pending snapshots are written.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ready = false;
        if (_stopping is null)
        {
            return;
        }

        long started = TimeProvider.System.GetTimestamp();
        TimeSpan Remaining()
        {
            TimeSpan left = _runtime.Options.ShutdownTimeout - TimeProvider.System.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        _receiver.BeginDrain();
        await _stopping.CancelAsync().ConfigureAwait(false);
        _commits.Set();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(Remaining(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // Unfinished work is retried after restart (outbox rows stay, messages are redelivered).
        }

        if (_settings.Roles.HasFlag(SourceFlowRoles.Relay))
        {
            await _relay.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);   // the next node relays at once
        }

        if (_settings.Roles.HasFlag(SourceFlowRoles.Subscriptions) && _subscriptions.HasSubscriptions)
        {
            try
            {
                await _subscriptions.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
                if (_runtime.Storage.Nodes is { } nodes)
                {
                    await nodes.LeaveAsync(_runtime.Options.NodeId, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.HeartbeatFailed(_logger, exception);   // the leases expire after their TTL instead
            }
        }

        await _receiver.DrainAsync(Remaining()).ConfigureAwait(false);
        await _runtime.Snapshots.StopAsync(Remaining()).ConfigureAwait(false);
        _notifications?.Dispose();
    }

    /// <summary>Wakes the background loops now (for example after the test clock moved).</summary>
    public void Pulse()
    {
        _commits.Set();
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_ready ? HealthCheckResult.Healthy("SourceFlow is running.") : HealthCheckResult.Unhealthy("SourceFlow is not started."));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _stopping?.Dispose();
        await _receiver.DisposeAsync().ConfigureAwait(false);
        if (_ingestor is not null)
        {
            await _ingestor.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ADR-006, doc 04 §6: the inbox must remember a message at least as long as the broker can redeliver it, or a late
    // redelivery is handled twice (CR-12): fatal in Validate mode, a warning otherwise. Delayed retries longer than the
    // broker can wait go through the outbox, except on an ordered endpoint, where they are capped, and said so (CR-13).
    private void CheckTransportLimits()
    {
        TransportCapabilities capabilities = _runtime.Transport.Capabilities;
        if (_runtime.Options.InboxRetention < capabilities.MaxRetention)
        {
            string problem = $"SourceFlowOptions.InboxRetention ({_runtime.Options.InboxRetention}) is shorter than the transport's message retention ({capabilities.MaxRetention}): "
                + "a message redelivered after its inbox entry expired would be handled twice (ADR-006). Set InboxRetention to at least the broker's retention.";
            if (_settings.Provisioning == ProvisioningMode.Validate)
            {
                throw new InvalidOperationException(problem);
            }

            Log.InboxRetentionShort(_logger, problem);
        }

        TimeSpan[] capped = [.. _runtime.Options.DelayedRetries.Where(d => d > capabilities.MaxDelay)];
        if (capped.Length > 0 && _runtime.Routing.OrderedEndpoint && _settings.Roles.HasFlag(SourceFlowRoles.Receiver))
        {
            Log.DelayedRetriesCapped(_logger, capabilities.MaxDelay, string.Join(", ", capped));
        }
    }

    // Batched retention cleanup (doc 05 §9): cutoffs on the business clock, the interval on the system clock.
    // doc 14 Q-B4 / SF0018: every domain query binds against this service's catalog, or startup fails.
    private void CheckDomainQueries()
    {
        if (_runtime.Model.DomainQueries.Count == 0 || _queries is not { } service)
        {
            return;
        }

        var problems = new List<string>();
        foreach (Query.DomainQueryDescriptor query in _runtime.Model.DomainQueries)
        {
            problems.AddRange(service.CheckDomainQuery(query).Select(p => $"{query.Name} v{query.Version}: {p}"));
        }

        if (problems.Count > 0)
        {
            throw new Query.DomainQueryException("SF0018: domain queries don't match this service's catalog: " + string.Join(" ", problems));
        }
    }

    // doc 15 §5.6: in-flight instances finish on the version they started on, so a version of a workflow this service
    // still runs can only be removed once none of its instances is active. Fails startup otherwise.
    private async Task CheckWorkflowVersionsAsync(CancellationToken cancellationToken)
    {
        HashSet<string> registered = [.. _runtime.Model.ProcessManagers.Where(p => p.IsWorkflow).Select(p => p.Name)];
        if (registered.Count == 0)
        {
            return;
        }

        if (_runtime.Storage.Monitor is not { } monitor)
        {
            Log.WorkflowVersionsUnchecked(_logger);
            return;
        }

        HashSet<string> names = [.. registered.Select(p => Workflows.WorkflowRuntime.TryParseProcessName(p, out string? name, out _) ? name : p)];
        IReadOnlyDictionary<string, int> active = await monitor.CountActiveProcessesAsync(cancellationToken).ConfigureAwait(false);
        List<string> removed =
        [
            .. active
                .Where(a => a.Value > 0 && !registered.Contains(a.Key)
                    && Workflows.WorkflowRuntime.TryParseProcessName(a.Key, out string? name, out _) && names.Contains(name))
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => $"{a.Key} has {a.Value} active instance(s)"),
        ];
        if (removed.Count > 0)
        {
            throw new Workflows.WorkflowVersionRemovedException(
                $"A workflow version with active instances is no longer registered: {string.Join("; ", removed)}. Keep the version until " +
                "its instances finish (`dotnet sourceflow workflow list --name <name> --version <v> --active`), or abort them first.");
        }
    }

    // doc 14 §2.2: register this service's schemas; a changed published (type, version) fails startup.
    private async Task RegisterSchemasAsync(CancellationToken cancellationToken)
    {
        if (_runtime.Storage.Schemas is not { } store || _runtime.Options.SchemaRegistration == SchemaRegistrationMode.Off)
        {
            return;
        }

        Catalog.SchemaCatalog catalog = Catalog.SchemaCatalog.Build(_runtime.Model);
        IReadOnlyList<Catalog.SchemaConflict> conflicts = await store.RegisterAsync(_runtime.ServiceName, catalog.Entries, cancellationToken).ConfigureAwait(false);
        if (conflicts.Count == 0)
        {
            return;
        }

        // CR-17: fingerprints registered under the earlier rule hashed the annotations too. Recomputed from the stored
        // schema, an unchanged structure gives today's fingerprint: those are re-registered, once, and are not conflicts.
        Dictionary<(string, int), Catalog.SchemaEntry> stored = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(e => (e.Type, e.Version));
        Catalog.SchemaEntry[] unchanged = [.. conflicts
            .Where(c => stored.TryGetValue((c.Type, c.Version), out Catalog.SchemaEntry? registered) && registered.Fingerprint == c.CurrentFingerprint)
            .Select(c => catalog.TryGet(c.Type, c.Version, out Catalog.SchemaEntry? current) ? current : null)
            .OfType<Catalog.SchemaEntry>()];
        if (unchanged.Length > 0)
        {
            await store.ReplaceAsync(unchanged, cancellationToken).ConfigureAwait(false);
            conflicts = [.. conflicts.Where(c => !unchanged.Any(u => u.Type == c.Type && u.Version == c.Version))];
            if (conflicts.Count == 0)
            {
                return;
            }
        }

        if (_runtime.Options.SchemaRegistration == SchemaRegistrationMode.Enforce)
        {
            throw new Catalog.SchemaConflictException(conflicts);
        }

        foreach (Catalog.SchemaConflict conflict in conflicts)
        {
            Log.SchemaConflict(_logger, conflict.Type, conflict.Version, conflict.RegisteredFingerprint, conflict.CurrentFingerprint);
        }
    }

    // doc 06 §4: heartbeat into the node registry and keep the live count the fair share uses.
    private async Task HeartbeatAsync(INodeRegistry nodes, CancellationToken stopping)
    {
        TimeSpan window = _runtime.Options.NodeHeartbeatInterval * 3;
        _runtime.LiveSubscriptionNodes = await nodes.HeartbeatAsync(_runtime.Options.NodeId, (int)SourceFlowRoles.Subscriptions, window, stopping).ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(INodeRegistry nodes, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_runtime.Options.NodeHeartbeatInterval, TimeProvider.System, stopping).ConfigureAwait(false);
                await HeartbeatAsync(nodes, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.HeartbeatFailed(_logger, exception);
            }
        }
    }

    private async Task RetentionLoopAsync(IStoreRetention retention, CancellationToken stopping)
    {
        SourceFlowOptions options = _runtime.Options;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset now = _runtime.Time.GetUtcNow();
                int inbox = await retention.PurgeInboxAsync(now - options.InboxRetention, options.RetentionBatchSize, stopping).ConfigureAwait(false);
                int deadLetters = await retention.PurgeDeadLettersAsync(now - options.DeadLetterRetention, options.RetentionBatchSize, stopping).ConfigureAwait(false);
                if (inbox + deadLetters > 0)
                {
                    Log.RetentionPurged(_logger, inbox, deadLetters);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.RetentionFailed(_logger, exception);
            }

            try
            {
                await Task.Delay(options.RetentionInterval, TimeProvider.System, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task LoopAsync(Func<CancellationToken, ValueTask<int>> pass, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            Task wake = _commits.WaitTask;
            int work;
            try
            {
                work = await pass(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log.OutboxLoopFailed(_logger, exception);
                work = 0;
            }

            if (work == 0)
            {
                try
                {
                    // Poll fallback on the system clock: a fake business clock must not stop the loops.
                    await wake.WaitAsync(_runtime.Options.PollInterval, TimeProvider.System, stopping).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Poll fallback.
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}

/// <summary>A reusable wake-up signal: <see cref="Set"/> completes the current <see cref="WaitTask"/>.</summary>
internal sealed class AsyncSignal
{
    private TaskCompletionSource _current = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitTask => Volatile.Read(ref _current).Task;

    public void Set()
    {
        Interlocked.Exchange(ref _current, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
}
