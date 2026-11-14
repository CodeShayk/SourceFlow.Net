using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Testing;
using SourceFlow.Transport;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-MW-21..27 (WP-C.15, ADR-012, CR-46): every middleware stage runs where the design puts it.</summary>
[Trait("Category", "Unit")]
public sealed class StageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-MW-21")]
    [Trait("Covers", "ADR-012")]
    public async Task ALocalSend_RunsTheSendStage_ThenTheHandleStage_OnceEach()
    {
        var seen = new List<(PipelineStage Stage, string Type)>();
        await using SourceFlowTestHost host = await StartAsync(sf => sf.UseMiddleware(_ => new Recording(seen), PipelineStage.Send | PipelineStage.Handle));

        (await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);

        seen.ShouldBe([(PipelineStage.Send, nameof(OpenAccount)), (PipelineStage.Handle, nameof(OpenAccount))]);
    }

    [Fact]
    [Trait("Id", "UT-MW-22")]
    [Trait("Covers", "ADR-012")]
    public async Task AHandleStageMiddleware_CanRefuseACommand_WithoutItBeingDecided()
    {
        await using SourceFlowTestHost host = await StartAsync(sf => sf.UseMiddleware(_ => new Refusing(), PipelineStage.Handle));
        var id = AccountId.New();

        CommandResult result = await host.SendAsync(new OpenAccount(id, "Mallory", 1m), cancellationToken: Ct);

        result.Status.ShouldBe(CommandStatus.Rejected);
        result.Error!.Code.ShouldBe("auth.denied");
        (await host.EventsAsync(StreamId.For(BankAccount.AggregateName, id))).ShouldBeEmpty("the command was never decided");
    }

    [Fact]
    [Trait("Id", "UT-MW-23")]
    [Trait("Covers", "ADR-012;RT-R2")]
    public async Task AHandleStageMiddleware_SeesRemoteCommands_AndReceiveMiddleware_TheDeserialisedMessage()
    {
        var seen = new List<(PipelineStage Stage, string Type)>();
        object? received = null;
        await using SourceFlowTestHost host = await StartAsync(sf =>
        {
            sf.UseMiddleware(_ => new Recording(seen), PipelineStage.Handle);
            sf.UseMiddleware(_ => new Peeking(m => received = m), PipelineStage.Receive);
        });

        var id = AccountId.New();
        host.Broker.Send("accounts", host.Runtime.CreateEnvelope(new OpenAccount(id, "Grace", 2m)));
        await host.WaitForIdleAsync();

        received.ShouldBeOfType<OpenAccount>().Holder.ShouldBe("Grace", "deserialised before user middleware");
        seen.ShouldBe([(PipelineStage.Handle, nameof(OpenAccount))], "a command from another service is handled through the Handle stage too");
    }

    [Fact]
    [Trait("Id", "UT-MW-24")]
    [Trait("Covers", "ADR-012")]
    public async Task APublishStageMiddleware_ChangesTheEnvelopeTheBrokerGets_OrDropsTheMessage()
    {
        var transport = new RecordingTransport();
        await using SourceFlowTestHost host = await StartAsync(sf =>
        {
            sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
            sf.UseTransport(_ => transport);
            sf.Roles = Hosting.SourceFlowRoles.Api;
            sf.UseMiddleware(_ => new Stamping(), PipelineStage.Publish);
        });
        OutboxRelay relay = host.Services.GetRequiredService<OutboxRelay>();

        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(AccountId.New(), "drop-me", 1m), cancellationToken: Ct);
        await relay.RunOnceAsync(Ct);

        MessageEnvelope published = transport.Published.ShouldHaveSingleItem("the second was dropped by the middleware");
        published.Headers!["x-stamped"].ShouldBe("accounts.events");
        host.Store.OutboxSnapshot().ShouldBeEmpty("a dropped message completes its row");
    }

    [Fact]
    [Trait("Id", "UT-MW-25")]
    [Trait("Covers", "ADR-012")]
    public async Task AProjectStageMiddleware_SeesTheEventAndTheSubscriptionsSession()
    {
        var seen = new List<string>();
        await using SourceFlowTestHost host = await StartAsync(sf => sf.UseMiddleware(_ => new Projecting(seen), PipelineStage.Project));

        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        seen.ShouldContain(s => s.StartsWith("projection:", StringComparison.Ordinal) && s.EndsWith("|AccountOpened|session", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Id", "UT-MW-26")]
    [Trait("Covers", "ADR-012")]
    public async Task ThePublishAndProjectStages_RunOnlyWithMiddleware()
    {
        var pipeline = new MessagePipeline([new MiddlewareRegistration(_ => new Recording([]), PipelineStage.Project)]);

        pipeline.Has(PipelineStage.Project).ShouldBeTrue();
        pipeline.Has(PipelineStage.Publish).ShouldBeFalse("no Publish middleware: the relay skips the stage");
        await using SourceFlowTestHost host = await StartAsync(_ => { });
        host.Runtime.Pipeline.Has(PipelineStage.Publish).ShouldBeFalse();
        host.Runtime.Pipeline.Has(PipelineStage.Project).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-MW-27")]
    [Trait("Covers", "ADR-012;doc 04 4.1")]
    public async Task TheLogScope_WrapsEveryStageButReceive()
    {
        var logger = new ScopeLogger();
        var pipeline = new MessagePipeline([], logger);
        foreach (PipelineStage stage in new[] { PipelineStage.Send, PipelineStage.Handle, PipelineStage.Publish, PipelineStage.Project, PipelineStage.Receive })
        {
            var context = new MessageContext(stage, Envelope(), new ServiceCollection().BuildServiceProvider(), Ct);
            await pipeline.InvokeAsync(context, _ => ValueTask.CompletedTask);
        }

        logger.Scopes.ShouldBe(4, "the receive endpoint opens its own");
    }

    private static Task<SourceFlowTestHost> StartAsync(Action<Hosting.SourceFlowBuilder> configure)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            configure(sf);
        }, cancellationToken: Ct);
    }

    private static MessageEnvelope Envelope()
    {
        return new MessageEnvelope
        {
            MessageId = new MessageId(Guid.CreateVersion7()),
            Type = "bank.test",
            TypeVersion = 1,
            Source = "tests",
            Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Body = Array.Empty<byte>(),
        };
    }

    private sealed class Recording(List<(PipelineStage Stage, string Type)> seen) : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            lock (seen)
            {
                seen.Add((context.Stage, context.Message?.GetType().Name ?? "?"));
            }

            return next(context);
        }
    }

    private sealed class Refusing : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            context.Result = new CommandResult(CommandStatus.Rejected, StreamVersion.None, new Error("auth.denied", "Not allowed."), ConsistencyToken.None);
            context.Outcome = HandlingOutcome.Rejected;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Peeking(Action<object?> peek) : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            peek(context.Message);
            return next(context);
        }
    }

    private sealed class Stamping : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            if (context.Envelope.Body.Span.IndexOf("drop-me"u8) >= 0)
            {
                return ValueTask.CompletedTask;
            }

            var headers = new Dictionary<string, string>(context.Envelope.Headers ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["x-stamped"] = (string)context.Items["sourceflow.destination"]!,
            };
            context.Envelope = context.Envelope with { Headers = headers };
            return next(context);
        }
    }

    private sealed class Projecting(List<string> seen) : IMessageMiddleware
    {
        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            lock (seen)
            {
                seen.Add($"{context.Items["sourceflow.subscription"]}|{context.Message?.GetType().Name}|{(context.Session is null ? "none" : "session")}");
            }

            return next(context);
        }
    }

    private sealed class ScopeLogger : ILogger
    {
        public int Scopes { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            Scopes++;
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class RecordingTransport : ITransport
    {
        public List<MessageEnvelope> Published { get; } = [];

        public string SystemName => "test";

        public TransportCapabilities Capabilities => new(1024 * 1024, 10, TimeSpan.FromDays(4), NativeDelay: false, TimeSpan.Zero);

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken) => PublishAsync(endpoint, messages, cancellationToken);

        public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            Published.AddRange(messages);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
