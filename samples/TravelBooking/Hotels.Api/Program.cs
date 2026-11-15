using Hotels;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;
using TravelBooking;

// The Hotels service: hotels and their rooms. It holds, releases and confirms rooms for the Bookings service's workflow.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
IConfiguration configuration = builder.Configuration;
bool demoQueries = ServiceHost.DemoQueryApi(configuration);

builder.Services.AddSourceFlow(sf => sf
    .AddHotelsService()                         // the service: its module, read model and publications
    .UseInfrastructure(configuration)           // PostgreSQL, the transport to Bookings, provisioning
    .WithHealthChecks()
    .When(demoQueries, s => s.WithDemoQueryApi()));
builder.Services.AddTelemetry(configuration, HotelsService.Name);

WebApplication app = builder.Build();

// `dotnet Hotels.Api.dll sourceflow dlq list` runs the SourceFlow CLI in this service.
if (await SourceFlow.Tool.SourceFlowCli.TryRunAsync(args, app.Services) is { } exitCode)
{
    return exitCode;
}

app.MapSourceFlowHealthChecks();

// Hotel staff: register a hotel, and release a hold by hand (which makes the workflow's confirmation fail after its
// point of no return, so the instance stops for an operator).
app.MapCommand<RegisterHotel>("/hotels/{hotelId}");
app.MapCommand<ReleaseRoom>("/hotels/{hotelId}/holds/{bookingId}/release");

if (demoQueries)
{
    app.MapSourceFlowQueryApi().AllowAnonymous();
    app.MapDomainQueries().AllowAnonymous();
}

app.Run();
return 0;
