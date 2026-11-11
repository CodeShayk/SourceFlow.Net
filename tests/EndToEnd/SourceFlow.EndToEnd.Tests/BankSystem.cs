using Bank.Accounts;
using Bank.Contracts;
using Bank.Notifications;
using Bank.Transfers;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Hosting;
using SourceFlow.Testing;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>The Bank sample as two services on one in-memory broker: Accounts (with Transfers) and Notifications.</summary>
internal sealed class BankSystem : IAsyncDisposable
{
    private BankSystem(FakeTimeProvider time, InMemoryBroker broker, SourceFlowTestHost accounts, SourceFlowTestHost? notifications)
    {
        Time = time;
        Broker = broker;
        Accounts = accounts;
        Notifications = notifications;
    }

    public FakeTimeProvider Time { get; }

    public InMemoryBroker Broker { get; }

    public SourceFlowTestHost Accounts { get; }

    public SourceFlowTestHost? Notifications { get; }

    public IReadOnlyList<SourceFlowTestHost> Hosts => Notifications is null ? [Accounts] : [Accounts, Notifications];

    public static async Task<BankSystem> StartAsync(bool withNotifications = false, bool inlineSummary = true, int partitions = 1, Action<SourceFlowBuilder>? accounts = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero));
        var broker = new InMemoryBroker(time);
        SourceFlowTestHost accountsHost = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<ContractsModule>();
            sf.AddModule<AccountsModule>();
            sf.AddModule<TransfersModule>();
            sf.Routing(r => r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events"));
            sf.Projections(p =>
            {
                ProjectionModeBuilder summary = p.Add<AccountSummaryProjection>();
                if (!inlineSummary)
                {
                    summary.Async();
                }
                else
                {
                    summary.Inline();
                }
            });
            sf.Configure(o => o.SubscriptionPartitions = partitions);
            accounts?.Invoke(sf);
        }, time, broker);

        SourceFlowTestHost? notificationsHost = null;
        if (withNotifications)
        {
            notificationsHost = await SourceFlowTestHost.StartAsync(sf =>
            {
                sf.ServiceName = "notifications";
                sf.AddModule<ContractsModule>();
                sf.AddModule<NotificationsModule>();
                sf.Routing(r => r.Subscribe("accounts.events"));
            }, time, broker);
        }

        return new BankSystem(time, broker, accountsHost, notificationsHost);
    }

    public Task IdleAsync()
    {
        return SourceFlowTestHost.WaitForIdleAsync(Hosts);
    }

    public async Task<AccountId> OpenAsync(decimal initial = 100m, string holder = "Ada")
    {
        var id = AccountId.New(Time);
        (await Accounts.SendAsync(new OpenAccount(id, holder, initial))).Status.ShouldBe(CommandStatus.Accepted);
        return id;
    }

    public Task<AccountSummary?> SummaryAsync(AccountId id)
    {
        return Accounts.DocumentAsync<AccountSummaryProjection, AccountSummary>(id.ToStreamSegment());
    }

    public async ValueTask DisposeAsync()
    {
        if (Notifications is not null)
        {
            await Notifications.DisposeAsync();
        }

        await Accounts.DisposeAsync();
    }
}
