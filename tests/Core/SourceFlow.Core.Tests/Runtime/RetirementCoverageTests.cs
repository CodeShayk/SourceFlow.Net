using Bank.Accounts;
using Bank.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Testing;
using SourceFlow.Transport;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>
/// Unit tests that plan 07 names as the v3 replacements of the v2 AWS provider's tests (WP-3.R): UT-CMD-12, UT-CMD-23 and
/// UT-OUT-05..07. The behaviour existed; these make each row's test explicit before the v2 code is deleted.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RetirementCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-CMD-12")]
    [Trait("Covers", "ADR-006;doc 03 7")]
    public async Task MappedPublication_IsEnqueuedToTheOutbox_InTheCommandsOwnCommit()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.Roles = SourceFlowRoles.Api;   // no relay: the row stays visible
            sf.Routing(r => r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events"));
        }, cancellationToken: Ct);
        long commits = host.Store.CommitCount;

        await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 5m), cancellationToken: Ct);

        OutboxRecord row = host.Store.OutboxSnapshot().ShouldHaveSingleItem();
        row.Message.Envelope.Type.ShouldBe(host.Runtime.Model.Registry.Get(typeof(AccountOpenedV1)).Name, "the contract, not the domain event");
        row.Message.Destination.ShouldBe("accounts.events");
        (host.Store.CommitCount - commits).ShouldBe(1, "event and outbox row in one commit");
    }

    [Fact]
    [Trait("Id", "UT-CMD-23")]
    [Trait("Covers", "ADR-007")]
    public async Task RoutedCommand_IsSentDirectToItsOwner_AndReturnsDispatched()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "transfers";
            sf.AddModule<RemoteCommands>();
#pragma warning disable SF0014 // the Accounts service owns Deposit
            sf.Routing(r => r.Command<Deposit>().To("accounts"));
#pragma warning restore SF0014
        }, cancellationToken: Ct);
        host.Broker.DeclareEndpoint("accounts");
        await using ITransportReceiver owner = await host.Broker.CreateTransport().CreateReceiverAsync(new ReceiverOptions("accounts", []), Ct);

        CommandResult result = await host.SendAsync(new Deposit(AccountId.New(), 5m), cancellationToken: Ct);

        result.Status.ShouldBe(CommandStatus.Dispatched);
        ReceivedMessage received = (await owner.ReceiveAsync(10, TimeSpan.FromSeconds(1), Ct)).ShouldHaveSingleItem();
        received.Envelope.Type.ShouldBe("bank.account.deposit");
        host.Store.OutboxSnapshot().ShouldBeEmpty("an API send goes straight to the broker, not through the outbox");
    }

    [Fact]
    [Trait("Id", "UT-OUT-05")]
    [Trait("Id", "UT-OUT-06")]
    [Trait("Id", "UT-OUT-07")]
    [Trait("Covers", "ADR-006;doc 04 6")]
    public async Task PublishFailure_BacksOffItsKey_LaterRowsOfTheKeyWait_OtherKeysProceed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(56));
        var transport = new SelectiveTransport();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.Roles = SourceFlowRoles.Api;   // the test runs the relay
            sf.Routing(r =>
            {
                r.Publish<AccountOpened>().AsIs().To("accounts.events");
                r.Publish<MoneyDeposited>().AsIs().To("accounts.events");
            });
            sf.UseTransport(_ => transport);
            sf.Configure(o => o.OutboxPartitions = 1);
        }, time, cancellationToken: Ct);
        OutboxRelay relay = host.Services.GetRequiredService<OutboxRelay>();
        AccountId failing = AccountId.New(time);
        AccountId healthy = AccountId.New(time);
        transport.FailingKey = StreamId.For(BankAccount.AggregateName, failing).Value;
        await host.SendAsync(new OpenAccount(failing, "Ada", 5m), cancellationToken: Ct);
        await host.SendAsync(new Deposit(failing, 1m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(healthy, "Grace", 5m), cancellationToken: Ct);
        await host.SendAsync(new Deposit(healthy, 1m), cancellationToken: Ct);

        await relay.RunOnceAsync(Ct);
        await relay.RunOnceAsync(Ct);

        transport.Published.Select(e => e.PartitionKey).ShouldNotContain(transport.FailingKey);
        transport.Published.Count.ShouldBe(2, "the healthy key's two events");
        OutboxRecord head = host.Store.OutboxSnapshot().OrderBy(r => r.Sequence).First();
        head.Attempts.ShouldBe(1);
        head.Message.DeliverAt.ShouldBeGreaterThan(time.GetUtcNow(), "backed off");
        host.Store.OutboxSnapshot().Count.ShouldBe(2, "the failed head and the row behind it, in order");

        transport.FailingKey = null;
        time.Advance(TimeSpan.FromMinutes(10));
        await relay.RunOnceAsync(Ct);
        await relay.RunOnceAsync(Ct);

        host.Store.OutboxSnapshot().ShouldBeEmpty();
        transport.Published.Where(e => e.PartitionKey == StreamId.For(BankAccount.AggregateName, failing).Value).Select(e => e.Type)
            .ShouldBe(["bank.account.opened", "bank.account.deposited"], "the key's order is kept");
    }

    private sealed class RemoteCommands : ISourceFlowModule
    {
        public static string ModuleName => nameof(RemoteCommands);

        public static void Register(ISourceFlowModuleBuilder builder)
        {
            builder.AddMessage<Deposit>("bank.account.deposit", 1);
            builder.AddJsonTypeInfoResolver(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
        }
    }

    // Fails every publish of one partition key, as a broker partition or a poison message would.
    private sealed class SelectiveTransport : ITransport
    {
        public string? FailingKey { get; set; }

        public List<MessageEnvelope> Published { get; } = [];

        public string SystemName => "test";

        public TransportCapabilities Capabilities => new(1024 * 1024, 10, TimeSpan.FromDays(4), NativeDelay: false, TimeSpan.Zero);

        public ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            return PublishAsync(endpoint, messages, cancellationToken);
        }

        public ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
        {
            if (messages.Any(m => m.PartitionKey == FailingKey))
            {
                throw new TransportUnavailableException("partition unavailable");
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
