using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Query;

/// <summary>
/// QRY-64 (WP-C.12, CR-21): with a SQL engine registered, only what no SQL engine can run (aggregate folds, schema rows)
/// goes to the interpreter, time-boxed to the engine's QueryTimeout and capped in rows; an event or read-model query the
/// engine can't compile is refused (SFQ0410).
/// </summary>
[Trait("Category", "Unit")]
public sealed class QueryFallbackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "QRY-64")]
    [Trait("Covers", "doc 14 7.1;doc 14 7.3")]
    public async Task AnInterpretedQuery_TimesOutAtTheEnginesQueryTimeout()
    {
        var store = new InMemoryStore(TimeProvider.System);
        await using SourceFlowTestHost host = await StartAsync(store, slowStreams: true);
        var id = AccountId.New();
        (await host.SendAsync(new OpenAccount(id, "Ada", 10m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);
        IQueryService queries = host.Services.GetRequiredService<IQueryService>();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Should.ThrowAsync<TimeoutException>(() => queries.QueryPageAsync(
            "from aggregate \"bank-account\" id @id | project version", new Dictionary<string, object?> { ["id"] = id.Value }, cancellationToken: Ct).AsTask());
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "the fold is cut off at the engine's 200 ms timeout, not after the slow read");
    }

    [Fact]
    [Trait("Id", "QRY-64")]
    public async Task AnEventOrReadModelQueryTheEngineCantCompile_IsRefused_AndTheInterpreterIsCapped()
    {
        var store = new InMemoryStore(TimeProvider.System);
        await using (SourceFlowTestHost host = await StartAsync(store, slowStreams: false))
        {
            QueryException refused = await Should.ThrowAsync<QueryException>(() => host.Services.GetRequiredService<IQueryService>()
                .QueryPageAsync("from readmodel \"account-summary\" | project holder", cancellationToken: Ct).AsTask());
            refused.Code.ShouldBe(QueryCodes.NotCompilable);
        }

        // Without an engine the interpreter runs everything, but reads at most MaxInterpretedRows source rows.
        await using SourceFlowTestHost capped = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "bank";
            sf.AddModule<AccountsModule>();
        }, services: s => s.AddSingleton(QueryPolicySettings.Default with { MaxInterpretedRows = 2 }), cancellationToken: Ct);
        for (int i = 0; i < 3; i++)
        {
            (await capped.SendAsync(new OpenAccount(AccountId.New(), "H" + i, 1m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);
        }

        await capped.WaitForIdleAsync();
        QueryException tooMany = await Should.ThrowAsync<QueryException>(() => capped.Services.GetRequiredService<IQueryService>()
            .QueryPageAsync("from readmodel \"account-summary\" | project holder", cancellationToken: Ct).AsTask());
        tooMany.Code.ShouldBe(QueryCodes.CostCeiling);
    }

    private static Task<SourceFlowTestHost> StartAsync(InMemoryStore store, bool slowStreams)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "bank";
            sf.AddModule<AccountsModule>();
            sf.UseStorage(_ => new StoragePorts(slowStreams ? new SlowStreams(store) : store, store, store, store, store, store, store, store, store, store)
            {
                Retention = store, Nodes = store, Schemas = store, DocumentAdmin = store, Aliases = store, Monitor = store, PersonalData = store,
            });
        }, store: store, services: s => s.AddSingleton<IQueryEngine>(new DecliningEngine()), cancellationToken: Ct);
    }

    // A SQL engine stand-in that compiles nothing and has a 200 ms statement timeout.
    private sealed class DecliningEngine : IQueryEngine
    {
        public TimeSpan? QueryTimeout => TimeSpan.FromMilliseconds(200);

        public bool CanExecute(BoundQuery query) => false;

        public IAsyncEnumerable<QueryResultRow> ExecuteAsync(QueryExecution execution, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    // Stream reads that take 30 seconds unless cancelled.
    private sealed class SlowStreams(InMemoryStore store) : IEventStore
    {
        public async ValueTask<StreamSlice> ReadStreamAsync(string tenantId, StreamId stream, StreamVersion fromInclusive, int maxCount, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), TimeProvider.System, cancellationToken);
            return await store.ReadStreamAsync(tenantId, stream, fromInclusive, maxCount, cancellationToken);
        }

        public ValueTask<StreamInfo?> GetStreamInfoAsync(string tenantId, StreamId stream, CancellationToken cancellationToken) => store.GetStreamInfoAsync(tenantId, stream, cancellationToken);

        public ValueTask ArchiveStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken) => store.ArchiveStreamAsync(tenantId, stream, cancellationToken);

        public ValueTask DeleteStreamAsync(string tenantId, StreamId stream, CancellationToken cancellationToken) => store.DeleteStreamAsync(tenantId, stream, cancellationToken);
    }
}
