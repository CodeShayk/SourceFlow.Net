using Bank.Accounts;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Testing;

namespace SourceFlow.Core.Tests.Query;

/// <summary>UT-QL-41..60: the in-memory interpreter and <see cref="IQueryService"/> (doc 14 §5–§9, WP-1.13).</summary>
[Trait("Category", "Unit")]
public sealed class QueryServiceTests : IAsyncLifetime
{
    private const string Summary = "from readmodel \"account-summary\"";
    private static readonly QueryPermissions ReadModelsOnly = QueryPermissions.Of(QueryPermission.ReadModels);

    private SourceFlowTestHost _host = null!;
    private readonly List<AccountId> _accounts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IQueryService Queries => _host.Services.GetRequiredService<IQueryService>();

    public async ValueTask InitializeAsync()
    {
        _host = await StartAsync();

        // Five accounts: balances 100, 250, 400, 550, 700 (opened at 100 + i * 150 via a deposit), the last one closed.
        string[] holders = ["Ada", "Grace", "Linus", "Barbara", "Edsger"];
        for (int i = 0; i < holders.Length; i++)
        {
            var id = AccountId.New();
            _accounts.Add(id);
            (await _host.SendAsync(new OpenAccount(id, holders[i], 100m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);
            if (i > 0)
            {
                (await _host.SendAsync(new Deposit(id, 150m * i), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);
            }
        }

        (await _host.SendAsync(new Withdraw(_accounts[1], 50m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);   // 250 → 200
        (await _host.SendAsync(new Deposit(_accounts[1], 50m), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);    // 200 → 250
        (await _host.SendAsync(new CloseAccount(_accounts[4], "moved"), cancellationToken: Ct)).Status.ShouldBe(CommandStatus.Accepted);
        await _host.WaitForIdleAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    [Trait("Id", "UT-QL-41")]
    [Trait("Covers", "doc 14 5.2")]
    public async Task ReadModel_WhereProjectOrder()
    {
        QueryPage page = await Queries.QueryPageAsync(Summary + " | where balance >= 250 | project holder, balance | order by balance desc", cancellationToken: Ct);

        page.Columns.Select(c => c.Name).ShouldBe(["holder", "balance"]);
        page.Rows.Select(r => (string)r["holder"]!).ShouldBe(["Edsger", "Barbara", "Linus", "Grace"]);
        page.Rows.Select(r => (decimal)r["balance"]!).ShouldBe([700m, 550m, 400m, 250m]);
        page.Continuation.ShouldBeNull();
        page.QueryHash.ShouldStartWith("sha256:");
        page.Consistency.ShouldHaveSingleItem().ShouldBe(new QuerySourceConsistency("readmodel.account-summary", "eventual", false));
    }

    [Fact]
    [Trait("Id", "UT-QL-42")]
    [Trait("Covers", "doc 14 5.10")]
    public async Task SqlSyntax_GivesTheSameRows()
    {
        QueryPage sfql = await Queries.QueryPageAsync(Summary + " | where balance between 200 and 600 | project holder | order by holder", cancellationToken: Ct);
        QueryPage sql = await Queries.QueryPageAsync(
            "SELECT holder FROM readmodel.\"account-summary\" WHERE balance BETWEEN 200 AND 600 ORDER BY holder",
            options: new QueryOptions { Language = QueryLanguage.Sql },
            cancellationToken: Ct);

        sql.Rows.Select(r => r[0]).ShouldBe(sfql.Rows.Select(r => r[0]));
        sql.Rows.Select(r => r[0]).ShouldBe(["Barbara", "Grace", "Linus"]);
        sql.QueryHash.ShouldBe(sfql.QueryHash);
    }

    [Fact]
    [Trait("Id", "UT-QL-43")]
    [Trait("Covers", "doc 14 8")]
    public async Task PersonalData_IsRedacted_WithoutThePiiPermission()
    {
        // AccountOpened.Holder is [PersonalData]; the read model's copy is not marked, so it is the event that is redacted.
        const string query = "from events of \"bank.account.opened\" | where stream = @s | project data.holder, data.initialDeposit";
        IReadOnlyDictionary<string, object?> stream = Params(("s", Stream(_accounts[0])));

        QueryPage redacted = await Queries.QueryPageAsync(query, stream, options: new QueryOptions { Permissions = QueryPermissions.Of(QueryPermission.Events) }, cancellationToken: Ct);
        QueryPage clear = await Queries.QueryPageAsync(query, stream, options: new QueryOptions { Permissions = QueryPermissions.Of(QueryPermission.Events, QueryPermission.PersonalData) }, cancellationToken: Ct);

        redacted.Rows.ShouldHaveSingleItem().Values.ShouldBe(["[redacted]", 100m]);
        clear.Rows.ShouldHaveSingleItem().Values.ShouldBe(["Ada", 100m]);
    }

    [Fact]
    [Trait("Id", "UT-QL-44")]
    [Trait("Covers", "doc 14 8")]
    public async Task MissingPermission_IsRejected()
    {
        QueryException e = await Should.ThrowAsync<QueryException>(async () =>
            await Queries.QueryPageAsync("from events of \"bank.account.deposited\" | where stream = @s", Params(("s", "x")), options: new QueryOptions { Permissions = ReadModelsOnly }, cancellationToken: Ct));

        e.Code.ShouldBe(QueryCodes.PermissionDenied);
    }

    [Fact]
    [Trait("Id", "UT-QL-45")]
    [Trait("Covers", "doc 14 5.3")]
    public async Task Summarize_GroupsAndAggregates_IgnoringNulls()
    {
        QueryPage page = await Queries.QueryPageAsync(
            "from events of \"bank.account.deposited\", \"bank.account.withdrawn\" | where stream = @s | summarize n = count(), total = sum(data.amount), biggest = max(data.amount) by type | order by type",
            Params(("s", Stream(_accounts[1]))),
            cancellationToken: Ct);

        page.Rows.Select(r => ((string)r["type"]!, (long)r["n"]!, (decimal)r["total"]!, (decimal)r["biggest"]!)).ShouldBe(
        [
            ("bank.account.deposited", 2L, 200m, 150m),
            ("bank.account.withdrawn", 1L, 50m, 50m),
        ]);
    }

    [Fact]
    [Trait("Id", "UT-QL-46")]
    [Trait("Covers", "doc 14 5.3")]
    public async Task Summarize_WithoutKeys_GivesOneRow_EvenWhenEmpty()
    {
        QueryPage all = await Queries.QueryPageAsync(Summary + " | summarize n = count(), total = sum(balance), mean = avg(balance)", cancellationToken: Ct);
        QueryPage none = await Queries.QueryPageAsync(Summary + " | where balance > 10000 | summarize n = count(), total = sum(balance)", cancellationToken: Ct);

        all.Rows.ShouldHaveSingleItem().Values.ShouldBe([5L, 2000m, 400m]);
        none.Rows.ShouldHaveSingleItem().Values.ShouldBe([0L, null]);
    }

    [Fact]
    [Trait("Id", "UT-QL-47")]
    [Trait("Covers", "doc 14 7.4")]
    public async Task KeysetPages_AreDisjoint_Ordered_AndComplete()
    {
        var holders = new List<string>();
        string? continuation = null;
        int pages = 0;
        do
        {
            QueryPage page = await Queries.QueryPageAsync(Summary + " | project holder | order by holder", continuation: continuation, options: new QueryOptions { PageSize = 2 }, cancellationToken: Ct);
            page.Rows.Count.ShouldBeLessThanOrEqualTo(2);
            holders.AddRange(page.Rows.Select(r => (string)r[0]!));
            continuation = page.Continuation;
            pages++;
        }
        while (continuation is not null);

        pages.ShouldBe(3);
        holders.ShouldBe(["Ada", "Barbara", "Edsger", "Grace", "Linus"]);
    }

    [Fact]
    [Trait("Id", "UT-QL-48")]
    [Trait("Covers", "doc 14 7.4")]
    public async Task QueryAsync_StreamsEveryPage()
    {
        var holders = new List<string>();
        await foreach (QueryRow row in Queries.QueryAsync(Summary + " | project holder | order by holder desc", options: new QueryOptions { PageSize = 2 }, cancellationToken: Ct))
        {
            holders.Add((string)row[0]!);
        }

        holders.ShouldBe(["Linus", "Grace", "Edsger", "Barbara", "Ada"]);
    }

    [Fact]
    [Trait("Id", "UT-QL-49")]
    [Trait("Covers", "doc 14 7.4")]
    public async Task Take_IsHonouredAcrossPages()
    {
        QueryPage first = await Queries.QueryPageAsync(Summary + " | project balance | order by balance | take 3", options: new QueryOptions { PageSize = 2 }, cancellationToken: Ct);
        QueryPage second = await Queries.QueryPageAsync(Summary + " | project balance | order by balance | take 3", continuation: first.Continuation, options: new QueryOptions { PageSize = 2 }, cancellationToken: Ct);

        first.Rows.Select(r => r[0]).ShouldBe([100m, 250m]);
        second.Rows.Select(r => r[0]).ShouldBe([400m]);
        second.Continuation.ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-QL-50")]
    [Trait("Covers", "doc 14 7.4")]
    public async Task Continuation_IsRejected_ForAnotherQuery_OrWhenTampered()
    {
        QueryPage first = await Queries.QueryPageAsync(Summary + " | project holder | order by holder", options: new QueryOptions { PageSize = 2 }, cancellationToken: Ct);
        first.Continuation.ShouldNotBeNull();

        (await Should.ThrowAsync<QueryException>(async () =>
            await Queries.QueryPageAsync(Summary + " | project holder | order by holder desc", continuation: first.Continuation, cancellationToken: Ct))).Code.ShouldBe(QueryCodes.Continuation);
        (await Should.ThrowAsync<QueryException>(async () =>
            await Queries.QueryPageAsync(Summary + " | project holder | order by holder", continuation: "not-a-token", cancellationToken: Ct))).Code.ShouldBe(QueryCodes.Continuation);
    }

    [Fact]
    [Trait("Id", "QRY-62")]
    [Trait("Covers", "doc 14 7.4;doc 14 12")]
    public async Task ForgedContinuationTokens_AreRejected_NeverAnotherError()
    {
        const string Limited = Summary + " | project holder, balance | order by holder | take 3";
        var paged = new QueryOptions { PageSize = 2 };
        string token = (await Queries.QueryPageAsync(Limited, options: paged, cancellationToken: Ct)).Continuation!;

        async Task<string?> RunAsync(string forged)
        {
            try
            {
                await Queries.QueryPageAsync(Limited, continuation: forged, options: paged, cancellationToken: Ct);
                return null;
            }
            catch (QueryException e)
            {
                return e.Code;
            }
        }

        // Lifting the limit, dropping a sort key, or a key of another type than its sort item: refused (CR-36).
        (await RunAsync(Forge(token, t => t.Remove("r")))).ShouldBe(QueryCodes.Continuation);
        (await RunAsync(Forge(token, t => t["r"] = 1_000_000))).ShouldBe(QueryCodes.Continuation);
        (await RunAsync(Forge(token, t => t["k"]!.AsArray().RemoveAt(0)))).ShouldBe(QueryCodes.Continuation);
        (await RunAsync(Forge(token, t => t["k"]![0] = new System.Text.Json.Nodes.JsonArray("l", "5")))).ShouldBe(QueryCodes.Continuation);
        (await RunAsync(token)).ShouldBeNull("the real token still works");

        // Any key, of any tag and value: either a page or SFQ0601, never another exception.
        string[] tags = ["n", "l", "d", "b", "t", "u", "p", "s", "x"];
        await Gen.Select(Gen.Int[0, tags.Length - 1], Gen.String[0, 12], Gen.Int[0, 2])
            .SampleAsync(async sample =>
            {
                (int tag, string value, int position) = sample;
                string forged = Forge(token, t => t["k"]![Math.Min(position, t["k"]!.AsArray().Count - 1)] = new System.Text.Json.Nodes.JsonArray(tags[tag], value));
                string? code = await RunAsync(forged);
                (code is null or QueryCodes.Continuation).ShouldBeTrue(code);
            }, iter: 200);
    }

    private static string Forge(string token, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(token))!.AsObject();
        change(root);
        return System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    [Fact]
    [Trait("Id", "UT-QL-51")]
    [Trait("Covers", "doc 14 7.4")]
    public async Task Skip_OnReadModels()
    {
        QueryPage page = await Queries.QueryPageAsync(Summary + " | project balance | order by balance | skip 1 | take 2", cancellationToken: Ct);

        page.Rows.Select(r => r[0]).ShouldBe([250m, 400m]);
    }

    [Fact]
    [Trait("Id", "UT-QL-52")]
    [Trait("Covers", "doc 14 5.6")]
    public async Task Aggregate_AsOfVersion_FoldsThePrefix()
    {
        IReadOnlyDictionary<string, object?> id = Params(("id", _accounts[1].Value));

        QueryPage latest = await Queries.QueryPageAsync("from aggregate \"bank-account\" id @id | project version, balance", id, cancellationToken: Ct);
        QueryPage atTwo = await Queries.QueryPageAsync("from aggregate \"bank-account\" id @id as of version 2 | project version, balance", id, cancellationToken: Ct);
        QueryPage atThree = await Queries.QueryPageAsync("from aggregate \"bank-account\" id @id as of version 3 | project version, balance", id, cancellationToken: Ct);

        latest.Rows.ShouldHaveSingleItem().Values.ShouldBe([4L, 250m]);
        atTwo.Rows.ShouldHaveSingleItem().Values.ShouldBe([2L, 250m]);
        atThree.Rows.ShouldHaveSingleItem().Values.ShouldBe([3L, 200m]);
        latest.Consistency.ShouldHaveSingleItem().Level.ShouldBe("strong");
    }

    [Fact]
    [Trait("Id", "UT-QL-53")]
    [Trait("Covers", "doc 14 5.7")]
    public async Task Schema_ListsFieldsByVersion()
    {
        QueryPage page = await Queries.QueryPageAsync("from schema of \"bank.account.opened\" | project field, fieldType, personalData | order by field", cancellationToken: Ct);

        page.Rows.Select(r => ((string)r[0]!, (string)r[1]!, (bool)r[2]!)).ShouldBe(
        [
            ("accountId", "string", false),   // strongly typed ids are JSON strings
            ("holder", "string", true),
            ("initialDeposit", "decimal", false),
            ("openedAt", "timestamp", false),
        ]);
    }

    [Fact]
    [Trait("Id", "UT-QL-54")]
    [Trait("Covers", "doc 14 5.4")]
    public async Task Join_ReadModelToEvents_OnAggregateKey()
    {
        QueryPage page = await Queries.QueryPageAsync(
            Summary + " as a | join events of \"bank.account.withdrawn\" as e on e.aggregatekey = a.id | project a.holder, e.data.amount",
            cancellationToken: Ct);

        page.Rows.ShouldHaveSingleItem().Values.ShouldBe(["Grace", 50m]);
    }

    [Fact]
    [Trait("Id", "UT-QL-55")]
    [Trait("Covers", "doc 14 5.4")]
    public async Task LeftJoin_KeepsUnmatchedRows_WithNulls()
    {
        QueryPage page = await Queries.QueryPageAsync(
            Summary + " as a | join kind=left events of \"bank.account.closed\" as e on e.aggregatekey = a.id | project a.holder, e.data.reason | order by a.holder",
            cancellationToken: Ct);

        page.Rows.Select(r => ((string)r[0]!, r[1])).ShouldBe(
        [
            ("Ada", null),
            ("Barbara", null),
            ("Edsger", "moved"),
            ("Grace", null),
            ("Linus", null),
        ]);
    }

    [Fact]
    [Trait("Id", "UT-QL-56")]
    [Trait("Covers", "doc 14 5.5")]
    public async Task InSubquery_AndExists()
    {
        var sql = new QueryOptions { Language = QueryLanguage.Sql };
        QueryPage inQuery = await Queries.QueryPageAsync(
            // The subquery's events source is under the cost guard like any other (WP-C.1): typed and time-bounded.
            "SELECT holder FROM readmodel.\"account-summary\" WHERE id IN (SELECT aggregatekey FROM events.\"bank.account.withdrawn\" WHERE time >= ago(7d))",
            options: sql,
            cancellationToken: Ct);
        QueryPage notExists = await Queries.QueryPageAsync(
            "SELECT a.holder FROM readmodel.\"account-summary\" a WHERE NOT EXISTS (SELECT 1 FROM events.\"bank.account.deposited\" e WHERE e.aggregatekey = a.id)",
            options: sql,
            cancellationToken: Ct);

        inQuery.Rows.ShouldHaveSingleItem()[0].ShouldBe("Grace");
        notExists.Rows.ShouldHaveSingleItem()[0].ShouldBe("Ada");
    }

    [Fact]
    [Trait("Id", "UT-QL-57")]
    [Trait("Covers", "doc 14 8")]
    public async Task Tenancy_IsolatesRows()
    {
        QueryPage other = await Queries.QueryPageAsync(Summary, options: new QueryOptions { Tenant = "another-tenant" }, cancellationToken: Ct);
        QueryPage events = await Queries.QueryPageAsync(
            "from events of \"bank.account.opened\" | summarize n = count() options (allow_scan = true)",
            options: new QueryOptions { Tenant = "another-tenant" },
            cancellationToken: Ct);

        other.Rows.ShouldBeEmpty();
        events.Rows.ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Fact]
    [Trait("Id", "UT-QL-58")]
    [Trait("Covers", "doc 14 5.5")]
    public async Task ThreeValuedLogic_MissingAndNullAreNeverEqual()
    {
        // A null compared with anything is unknown (the row is dropped); coalesce picks the first non-null value.
        QueryPage equal = await Queries.QueryPageAsync("from events of \"bank.account.opened\" | where data.initialDeposit = 100 and stream = @s | summarize n = count()", Params(("s", Stream(_accounts[0]))), cancellationToken: Ct);
        QueryPage coalesced = await Queries.QueryPageAsync(Summary + " | where coalesce(null, balance) = 100 | summarize n = count()", cancellationToken: Ct);

        equal.Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        coalesced.Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact]
    [Trait("Id", "UT-QL-59")]
    [Trait("Covers", "doc 14 6")]
    public async Task AtLeast_WaitsForTheProjection_ThenTimesOutOrReturnsStale()
    {
        // Another node holds the projection's only lease before this host starts, so the read model can't catch up.
        var store = new SourceFlow.Storage.InMemory.InMemoryStore();
        LeaseToken? held = await store.TryAcquireAsync("sub:projection:account-summary@1:0", "someone-else", TimeSpan.FromMinutes(5), Ct);
        held.ShouldNotBeNull();
        await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "bank";
            sf.AddModule<AccountsModule>();
        }, store: store);
        host.Runtime.AsyncProjections.ShouldHaveSingleItem().Collection.ShouldBe("account-summary@1");
        var id = AccountId.New();
        CommandResult result = await host.SendAsync(new OpenAccount(id, "Ada", 100m), cancellationToken: Ct);
        IQueryService queries = host.Services.GetRequiredService<IQueryService>();
        IReadOnlyDictionary<string, object?> token = Params(("token", result.Token.ToString()));

        QueryException timeout = await Should.ThrowAsync<QueryException>(async () =>
            await queries.QueryPageAsync(Summary + " options (consistency = at_least(@token), timeout = 100ms)", token, cancellationToken: Ct));
        QueryPage stale = await queries.QueryPageAsync(Summary + " options (consistency = at_least(@token), timeout = 100ms, on_timeout = 'stale')", token, cancellationToken: Ct);

        timeout.Code.ShouldBe(QueryCodes.ConsistencyTimeout);
        stale.Rows.ShouldBeEmpty();
        stale.Consistency.ShouldHaveSingleItem().ShouldBe(new QuerySourceConsistency("readmodel.account-summary", "at_least", true));

        await host.Store.ReleaseAsync(held, Ct);
        QueryPage fresh = await queries.QueryPageAsync(Summary + " | project holder options (consistency = at_least(@token), timeout = 10s)", token, cancellationToken: Ct);
        fresh.Rows.ShouldHaveSingleItem()[0].ShouldBe("Ada");
        fresh.Consistency[0].Stale.ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-QL-60")]
    [Trait("Covers", "doc 14 9.5")]
    public async Task Validate_ReportsDiagnostics_AndParametersAreTyped()
    {
        QueryValidation ok = await Queries.ValidateAsync(Summary + " | where balance > @min | project holder", cancellationToken: Ct);
        QueryValidation bad = await Queries.ValidateAsync("DELETE FROM x", options: new QueryOptions { Language = QueryLanguage.Sql }, cancellationToken: Ct);

        ok.IsValid.ShouldBeTrue();
        ok.Columns.ShouldHaveSingleItem().ShouldBe(new QueryColumn("holder", QueryType.String));
        bad.IsValid.ShouldBeFalse();
        bad.Diagnostics[0].Code.ShouldBe(QueryCodes.Unsupported);

        (await Should.ThrowAsync<QueryException>(async () =>
            await Queries.QueryPageAsync(Summary + " | where balance > @min", Params(("min", "lots")), cancellationToken: Ct))).Code.ShouldBe(QueryCodes.TypeMismatch);
        (await Should.ThrowAsync<QueryException>(async () =>
            await Queries.QueryPageAsync(Summary + " | where balance > @min", cancellationToken: Ct))).Code.ShouldBe(QueryCodes.UnknownName);
        (await Queries.QueryPageAsync(Summary + " | where balance > @min", Params(("min", 500)), cancellationToken: Ct)).Rows.Count.ShouldBe(2);
        (await Queries.QueryPageAsync(Summary + " | where balance > @min", Params(("min", "500.5")), cancellationToken: Ct)).Rows.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Id", "UT-QL-66")]
    [Trait("Covers", "doc 14 5.2")]
    public async Task DefaultEventColumns_IncludeData_AndEnumsAreTypedByTheirJson()
    {
        QueryPage events = await Queries.QueryPageAsync("from events of \"bank.account.opened\" | where stream = @s", Params(("s", Stream(_accounts[0]))), cancellationToken: Ct);
        QueryPage summary = await Queries.QueryPageAsync(Summary + " | where status = 2 | project holder, status", cancellationToken: Ct);

        events.Columns.Select(c => c.Name).ShouldBe(["position", "stream", "version", "type", "typeversion", "time", "data"]);
        System.Text.Json.JsonElement data = events.Rows.ShouldHaveSingleItem()["data"].ShouldBeOfType<System.Text.Json.JsonElement>();
        data.GetProperty("holder").GetString().ShouldBe("Ada");
        summary.Columns[1].Type.ShouldBe(QueryType.Integer);   // AccountStatus is serialized as a number
        summary.Rows.ShouldHaveSingleItem().Values.ShouldBe(["Edsger", 2L]);
    }

    private static Task<SourceFlowTestHost> StartAsync()
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "bank";
            sf.AddModule<AccountsModule>();
        });
    }

    private static string Stream(AccountId id)
    {
        return StreamId.For(BankAccount.AggregateName, id).Value;
    }

    private static Dictionary<string, object?> Params(params (string Name, object? Value)[] values)
    {
        return values.ToDictionary(v => v.Name, v => v.Value);
    }
}
