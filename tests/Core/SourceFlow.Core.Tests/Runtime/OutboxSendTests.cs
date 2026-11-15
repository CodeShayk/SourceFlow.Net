using System.Text;
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Security;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;
using SourceFlow.Transport;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>
/// UT-CMD-33..36 (WP-C.5, ADR-006): every command the service sends goes through the outbox. Sent from a handler, it joins
/// the handler's transaction; sent from an API call, it is written in a session of its own and relayed with payload
/// protection, the size check and retries.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OutboxSendTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-CMD-33")]
    [Trait("Covers", "ADR-006;doc 03 5.2")]
    public async Task ASendFromAHandler_CommitsAndRollsBackWithTheHandler()
    {
        await using SourceFlowTestHost host = await RoutingHostAsync();
        ICommandSender sender = host.Services.GetRequiredService<ICommandSender>();
        MessageEnvelope cause = host.Runtime.CreateEnvelope(new Deposit(AccountId.New(), 1m));

        // The handler throws after sending: its transaction rolls back, and the command with it.
        await using (IStorageSession failing = await host.Runtime.Storage.Sessions.BeginAsync("-", Ct))
        using (AmbientSend.Enter(failing, cause))
        {
            (await sender.SendAsync(new Deposit(AccountId.New(), 5m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Dispatched);
        }

        host.Store.OutboxSnapshot().ShouldBeEmpty("nothing is sent for a handler that didn't commit");

        await using (IStorageSession handled = await host.Runtime.Storage.Sessions.BeginAsync("-", Ct))
        {
            using (AmbientSend.Enter(handled, cause))
            {
                await sender.SendAsync(new Deposit(AccountId.New(), 5m), cancellationToken: Ct);
            }

            host.Store.OutboxSnapshot().ShouldBeEmpty("queued in the handler's session until it commits");
            await handled.CommitAsync(Ct);
        }

        OutboxRecord row = host.Store.OutboxSnapshot().ShouldHaveSingleItem();
        row.Message.Destination.ShouldBe("accounts");
        row.Message.Envelope.CausationId.ShouldBe(cause.MessageId);
    }

    [Fact]
    [Trait("Id", "UT-CMD-34")]
    [Trait("Covers", "ADR-006")]
    public async Task ARedeliveredMessage_SendsTheSameCommandIds()
    {
        MessageEnvelope cause;
        await using (SourceFlowTestHost probe = await RoutingHostAsync())
        {
            cause = probe.Runtime.CreateEnvelope(new Deposit(AccountId.New(), 1m));
        }

        // The same message handled twice (a redelivery, on any node): the same command ids, so the owner's inbox drops the second.
        MessageId[] first = await HandleAsync(cause);
        MessageId[] second = await HandleAsync(cause);

        first.Length.ShouldBe(2);
        first[0].ShouldNotBe(first[1]);
        second.ShouldBe(first);
        AmbientSend.Current.ShouldBeNull("the ambient session ends with the handler");

        static async Task<MessageId[]> HandleAsync(MessageEnvelope cause)
        {
            await using SourceFlowTestHost host = await RoutingHostAsync();
            ICommandSender sender = host.Services.GetRequiredService<ICommandSender>();
            await using IStorageSession session = await host.Runtime.Storage.Sessions.BeginAsync("-", Ct);
            using (AmbientSend.Enter(session, cause))
            {
                await sender.SendAsync(new Deposit(AccountId.New(), 1m), cancellationToken: Ct);
                await sender.SendAsync(new Deposit(AccountId.New(), 2m), cancellationToken: Ct);
            }

            await session.CommitAsync(Ct);
            return [.. host.Store.OutboxSnapshot().OrderBy(r => r.Message.Envelope.Body.Length).ThenBy(r => r.Message.Envelope.MessageId.ToString()).Select(r => r.Message.Envelope.MessageId)];
        }
    }

    [Fact]
    [Trait("Id", "UT-CMD-35")]
    [Trait("Covers", "ADR-006;doc 10 3")]
    public async Task ARoutedApiSend_ToAProtectedDestination_IsEncryptedOnTheWire()
    {
        using var keys = new LocalKeyProvider();
        await using SourceFlowTestHost host = await RoutingHostAsync(sf => sf.Security(s => s.UseKeyProvider(keys).Protect(p => p.Protect("accounts"))));
        host.Broker.DeclareEndpoint("accounts");
        await using ITransportReceiver owner = await host.Broker.CreateTransport().CreateReceiverAsync(new ReceiverOptions("accounts", []), Ct);

        CommandResult result = await host.SendAsync(new Deposit(AccountId.New(), 4321.98m), cancellationToken: Ct);

        result.Status.ShouldBe(CommandStatus.Dispatched);
        ReceivedMessage received = (await owner.ReceiveAsync(10, TimeSpan.FromSeconds(5), Ct)).ShouldHaveSingleItem();
        Encoding.UTF8.GetString(received.Envelope.Body.Span).ShouldNotContain("4321.98", customMessage: "the routed command was sent in clear text (CR-07)");
    }

    [Fact]
    [Trait("Id", "UT-CMD-36")]
    [Trait("Covers", "doc 04 2.3")]
    public async Task AnOversizedRoutedCommand_IsMessageTooLarge_BeforeAnythingIsSent()
    {
        await using SourceFlowTestHost host = await RoutingHostAsync();

        await Should.ThrowAsync<MessageTooLargeException>(async () =>
            await host.SendAsync(new BulkUpload(AccountId.New(), new string('x', 2 * 1024 * 1024)), cancellationToken: Ct));
        host.Store.OutboxSnapshot().ShouldBeEmpty();
    }

    private static Task<SourceFlowTestHost> RoutingHostAsync(Action<Hosting.SourceFlowBuilder>? configure = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "transfers";
            sf.AddModule<RoutedCommandsModule>();
#pragma warning disable SF0014 // the Accounts service owns these commands
            sf.Routing(r =>
            {
                r.Command<Deposit>().To("accounts");
                r.Command<BulkUpload>().To("accounts");
            });
#pragma warning restore SF0014
            configure?.Invoke(sf);
        }, cancellationToken: Ct);
    }

    private sealed class RoutedCommandsModule : ISourceFlowModule
    {
        public static string ModuleName => nameof(RoutedCommandsModule);

        public static void Register(ISourceFlowModuleBuilder builder)
        {
            builder.AddMessage<Deposit>("bank.account.deposit", 1);
            builder.AddMessage<BulkUpload>("test.bulk.upload", 1);
            builder.AddJsonTypeInfoResolver(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
        }
    }
}

// Hand-registered (RoutedCommandsModule), so no [MessageType]: the generated CoreTestModule must not pick it up.
#pragma warning disable SF0001
public sealed record BulkUpload(AccountId AccountId, string Payload) : ICommand<BankAccount>;
#pragma warning restore SF0001
