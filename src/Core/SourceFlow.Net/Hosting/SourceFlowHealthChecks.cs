using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Hosting;

/// <summary>The SourceFlow health checks (doc 08 §5), by the suffix of their registration name.</summary>
public static class SourceFlowHealthChecks
{
    /// <summary>Startup done (validation, provisioning) and receivers started. Tag <c>ready</c>.</summary>
    public const string Ready = "ready";

    /// <summary>The store answers with the expected schema; degraded when slow. Tag <c>ready</c>.</summary>
    public const string Store = "store";

    /// <summary>The broker answers for the service's endpoint (cached); degraded when throttled. Tag <c>ready</c>.</summary>
    public const string Transport = "transport";

    /// <summary>The oldest due outbox message's age.</summary>
    public const string Outbox = "outbox";

    /// <summary>Subscription lag, stalls and the log horizon.</summary>
    public const string Subscriptions = "subscriptions";

    /// <summary>The node owns its fair share of subscription leases.</summary>
    public const string Leases = "leases";

    /// <summary>The key service issues data keys (cached); healthy when payload protection is off (doc 10 §3).</summary>
    public const string Keys = "keys";

    /// <summary>The tag of the readiness checks (store and transport are dependencies: never use them for liveness).</summary>
    public const string ReadyTag = "ready";

    /// <summary>The tag every SourceFlow check carries.</summary>
    public const string Tag = "sourceflow";

    /// <summary>Every check suffix, in registration order.</summary>
    public static IReadOnlyList<string> All { get; } = [Ready, Store, Transport, Outbox, Subscriptions, Leases, Keys];
}

/// <summary>One of the SourceFlow health checks (doc 08 §5); thresholds come from <see cref="SourceFlowOptions.Health"/>.</summary>
internal sealed class SourceFlowHealthCheck(string check, SourceFlowHost host, SourceFlowMonitor monitor) : IHealthCheck
{
    private SourceFlowRuntime Runtime => host.Runtime;

