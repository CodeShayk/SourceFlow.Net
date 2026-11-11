using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Testing;
using SourceFlow.Transport;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-OUT (outbox relay failures) and UT-RX (receive retries and parking).</summary>
[Trait("Category", "Unit")]
public sealed class DeliveryFailureTests
{
    [Fact]
    [Trait("Id", "UT-OUT-01")]
    [Trait("Covers", "ADR-006")]
    public async Task Relay_TransportDown_RetriesWithBackoff_ThenParksAfterMaxAttempts()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        var transport = new FailingTransport();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
            sf.UseTransport(_ => transport);
            sf.Roles = Hosting.SourceFlowRoles.Api;
            sf.Configure(o => o.OutboxMaxAttempts = 3);
        }, time);
        OutboxRelay relay = host.Services.GetRequiredService<OutboxRelay>();

        await host.SendAsync(new OpenAccount(AccountId.New(time), "Ada", 1m));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            (await relay.RunOnceAsync(CancellationToken.None)).ShouldBe(1);
            (await relay.RunOnceAsync(CancellationToken.None)).ShouldBe(0); // backing off: not due yet
            time.Advance(TimeSpan.FromMinutes(10));
        }

        transport.Attempts.ShouldBe(3);
        host.Store.OutboxSnapshot().ShouldBeEmpty();
        DeadLetter parked = (await host.DeadLetters.ListAsync(new DeadLetterQuery())).ShouldHaveSingleItem();
        parked.Reason.ShouldBe("Exhausted");
        parked.Destination.ShouldBe("accounts.events");
        parked.DestinationIsTopic.ShouldBeTrue();

        transport.Healthy = true;
        await host.DeadLetters.ReplayAsync(parked.Id, "broker restored");
        transport.Published.ShouldHaveSingleItem().Type.ShouldBe("bank.account.opened");
        (await host.DeadLetters.ListAsync(new DeadLetterQuery())).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-OUT-02")]
    public async Task Command_OverTheTransportLimit_FailsBeforeTheOutboxInsert()
    {
        var transport = new FailingTransport { Limit = 10 };
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
            sf.UseTransport(_ => transport);
            sf.Roles = Hosting.SourceFlowRoles.Api;
        });

        await Should.ThrowAsync<MessageTooLargeException>(() => host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 1m)).AsTask());
        host.Store.OutboxSnapshot().ShouldBeEmpty();
        host.Store.CommitCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "UT-RX-01")]
    [Trait("Covers", "doc 04 9.2")]
    public async Task Receive_TransientFailure_DelayedRetries_ThenParked()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        var flaky = new FlakyHandler { FailuresLeft = int.MaxValue };
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseMiddleware(_ => flaky, Pipeline.PipelineStage.Receive);
            sf.Configure(o =>
            {
                o.ImmediateRetries = 1;
                o.DelayedRetries.Clear();
                o.DelayedRetries.Add(TimeSpan.FromSeconds(10));
                o.DelayedRetries.Add(TimeSpan.FromSeconds(20));
            });
        }, time);

        host.Broker.Send("accounts", host.Runtime.CreateEnvelope(new Deposit(AccountId.New(time), 1m)));
        await host.WaitForIdleAsync();
        time.Advance(TimeSpan.FromSeconds(11));
        await host.WaitForIdleAsync();
        time.Advance(TimeSpan.FromSeconds(21));
        await host.WaitForIdleAsync();

        flaky.Calls.ShouldBe(6); // 3 deliveries x (1 + 1 immediate retry)
        DeadLetter parked = (await host.DeadLetters.ListAsync(new DeadLetterQuery())).ShouldHaveSingleItem();
        parked.Reason.ShouldBe("Exhausted");
        parked.Attempts.ShouldBe(3);
    }

    [Fact]
    [Trait("Id", "UT-RX-02")]
    public async Task Receive_TransientFailureThatRecovers_IsHandledOnce()
    {
        var flaky = new FlakyHandler { FailuresLeft = 1 };
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseMiddleware(_ => flaky, Pipeline.PipelineStage.Receive);
        });
        var id = AccountId.New();

        host.Broker.Send("accounts", host.Runtime.CreateEnvelope(new OpenAccount(id, "Ada", 5m)));
        await host.WaitForIdleAsync();

        (await host.EventsAsync(StreamId.For(BankAccount.AggregateName, id))).ShouldHaveSingleItem().ShouldBeOfType<AccountOpened>();
        (await host.DeadLetters.ListAsync(new DeadLetterQuery())).ShouldBeEmpty();
    }

    private sealed class FlakyHandler : Pipeline.IMessageMiddleware
    {
        private int _calls;

        public int FailuresLeft { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask InvokeAsync(Pipeline.MessageContext context, Pipeline.MessageDelegate next)
        {
            Interlocked.Increment(ref _calls);
            if (FailuresLeft-- > 0)
            {
                throw new TimeoutException("dependency down");
            }

            return next(context);
        }
    }

    private sealed class FailingTransport : ITransport
    {
        public bool Healthy { get; set; }

        public int Attempts { get; private set; }

        public int Limit { get; set; } = 1024 * 1024;

        public List<MessageEnvelope> Published { get; } = [];

        public string SystemName => "test";

        public TransportCapabilities Capabilities => new(Limit, 10, TimeSpan.FromDays(4), NativeDelay: false, TimeSpan.Zero);

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            return PublishAsync(endpoint, messages, cancellationToken);
        }

        public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            Attempts++;
            if (!Healthy)
            {
                throw new TimeoutException("broker unavailable");
            }

            Published.AddRange(messages);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
