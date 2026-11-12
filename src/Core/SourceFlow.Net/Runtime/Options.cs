using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using SourceFlow.Messaging;

namespace SourceFlow.Runtime;

/// <summary>
/// Runtime options. Every option is honoured or rejected by validation at startup (GAP-03, "no inert options").
/// </summary>
public sealed class SourceFlowOptions
{
    /// <summary>The service name: envelope <c>source</c>, default endpoint and inbox consumer (required).</summary>
    [Required]
    [RegularExpression("^[a-z][a-z0-9-]{0,62}$", ErrorMessage = "ServiceName must be lowercase letters, digits and dashes, starting with a letter.")]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>The tenant used when no tenant resolver supplies one.</summary>
    [Required]
    public string DefaultTenant { get; set; } = MessageEnvelope.DefaultTenant;

    /// <summary>Re-decide attempts after a concurrency conflict (ADR-008).</summary>
    [Range(0, 50)]
    public int MaxConcurrencyRetries { get; set; } = 5;

    /// <summary>The number of outbox partitions (doc 04 §7).</summary>
    [Range(1, 1024)]
    public int OutboxPartitions { get; set; } = 16;

    /// <summary>Rows dispatched per outbox claim.</summary>
    [Range(1, 10_000)]
    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>Dispatch attempts before an outbox row is parked.</summary>
    [Range(1, 1000)]
    public int OutboxMaxAttempts { get; set; } = 10;

    /// <summary>Poll interval when no commit notification arrives.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Lease time-to-live for outbox and subscription partitions (doc 05 §7.4).</summary>
    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often a node heartbeats for lease fair-share (doc 06 §4). A node is live for three intervals.</summary>
    public TimeSpan NodeHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What startup does when a published schema changed without a new version (doc 14 §2.2): fail (default), log a
    /// warning, or skip registration.
    /// </summary>
    public SchemaRegistrationMode SchemaRegistration { get; set; } = SchemaRegistrationMode.Enforce;

    /// <summary>
    /// Logs message payloads (with <c>[PersonalData]</c> and <c>[Secret]</c> members redacted) when a message fails
    /// permanently. Off by default (doc 08 §4, doc 10 §5).
    /// </summary>
    public bool LogPayloads { get; set; }

    /// <summary>Concurrent receive workers (RX-R1).</summary>
    [Range(1, 1024)]
    public int ReceiveConcurrency { get; set; } = Math.Max(2, Environment.ProcessorCount * 2);

    /// <summary>Bounded channel size per receive worker (RX-R2).</summary>
    [Range(1, 10_000)]
    public int PrefetchPerWorker { get; set; } = 16;

    /// <summary>Immediate in-process retries of a transient failure before a delayed retry.</summary>
    [Range(0, 10)]
    public int ImmediateRetries { get; set; } = 3;

