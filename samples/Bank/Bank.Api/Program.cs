using Bank.Accounts;
using Bank.Api;
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

WebApplication app = builder.Build();

app.MapGet("/health", (RequestDelegate)(http => http.Response.WriteAsync("ok", http.RequestAborted)));

// Commands (doc 03 §5.1): POST with a JSON body; route values fill the ids. Responses carry ETag and SourceFlow-Consistency.
app.MapCommand<OpenAccount>("/accounts/{accountId}");
app.MapCommand<Deposit>("/accounts/{accountId}/deposits");
app.MapCommand<Withdraw>("/accounts/{accountId}/withdrawals");
app.MapCommand<CloseAccount>("/accounts/{accountId}/close");
app.MapCommand<RequestTransfer>("/transfers/{transferId}");

// Demo query endpoints, off unless Demo:QueryApi is true. The production query API (authorization, rate limits, OpenAPI)
// is SourceFlow.AspNetCore's MapSourceFlowQueryApi (WP-4.2); this sample endpoint must not be exposed publicly.
if (Flag(app.Configuration, "Demo:QueryApi", false))
{
    app.MapDemoQueryApi("/query", Flag(app.Configuration, "Demo:PersonalData", false));
}

app.Run();

static bool Flag(IConfiguration configuration, string key, bool fallback)
{
    return bool.TryParse(configuration[key], out bool value) ? value : fallback;
}
