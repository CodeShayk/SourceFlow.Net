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
    public static readonly ActivitySource ActivitySource = new(new ActivitySourceOptions(ActivitySourceName) { Version = Version, TelemetrySchemaUrl = SemanticConventions.SchemaUrl });

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
        public const string DbOperationName = "db.operation.name";
        public const string EventsCount = "sourceflow.events.count";
        public const string Subscription = "sourceflow.subscription";
        public const string Partition = "sourceflow.partition";
        public const string ProcessId = "sourceflow.process.id";
        public const string Consumer = "consumer";
        public const string Reason = "reason";
        public const string Stage = "stage";
        public const string RetryKind = "retry.kind";
        public const string SettleAction = "sourceflow.settle.action";
        public const string ProcessType = "process.type";
        public const string LeaseKind = "lease.kind";
        public const string CacheResult = "result";
        public const string Workflow = "workflow";
        public const string WorkflowVersion = "version";
        public const string TransitionKind = "kind";
        public const string WorkflowStatus = "status";
        public const string WorkflowStep = "sourceflow.workflow.step";
        public const string WorkflowAttempt = "sourceflow.workflow.attempt";
        public const string WorkflowTransition = "sourceflow.workflow.transition";
#pragma warning restore CS1591
    }

    /// <summary>Instrument names (doc 08 §3).</summary>
    public static class Instruments
    {
#pragma warning disable CS1591
        public const string CommandDuration = "sourceflow.command.duration";
        public const string AppendDuration = "sourceflow.eventstore.append.duration";
        public const string AppendEvents = "sourceflow.eventstore.append.events";
        public const string ConcurrencyConflicts = "sourceflow.eventstore.concurrency_conflicts";
        public const string LoadEvents = "sourceflow.aggregate.load.events";
        public const string CacheHits = "sourceflow.aggregate.cache.hits";
        public const string SnapshotsWritten = "sourceflow.snapshot.written";
        public const string SnapshotsDropped = "sourceflow.snapshot.dropped";
        public const string OutboxPending = "sourceflow.outbox.pending";
        public const string OutboxAge = "sourceflow.outbox.age";
        public const string OutboxDispatchDuration = "sourceflow.outbox.dispatch.duration";
        public const string InboxDuplicates = "sourceflow.inbox.duplicates";
        public const string RetryAttempts = "sourceflow.retry.attempts";
        public const string DeadLetterParked = "sourceflow.deadletter.parked";
        public const string DeadLetterReplayed = "sourceflow.deadletter.replayed";
        public const string DeadLetterDiscarded = "sourceflow.deadletter.discarded";
        public const string DeadLetterCount = "sourceflow.deadletter.count";
        public const string SubscriptionLag = "sourceflow.subscription.lag";
        public const string SubscriptionLagTime = "sourceflow.subscription.lag_time";
        public const string HorizonAge = "sourceflow.subscription.horizon_age";
        public const string SubscriptionStalled = "sourceflow.subscription.stalled";
        public const string SubscriptionBatchDuration = "sourceflow.subscription.batch.duration";
        public const string ProcessActive = "sourceflow.process.active";
        public const string LeaseOwned = "sourceflow.lease.owned";
        public const string WorkflowTransitions = "sourceflow.workflow.transitions";
        public const string WorkflowActive = "sourceflow.workflow.active";
        public const string WorkflowManualIntervention = "sourceflow.workflow.manual_intervention";
        public const string SentMessages = "messaging.client.sent.messages";
        public const string ConsumedMessages = "messaging.client.consumed.messages";
        public const string ProcessDuration = "messaging.process.duration";
#pragma warning restore CS1591
    }

    /// <summary>
    /// Every instrument SourceFlow publishes on its meter (doc 08 §3): the catalogue the reference dashboards and alert
    /// rules are generated from (<c>dotnet sourceflow observability export</c>, <c>deploy/observability</c>), so they cannot
    /// drift from the code. A test checks it against the instruments a running host creates and the tags it records.
    /// </summary>
    public static IReadOnlyList<InstrumentInfo> Catalogue { get; } =
    [
        new(Instruments.CommandDuration, InstrumentKind.Histogram, "s", "Command processing duration.", [Tags.CommandType, Tags.Outcome]),
        new(Instruments.AppendDuration, InstrumentKind.Histogram, "s", "Session commit duration.", []),
        new(Instruments.AppendEvents, InstrumentKind.Histogram, "{event}", "Events per append.", [Tags.AggregateType]),
        new(Instruments.ConcurrencyConflicts, InstrumentKind.Counter, "{conflict}", "Optimistic concurrency conflicts.", [Tags.AggregateType]),
        new(Instruments.LoadEvents, InstrumentKind.Histogram, "{event}", "Events folded per aggregate load.", [Tags.AggregateType, Tags.LoadSource]),
        new(Instruments.CacheHits, InstrumentKind.Counter, "{hit}", "Aggregate cache lookups by result (hit, miss, stale).", [Tags.AggregateType, Tags.CacheResult]),
        new(Instruments.SnapshotsWritten, InstrumentKind.Counter, "{snapshot}", "Snapshots written in the background.", [Tags.AggregateType]),
        new(Instruments.SnapshotsDropped, InstrumentKind.Counter, "{snapshot}", "Snapshots dropped because the queue was full or the write failed.", [Tags.AggregateType]),
        new(Instruments.OutboxPending, InstrumentKind.Gauge, "{message}", "Undelivered outbox messages per partition.", [Tags.Partition]),
        new(Instruments.OutboxAge, InstrumentKind.Gauge, "s", "How long the oldest due outbox message has waited (durability SLO).", [Tags.Partition]),
        new(Instruments.OutboxDispatchDuration, InstrumentKind.Histogram, "s", "Outbox batch dispatch duration.", [Tags.MessagingSystem]),
        new(Instruments.InboxDuplicates, InstrumentKind.Counter, "{message}", "Duplicates absorbed by the inbox.", [Tags.ConsumerGroup]),
        new(Instruments.RetryAttempts, InstrumentKind.Counter, "{attempt}", "Retry attempts.", [Tags.Stage, Tags.RetryKind]),
        new(Instruments.DeadLetterParked, InstrumentKind.Counter, "{message}", "Messages parked to dead letters.", [Tags.Consumer, Tags.Reason, Tags.MessageType]),
        new(Instruments.DeadLetterReplayed, InstrumentKind.Counter, "{message}", "Dead letters replayed by an operator.", [Tags.Consumer]),
        new(Instruments.DeadLetterDiscarded, InstrumentKind.Counter, "{message}", "Dead letters discarded by an operator.", [Tags.Consumer]),
        new(Instruments.DeadLetterCount, InstrumentKind.Gauge, "{message}", "Parked dead letters per consumer.", [Tags.Consumer]),
        new(Instruments.SubscriptionLag, InstrumentKind.Gauge, "{event}", "Events not yet processed per subscription partition.", [Tags.Subscription, Tags.Partition]),
        new(Instruments.SubscriptionLagTime, InstrumentKind.Gauge, "s", "Age of the oldest unprocessed event per subscription partition (freshness SLO).", [Tags.Subscription, Tags.Partition]),
        new(Instruments.HorizonAge, InstrumentKind.Gauge, "s", "How long the oldest writing transaction has held the subscription horizon back.", []),
        new(Instruments.SubscriptionStalled, InstrumentKind.Gauge, "{partition}", "Subscription partitions halted at a failing event (Stop policy).", [Tags.Subscription, Tags.Partition]),
        new(Instruments.SubscriptionBatchDuration, InstrumentKind.Histogram, "s", "Subscription batch duration.", [Tags.Subscription]),
        new(Instruments.ProcessActive, InstrumentKind.Gauge, "{instance}", "Process-manager instances not completed, per process type.", [Tags.ProcessType]),
        new(Instruments.LeaseOwned, InstrumentKind.Gauge, "{lease}", "Subscription partition leases this node owns.", [Tags.LeaseKind]),
        new(Instruments.WorkflowTransitions, InstrumentKind.Counter, "{transition}", "Workflow transitions by kind (success, failure, retry, timeout, compensate, manual, complete).", [Tags.Workflow, Tags.WorkflowVersion, Tags.TransitionKind]),
        new(Instruments.WorkflowActive, InstrumentKind.Gauge, "{instance}", "Workflow instances not finished, per workflow version and status.", [Tags.Workflow, Tags.WorkflowVersion, Tags.WorkflowStatus]),
        new(Instruments.WorkflowManualIntervention, InstrumentKind.Counter, "{instance}", "Workflow instances that entered manual intervention (alert on any).", [Tags.Workflow, Tags.WorkflowVersion]),
        new(Instruments.SentMessages, InstrumentKind.Counter, "{message}", "Messages sent to the broker.", [Tags.MessagingSystem, Tags.Destination, Tags.ErrorType]),
        new(Instruments.ConsumedMessages, InstrumentKind.Counter, "{message}", "Messages received from the broker.", [Tags.MessagingSystem, Tags.Destination, Tags.ConsumerGroup]),
        new(Instruments.ProcessDuration, InstrumentKind.Histogram, "s", "Received message processing duration.", [Tags.MessagingSystem, Tags.Destination, Tags.ConsumerGroup, Tags.ErrorType]),
    ];
}

