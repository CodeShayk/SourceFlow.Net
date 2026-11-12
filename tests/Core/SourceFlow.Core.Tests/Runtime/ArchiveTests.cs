using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Runtime;

/// <summary>
/// SCK-69..71 on the in-memory store (doc 05 §9, WP-4.5): archived streams are left out of read-model builds and of SFQL
/// <c>from events</c> unless <c>include_archived = true</c>, and stay loadable.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ArchiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "SCK-69")]
    [Trait("Covers", "doc 05 9;doc 14 5")]
    public async Task Sfql_LeavesArchivedStreamsOut_UnlessIncludeArchived()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "bank";
            sf.AddModule<AccountsModule>();
        }, store: store);
        AccountId cold = AccountId.New();
        AccountId warm = AccountId.New();
        await host.SendAsync(new OpenAccount(cold, "Ada", 10m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(warm, "Grace", 20m), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        await store.ArchiveStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(BankAccount.AggregateName, cold), Ct);
        IQueryService queries = host.Services.GetRequiredService<IQueryService>();

        QueryPage open = await queries.QueryPageAsync("from events of \"bank.account.opened\" | where time >= ago(1d) | project deposit = data.initialDeposit | order by deposit", cancellationToken: Ct);
        QueryPage all = await queries.QueryPageAsync("from events of \"bank.account.opened\" | where time >= ago(1d) | project deposit = data.initialDeposit | order by deposit options (include_archived = true)", cancellationToken: Ct);
        QueryPage sql = await queries.QueryPageAsync("SELECT COUNT(*) AS n FROM events.\"bank.account.opened\" WHERE time >= ago(1d)", options: new QueryOptions { Language = QueryLanguage.Sql }, cancellationToken: Ct);

        open.Rows.Select(r => r[0]).ShouldBe([20m]);
        all.Rows.Select(r => r[0]).ShouldBe([10m, 20m]);
        sql.Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact]
    [Trait("Id", "SCK-71")]
    [Trait("Covers", "doc 05 9")]
    public async Task ArchivedStreams_StayLoadable_AndRejectCommands()
    {
        var store = new InMemoryStore();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "bank";
            sf.AddModule<AccountsModule>();
        }, store: store);
        AccountId id = AccountId.New();
        await host.SendAsync(new OpenAccount(id, "Ada", 10m), cancellationToken: Ct);
        await host.SendAsync(new Deposit(id, 5m), cancellationToken: Ct);
        StreamId stream = StreamId.For(BankAccount.AggregateName, id);
        await store.ArchiveStreamAsync(MessageEnvelope.DefaultTenant, stream, Ct);
        IQueryService queries = host.Services.GetRequiredService<IQueryService>();
        var parameters = new Dictionary<string, object?> { ["s"] = stream.Value, ["id"] = id.Value };

        QueryPage events = await queries.QueryPageAsync("from stream @s | project version", parameters, cancellationToken: Ct);
        QueryPage folded = await queries.QueryPageAsync("from aggregate \"bank-account\" id @id | project version, balance", parameters, cancellationToken: Ct);

        events.Rows.Select(r => r[0]).ShouldBe([1L, 2L]);
        folded.Rows.ShouldHaveSingleItem().Values.ShouldBe([2L, 15m]);
        (await host.EventsAsync(stream)).Count.ShouldBe(2);
        (await Should.ThrowAsync<StreamClosedException>(async () => await host.SendAsync(new Deposit(id, 1m), cancellationToken: Ct))).Message.ShouldContain("archived");
    }

    [Fact]
    [Trait("Id", "SCK-70")]
    [Trait("Covers", "doc 05 9;doc 06 6")]
    public async Task Rebuild_LeavesArchivedStreamsOut()
    {
        var store = new InMemoryStore();
        DripId cold = DripId.New();
        DripId warm = DripId.New();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "drips";
            sf.AddModule<CoreTestModule>();
            sf.Projections(p => p.Add<DripsProjection>().Async());
            sf.Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(20));
        }, store: store);
        await host.SendAsync(new Drip(cold, "a"), cancellationToken: Ct);
        await host.SendAsync(new Drip(warm, "b"), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        (await host.DocumentAsync<DripsProjection, Drips>(cold.ToStreamSegment())).ShouldNotBeNull();

        await store.ArchiveStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(Dripper.AggregateName, cold), Ct);
        await EventuallyAsync(async () => (await store.ListAliasesAsync(Ct)).FirstOrDefault()?.Target == "test-drips@1", host);
        await store.RequestAsync("test-drips", "test-drips@1", "test-drips@1.2", Ct);
        await EventuallyAsync(async () => (await store.ListAliasesAsync(Ct)).FirstOrDefault()?.Target == "test-drips@1.2", host);

        var reader = (IDocumentReader)store;
        (await reader.GetAsync(MessageEnvelope.DefaultTenant, "test-drips@1.2", warm.ToStreamSegment(), Ct)).ShouldNotBeNull();
        (await reader.GetAsync(MessageEnvelope.DefaultTenant, "test-drips@1.2", cold.ToStreamSegment(), Ct)).ShouldBeNull("the rebuilt generation leaves the archived stream out");
        (await reader.GetAsync(MessageEnvelope.DefaultTenant, "test-drips@1", cold.ToStreamSegment(), Ct)).ShouldNotBeNull("the previous generation is untouched");
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, SourceFlowTestHost host)
    {
        for (int i = 0; i < 500; i++)
        {
            if (await condition())
            {
                return;
            }

            await host.WaitForIdleAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }

        throw new TimeoutException("The condition was not met.");
    }
}
