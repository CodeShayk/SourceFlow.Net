using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bank.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;
using SourceFlow.Query;

namespace SourceFlow.Hosting.Tests;

/// <summary>UT-ASP-21..36 (plan 05): the per-service query API and domain queries over HTTP (doc 14 §9, ADR-026).</summary>
[Trait("Category", "Unit")]
public sealed class QueryApiTests : IAsyncLifetime
{
    private const string All = "sourceflow.query.adhoc sourceflow.query.readmodels sourceflow.query.events sourceflow.query.validate sourceflow.query.explain sourceflow.query.schema";
    private const string Summary = "from readmodel \"account-summary\"";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        (_app, _client) = await StartAsync(api => api.RequireAuthorization("sourceflow-query"), o => o.RequestsPerMinute = 1000);
        foreach ((string holder, decimal balance) in new[] { ("Ada", 20m), ("=cmd|' /C calc'!A0", 50m), ("Grace", 900m) })
        {
            (await Post($"/accounts/{AccountId.New()}", JsonSerializer.Serialize(new { holder, initialDeposit = balance }), user: null)).EnsureSuccessStatusCode();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    [Trait("Id", "UT-ASP-21")]
    [Trait("Covers", "ADR-026")]
    public async Task Mapping_WithoutAnAuthorizationPolicy_FailsStartup()
    {
        InvalidOperationException e = await Should.ThrowAsync<InvalidOperationException>(async () => await StartAsync(_ => { }));

        e.Message.ShouldStartWith(QueryCodes.AuthorizationRequired);
    }

    [Fact]
    [Trait("Id", "UT-ASP-22")]
    public async Task Mapping_WithAnExplicitAllowAnonymous_Starts()
    {
        (WebApplication app, HttpClient client) = await StartAsync(api => api.AllowAnonymous());
        await using (app)
        {
            client.Dispose();
        }
    }

    [Fact]
    [Trait("Id", "UT-ASP-23")]
    [Trait("Covers", "doc 14 9.1")]
    public async Task Query_ReturnsColumnsRowsAndConsistency_WithSecurityHeaders()
    {
        HttpResponseMessage response = await Query(Summary + " | where balance < 100 | project holder, balance | order by balance");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("Cross-Origin-Resource-Policy").ShouldBe(["same-origin"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        JsonElement body = await Json(response);
        body.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["holder", "balance"]);
        body.GetProperty("rows")[0][1].GetString().ShouldBe("20");   // decimals are exact strings
        body.GetProperty("consistency").GetProperty("sources")[0].GetProperty("level").GetString().ShouldBe("eventual");
        body.GetProperty("queryHash").GetString()!.ShouldStartWith("sha256:");
    }

    [Fact]
    [Trait("Id", "UT-ASP-24")]
    [Trait("Covers", "doc 14 8")]
    public async Task Query_WithoutTheAdhocPermission_Is403()
    {
        HttpResponseMessage response = await Query(Summary, permissions: "sourceflow.query.readmodels");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await Json(response)).GetProperty("code").GetString().ShouldBe(QueryCodes.PermissionDenied);
    }

    [Fact]
    [Trait("Id", "UT-ASP-25")]
    public async Task Validate_NeverExecutes_AndReturnsDiagnostics()
    {
        JsonElement valid = await Json(await Post("/query/validate", Body(Summary + " | project holder")));
        JsonElement invalid = await Json(await Post("/query/validate", Body("DELETE FROM x", "sql")));

        valid.GetProperty("valid").GetBoolean().ShouldBeTrue();
        valid.GetProperty("columns")[0].GetProperty("name").GetString().ShouldBe("holder");
        invalid.GetProperty("valid").GetBoolean().ShouldBeFalse();
        invalid.GetProperty("diagnostics")[0].GetProperty("code").GetString().ShouldBe(QueryCodes.Unsupported);
        (await Post("/query/validate", Body(Summary), permissions: "sourceflow.query.adhoc")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Id", "UT-ASP-26")]
    public async Task Explain_ShowsThePlan_AndNeedsItsPermission()
    {
        JsonElement plan = await Json(await Post("/query/explain", Body(Summary + " | where balance < @b", parameters: "{\"b\": 5}")));

        plan.GetProperty("engine").GetString().ShouldBe("in-memory");
        plan.GetProperty("parameterCount").GetInt32().ShouldBe(1);
        (await Post("/query/explain", Body(Summary), permissions: "sourceflow.query.adhoc sourceflow.query.readmodels")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    [Trait("Id", "UT-ASP-27")]
    public async Task Schema_ListsSourcesAndFlagsPersonalData()
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/query/schema");
        Authenticate(request, All, null);
        JsonElement schema = await Json(await _client.SendAsync(request, Ct));

        JsonElement opened = schema.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "bank.account.opened");
        opened.GetProperty("versions")[0].GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "holder")
            .GetProperty("personalData").GetBoolean().ShouldBeTrue();
        schema.GetProperty("readModels")[0].GetProperty("name").GetString().ShouldBe("account-summary");
    }

    [Fact]
    [Trait("Id", "UT-ASP-28")]
    [Trait("Covers", "ADR-026")]
    public async Task Problems_NeverEchoTheCallersText()
    {
        const string text = "SELECT * FROM readmodel.\"account-summary\"; DROP TABLE secret_marker_xyz";
        HttpResponseMessage response = await Query(text, language: "sql");
        string body = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldNotContain("secret_marker_xyz");
        body.ShouldNotContain("DROP TABLE");
        JsonDocument.Parse(body).RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString().ShouldBe(QueryCodes.Unsupported);
    }

    [Fact]
    [Trait("Id", "UT-ASP-29")]
    public async Task TransportGuard_JsonOnly_BodySize_AndDepth()
    {
        HttpResponseMessage text = await Post("/query", "query", contentType: "text/plain");
        HttpResponseMessage large = await Post("/query", Body(Summary + " | where holder = '" + new string('a', 70_000) + "'"));
        HttpResponseMessage deep = await Post("/query", "{\"query\":\"x\",\"parameters\":" + string.Concat(Enumerable.Repeat("{\"a\":", 20)) + "1" + new string('}', 20) + "}");

        text.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        large.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        deep.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Id", "UT-ASP-30")]
    public async Task NdJson_StreamsRows_ThenATrailer()
    {
        HttpResponseMessage response = await Query(Summary + " | project holder | order by holder", accept: "application/x-ndjson", pageSize: 2);
        string[] lines = (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/x-ndjson");
        lines.Length.ShouldBe(3);
        JsonDocument.Parse(lines[0]).RootElement.GetProperty("holder").GetString().ShouldBe("=cmd|' /C calc'!A0");
        JsonDocument.Parse(lines[2]).RootElement.GetProperty("continuation").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Id", "UT-ASP-31")]
    [Trait("Covers", "ADR-026")]
    public async Task Csv_NeutralisesFormulas()
    {
        HttpResponseMessage response = await Query(Summary + " | project holder, balance | order by holder", accept: "text/csv");
        string csv = await response.Content.ReadAsStringAsync(Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe("holder,balance");
        lines[1].ShouldBe("'=cmd|' /C calc'!A0,50");
        lines[2].ShouldBe("Ada,20");
    }

    [Fact]
    [Trait("Id", "UT-ASP-32")]
    public async Task RateLimit_Returns429_WithRetryAfter()
    {
        (WebApplication app, HttpClient client) = await StartAsync(api => api.RequireAuthorization("sourceflow-query"), o => o.RequestsPerMinute = 2);
        await using (app)
        {
            HttpResponseMessage[] responses = new HttpResponseMessage[3];
            for (int i = 0; i < 3; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/query") { Content = new StringContent(Body(Summary), Encoding.UTF8, "application/json") };
                Authenticate(request, All, null);
                responses[i] = await client.SendAsync(request, Ct);
            }

            responses[0].StatusCode.ShouldBe(HttpStatusCode.OK);
            responses[1].StatusCode.ShouldBe(HttpStatusCode.OK);
            responses[2].StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            responses[2].Headers.RetryAfter.ShouldNotBeNull();
            (await Json(responses[2])).GetProperty("code").GetString().ShouldBe(QueryCodes.RateLimited);
            client.Dispose();
        }
    }

    [Fact]
    [Trait("Id", "UT-ASP-33")]
    public async Task InvalidContinuation_Is409()
    {
        HttpResponseMessage response = await Post("/query", Body(Summary, continuation: "not-a-token"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Json(response)).GetProperty("code").GetString().ShouldBe(QueryCodes.Continuation);
    }

    [Fact]
    [Trait("Id", "UT-ASP-34")]
    [Trait("Covers", "doc 14 8")]
    public async Task Tenant_ComesFromThePrincipal()
    {
        JsonElement own = await Json(await Query(Summary));
        JsonElement other = await Json(await Query(Summary, tenant: "another-tenant"));

        own.GetProperty("rows").GetArrayLength().ShouldBe(3);
        other.GetProperty("rows").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    [Trait("Id", "UT-ASP-35")]
    [Trait("Covers", "Q-B4")]
    public async Task DomainQueries_AreListed_AndRunWithQueryStringParameters()
    {
        var list = new HttpRequestMessage(HttpMethod.Get, "/queries");
        Authenticate(list, "", null);
        JsonElement queries = await Json(await _client.SendAsync(list, Ct));
        var run = new HttpRequestMessage(HttpMethod.Get, "/queries/accounts.low-balance/v1?below=60");
        Authenticate(run, "", null);
        JsonElement rows = await Json(await _client.SendAsync(run, Ct));
        var missing = new HttpRequestMessage(HttpMethod.Get, "/queries/accounts.nothing/v1");
        Authenticate(missing, "", null);

        JsonElement query = queries.GetProperty("queries").EnumerateArray().ShouldHaveSingleItem();
        query.GetProperty("path").GetString().ShouldBe("accounts.low-balance/v1");
        query.GetProperty("parameters")[0].GetString().ShouldBe("below");
        rows.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["accountId", "holder", "balance"]);
        rows.GetProperty("rows").EnumerateArray().Select(r => r[1].GetString()).ShouldBe(["Ada", "=cmd|' /C calc'!A0"]);
        (await _client.SendAsync(missing, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("Id", "UT-ASP-36")]
    public async Task Unauthenticated_Is401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/query") { Content = new StringContent(Body(Summary), Encoding.UTF8, "application/json") };

        (await _client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Id", "UT-ASP-37")]
    [Trait("Covers", "doc 14 9")]
    public void DastOpenApiDocument_ListsExactlyTheMappedQueryEndpoints()
    {
        // deploy/dast/query-api.openapi.json drives the nightly OWASP ZAP scan: it must not miss an endpoint.
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "SourceFlow.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!, "deploy", "dast", "query-api.openapi.json")));
        string[] documented = [.. document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(m => m.Name.ToUpperInvariant() + " " + p.Name))
            .Order(StringComparer.Ordinal)];
        string[] mapped = [.. _app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<SourceFlowQueryApiEndpointMetadata>() is not null)
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(m => m + " " + System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText!, ":[a-z]+}", "}").TrimEnd('/')))
            .Order(StringComparer.Ordinal)];

        documented.ShouldBe(mapped);
    }

    // ================================================================ helpers

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<RouteGroupBuilder> secure, Action<SourceFlowQueryApiOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "accounts";
            sf.AddModule<AccountsModule>();
            sf.UseInMemoryStorage();
            sf.UseInMemoryTransport();
            sf.Projections(p => p.Add<AccountSummaryProjection>().Inline());
        });
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", null);
        builder.Services.AddAuthorizationBuilder().AddPolicy("sourceflow-query", p => p.RequireAuthenticatedUser());
        builder.Services.AddSourceFlowQueryApi(configure);
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCommand<OpenAccount>("/accounts/{accountId}");
        secure(app.MapSourceFlowQueryApi());
        secure(app.MapDomainQueries());
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return (app, app.GetTestClient());
    }

    private Task<HttpResponseMessage> Query(string text, string? language = null, string permissions = All, string? tenant = null, string? accept = null, int? pageSize = null)
    {
        return Post("/query", Body(text, language, pageSize: pageSize), permissions: permissions, tenant: tenant, accept: accept);
    }

    private async Task<HttpResponseMessage> Post(string path, string body, string? permissions = All, string? tenant = null, string? accept = null, string contentType = "application/json", string? user = "alice")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (user is not null)
        {
            Authenticate(request, permissions ?? string.Empty, tenant);
        }

        if (accept is not null)
        {
            request.Headers.Accept.ParseAdd(accept);
        }

        return await _client.SendAsync(request, Ct);
    }

    private static void Authenticate(HttpRequestMessage request, string permissions, string? tenant)
    {
        request.Headers.Add("X-Test-User", "alice");
        request.Headers.Add("X-Test-Permissions", permissions);
        if (tenant is not null)
        {
            request.Headers.Add("X-Test-Tenant", tenant);
        }
    }

    private static string Body(string query, string? language = null, string? parameters = null, string? continuation = null, int? pageSize = null)
    {
        var body = new StringBuilder("{\"query\":").Append(JsonSerializer.Serialize(query));
        if (language is not null)
        {
            body.Append(",\"language\":\"").Append(language).Append('"');
        }

        if (parameters is not null)
        {
            body.Append(",\"parameters\":").Append(parameters);
        }

        if (continuation is not null)
        {
            body.Append(",\"continuation\":\"").Append(continuation).Append('"');
        }

        if (pageSize is not null)
        {
            body.Append(",\"pageSize\":").Append(pageSize.Value);
        }

        return body.Append('}').ToString();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    /// <summary>Authenticates from X-Test-User / X-Test-Permissions / X-Test-Tenant headers.</summary>
    private sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out Microsoft.Extensions.Primitives.StringValues user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()), new("scope", Request.Headers["X-Test-Permissions"].ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Tenant", out Microsoft.Extensions.Primitives.StringValues tenant))
            {
                claims.Add(new Claim("tenant", tenant.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
        }
    }
}
