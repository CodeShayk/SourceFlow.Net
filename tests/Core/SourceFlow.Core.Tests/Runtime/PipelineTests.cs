using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Storage;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-MW (middleware, ADR-012) and UT-HOST options validation.</summary>
[Trait("Category", "Unit")]
public sealed class PipelineTests
{
    private static readonly IServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static MessageContext Context(PipelineStage stage)
    {
        var envelope = new MessageEnvelope { MessageId = MessageId.Empty, Type = "t", TypeVersion = 1, Source = "s", Time = DateTimeOffset.UnixEpoch };
        return new MessageContext(stage, envelope, Services, CancellationToken.None);
    }

    [Fact]
    [Trait("Id", "UT-MW-01")]
    [Trait("Covers", "ADR-012")]
    public async Task Middleware_RunsOutermostFirst_AndWrapsTheTerminal()
    {
        var calls = new List<string>();
        var pipeline = new MessagePipeline(
        [
            new MiddlewareRegistration(_ => new Recording("a", calls), PipelineStage.All),
            new MiddlewareRegistration(_ => new Recording("b", calls), PipelineStage.Receive),
        ]);

        await pipeline.InvokeAsync(Context(PipelineStage.Receive), _ =>
        {
            calls.Add("terminal");
            return ValueTask.CompletedTask;
        });

        calls.ShouldBe(["a:before", "b:before", "terminal", "b:after", "a:after"]);
    }

    [Fact]
    [Trait("Id", "UT-MW-02")]
    public async Task Middleware_OnlyRunsForItsStages_AndFilters()
    {
        var calls = new List<string>();
        var pipeline = new MessagePipeline(
        [
            new MiddlewareRegistration(_ => new Recording("send-only", calls), PipelineStage.Send),
            new MiddlewareRegistration(_ => new Recording("filtered", calls), PipelineStage.All, ctx => ctx.Envelope.Type == "other"),
        ]);

        await pipeline.InvokeAsync(Context(PipelineStage.Project), _ => ValueTask.CompletedTask);

        calls.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-MW-03")]
    public async Task Middleware_CanShortCircuit()
    {
        var pipeline = new MessagePipeline([new MiddlewareRegistration(_ => new Blocking(), PipelineStage.All)]);
        bool reached = false;

        MessageContext context = Context(PipelineStage.Handle);
        await pipeline.InvokeAsync(context, _ =>
        {
            reached = true;
            return ValueTask.CompletedTask;
        });

        reached.ShouldBeFalse();
        context.Outcome.ShouldBe(HandlingOutcome.Rejected);
    }

    [Theory]
    [Trait("Id", "UT-MW-04")]
    [Trait("Covers", "doc 04 9.1")]
    [InlineData(typeof(JsonException), HandlingOutcome.Permanent)]
    [InlineData(typeof(ArgumentException), HandlingOutcome.Permanent)]
    [InlineData(typeof(TimeoutException), HandlingOutcome.Transient)]
    [InlineData(typeof(ConcurrencyException), HandlingOutcome.Transient)]
    [InlineData(typeof(InvalidOperationException), HandlingOutcome.Transient)]
    public void ErrorClassifier_Defaults(Type exceptionType, HandlingOutcome expected)
    {
        Exception exception = exceptionType == typeof(ConcurrencyException)
            ? new ConcurrencyException("conflict")
            : (Exception)Activator.CreateInstance(exceptionType)!;

        new ErrorClassifier().Classify(exception).ShouldBe(expected);
    }

    [Fact]
    [Trait("Id", "UT-MW-05")]
    public void ErrorClassifier_Overrides_AndUnwrapsSingleAggregate()
    {
        ErrorClassifier classifier = new ErrorClassifier().Treat<TimeoutException>(HandlingOutcome.Permanent);

        classifier.Classify(new AggregateException(new TimeoutException())).ShouldBe(HandlingOutcome.Permanent);
        Should.Throw<ArgumentOutOfRangeException>(() => classifier.Treat<Exception>(HandlingOutcome.Completed));
    }

    [Fact]
    [Trait("Id", "UT-HOST-01")]
    [Trait("Covers", "GAP-03")]
    public void Options_InvalidValues_FailValidation()
    {
        var options = new SourceFlowOptions { ServiceName = "Not Valid!", OutboxPartitions = 0, PollInterval = TimeSpan.FromHours(1), SubscriptionPartitions = 3 };

        ValidateOptionsResult attributes = new SourceFlowOptionsValidator().Validate(null, options);
        ValidateOptionsResult timing = new SourceFlowOptionsTimingValidator().Validate(null, options);

        attributes.Failed.ShouldBeTrue();
        attributes.FailureMessage.ShouldContain(nameof(SourceFlowOptions.ServiceName));
        attributes.FailureMessage.ShouldContain(nameof(SourceFlowOptions.OutboxPartitions));
        timing.Failed.ShouldBeTrue();
        timing.FailureMessage.ShouldContain(nameof(SourceFlowOptions.PollInterval));
        timing.FailureMessage.ShouldContain(nameof(SourceFlowOptions.SubscriptionPartitions));
    }

    [Fact]
    [Trait("Id", "UT-HOST-02")]
    public void Options_Defaults_AreValid()
    {
        var options = new SourceFlowOptions { ServiceName = "accounts" };

        new SourceFlowOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
        new SourceFlowOptionsTimingValidator().Validate(null, options).Succeeded.ShouldBeTrue();
        options.NodeId.ShouldNotBe(new SourceFlowOptions().NodeId);
    }

    private sealed class Recording(string name, List<string> calls) : IMessageMiddleware
    {
        public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            calls.Add(name + ":before");
            await next(context);
            calls.Add(name + ":after");
        }
    }

    private sealed class Blocking : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            context.Outcome = HandlingOutcome.Rejected;
            return ValueTask.CompletedTask;
        }
    }
}
