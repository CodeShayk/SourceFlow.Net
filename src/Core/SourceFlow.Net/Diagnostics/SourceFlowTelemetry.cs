using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace SourceFlow.Diagnostics;

/// <summary>
/// Telemetry names (ADR-019, doc 08). Subscribe with any OpenTelemetry SDK:
/// <c>AddSource(SourceFlowTelemetry.ActivitySourceName)</c> and <c>AddMeter(SourceFlowTelemetry.MeterName)</c>.
/// </summary>
public static class SourceFlowTelemetry
{
    /// <summary>The ActivitySource name.</summary>
    public const string ActivitySourceName = "SourceFlow";

    /// <summary>The Meter name.</summary>
    public const string MeterName = "SourceFlow";

    /// <summary>The <c>messaging.system</c> value for the in-memory transport.</summary>
    public const string InMemorySystem = "sourceflow.inmemory";

    /// <summary>The library version reported by the source and meter.</summary>
    public static readonly string Version = typeof(SourceFlowTelemetry).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "3.0.0";

    /// <summary>The shared ActivitySource. Spans cost one null check when nobody listens.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    /// <summary>Duration buckets, 0.5 ms to 10 s (doc 08 §3).</summary>
    public static readonly IReadOnlyList<double> DurationBuckets = [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    /// <summary>Count buckets, 1 to 1,000.</summary>
    public static readonly IReadOnlyList<long> CountBuckets = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000];

    /// <summary>Semantic-convention attribute names.</summary>
    public static class Tags
    {
#pragma warning disable CS1591
        public const string MessagingSystem = "messaging.system";
        public const string OperationType = "messaging.operation.type";
        public const string OperationName = "messaging.operation.name";
        public const string Destination = "messaging.destination.name";
        public const string MessageId = "messaging.message.id";
        public const string ConversationId = "messaging.message.conversation_id";
        public const string ConsumerGroup = "messaging.consumer.group.name";
        public const string BatchCount = "messaging.batch.message_count";
        public const string ErrorType = "error.type";
        public const string MessageType = "sourceflow.message.type";
        public const string CommandType = "sourceflow.command.type";
        public const string AggregateType = "sourceflow.aggregate.type";
        public const string StreamId = "sourceflow.stream.id";
        public const string Outcome = "sourceflow.outcome";
        public const string ConcurrencyRetries = "sourceflow.concurrency.retries";
        public const string LoadSource = "sourceflow.load.source";
        public const string Subscription = "sourceflow.subscription";
        public const string Partition = "sourceflow.partition";
        public const string ProcessId = "sourceflow.process.id";
        public const string Consumer = "consumer";
        public const string Reason = "reason";
        public const string Stage = "stage";
        public const string RetryKind = "retry.kind";
        public const string SettleAction = "sourceflow.settle.action";
#pragma warning restore CS1591
    }
}

/// <summary>
/// The SourceFlow instruments of one host (doc 08 §3). Each host gets its own <see cref="Meter"/> instance named
/// <see cref="SourceFlowTelemetry.MeterName"/>, so tests can listen to exactly one host.
/// </summary>
public sealed class SourceFlowMetrics : IDisposable
{
    /// <summary>Creates the meter and every instrument.</summary>
    public SourceFlowMetrics()
    {
        Meter = new Meter(new MeterOptions(SourceFlowTelemetry.MeterName) { Version = SourceFlowTelemetry.Version });
        var duration = new InstrumentAdvice<double> { HistogramBucketBoundaries = SourceFlowTelemetry.DurationBuckets };
        var count = new InstrumentAdvice<long> { HistogramBucketBoundaries = SourceFlowTelemetry.CountBuckets };

        CommandDuration = Meter.CreateHistogram("sourceflow.command.duration", "s", "Command processing duration.", tags: null, advice: duration);
        AppendDuration = Meter.CreateHistogram("sourceflow.eventstore.append.duration", "s", "Session commit duration.", tags: null, advice: duration);
        AppendEvents = Meter.CreateHistogram("sourceflow.eventstore.append.events", "{event}", "Events per append.", tags: null, advice: count);
        ConcurrencyConflicts = Meter.CreateCounter<long>("sourceflow.eventstore.concurrency_conflicts", "{conflict}", "Optimistic concurrency conflicts.");
        LoadEvents = Meter.CreateHistogram("sourceflow.aggregate.load.events", "{event}", "Events folded per aggregate load.", tags: null, advice: count);
        OutboxDispatchDuration = Meter.CreateHistogram("sourceflow.outbox.dispatch.duration", "s", "Outbox batch dispatch duration.", tags: null, advice: duration);
        InboxDuplicates = Meter.CreateCounter<long>("sourceflow.inbox.duplicates", "{message}", "Duplicates absorbed by the inbox.");
        AggregateCacheHits = Meter.CreateCounter<long>("sourceflow.aggregate.cache.hits", "{hit}", "Aggregate cache lookups by result (hit, miss, stale).");
        SnapshotsWritten = Meter.CreateCounter<long>("sourceflow.snapshot.written", "{snapshot}", "Snapshots written in the background.");
        SnapshotsDropped = Meter.CreateCounter<long>("sourceflow.snapshot.dropped", "{snapshot}", "Snapshots dropped because the queue was full or the write failed.");
        RetryAttempts = Meter.CreateCounter<long>("sourceflow.retry.attempts", "{attempt}", "Retry attempts.");
        DeadLettersParked = Meter.CreateCounter<long>("sourceflow.deadletter.parked", "{message}", "Messages parked to dead letters.");
        SubscriptionBatchDuration = Meter.CreateHistogram("sourceflow.subscription.batch.duration", "s", "Subscription batch duration.", tags: null, advice: duration);
        SentMessages = Meter.CreateCounter<long>("messaging.client.sent.messages", "{message}", "Messages sent to the broker.");
        ConsumedMessages = Meter.CreateCounter<long>("messaging.client.consumed.messages", "{message}", "Messages received from the broker.");
        ProcessDuration = Meter.CreateHistogram("messaging.process.duration", "s", "Received message processing duration.", tags: null, advice: duration);
    }

    /// <summary>The meter.</summary>
    public Meter Meter { get; }

#pragma warning disable CS1591 // names are the documented instrument names
    public Histogram<double> CommandDuration { get; }

    public Histogram<double> AppendDuration { get; }

    public Histogram<long> AppendEvents { get; }

    public Counter<long> ConcurrencyConflicts { get; }

    public Histogram<long> LoadEvents { get; }

    public Histogram<double> OutboxDispatchDuration { get; }

    public Counter<long> InboxDuplicates { get; }

    /// <summary><c>sourceflow.aggregate.cache.hits</c>, tagged with <c>result</c>: hit, miss or stale.</summary>
    public Counter<long> AggregateCacheHits { get; }

    /// <summary><c>sourceflow.snapshot.written</c>.</summary>
    public Counter<long> SnapshotsWritten { get; }

    /// <summary><c>sourceflow.snapshot.dropped</c>.</summary>
    public Counter<long> SnapshotsDropped { get; }

    public Counter<long> RetryAttempts { get; }

    public Counter<long> DeadLettersParked { get; }

    public Histogram<double> SubscriptionBatchDuration { get; }

    public Counter<long> SentMessages { get; }

    public Counter<long> ConsumedMessages { get; }

    public Histogram<double> ProcessDuration { get; }
#pragma warning restore CS1591

    /// <inheritdoc />
    public void Dispose()
    {
        Meter.Dispose();
    }
}
