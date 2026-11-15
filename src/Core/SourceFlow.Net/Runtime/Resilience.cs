using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SourceFlow.Pipeline;
using SourceFlow.Storage;

namespace SourceFlow.Runtime;

/// <summary>
/// The names of SourceFlow's resilience pipelines (ADR-013, doc 04 §9.1, CR-39): Polly v8 pipelines, one engine for every
/// retry the runtime makes. Customise them with <c>sf.Resilience(r =&gt; r.Configure(name, builder =&gt; …))</c>.
/// </summary>
public static class SourceFlowResilience
{
    /// <summary>Re-deciding a command after an optimistic-concurrency conflict: full-jitter backoff, 5–50 ms (ADR-008).</summary>
    public const string Concurrency = "sourceflow.concurrency";

    /// <summary>Immediate retries of a transient failure on receive and in subscriptions, before the delayed schedule.</summary>
    public const string Immediate = "sourceflow.immediate";

    /// <summary>The outbox relay's broker calls: a circuit breaker per destination, so an outage isn't hammered (F-07, CR-45).</summary>
    public const string Outbox = "sourceflow.outbox";

    /// <summary>The broker dead-letter ingestor's polls: exponential backoff up to the ingest interval.</summary>
    public const string Ingest = "sourceflow.ingest";
}

/// <summary>Customises SourceFlow's resilience pipelines (<see cref="SourceFlowResilience"/>).</summary>
public sealed class ResilienceBuilder
{
    internal ResilienceBuilder()
    {
    }

