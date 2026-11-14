using System.Collections.Concurrent;
using System.Text.Json;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Storage;
using SourceFlow.Testing;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-RX-26, UT-RX-27, UT-RT-26 (WP-C.6): parking scope, draining shutdown, retention and delay limits.</summary>
[Trait("Category", "Unit")]
public sealed class ReceivePathTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-RX-26")]
    [Trait("Covers", "doc 06 2")]
    public async Task Parking_WithLogPayloads_LogsThePayload_FromALiveScope()
    {
        var logs = new CapturingLoggerFactory();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseMiddleware(_ => new Throwing(new JsonException("bad body")), PipelineStage.Receive);
            sf.Configure(o => o.LogPayloads = true);
        }, services: s => s.AddSingleton<ILoggerFactory>(logs));

        host.Broker.Send("accounts", host.Runtime.CreateEnvelope(new Deposit(AccountId.New(), 1m)));
        await host.WaitForIdleAsync();

        DeadLetter parked = (await host.DeadLetters.ListAsync(new DeadLetterQuery(), Ct)).ShouldHaveSingleItem();
        parked.Reason.ShouldBe("DeserializationFailed");
        logs.Events.ShouldContain(e => e.Id == 1108);   // CR-10: resolved from the attempt's scope, not a disposed one
        host.Broker.PendingCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "UT-RX-27")]
    [Trait("Covers", "doc 01 9")]
    public async Task Shutdown_FinishesTheHandlerInProgress_AndReleasesQueuedMessagesAtOnce()
    {
        var gate = new Gate();
        var transport = new RecordingTransport(new InMemoryBroker());
        SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseTransport(_ => transport);
            sf.UseMiddleware(_ => gate, PipelineStage.Receive);
            sf.Configure(o => o.ImmediateRetries = 0);
        }, broker: transport.Broker);
        var account = AccountId.New();
        string key = account.ToString();   // one key: one worker, in order
        transport.Broker.Send("accounts", host.Runtime.CreateEnvelope(new OpenAccount(account, "Ada", 5m), partitionKey: key));
        transport.Broker.Send("accounts", host.Runtime.CreateEnvelope(new Deposit(account, 1m), partitionKey: key));
        transport.Broker.Send("accounts", host.Runtime.CreateEnvelope(new Deposit(account, 2m), partitionKey: key));

        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);
        Hosting.SourceFlowHost service = host.Services.GetRequiredService<Hosting.SourceFlowHost>();
        await Until(() => service.ReceiveInFlight == 3);   // all three received: one in the handler, two queued behind it

        Task stopping = host.DisposeAsync().AsTask();   // StoppingAsync starts draining before anything awaits
        gate.Open();
        await stopping.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);

        gate.Calls.ShouldBe(1);                          // the handler in progress finished; the queued two never ran
        transport.Broker.PendingCount.ShouldBe(2);       // left for another instance…
        transport.Abandons.ShouldBe([TimeSpan.Zero, TimeSpan.Zero]);   // …and visible again at once
    }

    [Fact]
    [Trait("Id", "UT-RT-26")]
    [Trait("Covers", "ADR-006")]
    public async Task InboxRetention_ShorterThanTheBrokers_FailsValidate_AndWarnsOtherwise()
    {
        var broker = new InMemoryBroker();
        var transport = new RecordingTransport(broker, c => c with { MaxRetention = TimeSpan.FromDays(30) });

        InvalidOperationException failed = await Should.ThrowAsync<InvalidOperationException>(() => SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseTransport(_ => transport);
            sf.Provisioning = ProvisioningMode.Validate;
        }, broker: broker));
        failed.Message.ShouldContain("InboxRetention");

        var logs = new CapturingLoggerFactory();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseTransport(_ => transport);
            sf.Provisioning = ProvisioningMode.CreateIfMissing;
        }, broker: broker, services: s => s.AddSingleton<ILoggerFactory>(logs));
        logs.Events.ShouldContain(e => e.Id == 1405);
    }

    [Fact]
    [Trait("Id", "UT-RT-26")]
    [Trait("Covers", "doc 04 7")]
    public async Task DelayedRetry_LongerThanTheBrokerCanWait_GoesThroughTheOutbox_AndKeepsCounting()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        var transport = new RecordingTransport(new InMemoryBroker(time), c => c with { MaxDelay = TimeSpan.FromSeconds(5) });
        var failing = new Throwing(new TimeoutException("dependency down"));
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseTransport(_ => transport);
            sf.UseMiddleware(_ => failing, PipelineStage.Receive);
            sf.Configure(o =>
            {
                o.ImmediateRetries = 0;
                o.DelayedRetries.Clear();
                o.DelayedRetries.Add(TimeSpan.FromSeconds(10));
                o.DelayedRetries.Add(TimeSpan.FromSeconds(20));
            });
        }, time, transport.Broker);

        MessageEnvelope sent = host.Runtime.CreateEnvelope(new Deposit(AccountId.New(time), 1m));
        transport.Broker.Send("accounts", sent);
        await host.WaitForIdleAsync();
        failing.Calls.ShouldBe(1);
        time.Advance(TimeSpan.FromSeconds(9));
        await host.WaitForIdleAsync();
        failing.Calls.ShouldBe(1);                       // the full 10 s, not the broker's 5 s
        time.Advance(TimeSpan.FromSeconds(2));
        await host.WaitForIdleAsync();
        failing.Calls.ShouldBe(2);
        time.Advance(TimeSpan.FromSeconds(21));
        await host.WaitForIdleAsync();

        failing.Calls.ShouldBe(3);
        transport.Abandons.ShouldBeEmpty();              // never asked the broker for more than it can wait
        DeadLetter parked = (await host.DeadLetters.ListAsync(new DeadLetterQuery(), Ct)).ShouldHaveSingleItem();
        parked.Reason.ShouldBe("Exhausted");
        parked.Attempts.ShouldBe(3);                     // counted across the re-sends (sfattempt)
        parked.Envelope.MessageId.ShouldBe(sent.MessageId);
    }

    [Fact]
    [Trait("Id", "UT-RT-26")]
    [Trait("Covers", "doc 04 7")]
    public async Task DelayedRetry_LongerThanTheBrokerCanWait_OnAnOrderedEndpoint_IsCapped_AndSaidSo()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        var transport = new RecordingTransport(new InMemoryBroker(time), c => c with { MaxDelay = TimeSpan.FromSeconds(5) });
        var failing = new Throwing(new TimeoutException("dependency down"));
        var logs = new CapturingLoggerFactory();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseTransport(_ => transport);
            sf.Routing(r => r.OrderedEndpoint());
            sf.UseMiddleware(_ => failing, PipelineStage.Receive);
            sf.Configure(o =>
            {
                o.ImmediateRetries = 0;
                o.DelayedRetries.Clear();
                o.DelayedRetries.Add(TimeSpan.FromSeconds(10));
            });
        }, time, transport.Broker, services: s => s.AddSingleton<ILoggerFactory>(logs));
        logs.Events.ShouldContain(e => e.Id == 1404);

        transport.Broker.Send("accounts", host.Runtime.CreateEnvelope(new Deposit(AccountId.New(time), 1m)));
        await host.WaitForIdleAsync();
        time.Advance(TimeSpan.FromSeconds(6));
        await host.WaitForIdleAsync();

        transport.Abandons.ShouldBe([TimeSpan.FromSeconds(5)]);
        failing.Calls.ShouldBe(2);
        host.Store.DueOutboxCount().ShouldBe(0);
    }

    private static async Task Until(Func<bool> condition)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(10);
        while (!condition())
        {
            TimeProvider.System.GetUtcNow().ShouldBeLessThan(deadline, "the condition did not become true");
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
        }
    }

    private sealed class Throwing(Exception exception) : IMessageMiddleware
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            Interlocked.Increment(ref _calls);
            throw exception;
        }
    }

    // Holds the first message until opened.
    private sealed class Gate : IMessageMiddleware
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;

        public int Calls => Volatile.Read(ref _calls);

        public void Open()
        {
            _open.TrySetResult();
        }

        public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
        {
            Interlocked.Increment(ref _calls);
            _entered.TrySetResult();
            await _open.Task.WaitAsync(context.CancellationToken);
            await next(context);
        }
    }

    // The in-memory transport, with adjustable limits and a record of every abandon's delay.
    private sealed class RecordingTransport(InMemoryBroker broker, Func<TransportCapabilities, TransportCapabilities>? limits = null) : ITransport
    {
        private readonly ITransport _inner = broker.CreateTransport();
        private readonly ConcurrentQueue<TimeSpan> _abandons = new();

        public InMemoryBroker Broker => broker;

        public IReadOnlyList<TimeSpan> Abandons => [.. _abandons];

        public string SystemName => _inner.SystemName;

        public TransportCapabilities Capabilities => limits is null ? _inner.Capabilities : limits(_inner.Capabilities);

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            return _inner.SendAsync(endpoint, messages, cancellationToken);
        }

        public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            return _inner.PublishAsync(topic, messages, cancellationToken);
        }

        public async ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            return new Receiver(await _inner.CreateReceiverAsync(options, cancellationToken), _abandons);
        }

        public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken)
        {
            return _inner.CreateDeadLetterReceiverAsync(endpoint, cancellationToken);
        }

        public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
        {
            return _inner.ProvisionAsync(topology, mode, cancellationToken);
        }

        private sealed class Receiver(ITransportReceiver inner, ConcurrentQueue<TimeSpan> abandons) : ITransportReceiver
        {
            public async ValueTask<IReadOnlyList<ReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan wait, CancellationToken cancellationToken)
            {
                return [.. (await inner.ReceiveAsync(maxMessages, wait, cancellationToken)).Select(m => new Message(m, abandons))];
            }

            public ValueTask DisposeAsync()
            {
                return inner.DisposeAsync();
            }
        }

        private sealed class Message(ReceivedMessage inner, ConcurrentQueue<TimeSpan> abandons) : ReceivedMessage
        {
            public override MessageEnvelope Envelope => inner.Envelope;

            public override int DeliveryCount => inner.DeliveryCount;

            public override ValueTask CompleteAsync(CancellationToken cancellationToken)
            {
                return inner.CompleteAsync(cancellationToken);
            }

            public override ValueTask AbandonAsync(TimeSpan delay, CancellationToken cancellationToken)
            {
                abandons.Enqueue(delay);
                return inner.AbandonAsync(delay, cancellationToken);
            }

            public override ValueTask RenewAsync(CancellationToken cancellationToken)
            {
                return inner.RenewAsync(cancellationToken);
            }

            public override ValueTask DeadLetterAsync(string reason, CancellationToken cancellationToken)
            {
                return inner.DeadLetterAsync(reason, cancellationToken);
            }
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<(int Id, string Message)> _events = new();

        public IReadOnlyList<(int Id, string Message)> Events => [.. _events];

        public ILogger CreateLogger(string categoryName)
        {
            return new Capturing(_events);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Capturing(ConcurrentQueue<(int Id, string Message)> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                events.Enqueue((eventId.Id, formatter(state, exception)));
            }
        }
    }
}
