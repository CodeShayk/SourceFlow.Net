using Bank.Accounts;
using Bank.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Azure;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Storage.Tests.PostgreSql;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// Cross-service scenarios on PostgreSQL over every transport (plan 06 §4, WP-3.6): the same Bank services and assertions
/// on the in-memory broker, LocalStack, the Pub/Sub emulator and the Service Bus emulator.
/// </summary>
/// <remarks>
/// E2E-17..20: an integration event reaches every subscribing service once (one subscription per service, RT-R4, GAP-13).
/// E2E-21..24: a command routed to its owner is decided there, in the owner's own stream, exactly once although the broker
/// delivers it twice (ADR-007, GAP-19).
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Requires", "Docker")]
public sealed class CrossServiceScenarios(PostgreSqlServer postgreSql, LocalStackServer localStack, PubSubEmulatorServer pubSub, ServiceBusEmulatorServer serviceBus)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private Servers Servers => new(postgreSql, localStack, pubSub, serviceBus);

    // ================================================================ E2E-17..20: integration events, one subscription per service

    [Fact]
    [Trait("Id", "E2E-17")]
    [Trait("Covers", "RT-R4;GAP-13")]
    public Task IntegrationEvent_ReachesEveryService_InMemory() => IntegrationEventReachesEveryServiceAsync(Broker.InMemory);

    [Fact]
    [Trait("Id", "E2E-18")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    public Task IntegrationEvent_ReachesEveryService_Aws() => IntegrationEventReachesEveryServiceAsync(Broker.Aws);

    [Fact]
    [Trait("Id", "E2E-19")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    public Task IntegrationEvent_ReachesEveryService_Gcp() => IntegrationEventReachesEveryServiceAsync(Broker.Gcp);

    [Fact]
    [Trait("Id", "E2E-20")]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    public Task IntegrationEvent_ReachesEveryService_Azure() => IntegrationEventReachesEveryServiceAsync(Broker.Azure);

    // ================================================================ E2E-21..24: a remote command, owned by Notifications

    [Fact]
    [Trait("Id", "E2E-21")]
    [Trait("Covers", "ADR-007;GAP-19")]
    public Task RemoteCommand_IsDecidedOnceByItsOwner_InMemory() => RemoteCommandIsDecidedOnceByItsOwnerAsync(Broker.InMemory);

    [Fact]
    [Trait("Id", "E2E-22")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    public Task RemoteCommand_IsDecidedOnceByItsOwner_Aws() => RemoteCommandIsDecidedOnceByItsOwnerAsync(Broker.Aws);

    [Fact]
    [Trait("Id", "E2E-23")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    public Task RemoteCommand_IsDecidedOnceByItsOwner_Gcp() => RemoteCommandIsDecidedOnceByItsOwnerAsync(Broker.Gcp);

    [Fact]
    [Trait("Id", "E2E-24")]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    public Task RemoteCommand_IsDecidedOnceByItsOwner_Azure() => RemoteCommandIsDecidedOnceByItsOwnerAsync(Broker.Azure);

    // ================================================================ scenarios

    // Accounts publishes AccountOpenedV1. Notifications (two competing instances) and Loyalty each have their own
    // subscription: every service welcomes every customer, once.
    private async Task IntegrationEventReachesEveryServiceAsync(Broker broker)
    {
        await using CrossServiceSystem system = await CrossServiceSystem.StartAsync(Servers, broker);
        ICommandSender accounts = system.Accounts.Services.GetRequiredService<ICommandSender>();
        AccountId[] opened = [.. Enumerable.Range(0, 5).Select(_ => AccountId.New())];

        foreach (AccountId id in opened)
        {
            (await accounts.SendAsync(new OpenAccount(id, "Ada", 10m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);
        }

        foreach (IHost subscriber in (IHost[])[system.Notifications[0], system.Loyalty])
        {
            foreach (AccountId id in opened)
            {
                await EventuallyAsync(async () => (await CrossServiceSystem.EventsAsync(subscriber, CrossServiceSystem.Inbox(id))).Count > 0, $"the welcome for {id} in {Name(subscriber)}", system);
            }
        }

        await QuietAsync();
        foreach (IHost subscriber in (IHost[])[system.Notifications[0], system.Notifications[1], system.Loyalty])
        {
            foreach (AccountId id in opened)
            {
                IReadOnlyList<(string Type, object Event)> inbox = await CrossServiceSystem.EventsAsync(subscriber, CrossServiceSystem.Inbox(id));
                inbox.Select(e => e.Type).ShouldBe(["notifications.welcome.sent"], $"{Name(subscriber)} welcomes {id} once");
            }
        }

        (await CrossServiceSystem.EventsAsync(system.Accounts, CrossServiceSystem.Inbox(opened[0]))).ShouldBeEmpty("Accounts does not own the inbox");
        foreach (string service in (string[])["notifications", "loyalty"])
        {
            int handled = system.Received.Deliveries(service, AccountOpened) - system.Received.Duplicates(service, AccountOpened);
            handled.ShouldBe(opened.Length, $"{service} handles each event once, however many instances it runs");
        }
    }

    // Accounts routes RequestStatement to Notifications, the owner. Every message Accounts sends reaches the broker twice;
    // Notifications decides each statement once, in its own stream, which it can read back (replay) from the start.
    private async Task RemoteCommandIsDecidedOnceByItsOwnerAsync(Broker broker)
    {
        await using CrossServiceSystem system = await CrossServiceSystem.StartAsync(Servers, broker, duplicateAccountsMessages: true);
        ICommandSender accounts = system.Accounts.Services.GetRequiredService<ICommandSender>();
        AccountId account = AccountId.New();
        var customer = new CustomerId(account.Value);
        (await accounts.SendAsync(new OpenAccount(account, "Ada", 10m), null, Ct)).Status.ShouldBe(CommandStatus.Accepted);

        CommandResult october = await accounts.SendAsync(new RequestStatement(customer, "2026-10"), null, Ct);
        CommandResult november = await accounts.SendAsync(new RequestStatement(customer, "2026-11"), null, Ct);

        october.Status.ShouldBe(CommandStatus.Dispatched, "Accounts does not own the command; it routes it");
        november.Status.ShouldBe(CommandStatus.Dispatched);
        StreamId inbox = CrossServiceSystem.Inbox(account);
        IHost owner = system.Notifications[0];
        await EventuallyAsync(async () => (await CrossServiceSystem.EventsAsync(owner, inbox)).Count(e => e.Type == "notifications.statement.sent") == 2, "both statements", system);

        // Every message reached the broker twice: Notifications got each request twice and its inbox absorbed one of each.
        await EventuallyAsync(() => Task.FromResult(system.Received.Duplicates("notifications", StatementRequest) >= 2), "the duplicate requests", system);
        await QuietAsync();
        system.Received.Deliveries("notifications", StatementRequest).ShouldBe(4);
        system.Received.Duplicates("notifications", StatementRequest).ShouldBe(2);
        IReadOnlyList<(string Type, object Event)> replayed = await CrossServiceSystem.EventsAsync(owner, inbox);
        replayed.Select(e => e.Type).ShouldBe(["notifications.welcome.sent", "notifications.statement.sent", "notifications.statement.sent"], ignoreOrder: true);
        replayed.Select(e => e.Event).OfType<StatementSent>().Select(s => s.Period).ShouldBe(["2026-10", "2026-11"], ignoreOrder: true, "each statement once although every message was delivered twice");
        (await CrossServiceSystem.EventsAsync(system.Accounts, inbox)).ShouldBeEmpty("the owner's stream is in the owner's store");
        (await CrossServiceSystem.EventsAsync(system.Loyalty, inbox)).Select(e => e.Type).ShouldBe(["notifications.welcome.sent"], "Loyalty only subscribes; the duplicated publication is absorbed too");
    }

    // ================================================================ helpers

    private const string StatementRequest = "notifications.statement.request.v1";
    private const string AccountOpened = "accounts.account-opened.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Name(IHost host)
    {
        return host.Services.GetRequiredService<Runtime.SourceFlowRuntime>().ServiceName;
    }

    // Lets redelivered and duplicated messages arrive, so "once" is checked after they had their chance.
    private static Task QuietAsync()
    {
        return Task.Delay(TimeSpan.FromSeconds(3), TimeProvider.System, Ct);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what, CrossServiceSystem system)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + Patience;
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out after {Patience.TotalSeconds:0} s waiting for {what}. {await system.DiagnoseAsync()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
        }
    }
}
