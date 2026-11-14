using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Testing;
using SourceFlow.Transport;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>UT-OUT-19..20 (WP-C.6, CR-43): the relay publishes a destination's rows in one batch call and renews its lease while it drains.</summary>
[Trait("Category", "Unit")]
public sealed class OutboxRelayBatchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-OUT-19")]
    [Trait("Covers", "doc 04 7")]
    public async Task ClaimedRows_ForOneDestination_GoOutInOneBatchCall()
    {
        var transport = new RecordingTransport();
        await using SourceFlowTestHost host = await StartAsync(transport, new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56)));
        for (int i = 0; i < 4; i++)
        {
            await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 5m), cancellationToken: Ct);   // four keys
        }

        await host.Services.GetRequiredService<OutboxRelay>().RunOnceAsync(Ct);

        transport.Calls.ShouldHaveSingleItem().ShouldBe(4);
        host.Store.OutboxSnapshot().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-OUT-20")]
    [Trait("Covers", "doc 05 7.4")]
    public async Task ALongDrain_RenewsItsLease_SoItIsNotFencedHalfway()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        var transport = new RecordingTransport { OnPublish = () => time.Advance(TimeSpan.FromSeconds(20)) };   // a slow broker
        await using SourceFlowTestHost host = await StartAsync(transport, time, o =>
        {
            o.OutboxBatchSize = 1;                       // one row per claim: the drain spans several claims
            o.LeaseTtl = TimeSpan.FromSeconds(30);
        });
        for (int i = 0; i < 4; i++)
        {
            await host.SendAsync(new OpenAccount(AccountId.New(time), "Ada", 5m), cancellationToken: Ct);
        }

        int work = await host.Services.GetRequiredService<OutboxRelay>().RunOnceAsync(Ct);   // 80 s of publishing on a 30 s lease

        work.ShouldBe(4);
        transport.Calls.Sum().ShouldBe(4);
        host.Store.OutboxSnapshot().ShouldBeEmpty("each delivered row was deleted under a live lease");
    }

    private static Task<SourceFlowTestHost> StartAsync(RecordingTransport transport, TimeProvider time, Action<SourceFlowOptions>? options = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Roles = SourceFlowRoles.Api;   // the test runs the relay
            sf.Routing(r => r.Publish<AccountOpened>().AsIs().To("accounts.events"));
            sf.UseTransport(_ => transport);
            sf.Configure(o =>
            {
                o.OutboxPartitions = 1;
                options?.Invoke(o);
            });
        }, time, cancellationToken: Ct);
    }

    // Records the size of every publish call.
    private sealed class RecordingTransport : ITransport
    {
        public List<int> Calls { get; } = [];

        public Action? OnPublish { get; init; }

        public string SystemName => "test";

        public TransportCapabilities Capabilities => new(1024 * 1024, 10, TimeSpan.FromDays(4), NativeDelay: false, TimeSpan.Zero);

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            return PublishAsync(endpoint, messages, cancellationToken);
        }

        public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            Calls.Add(messages.Count);
            OnPublish?.Invoke();
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