    /// <summary>Delayed retry schedule after immediate retries (doc 04 §9.2); exhausting it parks the message.</summary>
    public IList<TimeSpan> DelayedRetries { get; } = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)];

    /// <summary>
    /// The largest subscription batch. Batches are adaptive (doc 06 §2): each partition starts at 100 events (or this value
    /// when smaller), doubles while batches come back full (catching up, rebuilds), and drops back when caught up.
    /// </summary>
    [Range(1, 10_000)]
    public int SubscriptionBatchSize { get; set; } = 1000;

    /// <summary>Partitions of an async subscription (a power of two, doc 06 §3).</summary>
    [Range(1, 1024)]
    public int SubscriptionPartitions { get; set; } = 1;

    /// <summary>
    /// A new read-model generation takes over reads once every partition is at most this many events behind the log
    /// (doc 06 §6). 0 waits until it is fully caught up.
    /// </summary>
    [Range(0, 1_000_000)]
    public int SwitchLagThreshold { get; set; } = 100;

    /// <summary>How long a retired read-model generation's data is kept after the switch before it is dropped (doc 06 §6).</summary>
    public TimeSpan RetainPreviousFor { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a received message stays locked by this instance (visibility timeout, lease); the receiver renews it at
    /// half this interval while the handler runs.
    /// </summary>
    public TimeSpan ReceiveLockDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Moves messages from the endpoint's native dead-letter queue into the dead-letter store (doc 07 §3); on by default.</summary>
    public bool IngestBrokerDeadLetters { get; set; } = true;

    /// <summary>
    /// How often the ingestor checks the endpoint's native dead-letter queue when it was empty (doc 07 §3). It drains without
    /// waiting while messages arrive, and backs off up to this interval after a broker error. Every check is a broker call,
    /// so a short interval costs requests on pay-per-call brokers.
    /// </summary>
    public TimeSpan DeadLetterIngestInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the monitor samples the outbox backlog, dead letters, subscription lag, the log horizon, process managers
    /// and leases for the observable gauges and health checks (doc 08 §3.2, §5). Each sample is a few aggregate queries.
    /// </summary>
    public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The health check thresholds (doc 08 §5).</summary>
    public HealthThresholds Health { get; } = new();

    /// <summary>The in-process aggregate state cache (doc 05 §8.2); off by default.</summary>
    public AggregateCacheMode AggregateCache { get; set; } = AggregateCacheMode.Off;

    /// <summary>The most aggregate states the cache holds; the least recently used are evicted first.</summary>
    [Range(1, 10_000_000)]
    public int AggregateCacheSize { get; set; } = 10_000;

    /// <summary>
    /// Snapshots waiting to be written in the background (doc 05 §8.1). When the queue is full the oldest is dropped:
    /// a lost snapshot only means a longer load.
    /// </summary>
    [Range(1, 1_000_000)]
    public int SnapshotQueueCapacity { get; set; } = 1024;

    /// <summary>How long processed inbox rows are kept for deduplication (doc 05 §9); redeliveries older than this are not detected.</summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long replayed or discarded dead letters are kept for audit; parked ones are never purged.</summary>
    public TimeSpan DeadLetterRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often the relay role runs retention cleanup.</summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Rows deleted per cleanup statement.</summary>
    [Range(1, 100_000)]
    public int RetentionBatchSize { get; set; } = 5000;

    /// <summary>How long shutdown waits for in-flight work (RX-R6).</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The node id used as lease owner; defaults to a random id per process.</summary>
    [Required]
    public string NodeId { get; set; } = Environment.MachineName.ToLowerInvariant() + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + "-" + System.Security.Cryptography.RandomNumberGenerator.GetHexString(8, lowercase: true);
}

/// <summary>Validates the attribute rules of <see cref="SourceFlowOptions"/> (source-generated, AOT-safe).</summary>
[OptionsValidator]
public sealed partial class SourceFlowOptionsValidator : IValidateOptions<SourceFlowOptions>;

/// <summary>Validates the time-based rules of <see cref="SourceFlowOptions"/> that attributes can't express trim-safely.</summary>
public sealed class SourceFlowOptionsTimingValidator : IValidateOptions<SourceFlowOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SourceFlowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        Between(options.PollInterval, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(10), nameof(options.PollInterval), failures);
        Between(options.LeaseTtl, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(options.LeaseTtl), failures);
        Between(options.ShutdownTimeout, TimeSpan.Zero, TimeSpan.FromMinutes(10), nameof(options.ShutdownTimeout), failures);
        Between(options.InboxRetention, TimeSpan.FromMinutes(1), TimeSpan.FromDays(3650), nameof(options.InboxRetention), failures);
        Between(options.DeadLetterRetention, TimeSpan.FromMinutes(1), TimeSpan.FromDays(3650), nameof(options.DeadLetterRetention), failures);
        Between(options.RetentionInterval, TimeSpan.FromSeconds(1), TimeSpan.FromDays(7), nameof(options.RetentionInterval), failures);
        Between(options.RetainPreviousFor, TimeSpan.Zero, TimeSpan.FromDays(3650), nameof(options.RetainPreviousFor), failures);
        Between(options.ReceiveLockDuration, TimeSpan.FromSeconds(1), TimeSpan.FromHours(12), nameof(options.ReceiveLockDuration), failures);
        Between(options.DeadLetterIngestInterval, TimeSpan.FromMilliseconds(100), TimeSpan.FromHours(1), nameof(options.DeadLetterIngestInterval), failures);
        Between(options.MonitorInterval, TimeSpan.FromMilliseconds(100), TimeSpan.FromHours(1), nameof(options.MonitorInterval), failures);
        options.Health.Validate(failures);
        if (options.LeaseTtl <= options.PollInterval)
        {
            failures.Add($"{nameof(options.LeaseTtl)} ({options.LeaseTtl}) must be longer than {nameof(options.PollInterval)} ({options.PollInterval}) so leases are renewed in time.");
        }

        if (options.DelayedRetries.Any(d => d <= TimeSpan.Zero))
        {
            failures.Add($"{nameof(options.DelayedRetries)} must contain positive delays.");
        }

        if ((options.SubscriptionPartitions & (options.SubscriptionPartitions - 1)) != 0)
        {
            failures.Add($"{nameof(options.SubscriptionPartitions)} must be a power of two (doc 06 §3).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Between(TimeSpan value, TimeSpan min, TimeSpan max, string name, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{name} must be between {min} and {max}; got {value}.");
        }
    }
}

