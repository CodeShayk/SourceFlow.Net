using Bank.Accounts;
using Bank.Contracts;
using Bank.Notifications;
using Bank.Transfers;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("SourceFlow")
    ?? throw new InvalidOperationException("Set ConnectionStrings__SourceFlow to a PostgreSQL connection string.");

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "bank";
    sf.AddModule<ContractsModule>();
    sf.AddModule<AccountsModule>();
    sf.AddModule<TransfersModule>();
    sf.AddModule<NotificationsModule>();
    sf.Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(200));
    sf.UsePostgreSql(connectionString, o => o.AutoMigrate = Flag(builder.Configuration, "SourceFlow:AutoMigrate", true));
    sf.UseInMemoryTransport();
});

bool demoQueries = Flag(builder.Configuration, "Demo:QueryApi", false);
if (demoQueries)
{
    // Local demo only: every caller gets the read, validate, explain and schema permissions (no PII unless asked).
    bool personalData = Flag(builder.Configuration, "Demo:PersonalData", false);
    builder.Services.AddSourceFlowQueryApi(o => o.Permissions = _ => SourceFlow.Query.QueryPermissions.Of(
    [
        SourceFlow.Query.QueryPermission.Adhoc, SourceFlow.Query.QueryPermission.Events, SourceFlow.Query.QueryPermission.ReadModels,
        SourceFlow.Query.QueryPermission.Aggregates, SourceFlow.Query.QueryPermission.Schema, SourceFlow.Query.QueryPermission.Validate,
        SourceFlow.Query.QueryPermission.Explain, .. personalData ? [SourceFlow.Query.QueryPermission.PersonalData] : Array.Empty<string>(),
    ]));
}

WebApplication app = builder.Build();

app.MapGet("/health", (RequestDelegate)(http => http.Response.WriteAsync("ok", http.RequestAborted)));

// Commands (doc 03 §5.1): POST with a JSON body; route values fill the ids. Responses carry ETag and SourceFlow-Consistency.
app.MapCommand<OpenAccount>("/accounts/{accountId}");
app.MapCommand<Deposit>("/accounts/{accountId}/deposits");
app.MapCommand<Withdraw>("/accounts/{accountId}/withdrawals");
app.MapCommand<CloseAccount>("/accounts/{accountId}/close");
app.MapCommand<RequestTransfer>("/transfers/{transferId}");

// The query API and named domain queries (doc 14 §9), off unless Demo:QueryApi is true. A real service maps them with
// .RequireAuthorization("policy") (startup fails closed otherwise, SFQ0504); the demo opts out explicitly.
if (demoQueries)
{
    app.MapSourceFlowQueryApi().AllowAnonymous();
    app.MapDomainQueries().AllowAnonymous();
}

app.Run();

static bool Flag(IConfiguration configuration, string key, bool fallback)
{
    return bool.TryParse(configuration[key], out bool value) ? value : fallback;
}
