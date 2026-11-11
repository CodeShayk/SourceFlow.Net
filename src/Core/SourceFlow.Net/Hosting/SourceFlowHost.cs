using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SourceFlow.Diagnostics;
using SourceFlow.Runtime;
using SourceFlow.Transport;

namespace SourceFlow.Hosting;

/// <summary>
/// The hosted lifecycle (doc 01 §9): provision → start receiver, relay and subscriptions → ready → drain on stop.
/// Background loops wake on commit notifications and fall back to <see cref="SourceFlowOptions.PollInterval"/>.
/// </summary>
public sealed class SourceFlowHost : IHostedService, IHealthCheck, IAsyncDisposable
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

    internal SourceFlowHost(SourceFlowRuntime runtime, OutboxRelay relay, ReceiveEndpoint receiver, SubscriptionEngine subscriptions, HostSettings settings)
    {
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
        var topology = new TransportTopology(
            _runtime.Routing.Endpoint,
            [.. _runtime.Routing.PublishedTopics],
            [.. _runtime.Routing.Subscriptions.Select(s => s.Topic)],
            [.. _runtime.Routing.Commands.Values.Select(c => c.Endpoint).Distinct(StringComparer.Ordinal)]);
        await _runtime.Transport.ProvisionAsync(topology, _settings.Provisioning, cancellationToken).ConfigureAwait(false);

        _stopping = new CancellationTokenSource();
        CancellationToken stopping = _stopping.Token;
        _notifications = _runtime.Storage.Notifications.Subscribe(_commits.Set);

        if (_settings.Roles.HasFlag(SourceFlowRoles.Receiver))
        {
            await _receiver.StartAsync(stopping).ConfigureAwait(false);
            _loops.Add(Task.Run(() => _receiver.RunAsync(stopping), CancellationToken.None));
        }

        if (_settings.Roles.HasFlag(SourceFlowRoles.Relay))
        {
            _loops.Add(Task.Run(() => LoopAsync(ct => _relay.RunOnceAsync(ct), stopping), CancellationToken.None));
        }

        if (_settings.Roles.HasFlag(SourceFlowRoles.Subscriptions) && _subscriptions.Definitions.Count > 0)
        {
            _loops.Add(Task.Run(() => LoopAsync(ct => _subscriptions.RunOnceAsync(ct), stopping), CancellationToken.None));
        }

        _ready = true;
        Log.Started(_logger, _runtime.ServiceName, _runtime.Model.Modules.Count, _runtime.Model.Aggregates.Count, _subscriptions.Definitions.Count);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ready = false;
        if (_stopping is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _commits.Set();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(_runtime.Options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // Unfinished work is retried after restart (outbox rows stay, messages are redelivered).
        }

        await _receiver.DrainAsync(_runtime.Options.ShutdownTimeout).ConfigureAwait(false);
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
