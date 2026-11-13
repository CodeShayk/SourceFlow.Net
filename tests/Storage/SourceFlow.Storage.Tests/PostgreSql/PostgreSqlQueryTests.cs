using System.Globalization;
using Npgsql;
using System.Text;
using System.Text.Json;
using Bank.Accounts;
using CsCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Runtime;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.PostgreSQL.Query;

namespace SourceFlow.Storage.Tests.PostgreSql;

/// <summary>
/// QRY-01..20, 40..60: the SFQL PostgreSQL compiler (doc 14 §7, §10, §12; WP-2.8). Every query runs twice over the same
/// PostgreSQL data, compiled to SQL and in the in-memory interpreter, and the rows must be identical (cross-provider
/// equivalence). Golden SQL, the injection corpus and the no-literal property cover ADR-026.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Provider", "PostgreSQL")]
[Trait("Requires", "Docker")]
public sealed class PostgreSqlQueryTests(PostgreSqlServer server) : PostgreSqlTest(server)
{
    private static readonly RuntimeModel Model = new SourceFlowModelBuilder().AddModule<AccountsModule>().Build();
    private static readonly QueryCatalog Catalog = QueryCatalog.Build(Model);

    private IHost _host = null!;
    private string _schema = null!;
    private ServiceProvider _reference = null!;
    private readonly List<AccountId> _accounts = [];
    private readonly List<CommandResult> _results = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IQueryService Compiled => _host.Services.GetRequiredService<IQueryService>();

