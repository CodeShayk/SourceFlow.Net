using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Hosting;

// The cloud AOT check (ARCH-06): a service on each AOT-marked cloud transport (AWS, Azure), published with PublishAot and
// warnings as errors in CI. A service has one transport, so the argument picks it; the AOT compiler analyses both. It is
// never run. The GCP adapter is not AOT-marked (plan 09 IR-08).
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
string cloud = args.Length > 0 ? args[0] : "aws";

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "aot-check";
    sf.Routing(r => r.Subscribe("aot.events"));
    sf.UseInMemoryStorage();
    _ = cloud switch
    {
        "azure" => sf.UseAzureTransport(o => o.FullyQualifiedNamespace = "aot-check.servicebus.windows.net"),
        _ => sf.UseAwsTransport(o => o.Region = "eu-west-1"),
    };
});

builder.Build().Run();
