using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceFlow.Diagnostics;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>
/// UT-CMD, UT-MSG-29/30, UT-STO-09, UT-MW-06..11 and 15..20, UT-RX-17..19, UT-HOST-03..08 and UT-RT-15..20: the v3 tests
/// the v2 disposition matrix (plan 07) names for the retired core suites.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RuntimeCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ commands (UT-CMD)

    [Fact]
    [Trait("Id", "UT-CMD-01")]
    [Trait("Id", "UT-CMD-24")]
    [Trait("Covers", "ADR-002;ADR-012")]
    public async Task Accepted_AppendsEventsWithCausationAndCorrelation_ThroughHandleMiddleware()
    {
        var handled = new List<MessageEnvelope>();
        await using SourceFlowTestHost host = await CountersAsync(sf => sf.UseMiddleware(_ => new Capture(handled), PipelineStage.Handle));
        var id = CounterId.New();

        CommandResult result = await host.SendAsync(new Increment(id), new SendOptions { CorrelationId = new CorrelationId("conversation-7") }, Ct);

        result.Status.ShouldBe(CommandStatus.Accepted);
        MessageEnvelope command = handled.ShouldHaveSingleItem();
        StoredEvent stored = (await host.Store.ReadStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(Counter.AggregateName, id), new StreamVersion(1), 10, Ct)).Events.ShouldHaveSingleItem();
        stored.Metadata.CausationId.ShouldBe(command.MessageId);
        stored.Metadata.CorrelationId.Value.ShouldBe("conversation-7");
    }

    [Fact]
    [Trait("Id", "UT-CMD-02")]
    [Trait("Covers", "doc 14 6")]
    public async Task Accepted_ReturnsTheNewVersion_AndAConsistencyTokenAtTheLastEvent()
    {
        await using SourceFlowTestHost host = await CountersAsync();
        var id = CounterId.New();

        await host.SendAsync(new Increment(id), cancellationToken: Ct);
        CommandResult second = await host.SendAsync(new Increment(id), cancellationToken: Ct);

        second.Version.ShouldBe(new StreamVersion(2));
        StoredEvent last = (await host.Store.ReadStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(Counter.AggregateName, id), new StreamVersion(1), 10, Ct)).Events[^1];
        second.Token.Position.ShouldBe(last.Position);
    }

    [Fact]
    [Trait("Id", "UT-CMD-05")]
    [Trait("Covers", "GAP-20")]
    public async Task DefaultTargetId_IsRejectedAsAnInvalidCommand()
    {
        await using SourceFlowTestHost host = await CountersAsync();

        CommandResult result = await host.SendAsync(new Increment(default), cancellationToken: Ct);

        result.Status.ShouldBe(CommandStatus.Rejected);
        result.Error!.Code.ShouldBe("sourceflow.invalid_command");
    }

    [Fact]
    [Trait("Id", "UT-CMD-26")]
    public async Task HandleMiddleware_ShortCircuitWithoutAResult_Throws()
    {
        await using SourceFlowTestHost host = await CountersAsync(sf => sf.UseMiddleware(_ => new Swallow(), PipelineStage.Handle));

        await Should.ThrowAsync<InvalidOperationException>(async () => await host.SendAsync(new Increment(CounterId.New()), cancellationToken: Ct));
    }

    [Fact]
    [Trait("Id", "UT-CMD-27")]
    public async Task NullCommand_Throws()
    {
        await using SourceFlowTestHost host = await CountersAsync();

        await Should.ThrowAsync<ArgumentNullException>(async () => await host.Commands.SendAsync<Increment>(null!, null, Ct));
    }

    [Fact]
    [Trait("Id", "UT-CMD-31")]
    [Trait("Covers", "ADR-002;GAP-06")]
    public async Task Load_IsAPureFold_ReplayHasNoSideEffects()
    {
        await using SourceFlowTestHost host = await CountersAsync();
        var id = CounterId.New();
        for (int i = 0; i < 4; i++)
        {
            await host.SendAsync(new Increment(id), cancellationToken: Ct);
        }

        await host.WaitForIdleAsync();   // the async subscriptions checkpoint the batch in the background
        IAggregateReader reader = host.Services.GetRequiredService<IAggregateReader>();
        long commits = host.Store.CommitCount;
        AggregateSnapshot<Counter> first = await reader.LoadAsync<Counter, CounterId>(id, cancellationToken: Ct);
        AggregateSnapshot<Counter> again = await reader.LoadAsync<Counter, CounterId>(id, cancellationToken: Ct);

        first.ShouldBe(again);
        first.State.Count.ShouldBe(4);
        host.Store.CommitCount.ShouldBe(commits);
        (await host.EventsAsync(StreamId.For(Counter.AggregateName, id))).Count.ShouldBe(4);
    }

    // ================================================================ messages and store

    [Fact]
    [Trait("Id", "UT-MSG-29")]
    [Trait("Covers", "doc 04 1")]
    public async Task Actor_PropagatesIntoEventMetadata()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
        }, services: s => s.AddSingleton<IActorAccessor>(new FixedActor("alice")));
        var id = CounterId.New();

        await host.SendAsync(new Increment(id), cancellationToken: Ct);

        StoredEvent stored = (await host.Store.ReadStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(Counter.AggregateName, id), new StreamVersion(1), 10, Ct)).Events.ShouldHaveSingleItem();
        stored.Metadata.Actor.ShouldBe("alice");
    }

    [Fact]
    [Trait("Id", "UT-MSG-30")]
    [Trait("Covers", "doc 04 1")]
    public async Task Envelope_IsImmutable()
    {
        await using SourceFlowTestHost host = await CountersAsync();
        MessageEnvelope original = host.Runtime.CreateEnvelope(new Increment(CounterId.New()), headers: new Dictionary<string, string> { ["k"] = "v" });

        MessageEnvelope changed = original with { Type = "other" };

        original.Type.ShouldBe("test.counter.increment");
        changed.ShouldNotBeSameAs(original);
        typeof(MessageEnvelope).GetProperty(nameof(MessageEnvelope.Type))!.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
            .ShouldContain(typeof(System.Runtime.CompilerServices.IsExternalInit));
        (original.Headers as IDictionary<string, string>)?.IsReadOnly.ShouldBe(true);
    }

    [Fact]
    [Trait("Id", "UT-STO-09")]
    [Trait("Covers", "doc 04 6")]
    public async Task Inbox_Seen_ReturnsTheStoredResult()
    {
        var store = new InMemoryStore();
        MessageId message = MessageIds.New(TimeProvider.System);
        await using (IStorageSession session = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct))
        {
            session.RecordInbox("consumer", message, new InboxResult(CommandStatus.Rejected, new StreamVersion(3), "account.not_open"));
            await session.CommitAsync(Ct);
        }

        await using IStorageSession reader = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        InboxEntry seen = (await reader.InboxSeenAsync("consumer", message, Ct)).ShouldNotBeNull();
        seen.Result.ShouldBe(new InboxResult(CommandStatus.Rejected, new StreamVersion(3), "account.not_open"));
        (await reader.InboxSeenAsync("other", message, Ct)).ShouldBeNull();
    }

    // ================================================================ middleware (UT-MW-06..11)

    [Fact]
    [Trait("Id", "UT-MW-06")]
    [Trait("Id", "UT-MW-07")]
    public async Task Middleware_CanCatchTheTerminalException_OrLetItPropagate()
    {
        var catching = new MessagePipeline([new MiddlewareRegistration(_ => new Catching(), PipelineStage.All)]);
        var passing = new MessagePipeline([new MiddlewareRegistration(_ => new Passing(), PipelineStage.All)]);
        MessageContext caught = Context(PipelineStage.Receive);

        await catching.InvokeAsync(caught, _ => throw new TimeoutException("terminal"));
        await Should.ThrowAsync<TimeoutException>(async () => await passing.InvokeAsync(Context(PipelineStage.Receive), _ => throw new TimeoutException("terminal")));

        caught.Items["caught"].ShouldBe("terminal");
    }

    [Fact]
    [Trait("Id", "UT-MW-08")]
    [Trait("Id", "UT-MW-11")]
    public async Task Middleware_SeesTheSameMessageInstance_AndSharesItems_WhileTheEnvelopeStaysImmutable()
    {
        object message = new Increment(CounterId.New());
        object? seenByMiddleware = null;
        object? seenByTerminal = null;
        MessageContext context = Context(PipelineStage.Handle);
        context.Message = message;
        MessageEnvelope envelope = context.Envelope;
        var pipeline = new MessagePipeline([new MiddlewareRegistration(_ => new Inline(async (ctx, next) =>
        {
            seenByMiddleware = ctx.Message;
            ctx.Items["tenant-checked"] = true;
            await next(ctx);
        }), PipelineStage.All)]);

        await pipeline.InvokeAsync(context, ctx =>
        {
            seenByTerminal = ctx.Message;
            ctx.Items["tenant-checked"].ShouldBe(true);
            return ValueTask.CompletedTask;
        });

        seenByMiddleware.ShouldBeSameAs(message);
        seenByTerminal.ShouldBeSameAs(message);
        context.Envelope.ShouldBeSameAs(envelope);
    }

    [Fact]
    [Trait("Id", "UT-MW-09")]
    [Trait("Covers", "ADR-012")]
    public async Task HandleOnlyMiddleware_IsNotInvokedAtReceive()
    {
        int calls = 0;
        var pipeline = new MessagePipeline([new MiddlewareRegistration(_ => new Inline(async (ctx, next) =>
        {
            calls++;
            await next(ctx);
        }), PipelineStage.Handle)]);

        await pipeline.InvokeAsync(Context(PipelineStage.Receive), _ => ValueTask.CompletedTask);
        await pipeline.InvokeAsync(Context(PipelineStage.Handle), _ => ValueTask.CompletedTask);

        calls.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-MW-10")]
    public async Task CallingNextTwice_InvokesTheTerminalTwice()
    {
        int terminal = 0;
        var pipeline = new MessagePipeline([new MiddlewareRegistration(_ => new Inline(async (ctx, next) =>
        {
            await next(ctx);
            await next(ctx);
        }), PipelineStage.All)]);

        await pipeline.InvokeAsync(Context(PipelineStage.Receive), _ =>
        {
            terminal++;
            return ValueTask.CompletedTask;
        });

        terminal.ShouldBe(2);
    }

    // ================================================================ built-ins (UT-MW-15..20, UT-RX-17..19)

    [Fact]
    [Trait("Id", "UT-MW-15")]
    [Trait("Id", "UT-RX-17")]
    [Trait("Covers", "ADR-019")]
    public async Task Telemetry_StartsAConsumerSpanOnReceive_LinkedToTheProducer()
    {
        using var metrics = new SourceFlowMetrics();
        var spans = new List<Activity>();
        string consumer = "telemetry-" + Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(Environment.ProcessId)).ToString("N")[..12];
        using ActivityListener listener = Listen(spans, "process " + consumer);
        var producer = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        MessageContext received = Context(PipelineStage.Receive) is var c ? c : null!;
        received.Envelope = received.Envelope with { TraceParent = $"00-{producer.TraceId}-{producer.SpanId}-01" };
        var telemetry = new TelemetryMiddleware(metrics, consumer);

        await telemetry.InvokeAsync(received, _ => ValueTask.CompletedTask);
        await telemetry.InvokeAsync(Context(PipelineStage.Handle), _ => ValueTask.CompletedTask);

        Activity span;
        lock (spans)
        {
            span = spans.ShouldHaveSingleItem();
        }

        span.DisplayName.ShouldBe("process " + consumer);
        span.Kind.ShouldBe(ActivityKind.Consumer);
        span.Links.ShouldHaveSingleItem().Context.TraceId.ShouldBe(producer.TraceId);
        span.GetTagItem(SourceFlowTelemetry.Tags.MessageType).ShouldBe("t");
    }

    [Fact]
    [Trait("Id", "UT-MW-16")]
    [Trait("Id", "UT-RX-19")]
    [Trait("Covers", "ADR-019;GAP-14")]
    public async Task Telemetry_RecordsProcessDurationOnce_WithTheErrorTypeOnFailure()
    {
        using var metrics = new SourceFlowMetrics();
        var recorded = new List<(double Value, string? Error)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter) && instrument.Name == "messaging.process.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? error = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == SourceFlowTelemetry.Tags.ErrorType)
                {
                    error = tag.Value as string;
                }
            }

            recorded.Add((value, error));
        });
        listener.Start();
        var telemetry = new TelemetryMiddleware(metrics, "accounts");
        MessageContext failed = Context(PipelineStage.Receive);

        await telemetry.InvokeAsync(Context(PipelineStage.Receive), ctx =>
        {
            ctx.Outcome = HandlingOutcome.Completed;
            return ValueTask.CompletedTask;
        });
        await telemetry.InvokeAsync(failed, ctx =>
        {
            ctx.Outcome = HandlingOutcome.Permanent;
            ctx.Exception = new JsonException("bad body");
            return ValueTask.CompletedTask;
        });

        recorded.Count.ShouldBe(2);
        recorded[0].Error.ShouldBeNull();
        recorded[1].Error.ShouldBe(nameof(JsonException));
    }

    [Fact]
    [Trait("Id", "UT-MW-17")]
    public async Task LoggingScope_CarriesMessageIdTypeCorrelationAndTenant()
    {
        var logger = new ScopeLogger();
        MessageContext context = Context(PipelineStage.Receive);
        context.Envelope = context.Envelope with { CorrelationId = new CorrelationId("c-1"), TenantId = "tenant-a" };

        await new LoggingScopeMiddleware(logger).InvokeAsync(context, _ => ValueTask.CompletedTask);

        IReadOnlyDictionary<string, object?> scope = logger.Scopes.ShouldHaveSingleItem();
        scope["MessageType"].ShouldBe("t");
        scope["CorrelationId"].ShouldBe("c-1");
        scope["TenantId"].ShouldBe("tenant-a");
        scope.ShouldContainKey("MessageId");
    }

    [Theory]
    [Trait("Id", "UT-MW-18")]
    [Trait("Id", "UT-MW-19")]
    [Trait("Covers", "doc 04 9.1")]
    [InlineData(typeof(JsonException), HandlingOutcome.Permanent)]
    [InlineData(typeof(UnknownMessageTypeException), HandlingOutcome.Permanent)]
    [InlineData(typeof(TimeoutException), HandlingOutcome.Transient)]
    [InlineData(typeof(HttpRequestException), HandlingOutcome.Transient)]
    public async Task ErrorClassification_MapsExceptionsToOutcomes(Type exception, HandlingOutcome expected)
    {
        MessageContext context = Context(PipelineStage.Receive);
        Exception thrown = exception == typeof(UnknownMessageTypeException) ? new UnknownMessageTypeException("x", 1) : (Exception)Activator.CreateInstance(exception)!;

        await new ErrorClassificationMiddleware(new ErrorClassifier()).InvokeAsync(context, _ => throw thrown);

        context.Outcome.ShouldBe(expected);
        context.Exception.ShouldBeSameAs(thrown);
    }

    [Fact]
    [Trait("Id", "UT-MW-20")]
    public async Task ErrorClassification_LetsCancellationThrough()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var envelope = new MessageEnvelope { MessageId = MessageId.Empty, Type = "t", TypeVersion = 1, Source = "s", Time = DateTimeOffset.UnixEpoch };
        var context = new MessageContext(PipelineStage.Receive, envelope, EmptyServices, cancelled.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await new ErrorClassificationMiddleware(new ErrorClassifier()).InvokeAsync(context, ctx => throw new OperationCanceledException(ctx.CancellationToken)));
    }

    [Fact]
    [Trait("Id", "UT-RX-18")]
    [Trait("Covers", "ADR-019")]
    public async Task Receive_CountsConsumedMessages()
    {
        await using SourceFlowTestHost host = await CountersAsync();
        long consumed = 0;
        Meter meter = host.Services.GetRequiredService<SourceFlowMetrics>().Meter;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "messaging.client.consumed.messages")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref consumed, value));
        listener.Start();

        await host.Runtime.Transport.SendAsync(host.Runtime.Routing.Endpoint,
            [host.Runtime.CreateEnvelope(new Increment(CounterId.New())), host.Runtime.CreateEnvelope(new Increment(CounterId.New()))], Ct);
        await host.WaitForIdleAsync();

        Interlocked.Read(ref consumed).ShouldBe(2);
    }

    // ================================================================ hosting (UT-HOST-03..08)

    [Fact]
    [Trait("Id", "UT-HOST-03")]
    [Trait("Covers", "GAP-16")]
    public async Task AddSourceFlow_RegistersTheServices()
    {
        await using ServiceProvider provider = Services(sf => sf.UseInMemoryStorage());

        provider.GetRequiredService<ICommandSender>().ShouldNotBeNull();
        provider.GetRequiredService<IQueryService>().ShouldNotBeNull();
        provider.GetRequiredService<IAggregateReader>().ShouldNotBeNull();
        provider.GetRequiredService<IDeadLetterManager>().ShouldNotBeNull();
        provider.GetRequiredService<RuntimeModel>().Aggregates.ShouldContainKey(typeof(Counter));
        provider.GetServices<IHostedService>().OfType<SourceFlowHost>().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Id", "UT-HOST-04")]
    public void AddSourceFlow_WithoutStorage_Fails()
    {
        InvalidOperationException e = Should.Throw<InvalidOperationException>(() => Services(_ => { }));

        e.Message.ShouldContain("No storage is configured");
    }

    [Fact]
    [Trait("Id", "UT-HOST-05")]
    public async Task ApiRole_RunsNoBackgroundWork()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<Bank.Accounts.AccountsModule>();
            sf.Projections(p => p.Add<Bank.Accounts.AccountSummaryProjection>().Async());
            sf.Roles = SourceFlowRoles.Api;
        });

        (await host.SendAsync(new Bank.Accounts.OpenAccount(Bank.Accounts.AccountId.New(), "Ada", 1m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);
        host.Runtime.Storage.Notifications.ShouldNotBeNull();

        // Commands still run in process, but no subscription loop picked the event up.
        (await host.Store.GetAsync("projection:account-summary@1", 0, Ct)).ShouldBe(GlobalPosition.Start);
    }

    [Fact]
    [Trait("Id", "UT-HOST-06")]
    [Trait("Covers", "GAP-03")]
    public async Task InvalidOptions_FailHostStart()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
            sf.UseInMemoryStorage();
            sf.Configure(o => o.OutboxPartitions = 0);
        });
        using IHost host = builder.Build();

        OptionsValidationException e = await Should.ThrowAsync<OptionsValidationException>(async () => await host.StartAsync(Ct));

        e.Message.ShouldContain(nameof(SourceFlowOptions.OutboxPartitions));
    }

    [Fact]
    [Trait("Id", "UT-HOST-07")]
    [Trait("Covers", "DC-1")]
    public void MessagesWithoutJsonMetadata_FailTheModel_ThereIsNoReflectionFallback()
    {
        InvalidOperationException e = Should.Throw<InvalidOperationException>(() => new SourceFlowModelBuilder().AddModule<NoContextModule>().Build());

        e.Message.ShouldContain("SF0010");
    }

    [Fact]
    [Trait("Id", "UT-HOST-08")]
    public void AddSourceFlow_WithoutAServiceName_Fails()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddSourceFlow(sf => sf.AddModule<CoreTestModule>())).Message.ShouldContain("ServiceName");
    }

    // ================================================================ options (UT-RT-15..20)

    [Theory]
    [Trait("Id", "UT-RT-15")]
    [Trait("Id", "UT-RT-16")]
    [Trait("Id", "UT-RT-17")]
    [Trait("Id", "UT-RT-18")]
    [Trait("Id", "UT-RT-19")]
    [Trait("Id", "UT-RT-20")]
    [Trait("Covers", "GAP-03")]
    [InlineData("OutboxPartitions", nameof(SourceFlowOptions.OutboxPartitions))]
    [InlineData("SubscriptionBatchSize", nameof(SourceFlowOptions.SubscriptionBatchSize))]
    [InlineData("LeaseVsPoll", nameof(SourceFlowOptions.LeaseTtl))]
    [InlineData("DelayedRetries", nameof(SourceFlowOptions.DelayedRetries))]
    [InlineData("SubscriptionPartitions", nameof(SourceFlowOptions.SubscriptionPartitions))]
    [InlineData("RetentionInterval", nameof(SourceFlowOptions.RetentionInterval))]
    public void EachInvalidOption_FailsWithAPreciseMessage(string change, string property)
    {
        var options = new SourceFlowOptions { ServiceName = "accounts" };
        switch (change)
        {
            case "OutboxPartitions":
                options.OutboxPartitions = 0;
                break;
            case "SubscriptionBatchSize":
                options.SubscriptionBatchSize = 0;
                break;
            case "LeaseVsPoll":
                options.PollInterval = TimeSpan.FromSeconds(30);
                options.LeaseTtl = TimeSpan.FromSeconds(10);
                break;
            case "DelayedRetries":
                options.DelayedRetries.Clear();
                options.DelayedRetries.Add(TimeSpan.Zero);
                break;
            case "SubscriptionPartitions":
                options.SubscriptionPartitions = 6;
                break;
            default:
                options.RetentionInterval = TimeSpan.Zero;
                break;
        }

        ValidateOptionsResult attributes = new SourceFlowOptionsValidator().Validate(null, options);
        ValidateOptionsResult timing = new SourceFlowOptionsTimingValidator().Validate(null, options);

        string failures = string.Join(" ", attributes.Failures ?? []) + " " + string.Join(" ", timing.Failures ?? []);
        (attributes.Failed || timing.Failed).ShouldBeTrue();
        failures.ShouldContain(property);
    }

    // ================================================================ helpers

    private static readonly IServiceProvider EmptyServices = new ServiceCollection().BuildServiceProvider();

    private static MessageContext Context(PipelineStage stage)
    {
        var envelope = new MessageEnvelope { MessageId = MessageId.Empty, Type = "t", TypeVersion = 1, Source = "s", Time = DateTimeOffset.UnixEpoch };
        return new MessageContext(stage, envelope, EmptyServices, CancellationToken.None);
    }

    private static Task<SourceFlowTestHost> CountersAsync(Action<SourceFlowBuilder>? configure = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
            configure?.Invoke(sf);
        });
    }

    private static ServiceProvider Services(Action<SourceFlowBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "counters";
            sf.AddModule<CoreTestModule>();
            configure(sf);
        });
        return services.BuildServiceProvider();
    }

    private static ActivityListener Listen(List<Activity> spans, string name)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceFlowTelemetry.ActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == name)
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class Capture(List<MessageEnvelope> envelopes) : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            envelopes.Add(context.Envelope);
            return next(context);
        }
    }

    private sealed class Swallow : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Catching : IMessageMiddleware
    {
        public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            try
            {
                await next(context);
            }
            catch (TimeoutException e)
            {
                context.Items["caught"] = e.Message;
            }
        }
    }

    private sealed class Passing : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            return next(context);
        }
    }

    private sealed class Inline(Func<MessageContext, MessageDelegate, ValueTask> body) : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            return body(context, next);
        }
    }

    private sealed class FixedActor(string actor) : IActorAccessor
    {
        public string? Actor => actor;
    }

    private sealed class ScopeLogger : ILogger
    {
        public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            if (state is IReadOnlyDictionary<string, object?> values)
            {
                Scopes.Add(values);
            }
            else if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                Scopes.Add(pairs.ToDictionary(p => p.Key, p => p.Value));
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}

// A module whose message has no JSON metadata (no JsonSerializerContext): the model must refuse it (UT-HOST-07).
#pragma warning disable SF0001 // deliberately unattributed: the module registers it by hand
public sealed record Unserializable(int Value) : IEvent;
#pragma warning restore SF0001

public sealed class NoContextModule : ISourceFlowModule
{
    public static string ModuleName => "NoContextModule";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        builder.AddMessage<Unserializable>("tests.unserializable", 1);
    }
}
