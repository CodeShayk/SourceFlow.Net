using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.CircuitBreaker;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-RES-01..06 (WP-C.14, ADR-013, CR-39, CR-45): one Polly resilience engine for every retry the runtime makes.</summary>
[Trait("Category", "Unit")]
public sealed class ResilienceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SourceFlowPipelines Pipelines(SourceFlowOptions? options = null, TimeProvider? time = null, IReadOnlyDictionary<string, List<Action<ResiliencePipelineBuilder>>>? custom = null)
    {
        return new SourceFlowPipelines(options ?? new SourceFlowOptions(), new ErrorClassifier(), custom, time);
    }

    [Fact]
    [Trait("Id", "UT-RES-01")]
    [Trait("Covers", "ADR-008;ADR-013")]
    public async Task Concurrency_RetriesAConflict_WithFullJitterUpTo50Ms()
    {
        SourceFlowPipelines pipelines = Pipelines(new SourceFlowOptions { MaxConcurrencyRetries = 5 });
        var delays = new List<TimeSpan>();
        int calls = 0;
        ResilienceContext context = ResilienceContextPool.Shared.Get(Ct);
        context.Properties.Set(SourceFlowPipelines.OnConcurrencyRetry, (_, delay) => delays.Add(delay));

        await Should.ThrowAsync<ConcurrencyException>(async () => await pipelines.Concurrency.ExecuteAsync<int, int>((_, _) =>
        {
            calls++;
            throw new ConcurrencyException("conflict");
        }, context, 0));

        calls.ShouldBe(6, "the first decide and five re-decides");
        delays.Count.ShouldBe(5);
        delays.ShouldAllBe(d => d >= TimeSpan.Zero && d <= TimeSpan.FromMilliseconds(50));
        (await pipelines.Concurrency.ExecuteAsync(_ => ValueTask.FromResult(7), Ct)).ShouldBe(7);
        await Should.ThrowAsync<InvalidOperationException>(async () => await pipelines.Concurrency.ExecuteAsync<int>(_ => throw new InvalidOperationException(), Ct));
    }

    [Fact]
    [Trait("Id", "UT-RES-02")]
    [Trait("Covers", "ADR-013;doc 04 9.1")]
    public async Task Immediate_RetriesTransientOutcomesAndFailures_AtOnce_NeverPermanentOnes()
    {
        SourceFlowPipelines pipelines = Pipelines(new SourceFlowOptions { ImmediateRetries = 3 });

        int transient = 0;
        await Should.ThrowAsync<TimeoutException>(async () => await pipelines.Immediate.ExecuteAsync(_ =>
        {
            transient++;
            throw new TimeoutException();
        }, Ct));
        transient.ShouldBe(4, "the attempt and three immediate retries");

        int permanent = 0;
        await Should.ThrowAsync<System.Text.Json.JsonException>(async () => await pipelines.Immediate.ExecuteAsync(_ =>
        {
            permanent++;
            throw new System.Text.Json.JsonException();
        }, Ct));
        permanent.ShouldBe(1, "a permanent failure is not retried");

        int lost = 0;
        await Should.ThrowAsync<LeaseLostException>(async () => await pipelines.Immediate.ExecuteAsync(_ =>
        {
            lost++;
            throw new LeaseLostException("lease");
        }, Ct));
        lost.ShouldBe(1, "a lost lease ends the work at once");

        var delays = new List<TimeSpan>();
        ResilienceContext context = ResilienceContextPool.Shared.Get(Ct);
        context.Properties.Set(SourceFlowPipelines.OnImmediateRetry, (_, delay) => delays.Add(delay));
        int handled = 0;
        MessageContext last = await pipelines.Immediate.ExecuteAsync((_, _) =>
        {
            handled++;
            var message = new MessageContext(PipelineStage.Receive, Envelope(), new EmptyServices(), Ct) { Outcome = handled < 3 ? HandlingOutcome.Transient : HandlingOutcome.Completed };
            return ValueTask.FromResult(message);
        }, context, 0);
        handled.ShouldBe(3);
        last.Outcome.ShouldBe(HandlingOutcome.Completed);
        delays.ShouldAllBe(d => d == TimeSpan.Zero, "immediate retries don't wait");
    }

    [Fact]
    [Trait("Id", "UT-RES-03")]
    [Trait("Covers", "ADR-013;CR-45")]
    public async Task Outbox_OpensItsBreakerAfterTransientFailures_AndHalfOpensAfterTheBreak()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        SourceFlowPipelines pipelines = Pipelines(time: time);
        ResiliencePipeline queue = pipelines.Outbox("queue:billing");

        // Permanent failures (a message too large, say) never open it.
        for (int i = 0; i < 30; i++)
        {
            await Should.ThrowAsync<System.Text.Json.JsonException>(async () => await queue.ExecuteAsync(_ => throw new System.Text.Json.JsonException(), Ct));
        }

        await queue.ExecuteAsync(_ => ValueTask.CompletedTask, Ct);

        // Twenty transient failures in a row do; the call isn't made while it is open.
        ResiliencePipeline topic = pipelines.Outbox("topic:orders-events");
        for (int i = 0; i < 20; i++)
        {
            await Should.ThrowAsync<TimeoutException>(async () => await topic.ExecuteAsync(_ => throw new TimeoutException(), Ct));
        }

        bool called = false;
        await Should.ThrowAsync<BrokenCircuitException>(async () => await topic.ExecuteAsync(_ =>
        {
            called = true;
            return ValueTask.CompletedTask;
        }, Ct));
        called.ShouldBeFalse();
        pipelines.Outbox("queue:billing").ShouldBeSameAs(queue, "one breaker per destination");
        await queue.ExecuteAsync(_ => ValueTask.CompletedTask, Ct);   // other destinations are unaffected

        time.Advance(TimeSpan.FromSeconds(16));
        await topic.ExecuteAsync(_ => ValueTask.CompletedTask, Ct);   // half-open: a trial call goes through and closes it
        await topic.ExecuteAsync(_ => ValueTask.CompletedTask, Ct);
    }

    [Fact]
    [Trait("Id", "UT-RES-04")]
    [Trait("Covers", "ADR-013")]
    public async Task Ingest_RetriesAFailedPoll_WithBackoffCappedAtTheInterval()
    {
        SourceFlowPipelines pipelines = Pipelines(new SourceFlowOptions { DeadLetterIngestInterval = TimeSpan.FromMilliseconds(100) });
        var delays = new List<TimeSpan>();
        ResilienceContext context = ResilienceContextPool.Shared.Get(Ct);
        context.Properties.Set(SourceFlowPipelines.OnIngestRetry, (error, delay) =>
        {
            error.ShouldBeOfType<TimeoutException>();
            delays.Add(delay);
            return ValueTask.CompletedTask;
        });

        int polls = 0;
        int moved = await pipelines.Ingest.ExecuteAsync((_, _) =>
        {
            if (++polls < 4)
            {
                throw new TimeoutException("broker");
            }

            return ValueTask.FromResult(2);
        }, context, 0);

        moved.ShouldBe(2);
        polls.ShouldBe(4, "it keeps polling until the broker answers");
        delays.ShouldBe([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)], "1 s doubling, capped at the 100 ms interval");
    }

    [Fact]
    [Trait("Id", "UT-RES-05")]
    [Trait("Covers", "doc 04 9.1")]
    public void StoredRetries_DoubleUpToTheirCap_WithEqualJitterWhenAsked()
    {
        TimeSpan first = TimeSpan.FromSeconds(2);
        TimeSpan max = TimeSpan.FromMinutes(5);
        Enumerable.Range(1, 10).Select(a => Backoff.Exponential(a, first, max, jitter: false))
            .ShouldBe(new[] { 2, 4, 8, 16, 32, 64, 128, 256, 300, 300 }.Select(s => TimeSpan.FromSeconds(s)));

        var random = new Random(42);
        for (int attempt = 1; attempt <= 12; attempt++)
        {
            TimeSpan full = Backoff.Exponential(attempt, first, max, jitter: false);
            TimeSpan jittered = Backoff.Exponential(attempt, first, max, jitter: true, random);
            jittered.ShouldBeInRange(full / 2, full, $"attempt {attempt}");
        }
    }

    [Fact]
    [Trait("Id", "UT-RES-06")]
    [Trait("Covers", "ADR-013;doc 12 3")]
    public async Task ConfiguredStrategies_RunInsideSourceFlowsOwn_AndUnknownPipelinesAreRefused()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        int seen = 0;
        Hosting.SourceFlowBuilder? captured = null;
        Hosting.ServiceCollectionExtensions.AddSourceFlow(services, sf =>
        {
            captured = sf;
            sf.ServiceName = "bank";
            sf.UseInMemoryStorage();
            sf.UseInMemoryTransport();
            sf.Resilience(r => r.Configure(SourceFlowResilience.Immediate, b => b.AddStrategy(_ => new CountingStrategy(() => seen++), new CountingOptions())));
        });

        Should.Throw<ArgumentException>(() => captured!.Resilience(r => r.Configure("sourceflow.nope", _ => { })));
        SourceFlowRuntime runtime = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SourceFlowRuntime>(
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services));

        int attempts = 0;
        await Should.ThrowAsync<TimeoutException>(async () => await runtime.Resilience.Immediate.ExecuteAsync(_ =>
        {
            attempts++;
            throw new TimeoutException();
        }, Ct));
        seen.ShouldBe(attempts, "the added strategy runs inside the retry, once per attempt");
        attempts.ShouldBe(runtime.Options.ImmediateRetries + 1);
    }

    private static SourceFlow.Messaging.MessageEnvelope Envelope()
    {
        return new SourceFlow.Messaging.MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "bank.test",
            TypeVersion = 1,
            Source = "tests",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body = Array.Empty<byte>(),
        };
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class CountingOptions : ResilienceStrategyOptions;

    private sealed class CountingStrategy(Action onExecute) : ResilienceStrategy
    {
        protected override ValueTask<Outcome<TResult>> ExecuteCore<TResult, TState>(Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> callback, ResilienceContext context, TState state)
        {
            onExecute();
            return callback(context, state);
        }
    }
}
