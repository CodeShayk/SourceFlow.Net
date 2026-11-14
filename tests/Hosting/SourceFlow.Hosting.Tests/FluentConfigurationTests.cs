using System.Net;
using Bank.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;
using SourceFlow.Query;
using SourceFlow.Transport;

namespace SourceFlow.Hosting.Tests;

/// <summary>
/// The fluent configuration: a service and its features enabled in one <see cref="SourceFlowBuilder"/> chain
/// (<c>WithServiceName</c>, <c>WithRoles</c>, <c>WithProvisioning</c>, <c>WithHealthChecks</c>, <c>WithQueryApi</c>, <c>When</c>),
/// and the endpoints mapped with <c>MapSourceFlowHealthChecks</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FluentConfigurationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-HOST-09")]
    public void TheChain_SetsTheServiceAndItsFeatures_AndWhenAppliesOnlyWhenTrue()
    {
        SourceFlowBuilder? configured = null;
        var services = new ServiceCollection();
        services.AddSourceFlow(sf => configured = sf
            .WithServiceName("accounts")
            .WithRoles(SourceFlowRoles.Api)
            .WithProvisioning(ProvisioningMode.Validate)
            .AddModule<AccountsModule>()
            .UseInMemoryStorage()
            .UseInMemoryTransport()
            .When(false, s => s.WithServiceName("never"))
            .When(true, s => s.Configure(o => o.PollInterval = TimeSpan.FromSeconds(3))));

        configured.ShouldNotBeNull();
        configured.ServiceName.ShouldBe("accounts");
        configured.Roles.ShouldBe(SourceFlowRoles.Api);
        configured.Provisioning.ShouldBe(ProvisioningMode.Validate);
        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<SourceFlow.Runtime.SourceFlowOptions>>().Value.PollInterval.ShouldBe(TimeSpan.FromSeconds(3));
        Should.Throw<ArgumentException>(() => new ServiceCollection().AddSourceFlow(sf => sf.WithServiceName(" ")));
    }

    [Fact]
    [Trait("Id", "UT-ASP-38")]
    public async Task WithHealthChecks_AndMapSourceFlowHealthChecks_ServeLivenessReadinessAndEveryCheck()
    {
        await using WebApplication app = await StartAsync(sf => sf.WithHealthChecks(), queries: false, health: true);
        using HttpClient client = app.GetTestClient();

        foreach (string path in (string[])["/health", "/health/ready", "/health/sourceflow"])
        {
            HttpResponseMessage response = await client.GetAsync(path, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy", path);
        }

        app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Select(r => r.Name)
            .ShouldBe(["sourceflow.ready", "sourceflow.store", "sourceflow.transport", "sourceflow.outbox", "sourceflow.subscriptions", "sourceflow.leases", "sourceflow.keys"], ignoreOrder: true);
    }

    [Theory]
    [Trait("Id", "UT-ASP-39")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithQueryApi_IsSwitchedOnAndOff_ByWhen(bool enabled)
    {
        if (!enabled)
        {
            // Mapping the query API without enabling it fails at startup, not at the first request.
            (await Should.ThrowAsync<InvalidOperationException>(() => StartAsync(sf => sf.When(false, s => s.WithQueryApi()), queries: true)))
                .Message.ShouldContain("AddSourceFlowQueryApi");
            return;
        }

        await using WebApplication app = await StartAsync(
            sf => sf.When(true, s => s.WithQueryApi(o => o.Permissions = _ => QueryPermissions.Of([QueryPermission.Schema]))), queries: true);
        using HttpClient client = app.GetTestClient();

        (await client.GetAsync("/query/schema", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<WebApplication> StartAsync(Func<SourceFlowBuilder, SourceFlowBuilder> features, bool queries, bool health = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSourceFlow(sf => features(sf
            .WithServiceName("accounts")
            .AddModule<AccountsModule>()
            .UseInMemoryStorage()
            .UseInMemoryTransport()));
        WebApplication app = builder.Build();
        if (health)
        {
            app.MapSourceFlowHealthChecks();
        }

        try
        {
            if (queries)
            {
                app.MapSourceFlowQueryApi().AllowAnonymous();
            }

            await app.StartAsync(Ct);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
