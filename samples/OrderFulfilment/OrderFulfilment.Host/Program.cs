using OrderFulfilment;
using Orders;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;

// One of the four OrderFulfilment services, chosen by the Service setting (`--Service=orders`, or Service=orders in the
// environment): orders, inventory, payments or shipping. Each service has its own PostgreSQL schema, named after it, and
// reaches the others only through the broker.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
IConfiguration configuration = builder.Configuration;
string service = ServiceHost.ServiceName(configuration);
bool demoQueries = ServiceHost.DemoQueryApi(configuration);

builder.Services.AddSourceFlow(sf => sf
    .AddService(service, configuration)         // the service: its module, routes and subscriptions
    .UseInfrastructure(configuration)           // PostgreSQL, the transport to the other services, provisioning
    .WithHealthChecks()                         // store, transport, outbox, subscriptions, leases, keys
    .When(demoQueries, s => s.WithDemoQueryApi()));
builder.Services.AddTelemetry(configuration, service);

WebApplication app = builder.Build();

// `dotnet OrderFulfilment.Host.dll sourceflow workflow list --name order-fulfilment` runs the SourceFlow CLI with this
// service's model and storage (the container sets Service).
if (await SourceFlow.Tool.SourceFlowCli.TryRunAsync(args, app.Services) is { } exitCode)
{
    return exitCode;
}

app.MapSourceFlowHealthChecks();                // /health, /health/ready, /health/sourceflow

if (service == OrdersService.Name)
{
    // The order's commands: POST with a JSON body; the route fills the order id. Placing an order starts the
    // order-fulfilment workflow; the review and the address correction are what its waiting steps wait for.
    app.MapCommand<PlaceOrder>("/orders/{orderId}");
    app.MapCommand<ApproveOrder>("/orders/{orderId}/approve");
    app.MapCommand<RejectOrder>("/orders/{orderId}/reject");
    app.MapCommand<CorrectAddress>("/orders/{orderId}/address");
}

if (demoQueries)
{
    // GET /queries lists the named queries (in orders, orders.workflow-instances, generated with the workflow); POST /query
    // runs SFQL. Sample only: a real service requires an authorization policy.
    app.MapSourceFlowQueryApi().AllowAnonymous();
    app.MapDomainQueries().AllowAnonymous();
}

app.Run();
return 0;