/// <summary>How the in-process aggregate state cache is used (doc 05 §8.2).</summary>
public enum AggregateCacheMode
{
    /// <summary>No cache: every command loads from the snapshot and the stream.</summary>
    Off = 0,

    /// <summary>Start from the cached state and read the stream tail after it (usually no rows, one index probe).</summary>
    ReadTail = 1,

    /// <summary>
    /// Skip the tail read and rely on the append's version check: a stale entry causes a conflict, a reload and a
    /// re-decide. A rejection or no-op decided on cached state is confirmed against the tail before it is returned.
    /// Best with stream affinity (partition-keyed routing to one instance).
    /// </summary>
    Optimistic = 2,
}

/// <summary>What startup does with schema conflicts (doc 14 §2.2).</summary>
public enum SchemaRegistrationMode
{
    /// <summary>Register schemas; a changed published <c>(type, version)</c> fails startup.</summary>
    Enforce = 0,

    /// <summary>Register schemas; log conflicts as warnings.</summary>
    Warn = 1,

    /// <summary>Don't register schemas.</summary>
    Off = 2,
}

/// <summary>
/// The health check thresholds (doc 08 §5). Defaults follow the SLOs in doc 08 §6; each "degraded" threshold must be below
/// its "unhealthy" one.
/// </summary>
public sealed class HealthThresholds
{
    /// <summary><c>sourceflow.store</c> is degraded when a probe takes longer.</summary>
    public TimeSpan StoreLatencyDegraded { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a <c>sourceflow.transport</c> result is reused before the broker is asked again.</summary>
    public TimeSpan TransportCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><c>sourceflow.outbox</c> is degraded when the oldest due message has waited this long.</summary>
    public TimeSpan OutboxAgeDegraded { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary><c>sourceflow.outbox</c> is unhealthy when the oldest due message has waited this long.</summary>
    public TimeSpan OutboxAgeUnhealthy { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary><c>sourceflow.subscriptions</c> is degraded when a partition's oldest unprocessed event is this old.</summary>
    public TimeSpan LagTimeDegraded { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><c>sourceflow.subscriptions</c> is degraded when a writing transaction has held the horizon this long.</summary>
    public TimeSpan HorizonAgeDegraded { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary><c>sourceflow.subscriptions</c> is unhealthy when a partition has been stalled this long.</summary>
    public TimeSpan StalledUnhealthy { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary><c>sourceflow.leases</c> is degraded after this many node heartbeat intervals under the fair share.</summary>
    public int UnderShareIntervals { get; set; } = 2;

    internal void Validate(List<string> failures)
    {
        void Positive(TimeSpan value, string name)
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add($"Health.{name} must be positive.");
            }
        }

        Positive(StoreLatencyDegraded, nameof(StoreLatencyDegraded));
        Positive(TransportCacheDuration, nameof(TransportCacheDuration));
        Positive(OutboxAgeDegraded, nameof(OutboxAgeDegraded));
        Positive(LagTimeDegraded, nameof(LagTimeDegraded));
        Positive(HorizonAgeDegraded, nameof(HorizonAgeDegraded));
        Positive(StalledUnhealthy, nameof(StalledUnhealthy));
        if (OutboxAgeUnhealthy <= OutboxAgeDegraded)
        {
            failures.Add($"Health.{nameof(OutboxAgeUnhealthy)} must be longer than {nameof(OutboxAgeDegraded)}.");
        }

        if (UnderShareIntervals < 1)
        {
            failures.Add($"Health.{nameof(UnderShareIntervals)} must be at least 1.");
        }
    }
}
