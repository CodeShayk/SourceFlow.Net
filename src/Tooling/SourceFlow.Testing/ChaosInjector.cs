using SourceFlow.Runtime;

namespace SourceFlow.Testing;

/// <summary>The node "crashed" at a fault point (<see cref="ChaosInjector.CrashAt"/>).</summary>
public sealed class NodeCrashedException(string point) : Exception($"Injected crash at '{point}'.")
{
    /// <summary>The fault point.</summary>
    public string Point { get; } = point;
}

/// <summary>
/// An <see cref="IFaultInjector"/> for chaos tests (plan 06 CH-01..06, CH-09). Register one per node
/// (<c>services.AddSingleton&lt;IFaultInjector&gt;(injector)</c>) and arm it:
/// <list type="bullet">
/// <item><see cref="CrashAt"/>: the node dies at the n-th hit of a point. That call throws; from then on every point blocks
/// until the node is stopped, so the dead node writes nothing more and its leases expire, as after a process kill.</item>
/// <item><see cref="FreezeAt"/>: the node stalls at the n-th hit until <see cref="Resume"/>, as under SIGSTOP; it then
/// continues with whatever it held, which fencing must reject.</item>
/// </list>
/// </summary>
public sealed class ChaosInjector : IFaultInjector
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _hits = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<string> _triggered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (string Point, int Hit, bool Crash)? _armed;
    private volatile bool _dead;

    /// <summary>Completes with the point when the armed fault fires.</summary>
    public Task<string> Triggered => _triggered.Task;

    /// <summary>True once the node crashed.</summary>
    public bool IsDead => _dead;

    /// <summary>Dies at the <paramref name="hit"/>-th time the node reaches <paramref name="point"/>.</summary>
    public ChaosInjector CrashAt(string point, int hit = 1)
    {
        Arm(point, hit, crash: true);
        return this;
    }

    /// <summary>Stalls at the <paramref name="hit"/>-th time the node reaches <paramref name="point"/>, until <see cref="Resume"/>.</summary>
    public ChaosInjector FreezeAt(string point, int hit = 1)
    {
        Arm(point, hit, crash: false);
        return this;
    }

    /// <summary>Lets a frozen node continue.</summary>
    public void Resume()
    {
        _resumed.TrySetResult();
    }

    /// <summary>How many times the node reached <paramref name="point"/>.</summary>
    public int Hits(string point)
    {
        lock (_gate)
        {
            return _hits.GetValueOrDefault(point);
        }
    }

    /// <inheritdoc />
    public async ValueTask InjectAsync(string point, CancellationToken cancellationToken)
    {
        if (_dead)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);   // a dead node does nothing more
        }

        bool fire;
        bool crash;
        lock (_gate)
        {
            int hit = _hits[point] = _hits.GetValueOrDefault(point) + 1;
            fire = _armed is { } armed && armed.Point == point && armed.Hit == hit;
            crash = fire && _armed!.Value.Crash;
            if (fire)
            {
                _armed = null;
            }
        }

        if (!fire)
        {
            return;
        }

        if (crash)
        {
            _dead = true;
            _triggered.TrySetResult(point);
            throw new NodeCrashedException(point);
        }

        _triggered.TrySetResult(point);
        await _resumed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Arm(string point, int hit, bool crash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(point);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hit);
        lock (_gate)
        {
            _armed = (point, _hits.GetValueOrDefault(point) + hit, crash);
        }
    }
}
