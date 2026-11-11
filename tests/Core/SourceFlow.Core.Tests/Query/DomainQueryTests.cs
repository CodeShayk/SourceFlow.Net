using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Query;

// A hand-written domain query runtime (no [DomainQuery], so the generator ignores it) whose text doesn't bind.
public sealed record Broken(decimal Below) : IDomainQueryRuntime<Broken, BrokenRow>
{
    public static string QueryName => "tests.broken";

    public static int QueryVersion => 1;

    public static string QueryText => "from readmodel \"no-such-model\" | where balance < @below | project balance";

    public static bool IsSql => false;

    public static IReadOnlyList<DomainQueryParameter> QueryParameters { get; } = [new("below", typeof(decimal))];

    public static IReadOnlyList<string> ResultColumns { get; } = ["Balance"];

    public IReadOnlyDictionary<string, object?> ToParameters()
    {
        return new Dictionary<string, object?> { ["below"] = Below };
    }

    public static BrokenRow Materialize(IQueryRow row)
    {
        return new BrokenRow(DomainQueryValues.Get<decimal>(DomainQueryValues.Column(row, "Balance")));
    }
}

public sealed record BrokenRow(decimal Balance);

// Binds, but the result asks for a column the query doesn't project.
public sealed record Mismatched(decimal Below) : IDomainQueryRuntime<Mismatched, MismatchedRow>
{
    public static string QueryName => "tests.mismatched";

    public static int QueryVersion => 1;

    public static string QueryText => "from readmodel \"account-summary\" | where balance < @below | project balance";

    public static bool IsSql => false;

    public static IReadOnlyList<DomainQueryParameter> QueryParameters { get; } = [new("below", typeof(decimal))];

    public static IReadOnlyList<string> ResultColumns { get; } = ["Balance", "Segment"];

    public IReadOnlyDictionary<string, object?> ToParameters()
    {
        return new Dictionary<string, object?> { ["below"] = Below };
    }

    public static MismatchedRow Materialize(IQueryRow row)
    {
        return new MismatchedRow(0m, string.Empty);
    }
}

public sealed record MismatchedRow(decimal Balance, string Segment);

public sealed class BrokenQueriesModule : ISourceFlowModule
{
    public static string ModuleName => "BrokenQueriesModule";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        builder.AddDomainQuery<Broken, BrokenRow>();
        builder.AddDomainQuery<Mismatched, MismatchedRow>();
    }
}