    internal Dictionary<string, List<Action<ResiliencePipelineBuilder>>> Configurations { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds strategies to a pipeline (one of <see cref="SourceFlowResilience"/>'s names). They run inside SourceFlow's own
    /// strategies for that pipeline, for example a timeout per attempt.
    /// </summary>
    public ResilienceBuilder Configure(string pipeline, Action<ResiliencePipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (pipeline is not (SourceFlowResilience.Concurrency or SourceFlowResilience.Immediate or SourceFlowResilience.Outbox or SourceFlowResilience.Ingest))
        {
            throw new ArgumentException($"'{pipeline}' is not a SourceFlow resilience pipeline; use one of SourceFlowResilience's names.", nameof(pipeline));
        }

        (Configurations.TryGetValue(pipeline, out List<Action<ResiliencePipelineBuilder>>? list) ? list : Configurations[pipeline] = []).Add(configure);
        return this;
    }
}

/// <summary>
/// The runtime's resilience pipelines (ADR-013, CR-39). Infrastructure timing uses the system clock: a test's fake business
/// clock must not stall a re-decide or an ingest poll. Retries that outlive the process (an outbox row's next attempt, a
/// stalled subscription's next try, a redelivery delay) are stored, not slept: <see cref="Backoff"/> computes them.
/// </summary>
internal sealed class SourceFlowPipelines
{
    private readonly SourceFlowOptions _options;
    private readonly IReadOnlyDictionary<string, List<Action<ResiliencePipelineBuilder>>> _custom;
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ResiliencePipeline> _outbox = new(StringComparer.Ordinal);

    public SourceFlowPipelines(SourceFlowOptions options, ErrorClassifier classifier, IReadOnlyDictionary<string, List<Action<ResiliencePipelineBuilder>>>? custom = null, TimeProvider? time = null)
    {
        _options = options;
        _custom = custom ?? new Dictionary<string, List<Action<ResiliencePipelineBuilder>>>();
        _time = time ?? TimeProvider.System;
        Classifier = classifier;
        Concurrency = Build(SourceFlowResilience.Concurrency, b => b.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<ConcurrencyException>(),
            MaxRetryAttempts = Math.Max(1, options.MaxConcurrencyRetries),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(5),
            MaxDelay = TimeSpan.FromMilliseconds(50),
            OnRetry = args => args.Context.Properties.TryGetValue(OnConcurrencyRetry, out Action<int, TimeSpan>? callback) ? Notify(callback, args.AttemptNumber, args.RetryDelay) : default,
        }), enabled: options.MaxConcurrencyRetries > 0);
        Immediate = Build(SourceFlowResilience.Immediate, b => b.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome, args.Context.CancellationToken)),
            MaxRetryAttempts = Math.Max(1, options.ImmediateRetries),
            BackoffType = DelayBackoffType.Constant,
            Delay = TimeSpan.Zero,
            OnRetry = args => args.Context.Properties.TryGetValue(OnImmediateRetry, out Action<int, TimeSpan>? callback) ? Notify(callback, args.AttemptNumber, args.RetryDelay) : default,
        }), enabled: options.ImmediateRetries > 0);
        Ingest = Build(SourceFlowResilience.Ingest, b => b.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(e => e is not OperationCanceledException),
            MaxRetryAttempts = int.MaxValue,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1),
            MaxDelay = options.DeadLetterIngestInterval,
            OnRetry = args => args.Context.Properties.TryGetValue(OnIngestRetry, out Func<Exception?, TimeSpan, ValueTask>? callback) ? callback(args.Outcome.Exception, args.RetryDelay) : default,
        }), enabled: true);
    }

    /// <summary>A callback the re-decide gets before each retry, with the attempt number (0 for the first) and the delay.</summary>
    public static readonly ResiliencePropertyKey<Action<int, TimeSpan>> OnConcurrencyRetry = new("sourceflow.concurrency.retry");

    /// <summary>A callback an immediate retry gets before each retry, with the attempt number and the delay.</summary>
    public static readonly ResiliencePropertyKey<Action<int, TimeSpan>> OnImmediateRetry = new("sourceflow.immediate.retry");

    /// <summary>A callback the ingestor gets before each retry, with the failure and the delay.</summary>
    public static readonly ResiliencePropertyKey<Func<Exception?, TimeSpan, ValueTask>> OnIngestRetry = new("sourceflow.ingest.retry");

    public ErrorClassifier Classifier { get; }

    public ResiliencePipeline Concurrency { get; }

    public ResiliencePipeline Immediate { get; }

    public ResiliencePipeline Ingest { get; }

    /// <summary>
    /// The outbox pipeline of one destination: a circuit breaker that opens when every call in a 30 s window failed
    /// transiently (at least <c>MinimumThroughput</c> calls), so the relay stops sending to a broker that is down instead of
    /// burning every row's attempts (F-07). While it is open, <see cref="BrokenCircuitException"/> stops the drain.
    /// </summary>
    public ResiliencePipeline Outbox(string destination)
    {
        return _outbox.GetOrAdd(destination, _ => Build(SourceFlowResilience.Outbox, b => b.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is { } e && e is not OperationCanceledException && Classifier.Classify(e) != HandlingOutcome.Permanent),
            FailureRatio = 1.0,
            MinimumThroughput = 20,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),
        }), enabled: true));
    }

    private bool IsTransient(Outcome<object> outcome, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return outcome switch
        {
            { Exception: LeaseLostException or OperationCanceledException } => false,
            { Exception: { } e } => Classifier.Classify(e) != HandlingOutcome.Permanent,
            { Result: MessageContext context } => context.Outcome == HandlingOutcome.Transient,
            _ => false,
        };
    }

    private static ValueTask Notify(Action<int, TimeSpan> callback, int attempt, TimeSpan delay)
    {
        callback(attempt, delay);
        return default;
    }

    private ResiliencePipeline Build(string name, Action<ResiliencePipelineBuilder> defaults, bool enabled)
    {
        var builder = new ResiliencePipelineBuilder { Name = name, TimeProvider = _time };
        if (enabled)
        {
            defaults(builder);
        }

        if (_custom.TryGetValue(name, out List<Action<ResiliencePipelineBuilder>>? custom))
        {
            foreach (Action<ResiliencePipelineBuilder> configure in custom)
            {
                configure(builder);
            }
        }

        return builder.Build();
    }
}

/// <summary>
/// The delay of a stored retry (doc 04 §9.1, CR-39): exponential from a first delay, doubling per attempt up to a maximum,
/// with equal jitter (half fixed, half random) when asked, so retries of many rows spread out.
/// </summary>
internal static class Backoff
{
    public static TimeSpan Exponential(int attempt, TimeSpan first, TimeSpan max, bool jitter, Random? random = null)
    {
        double factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        double ticks = Math.Min(max.Ticks, first.Ticks * factor);
        if (jitter)
        {
            ticks = (ticks / 2) + ((random ?? Random.Shared).NextDouble() * ticks / 2);
        }

        return TimeSpan.FromTicks((long)ticks);
    }
}
