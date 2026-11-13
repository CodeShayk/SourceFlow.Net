using Bank.Accounts;
using Bank.Contracts;
using Bank.Notifications;
using Bank.Transfers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SourceFlow.AspNetCore;
using SourceFlow.Diagnostics;
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

    // Accounts publishes its public contract (AccountOpenedV1, no personal data); Notifications receives it through the
    // service's own subscription (RT-R4), exactly as it would if it ran as a separate service.
    sf.Routing(r =>
    {
        r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events");
        r.Subscribe("accounts.events");
    });
    sf.Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(200));
    sf.UsePostgreSql(connectionString, o => o.AutoMigrate = Flag(builder.Configuration, "SourceFlow:AutoMigrate", true));
    sf.UseInMemoryTransport();
});

// Health checks (doc 08 §5): liveness has no dependency checks, so a database outage does not restart the service.
builder.Services.AddHealthChecks().AddSourceFlow();

// Traces and metrics over OTLP when an endpoint is configured (OTEL_EXPORTER_OTLP_ENDPOINT), for example the Aspire
// dashboard in docker-compose.yml. SourceFlow publishes one ActivitySource and one Meter (doc 08 §1).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "bank"))
        .WithTracing(t => t.AddSource(SourceFlowTelemetry.ActivitySourceName))
        .WithMetrics(m => m.AddMeter(SourceFlowTelemetry.MeterName))
        .UseOtlpExporter();
}

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

#if !SOURCEFLOW_AOT_CHECK   // the CLI is not AOT-marked; the AOT check (ARCH-06) builds the service without it
// `dotnet run -- sourceflow <command>` runs the SourceFlow CLI with this service's model and storage (ADR-031).
if (await SourceFlow.Tool.SourceFlowCli.TryRunAsync(args, app.Services) is { } exitCode)
{
    return exitCode;
}
#endif

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });                               // liveness
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.ReadyTag) });
app.MapHealthChecks("/health/sourceflow", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.Tag) });

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
return 0;

static bool Flag(IConfiguration configuration, string key, bool fallback)
{
    return bool.TryParse(configuration[key], out bool value) ? value : fallback;
}