/// <summary>The kind of a SourceFlow instrument.</summary>
public enum InstrumentKind
{
    /// <summary>A monotonic counter.</summary>
    Counter,

    /// <summary>A histogram.</summary>
    Histogram,

    /// <summary>An observable gauge (sampled by the monitor).</summary>
    Gauge,
}

/// <summary>A SourceFlow instrument.</summary>
/// <param name="Name">The instrument name.</param>
/// <param name="Kind">Counter, histogram or gauge.</param>
/// <param name="Unit">The UCUM unit.</param>
/// <param name="Description">The description.</param>
/// <param name="Tags">The tags it may carry (low-cardinality by design).</param>
public sealed record InstrumentInfo(string Name, InstrumentKind Kind, string Unit, string Description, IReadOnlyList<string> Tags);

/// <summary>
/// The SourceFlow instruments of one host (doc 08 §3). Each host gets its own <see cref="Meter"/> instance named
/// <see cref="SourceFlowTelemetry.MeterName"/>, so tests can listen to exactly one host.
/// </summary>
public sealed class SourceFlowMetrics : IDisposable
{
    /// <summary>Creates the meter and every instrument.</summary>
    public SourceFlowMetrics()
    {
        Meter = new Meter(new MeterOptions(SourceFlowTelemetry.MeterName) { Version = SourceFlowTelemetry.Version, TelemetrySchemaUrl = SemanticConventions.SchemaUrl });
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
        DeadLettersReplayed = Meter.CreateCounter<long>("sourceflow.deadletter.replayed", "{message}", "Dead letters replayed by an operator.");
        DeadLettersDiscarded = Meter.CreateCounter<long>("sourceflow.deadletter.discarded", "{message}", "Dead letters discarded by an operator.");
        SubscriptionBatchDuration = Meter.CreateHistogram("sourceflow.subscription.batch.duration", "s", "Subscription batch duration.", tags: null, advice: duration);
        SentMessages = Meter.CreateCounter<long>("messaging.client.sent.messages", "{message}", "Messages sent to the broker.");
        ConsumedMessages = Meter.CreateCounter<long>("messaging.client.consumed.messages", "{message}", "Messages received from the broker.");
        ProcessDuration = Meter.CreateHistogram("messaging.process.duration", "s", "Received message processing duration.", tags: null, advice: duration);
        WorkflowTransitions = Meter.CreateCounter<long>("sourceflow.workflow.transitions", "{transition}", "Workflow transitions by kind (success, failure, retry, timeout, compensate, manual, complete).");
        WorkflowManualInterventions = Meter.CreateCounter<long>("sourceflow.workflow.manual_intervention", "{instance}", "Workflow instances that entered manual intervention (alert on any).");
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

    public Counter<long> DeadLettersReplayed { get; }

    public Counter<long> DeadLettersDiscarded { get; }

    public Histogram<double> SubscriptionBatchDuration { get; }

    public Counter<long> SentMessages { get; }

    public Counter<long> ConsumedMessages { get; }

    public Histogram<double> ProcessDuration { get; }

    /// <summary><c>sourceflow.workflow.transitions</c>, tagged with <c>workflow</c>, <c>version</c> and <c>kind</c>.</summary>
    public Counter<long> WorkflowTransitions { get; }

    /// <summary><c>sourceflow.workflow.manual_intervention</c>, tagged with <c>workflow</c> and <c>version</c>.</summary>
    public Counter<long> WorkflowManualInterventions { get; }
#pragma warning restore CS1591

    /// <inheritdoc />
    public void Dispose()
    {
        Meter.Dispose();
    }
}
