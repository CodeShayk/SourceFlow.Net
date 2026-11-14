using Inventory;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Orders;
using Payments;
using Shipping;
using SourceFlow.AspNetCore;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Diagnostics;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;

// One of the four OrderFulfilment services, chosen by the Service setting (for example `--Service=orders`). Each service
// has its own PostgreSQL schema, named after it, and reaches the others through the configured transport.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
IConfiguration configuration = builder.Configuration;
string service = configuration["Service"]
    ?? throw new InvalidOperationException("Set Service to orders, inventory, payments or shipping.");
string connectionString = configuration.GetConnectionString("SourceFlow")
    ?? throw new InvalidOperationException("Set ConnectionStrings__SourceFlow to a PostgreSQL connection string.");

if (service == InventoryService.Name)
{
    // The warehouse: SKUs listed under Inventory:OutOfStock can't be reserved.
    string[] outOfStock = [.. configuration.GetSection("Inventory:OutOfStock").GetChildren().Select(c => c.Value).OfType<string>()];
    builder.Services.AddSingleton<IStockLevels>(new ConfiguredStockLevels(outOfStock));
}

builder.Services.AddSourceFlow(sf =>
{
    _ = service switch
    {
        OrdersService.Name => sf.AddOrdersService(),
        InventoryService.Name => sf.AddInventoryService(),
        PaymentsService.Name => sf.AddPaymentsService(),
        ShippingService.Name => sf.AddShippingService(),
        _ => throw new InvalidOperationException($"'{service}' is not a service of this sample: use orders, inventory, payments or shipping."),
    };

    sf.UsePostgreSql(connectionString, o =>
    {
        o.Schema = service;
        o.AutoMigrate = !bool.TryParse(configuration["SourceFlow:AutoMigrate"], out bool migrate) || migrate;
    });
    UseTransport(sf, configuration);
});

builder.Services.AddHealthChecks().AddSourceFlow();

WebApplication app = builder.Build();

// `dotnet run -- --Service=orders sourceflow workflow list` runs the SourceFlow CLI with this service's model and storage.
if (await SourceFlow.Tool.SourceFlowCli.TryRunAsync(args, app.Services) is { } exitCode)
{
    return exitCode;
}

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.ReadyTag) });

if (service == OrdersService.Name)
{
    // The order's commands. Placing an order starts the order-fulfilment workflow; the review and the address
    // correction are what its waiting steps wait for.
    app.MapCommand<PlaceOrder>("/orders/{orderId}");
    app.MapCommand<ApproveOrder>("/orders/{orderId}/approve");
    app.MapCommand<RejectOrder>("/orders/{orderId}/reject");
    app.MapCommand<CorrectAddress>("/orders/{orderId}/address");
}

app.Run();
return 0;

// Transport:Kind is aws, azure or gcp; the other Transport keys are that cloud's settings.
static void UseTransport(SourceFlowBuilder sf, IConfiguration configuration)
{
    IConfigurationSection transport = configuration.GetSection("Transport");
    string prefix = transport["NamePrefix"] ?? string.Empty;
    switch (transport["Kind"])
    {
        case "aws":
            sf.UseAwsTransport(o =>
            {
                o.Region = transport["Region"];
                o.ServiceUrl = transport["ServiceUrl"] is { Length: > 0 } url ? new Uri(url) : null;
                o.NamePrefix = prefix;
            });
            break;
        case "azure":
            sf.UseAzureTransport(o =>
            {
                o.FullyQualifiedNamespace = transport["Namespace"];
                o.ConnectionString = transport["ConnectionString"];
                o.NamePrefix = prefix;
            });
            break;
        case "gcp":
            sf.UseGcpTransport(o =>
            {
                o.ProjectId = transport["ProjectId"] ?? throw new InvalidOperationException("Set Transport:ProjectId.");
                o.NamePrefix = prefix;
            });
            break;
        default:
            throw new InvalidOperationException("Set Transport:Kind to aws, azure or gcp.");
    }
}