/// <summary>UT-QL-90..93: named domain queries (doc 14 Q-B4, SF0018 at startup).</summary>
[Trait("Category", "Unit")]
public sealed class DomainQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-QL-90")]
    [Trait("Covers", "Q-B4")]
    public async Task DomainQuery_RunsInProcess_AndMaterializesTypedRows()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
        });
        var low = AccountId.New();
        var high = AccountId.New();
        await host.SendAsync(new OpenAccount(low, "Ada", 20m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(high, "Grace", 900m), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        IQueryService queries = host.Services.GetRequiredService<IQueryService>();

        IReadOnlyList<LowBalanceAccount> rows = await queries.ExecuteAsync(new LowBalanceAccounts(Below: 100m), cancellationToken: Ct);

        rows.ShouldBe([new LowBalanceAccount(low, "Ada", 20m)]);
    }

    [Fact]
    [Trait("Id", "UT-QL-91")]
    [Trait("Covers", "Q-B4")]
    public void DomainQuery_IsRegisteredWithItsContract()
    {
        RuntimeModel model = new SourceFlowModelBuilder().AddModule<AccountsModule>().Build();

        DomainQueryDescriptor query = model.DomainQueries.ShouldHaveSingleItem();
        query.Name.ShouldBe("accounts.low-balance");
        query.Version.ShouldBe(1);
        query.Language.ShouldBe(QueryLanguage.Sfql);
        query.Parameters.ShouldBe([new DomainQueryParameter("below", typeof(decimal))]);
        query.ResultColumns.ShouldBe(["AccountId", "Holder", "Balance"]);
        query.ResultType.ShouldBe(typeof(LowBalanceAccount));
    }

    [Fact]
    [Trait("Id", "UT-QL-92")]
    [Trait("Covers", "Q-B4")]
    public async Task DomainQueries_ThatDontBindOrDontMatchTheirResult_FailStartup()
    {
        DomainQueryException e = await Should.ThrowAsync<DomainQueryException>(async () =>
            await SourceFlowTestHost.StartAsync(sf =>
            {
                sf.ServiceName = "accounts";
                sf.AddModule<AccountsModule>();
                sf.AddModule<BrokenQueriesModule>();
            }));

        e.Message.ShouldContain("SF0018");
        e.Message.ShouldContain("tests.broken v1: SFQ0202");
        e.Message.ShouldContain("tests.mismatched v1: The result member 'Segment'");
        e.Message.ShouldNotContain("accounts.low-balance");
    }

    [Fact]
    [Trait("Id", "UT-QL-93")]
    [Trait("Covers", "Q-B4")]
    public async Task UnregisteredDomainQuery_IsRejected()
    {
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
        });

        await Should.ThrowAsync<DomainQueryException>(async () =>
            await host.Services.GetRequiredService<IQueryService>().ExecuteAsync(new Broken(1m), cancellationToken: Ct));
    }

    [Fact]
    [Trait("Id", "UT-QL-94")]
    [Trait("Covers", "doc 14 9.5")]
    public async Task TypedBuilder_OverEvents_TranslatesToParameterisedSfql()
    {
        await using SourceFlowTestHost host = await StartAsync();
        var a = AccountId.New();
        var b = AccountId.New();
        await host.SendAsync(new OpenAccount(a, "Ada", 10m), cancellationToken: Ct);
        await host.SendAsync(new OpenAccount(b, "Grace", 10m), cancellationToken: Ct);
        foreach (decimal amount in new[] { 50m, 500m, 250m })
        {
            await host.SendAsync(new Deposit(a, amount), cancellationToken: Ct);
        }

        await host.SendAsync(new Deposit(b, 300m), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        decimal minimum = 100m;

        EventQuery<MoneyDeposited> query = host.Services.GetRequiredService<IQueryService>().Events<MoneyDeposited>()
            .Since(TimeSpan.FromDays(1));
        query.Where(e => e.Amount >= minimum && e.AccountId == a).OrderByDescending(e => e.Amount).Take(5);
        IReadOnlyList<EventRecord<MoneyDeposited>> rows = await query.ToListAsync(cancellationToken: Ct);

        query.ToSfql().ShouldBe("from events of \"bank.account.deposited\" version 1 | where time >= ago(@p0) | where (data.amount >= @p1 and data.accountId = @p2) | project position, stream, version, time, data | order by data.amount desc | take @p3");
        query.ToSfql().ShouldNotContain("100");
        rows.Select(r => r.Data.Amount).ShouldBe([500m, 250m]);
        rows.ShouldAllBe(r => r.Stream == StreamId.For(BankAccount.AggregateName, a).Value);
    }

    [Fact]
    [Trait("Id", "UT-QL-95")]
    [Trait("Covers", "doc 14 9.5")]
    public async Task TypedBuilder_OverAReadModel_ReturnsTypedDocuments()
    {
        await using SourceFlowTestHost host = await StartAsync();
        foreach ((string holder, decimal balance) in new[] { ("Ada", 20m), ("Alan", 900m), ("Barbara", 30m), ("Annie", 40m) })
        {
            await host.SendAsync(new OpenAccount(AccountId.New(), holder, balance), cancellationToken: Ct);
        }

        await host.WaitForIdleAsync();

        IReadOnlyList<AccountSummary> rows = await host.Services.GetRequiredService<IQueryService>().ReadModel<AccountSummary>()
            .Where(s => s.Balance < 100m && s.Holder.StartsWith("A") && s.Status == AccountStatus.Open)
            .OrderBy(s => s.Holder)
            .ToListAsync(cancellationToken: Ct);

        rows.Select(r => (r.Holder, r.Balance, r.Status)).ShouldBe([("Ada", 20m, AccountStatus.Open), ("Annie", 40m, AccountStatus.Open)]);
        rows.ShouldAllBe(r => !r.Id.IsDefault);
    }

    private static Task<SourceFlowTestHost> StartAsync()
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
        });
    }
}
