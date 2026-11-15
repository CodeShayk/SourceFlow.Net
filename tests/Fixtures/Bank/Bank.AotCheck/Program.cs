using Bank.Accounts;
using Bank.Contracts;
using Bank.Notifications;
using Bank.Transfers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;
using SourceFlow.Query;
using SourceFlow.Storage.PostgreSQL;

// The AOT check (ARCH-06): a service built from the AOT-marked packages (core, PostgreSQL, ASP.NET Core) and the Bank
// domain, published with PublishAot and warnings as errors in CI. It is never run.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("SourceFlow") ?? "Host=localhost";

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "bank";
    sf.AddModule<ContractsModule>();
    sf.AddModule<AccountsModule>();
    sf.AddModule<TransfersModule>();
    sf.AddModule<NotificationsModule>();
    sf.Routing(r =>
    {
        r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events");
        r.Subscribe("accounts.events");
    });
    sf.UsePostgreSql(connectionString);
    sf.UseInMemoryTransport();
});
builder.Services.AddHealthChecks().AddSourceFlow();
builder.Services.AddSourceFlowQueryApi(o => o.Permissions = _ => QueryPermissions.Of([QueryPermission.ReadModels, QueryPermission.Validate]));

WebApplication app = builder.Build();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.ReadyTag) });
app.MapCommand<OpenAccount>("/accounts/{accountId}");
app.MapCommand<Deposit>("/accounts/{accountId}/deposits");
app.MapCommand<RequestTransfer>("/transfers/{transferId}");
app.MapSourceFlowQueryApi().RequireAuthorization();
app.MapDomainQueries().RequireAuthorization();
app.Run();