    private IQueryService Interpreted => _reference.GetRequiredService<IQueryService>();

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        string schema = _schema = PostgreSqlServer.NewSchema("q");
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development, DisableDefaults = true });
        builder.Services.AddSourceFlow(sf => Configure(sf, schema));
        _host = builder.Build();
        await _host.StartAsync(Ct);

        // The reference: the same schema through the interpreter (no provider engine).
        var services = new ServiceCollection();
        services.AddSourceFlow(sf => Configure(sf, schema));
        services.RemoveAll<IQueryEngine>();
        _reference = services.BuildServiceProvider();

        ICommandSender commands = _host.Services.GetRequiredService<ICommandSender>();
        string[] holders = ["Ada", "Grace", "Linus", "Barbara", "Edsger", "adam", "Zed"];
        for (int i = 0; i < holders.Length; i++)
        {
            var id = AccountId.New();
            _accounts.Add(id);
            _results.Add(await commands.SendAsync(new OpenAccount(id, holders[i], 100m + i), null, Ct));
            if (i is > 0 and < 5)
            {
                _results.Add(await commands.SendAsync(new Deposit(id, 150m * i + 0.25m), null, Ct));
            }
        }

        _results.Add(await commands.SendAsync(new Withdraw(_accounts[1], 50m), null, Ct));
        _results.Add(await commands.SendAsync(new Deposit(_accounts[1], 50m), null, Ct));
        _results.Add(await commands.SendAsync(new CloseAccount(_accounts[4], "moved"), null, Ct));
        _results.ShouldAllBe(r => r.Status == CommandStatus.Accepted);
    }

    public override async ValueTask DisposeAsync()
    {
        await _reference.DisposeAsync();
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
        await base.DisposeAsync();
    }

    public static TheoryData<string, string> EquivalentQueries => new()
    {
        { "QRY-01", "from readmodel \"account-summary\" | where balance >= 250 | project holder, balance | order by balance desc" },
        { "QRY-02", "from readmodel \"account-summary\" | summarize n = count(), total = sum(balance), smallest = min(balance), biggest = max(balance)" },
        { "QRY-03", "from readmodel \"account-summary\" | project holder | order by holder" },
        { "QRY-04", "from readmodel \"account-summary\" | where holder like 'A%' or holder in ('Grace', 'Zed') | project holder | order by holder desc" },
        { "QRY-05", "from readmodel \"account-summary\" | where balance between 200 and 600 and not (holder = 'Linus') | project holder, status, version" },
        { "QRY-06", "from readmodel \"account-summary\" | project holder, doubled = balance * 2, third = round(balance / 3, 4), up = upper(holder), n = length(holder), tag = concat(holder, '-', status)" },
        { "QRY-07", "from readmodel \"account-summary\" | summarize n = count(), total = sum(balance) by status | order by status" },
        { "QRY-08", "from readmodel \"account-summary\" | summarize n = count() by status | where n > 1" },
        { "QRY-09", "from events of \"bank.account.deposited\", \"bank.account.withdrawn\" | where time >= ago(1d) | summarize n = count(), total = sum(data.amount), biggest = max(data.amount) by type | order by type" },
        { "QRY-10", "from events of \"bank.account.opened\" | where time >= ago(1d) | project stream, version, typeversion, holder = data.holder, deposit = data.initialDeposit" },
        { "QRY-11", "from events of \"bank.account.deposited\" | where time >= ago(1d) | project position, aggregatekey, amount = data.amount, after = data.balanceAfter | order by amount desc" },
        { "QRY-12", "from events of \"bank.account.opened\", \"bank.account.deposited\" | where time >= ago(1d) | summarize n = count(), missing = count(data.initialDeposit) by type" },
        { "QRY-13", "from events of \"bank.account.opened\" | where time >= ago(1d) | summarize n = count() by day = bin(time, 1d)" },
        { "QRY-14", "from readmodel \"account-summary\" as a | join events of \"bank.account.withdrawn\" as e on e.aggregatekey = a.id | project a.holder, e.data.amount" },
        { "QRY-15", "from readmodel \"account-summary\" as a | join kind=left events of \"bank.account.closed\" as e on e.aggregatekey = a.id | project a.holder, reason = e.data.reason | order by a.holder" },
        { "QRY-16", "from readmodel \"account-summary\" | where coalesce(null, balance) > 300 and holder is not null | project holder, b = coalesce(balance, 0)" },
        { "QRY-17", "from events of \"bank.account.deposited\" | where time >= ago(1d) and data.amount > 300 | project amount = data.amount, big = case when data.amount > 500 then 'big' else 'small' end" },
        { "QRY-18", "from events of \"bank.account.opened\" | where time >= ago(1d) and has_tag('vip') | project stream" },
    };

    [Theory]
    [Trait("Covers", "doc 14 12")]
    [MemberData(nameof(EquivalentQueries))]
    public async Task CompiledAndInterpreted_GiveTheSameRows(string id, string query)
    {
        _ = id;
        IReadOnlyList<string> compiled = await RowsAsync(Compiled, query);
        IReadOnlyList<string> interpreted = await RowsAsync(Interpreted, query);

        compiled.ShouldBe(interpreted, $"{id}: {query}");
    }

    [Fact]
    [Trait("Id", "QRY-19")]
    [Trait("Covers", "doc 14 12")]
    public async Task SqlSyntax_Subqueries_AreEquivalent()
    {
        var sql = new QueryOptions { Language = QueryLanguage.Sql };
        string[] queries =
        [
            "SELECT holder FROM readmodel.\"account-summary\" WHERE id IN (SELECT aggregatekey FROM events.\"bank.account.withdrawn\") ORDER BY holder",
            "SELECT a.holder FROM readmodel.\"account-summary\" a WHERE NOT EXISTS (SELECT 1 FROM events.\"bank.account.deposited\" e WHERE e.aggregatekey = a.id) ORDER BY a.holder",
            "SELECT DISTINCT status FROM readmodel.\"account-summary\" ORDER BY status",
            "SELECT status, COUNT(*) AS n, SUM(balance) AS total FROM readmodel.\"account-summary\" GROUP BY status HAVING SUM(balance) > 100 ORDER BY total DESC",
        ];

        foreach (string query in queries)
        {
            IReadOnlyList<string> compiled = await RowsAsync(Compiled, query, options: sql);
            compiled.ShouldBe(await RowsAsync(Interpreted, query, options: sql), query);
            compiled.ShouldNotBeEmpty(query);
        }

        (await RowsAsync(Compiled, queries[0], options: sql)).ShouldBe(["Grace"]);
        (await RowsAsync(Compiled, queries[1], options: sql)).ShouldBe(["Ada", "Zed", "adam"]);   // ordinal: upper case first
    }

    [Fact]
    [Trait("Id", "QRY-20")]
    [Trait("Covers", "doc 14 7.4")]
    public async Task KeysetPages_AreInterchangeableBetweenEngines()
    {
        const string query = "from events of \"bank.account.opened\", \"bank.account.deposited\" | where time >= ago(1d) | project stream, type, version";
        var options = new QueryOptions { PageSize = 3 };
        var mixed = new List<string>();
        string? continuation = null;
        int page = 0;
        do
        {
            // Alternate engines page by page: the continuation token is engine-neutral.
            QueryPage result = await (page++ % 2 == 0 ? Compiled : Interpreted).QueryPageAsync(query, continuation: continuation, options: options, cancellationToken: Ct);
            mixed.AddRange(result.Rows.Select(Render));
            continuation = result.Continuation;
        }
        while (continuation is not null);

        mixed.Count.ShouldBe(12);   // 7 opened + 5 deposited
        mixed.ShouldBe(await RowsAsync(Compiled, query, options: new QueryOptions { PageSize = 100 }));
    }

    [Fact]
    [Trait("Id", "QRY-40")]
    [Trait("Covers", "doc 14 12")]
    public async Task PositionAndStream_Predicates()
    {
        GlobalPosition middle = _results[5].Token.Position;
        IReadOnlyDictionary<string, object?> parameters = new Dictionary<string, object?> { ["p"] = middle.ToString() };
        const string query = "from events of \"bank.account.deposited\" | where position > @p and time >= ago(1d) | project position, amount = data.amount";

        IReadOnlyList<string> compiled = await RowsAsync(Compiled, query, parameters);
        compiled.ShouldBe(await RowsAsync(Interpreted, query, parameters));
        compiled.ShouldNotBeEmpty();

        IReadOnlyDictionary<string, object?> stream = new Dictionary<string, object?> { ["s"] = StreamId.For(BankAccount.AggregateName, _accounts[1]).Value };
        const string streamQuery = "from stream @s | project version, type";
        (await RowsAsync(Compiled, streamQuery, stream)).ShouldBe(await RowsAsync(Interpreted, streamQuery, stream));
        (await RowsAsync(Compiled, streamQuery, stream)).Count.ShouldBe(4);
    }

    [Fact]
    [Trait("Id", "SCK-69")]
    [Trait("Covers", "doc 05 9;doc 14 12")]
    public async Task ArchivedStreams_AreLeftOut_UnlessIncludeArchived_InBothEngines()
    {
        await _host.Services.GetRequiredService<PostgreSqlStore>().ArchiveStreamAsync(MessageEnvelope.DefaultTenant, StreamId.For(BankAccount.AggregateName, _accounts[2]), Ct);
        string[] queries =
        [
            "from events of \"bank.account.opened\", \"bank.account.deposited\" | where time >= ago(1d) | summarize n = count() by type | order by type",
            "from events of \"bank.account.opened\", \"bank.account.deposited\" | where time >= ago(1d) | summarize n = count() by type | order by type options (include_archived = true)",
            "from readmodel \"account-summary\" as a | join events of \"bank.account.deposited\" as e on e.aggregatekey = a.id | project a.holder, e.data.amount | order by holder",
        ];

        foreach (string query in queries)
        {
            (await RowsAsync(Compiled, query)).ShouldBe(await RowsAsync(Interpreted, query), query);
        }

        (await RowsAsync(Compiled, queries[0])).ShouldBe(["bank.account.deposited | 4", "bank.account.opened | 6"]);
        (await RowsAsync(Compiled, queries[1])).ShouldBe(["bank.account.deposited | 5", "bank.account.opened | 7"]);
        (await RowsAsync(Compiled, queries[2])).ShouldNotContain(r => r.StartsWith("Linus", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Id", "QRY-41")]
    [Trait("Covers", "doc 14 8")]
    public async Task Tenancy_And_ReadOnly()
    {
        var other = new QueryOptions { Tenant = "someone-else" };
        (await RowsAsync(Compiled, "from readmodel \"account-summary\"", options: other)).ShouldBeEmpty();
        (await RowsAsync(Compiled, "from events of \"bank.account.opened\" | where time >= ago(1d)", options: other)).ShouldBeEmpty();
        (await RowsAsync(Compiled, "from events of \"bank.account.opened\" | where time >= ago(1d) | summarize n = count() options (tenants = 'all')")).ShouldBe(["7"]);
    }

    [Fact]
    [Trait("Id", "QRY-42")]
    [Trait("Covers", "ADR-026")]
    public void GoldenSql_ReadModel()
    {
        CompiledQuery compiled = Compile(
            "from readmodel \"account-summary\" | where status = 1 and balance < @below | project id, balance | order by balance | take 50",
            new Dictionary<string, object?> { ["below"] = 0m });

        compiled.Sql.ShouldBe(
            "SELECT * FROM (SELECT t0.id AS o0, (t0.data->>'balance')::numeric AS o1, (t0.data->>'balance')::numeric AS k0, t0.id AS k1 " +
            "FROM sf.documents t0 WHERE t0.tenant_id = $1::text AND t0.collection = $2::text " +
            "AND ((((t0.data->>'status')::bigint) = ($3::bigint)) AND (((t0.data->>'balance')::numeric) < ($4::numeric)))) q " +
            "ORDER BY q.k0 ASC NULLS LAST, q.k1 COLLATE \"C\" ASC NULLS LAST OFFSET $5::bigint LIMIT $6::bigint");
        compiled.Parameters.Select(p => p.Value).ShouldBe(["-", "account-summary@1", 1L, 0m, 0L, 51L]);
    }

    [Fact]
    [Trait("Id", "QRY-43")]
    [Trait("Covers", "ADR-026")]
    public void GoldenSql_EventsWithHorizonAndTieBreak()
    {
        CompiledQuery compiled = Compile("from events of \"bank.account.deposited\" | where data.amount >= @min and time >= ago(7d) | project stream, amount = data.amount | order by time desc",
            new Dictionary<string, object?> { ["min"] = 10m });

        compiled.Sql.ShouldContain("t0.event_type IN ($2::text)");
        compiled.Sql.ShouldContain("t0.tx_id < pg_snapshot_xmin(pg_current_snapshot())");
        compiled.Sql.ShouldContain("(t0.created_at) >= (($4::timestamptz - ($5::interval)))");
        compiled.Sql.ShouldContain("ORDER BY q.k0 DESC NULLS FIRST, q.k1_c ASC NULLS LAST, q.k1_s ASC NULLS LAST");
    }

    [Fact]
    [Trait("Id", "QRY-44")]
    [Trait("Covers", "ADR-026")]
    public async Task InjectionCorpus_IsRejectedOrHarmless()
    {
        string[] rejected =
        [
            "SELECT * FROM readmodel.\"account-summary\"; DROP TABLE x",
            "SELECT * FROM readmodel.\"account-summary\" WHERE holder = 'a' -- ' OR 1=1",
            "SELECT * FROM readmodel.\"account-summary\" /* x */",
            "SELECT usename FROM pg_catalog.pg_user",
            "SELECT * FROM information_schema.tables",
            "UPDATE readmodel.\"account-summary\" SET balance = 0",
            "SELECT * FROM readmodel.\"account-summary\" UNION SELECT * FROM readmodel.\"account-summary\"",
            "SELECT pg_sleep(10)",
            "SELECT * FROM readmodel.\"account-summary\" WHERE holder = 'a‮'",
            "SELECT * FROM readmodel.\"account-summary\" WHERE holder = 'a\0'",
            "SELECT * FROM \"q_x\".outbox",
        ];
        foreach (string text in rejected)
        {
            QueryException e = await Should.ThrowAsync<QueryException>(async () =>
                await Compiled.QueryPageAsync(text, options: new QueryOptions { Language = QueryLanguage.Sql }, cancellationToken: Ct));
            e.Code.ShouldStartWith("SFQ0", customMessage: text);
            int stage = int.Parse(e.Code.AsSpan(3, 2), CultureInfo.InvariantCulture);   // SFQ01xx syntax, SFQ02xx bind
            stage.ShouldBeInRange(1, 2, text);
        }

        string[] payloads =
        [
            "Open' OR '1'='1",
            "'; DROP TABLE documents; --",
            "\\'; SELECT pg_sleep(5); --",
            "<script>alert(1)</script>",
            "%' OR 1=1 OR holder LIKE '%",
            "Ada' UNION SELECT usename FROM pg_user --",
            "Аda",
        ];
        foreach (string payload in payloads)
        {
            var parameters = new Dictionary<string, object?> { ["h"] = payload };
            (await RowsAsync(Compiled, "from readmodel \"account-summary\" | where holder = @h or holder like @h", parameters)).ShouldBeEmpty(payload);
            (await RowsAsync(Compiled, "from readmodel \"account-summary\" | where holder = @h", parameters)).ShouldBeEmpty(payload);
        }

        // Stored markup is data: it is returned as a string, never interpreted.
        (await RowsAsync(Compiled, "from readmodel \"account-summary\" | where holder = 'Ada' | project holder")).ShouldBe(["Ada"]);
    }

    [Fact]
    [Trait("Id", "QRY-45")]
    [Trait("Covers", "doc 14 7.2")]
    public async Task IndexSync_CreatesQueryableIndexes_Idempotently_AndThePlannerUsesThem()
    {
        QueryCatalog catalog = QueryCatalog.Build(_host.Services.GetRequiredService<RuntimeModel>());

        QueryIndexPlan first = await PostgreSqlIndexSync.ApplyAsync(Server.DataSource, catalog, _schema, concurrently: false, Ct);
        QueryIndexPlan second = await PostgreSqlIndexSync.ApplyAsync(Server.DataSource, catalog, _schema, concurrently: true, Ct);

        first.Create.Select(i => i.Source).ShouldBe(["account-summary@1.status", "bank.account.deposited.accountId"]);
        first.Create.ShouldAllBe(i => i.Name.StartsWith(PostgreSqlIndexSync.Prefix, StringComparison.Ordinal));
        second.Create.ShouldBeEmpty();
        second.Unchanged.Count.ShouldBe(2);

        // The compiled predicate matches the index expression: with sequential scans off, the plan uses it.
        CompiledQuery compiled = Compile("from readmodel \"account-summary\" | where status = 1 | project holder", new Dictionary<string, object?>(), _schema);
        await using NpgsqlConnection connection = await Server.DataSource.OpenConnectionAsync(Ct);
        await using var batch = new NpgsqlBatch(connection);
        batch.BatchCommands.Add(new NpgsqlBatchCommand("SET enable_seqscan = off"));
        var explain = new NpgsqlBatchCommand("EXPLAIN " + compiled.Sql);
        foreach (NpgsqlParameter parameter in compiled.Parameters)
        {
            explain.Parameters.Add(parameter);
        }
        batch.BatchCommands.Add(explain);
        var plan = new StringBuilder();
        await using (NpgsqlDataReader reader = await batch.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                plan.AppendLine(reader.GetString(0));
            }
        }

        string index = first.Create.Single(i => i.Source == "account-summary@1.status").Name;
        plan.ToString().ShouldContain(index);
    }

    [Fact]
    [Trait("Id", "QRY-60")]
    [Trait("Category", "Property")]
    [Trait("Covers", "ADR-026")]
    public void NoLiteral_EverRequestValueIsAParameter()
    {
        string[] templates =
        [
            "from readmodel \"account-summary\" | where holder = {0} | project holder",
            "from readmodel \"account-summary\" | where holder like {0} or holder in ({0}, 'x') | project holder",
            "from readmodel \"account-summary\" | project holder, tag = concat(holder, {0}) | order by tag",
            "from events of \"bank.account.closed\" | where time >= ago(1d) and data.reason = {0}",
            "from readmodel \"account-summary\" | where coalesce(holder, {0}) = {0} | summarize n = count() by status",
        ];
        Gen.String[Gen.Char.AlphaNumeric, 6, 24]
            .Select(Gen.Int[0, templates.Length - 1])
            .Sample((literal, template) =>
            {
                string marker = "zq" + literal;
                string text = string.Format(CultureInfo.InvariantCulture, templates[template], "'" + marker + "'");
                CompiledQuery compiled = Compile(text, new Dictionary<string, object?>());
                compiled.Sql.ShouldNotContain(marker);
                compiled.Parameters.ShouldContain(p => Equals(p.Value, marker));
            }, iter: 200);
    }

    // ================================================================ helpers

    private void Configure(SourceFlowBuilder sf, string schema)
    {
        sf.ServiceName = "accounts";
        sf.AddModule<AccountsModule>();
        sf.Projections(p => p.Add<AccountSummaryProjection>().Inline());
        sf.UsePostgreSql(o =>
        {
            o.Schema = schema;
            o.DataSource = Server.DataSource;
        });
        sf.UseInMemoryTransport();
    }

    private static CompiledQuery Compile(string text, IReadOnlyDictionary<string, object?> parameters, string schema = "sf")
    {
        ParseResult parsed = SfqlParser.Parse(text, QueryLanguage.Sfql, QueryLimits.Default);
        parsed.Diagnostics.ShouldBeEmpty();
        BindResult bound = SfqlBinder.Bind(parsed.Query!, text, Catalog);
        bound.Diagnostics.ShouldBeEmpty();
        var execution = new QueryExecution(bound.Query!, parameters, "-", new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), null, 0, 51, QueryPolicySettings.Default);
        return PostgreSqlQueryCompiler.Compile(execution, schema);
    }

    private static async Task<IReadOnlyList<string>> RowsAsync(IQueryService service, string query, IReadOnlyDictionary<string, object?>? parameters = null, QueryOptions? options = null)
    {
        var rows = new List<string>();
        await foreach (QueryRow row in service.QueryAsync(query, parameters, options, Ct))
        {
            rows.Add(Render(row));
        }

        return rows;
    }

    private static string Render(QueryRow row)
    {
        return string.Join(" | ", row.Values.Select(Render));
    }

    private static string Render(object? value)
    {
        return value switch
        {
            null => "null",
            decimal d => (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
            DateTimeOffset t => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            JsonElement e => e.GetRawText(),
            string[] tags => "[" + string.Join(",", tags) + "]",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
    }
}