    private HealthThresholds Thresholds => Runtime.Options.Health;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return check switch
        {
            SourceFlowHealthChecks.Ready => host.CheckHealthAsync(context, cancellationToken),
            SourceFlowHealthChecks.Store => StoreAsync(cancellationToken),
            SourceFlowHealthChecks.Transport => TransportAsync(cancellationToken),
            SourceFlowHealthChecks.Outbox => OutboxAsync(cancellationToken),
            SourceFlowHealthChecks.Subscriptions => SubscriptionsAsync(cancellationToken),
            SourceFlowHealthChecks.Leases => LeasesAsync(cancellationToken),
            SourceFlowHealthChecks.Keys => KeysAsync(cancellationToken),
            _ => throw new InvalidOperationException($"Unknown SourceFlow health check '{check}'."),
        };
    }

    // A protected send needs a data key: a key service that refuses or is down fails those commands (doc 10 §3).
    private async Task<HealthCheckResult> KeysAsync(CancellationToken ct)
    {
        if (Runtime.Protector is not { } protector)
        {
            return HealthCheckResult.Healthy("Payload protection is off.");
        }

        Exception? failure = await protector.ProbeAsync(Thresholds.TransportCacheDuration, ct).ConfigureAwait(false);
        return failure switch
        {
            null => HealthCheckResult.Healthy("The key service issues data keys."),
            Security.KeyProviderException { IsTransient: true } => HealthCheckResult.Unhealthy($"The key service is unavailable: {failure.Message}", failure),
            _ => HealthCheckResult.Unhealthy($"The key service refuses to issue data keys: {failure.Message}", failure),
        };
    }

    private async Task<HealthCheckResult> StoreAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        StoreProbe probe;
        try
        {
            probe = Runtime.Storage.Monitor is { } storeMonitor
                ? await storeMonitor.ProbeAsync(ct).ConfigureAwait(false)
                : await HorizonProbeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"The store is unreachable: {e.Message}", e);
        }

        TimeSpan latency = Stopwatch.GetElapsedTime(started);
        var data = new Dictionary<string, object> { ["latencyMs"] = Math.Round(latency.TotalMilliseconds, 1), ["detail"] = probe.Detail };
        if (!probe.SchemaCurrent)
        {
            return HealthCheckResult.Unhealthy($"The store schema does not match: {probe.Detail}", data: data);
        }

        return latency > Thresholds.StoreLatencyDegraded
            ? HealthCheckResult.Degraded($"The store answered in {Ms(latency)} (over {Ms(Thresholds.StoreLatencyDegraded)}).", data: data)
            : HealthCheckResult.Healthy($"The store answered in {Ms(latency)}.", data);
    }

    private async ValueTask<StoreProbe> HorizonProbeAsync(CancellationToken ct)
    {
        await Runtime.Storage.Log.GetSafeHorizonAsync(ct).ConfigureAwait(false);
        return new StoreProbe(SchemaCurrent: true, "The log answered (the store reports no schema version).");
    }

    private async Task<HealthCheckResult> TransportAsync(CancellationToken ct)
    {
        TransportProbeResult result = await monitor.ProbeTransportAsync(ct).ConfigureAwait(false);
        var data = new Dictionary<string, object> { ["system"] = Runtime.Transport.SystemName, ["endpoint"] = Runtime.Routing.Endpoint };
        return result.Status switch
        {
            TransportProbeStatus.Reachable or TransportProbeStatus.NotProbed => HealthCheckResult.Healthy(result.Detail, data),
            TransportProbeStatus.Throttled => HealthCheckResult.Degraded($"The broker is throttling: {result.Detail}", result.Error, data),
            _ => HealthCheckResult.Unhealthy($"The broker is unreachable: {result.Detail}", result.Error, data),
        };
    }

    private async Task<HealthCheckResult> OutboxAsync(CancellationToken ct)
    {
        MonitorSnapshot snapshot = await monitor.CurrentAsync(ct).ConfigureAwait(false);
        if (snapshot.Outbox is null)
        {
            return Unread(snapshot, "the outbox backlog");
        }

        TimeSpan age = SourceFlowMonitor.OldestOutboxAge(snapshot);
        var data = new Dictionary<string, object> { ["oldestAgeSeconds"] = Math.Round(age.TotalSeconds, 1), ["pending"] = snapshot.Outbox.Sum(o => o.Pending) };
        return age >= Thresholds.OutboxAgeUnhealthy ? HealthCheckResult.Unhealthy($"The oldest due outbox message has waited {Seconds(age)}.", data: data)
            : age >= Thresholds.OutboxAgeDegraded ? HealthCheckResult.Degraded($"The oldest due outbox message has waited {Seconds(age)}.", data: data)
            : HealthCheckResult.Healthy($"The outbox is draining (oldest due message {Seconds(age)}).", data);
    }

    private async Task<HealthCheckResult> SubscriptionsAsync(CancellationToken ct)
    {
        MonitorSnapshot snapshot = await monitor.CurrentAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = Runtime.Time.GetUtcNow();
        PartitionReading[] stalled = [.. snapshot.Partitions.Where(p => p.Stall is not null)];
        PartitionReading[] lagging = [.. snapshot.Partitions.Where(p => p.LagTime > Thresholds.LagTimeDegraded)];
        var data = new Dictionary<string, object>
        {
            ["partitions"] = snapshot.Partitions.Count,
            ["maxLagSeconds"] = snapshot.Partitions.Count == 0 ? 0 : Math.Round(snapshot.Partitions.Max(p => p.LagTime.TotalSeconds), 1),
            ["stalled"] = stalled.Length,
            ["horizonAgeSeconds"] = Math.Round((snapshot.HorizonAge ?? TimeSpan.Zero).TotalSeconds, 1),
        };

        PartitionReading? longStall = stalled.FirstOrDefault(p => now - p.Stall!.StalledSince >= Thresholds.StalledUnhealthy);
        if (longStall is not null)
        {
            return HealthCheckResult.Unhealthy($"{Name(longStall)} has been stalled since {longStall.Stall!.StalledSince:u} at event {longStall.Stall.EventId} ({longStall.Stall.ErrorType}).", data: data);
        }

        if (stalled.Length > 0)
        {
            return HealthCheckResult.Degraded($"{Name(stalled[0])} is stalled at event {stalled[0].Stall!.EventId} ({stalled[0].Stall!.ErrorType}), retrying.", data: data);
        }

        if (lagging.Length > 0)
        {
            PartitionReading worst = lagging.MaxBy(p => p.LagTime)!;
            return HealthCheckResult.Degraded($"{Name(worst)} is {Seconds(worst.LagTime)} behind ({lagging.Length} partition(s) over {Seconds(Thresholds.LagTimeDegraded)}).", data: data);
        }

        if (snapshot.HorizonAge is { } horizon && horizon > Thresholds.HorizonAgeDegraded)
        {
            return HealthCheckResult.Degraded($"A writing transaction has held the subscription horizon for {Seconds(horizon)}.", data: data);
        }

        return snapshot.Errors.Any(e => e.StartsWith("subscriptions", StringComparison.Ordinal))
            ? HealthCheckResult.Degraded("Subscription lag could not be read: " + string.Join("; ", snapshot.Errors), data: data)
            : HealthCheckResult.Healthy($"{snapshot.Partitions.Count} subscription partition(s) are current.", data);
    }

    private async Task<HealthCheckResult> LeasesAsync(CancellationToken ct)
    {
        MonitorSnapshot snapshot = await monitor.CurrentAsync(ct).ConfigureAwait(false);
        LeaseReading leases = snapshot.Leases;
        var data = new Dictionary<string, object> { ["owned"] = leases.Owned, ["fairShare"] = leases.FairShare };
        if (!leases.Subscribing)
        {
            return HealthCheckResult.Healthy("This node does not run subscriptions (no Subscriptions role, or nothing to subscribe).", data);
        }

        if (leases.FairShare == 0)
        {
            return HealthCheckResult.Healthy("There are no subscription leases to own.", data);
        }

        TimeSpan allowed = Runtime.Options.NodeHeartbeatInterval * Thresholds.UnderShareIntervals;
        return leases.UnderShareSince is { } since && Runtime.Time.GetUtcNow() - since > allowed
            ? HealthCheckResult.Degraded($"This node owns {leases.Owned} of its fair share of {leases.FairShare} subscription leases, since {since:u}.", data: data)
            : HealthCheckResult.Healthy($"This node owns {leases.Owned} subscription lease(s) (fair share {leases.FairShare}).", data);
    }

    private static HealthCheckResult Unread(MonitorSnapshot snapshot, string what)
    {
        return snapshot.Errors.Count > 0
            ? HealthCheckResult.Degraded($"Could not read {what}: {string.Join("; ", snapshot.Errors)}")
            : HealthCheckResult.Healthy($"The store does not report {what}.");
    }

    private static string Name(PartitionReading p)
    {
        return $"Subscription '{p.Subscription}' partition {p.Partition.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string Ms(TimeSpan value)
    {
        return Math.Round(value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";
    }

    private static string Seconds(TimeSpan value)
    {
        return Math.Round(value.TotalSeconds, 1).ToString(CultureInfo.InvariantCulture) + " s";
    }
}
