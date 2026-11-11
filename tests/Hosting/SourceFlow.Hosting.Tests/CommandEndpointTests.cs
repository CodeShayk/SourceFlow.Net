using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bank.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;

namespace SourceFlow.Hosting.Tests;

/// <summary>UT-ASP (plan 05): MapCommand HTTP semantics (doc 03 §5.1).</summary>
[Trait("Category", "Unit")]
public sealed class CommandEndpointTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
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
        _app = builder.Build();
        _app.MapCommand<OpenAccount>("/accounts/{accountId}");
        _app.MapCommand<Deposit>("/accounts/{accountId}/deposits");
        _app.MapCommand<Withdraw>("/accounts/{accountId}/withdrawals");
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    [Trait("Id", "UT-ASP-01")]
    public async Task Accepted_Returns200_WithETagAndConsistencyToken()
    {
        var id = AccountId.New();

        HttpResponseMessage response = await Post($"/accounts/{id}", new { holder = "Ada", initialDeposit = 10 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.Tag.ShouldBe("\"1\"");
        response.Headers.GetValues(SourceFlowHeaders.Consistency).ShouldHaveSingleItem().ShouldContain(":");
        JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().ShouldBe("Accepted");
        body.RootElement.GetProperty("version").GetInt64().ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-ASP-02")]
    [Trait("Covers", "ADR-018")]
    public async Task Rejection_Returns422ProblemDetails_WithTheErrorCode()
    {
        var id = AccountId.New();
        await Post($"/accounts/{id}", new { holder = "Ada", initialDeposit = 10 });

        HttpResponseMessage response = await Post($"/accounts/{id}/withdrawals", new { amount = 50 });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("title").GetString().ShouldBe("account.insufficient_funds");
    }

    [Fact]
    [Trait("Id", "UT-ASP-03")]
    [Trait("Covers", "ADR-006")]
    public async Task IdempotencyKey_RepeatReturnsTheOriginalResult()
    {
        var id = AccountId.New();
        await Post($"/accounts/{id}", new { holder = "Ada", initialDeposit = 10 });

        HttpResponseMessage first = await Post($"/accounts/{id}/deposits", new { amount = 5 }, ("Idempotency-Key", "req-1"));
        HttpResponseMessage second = await Post($"/accounts/{id}/deposits", new { amount = 5 }, ("Idempotency-Key", "req-1"));

        first.Headers.ETag!.Tag.ShouldBe("\"2\"");
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Headers.ETag!.Tag.ShouldBe("\"2\"");
        JsonDocument.Parse(await second.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString().ShouldBe("Duplicate");
    }

    [Fact]
    [Trait("Id", "UT-ASP-04")]
    public async Task IfMatch_StaleVersion_Returns412()
    {
        var id = AccountId.New();
        await Post($"/accounts/{id}", new { holder = "Ada", initialDeposit = 10 });

        HttpResponseMessage stale = await Post($"/accounts/{id}/deposits", new { amount = 5 }, ("If-Match", "\"7\""));
        HttpResponseMessage current = await Post($"/accounts/{id}/deposits", new { amount = 5 }, ("If-Match", "\"1\""));

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [Trait("Id", "UT-ASP-05")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"amount\":\"lots\"}")]
    public async Task UnreadableBody_Returns400(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{AccountId.New()}/deposits")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    [Trait("Id", "UT-ASP-06")]
    public async Task HealthCheck_ReportsReady()
    {
        SourceFlowHost host = (SourceFlowHost)_app.Services.GetService(typeof(SourceFlowHost))!;

        (await host.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext())).Status
            .ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
    }

    private async Task<HttpResponseMessage> Post(string path, object body, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await _client.SendAsync(request);
    }
}
