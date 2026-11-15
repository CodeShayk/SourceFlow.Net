using Bookings;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;
using TravelBooking;

// The Bookings service: takes bookings from guests and runs the trip-booking workflow against the Hotels service.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
IConfiguration configuration = builder.Configuration;
bool demoQueries = ServiceHost.DemoQueryApi(configuration);

builder.Services.AddSourceFlow(sf => sf
    .AddBookingsService()                       // the service: its module, read model, routes and subscription
    .UseInfrastructure(configuration)           // PostgreSQL, the transport to Hotels, provisioning
    .WithHealthChecks()                         // store, transport, outbox, subscriptions, leases, keys
    .When(demoQueries, s => s.WithDemoQueryApi()));
builder.Services.AddTelemetry(configuration, BookingsService.Name);

WebApplication app = builder.Build();

// `dotnet Bookings.Api.dll sourceflow workflow list --name trip-booking` runs the SourceFlow CLI in this service.
if (await SourceFlow.Tool.SourceFlowCli.TryRunAsync(args, app.Services) is { } exitCode)
{
    return exitCode;
}

app.MapSourceFlowHealthChecks();                // /health, /health/ready, /health/sourceflow

// The guest's commands: POST with a JSON body; the route fills the booking id. Requesting a booking starts the
// trip-booking workflow; confirming or cancelling it is what the workflow's await-guest step waits for.
app.MapCommand<RequestBooking>("/bookings/{bookingId}");
app.MapCommand<ConfirmByGuest>("/bookings/{bookingId}/confirm");
app.MapCommand<CancelByGuest>("/bookings/{bookingId}/cancel");

if (demoQueries)
{
    // GET /queries lists the named queries, among them bookings.by-status and bookings.workflow-instances (generated
    // with the workflow); POST /query runs SFQL. Sample only: a real service requires an authorization policy.
    app.MapSourceFlowQueryApi().AllowAnonymous();
    app.MapDomainQueries().AllowAnonymous();
}

app.Run();
return 0;
