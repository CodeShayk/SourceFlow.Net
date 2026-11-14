using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SourceFlow.AspNetCore;
using SourceFlow.Cloud.Aws;
using SourceFlow.Cloud.Azure;
using SourceFlow.Cloud.Gcp;
using SourceFlow.Diagnostics;
using SourceFlow.Hosting;
using SourceFlow.Query;
using SourceFlow.Storage.PostgreSQL;
using SourceFlow.Transport;

namespace TravelBooking;

/// <summary>
/// What the two TravelBooking hosts share, as steps of the <see cref="SourceFlowBuilder"/> chain: PostgreSQL (a schema per
/// service), the cloud transport between the services, provisioning, health checks and the demo query API.
/// </summary>
public static class ServiceHost
{
    /// <summary>
    /// Stores the service in its own PostgreSQL schema, connects it to the configured transport, and sets provisioning.
    /// <list type="bullet">
    /// <item><c>ConnectionStrings:SourceFlow</c>: the PostgreSQL connection string.</item>
    /// <item><c>Transport:Kind</c>: <c>aws</c>, <c>azure</c> or <c>gcp</c>; the other <c>Transport</c> keys are that cloud's settings.</item>
    /// <item><c>SourceFlow:Provisioning</c>: <c>CreateIfMissing</c> creates queues and topics at startup (the compose demo);
    /// unset, Development creates them and other environments only validate them.</item>
    /// </list>
    /// </summary>
    public static SourceFlowBuilder UseInfrastructure(this SourceFlowBuilder sf, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(sf);
        ArgumentNullException.ThrowIfNull(configuration);
        string connectionString = configuration.GetConnectionString("SourceFlow")
            ?? throw new InvalidOperationException("Set ConnectionStrings__SourceFlow to a PostgreSQL connection string.");
        string schema = sf.ServiceName is { Length: > 0 } name ? name : throw new InvalidOperationException("Add the service before its infrastructure.");

        sf.UsePostgreSql(connectionString, o =>
            {
                o.Schema = schema;
                o.AutoMigrate = !bool.TryParse(configuration["SourceFlow:AutoMigrate"], out bool migrate) || migrate;
            })
            .When(Enum.TryParse(configuration["SourceFlow:Provisioning"], ignoreCase: true, out ProvisioningMode provisioning),
                s => s.WithProvisioning(provisioning));

        IConfigurationSection transport = configuration.GetSection("Transport");
        string prefix = transport["NamePrefix"] ?? string.Empty;
        return transport["Kind"] switch
        {
            "aws" => sf.UseAwsTransport(o =>
            {
                o.Region = transport["Region"];
                o.ServiceUrl = transport["ServiceUrl"] is { Length: > 0 } url ? new Uri(url) : null;
                o.NamePrefix = prefix;
            }),
            "azure" => sf.UseAzureTransport(o =>
            {
                o.FullyQualifiedNamespace = transport["Namespace"];
                o.ConnectionString = transport["ConnectionString"];
                o.NamePrefix = prefix;
            }),
            "gcp" => sf.UseGcpTransport(o =>
            {
                o.ProjectId = transport["ProjectId"] ?? throw new InvalidOperationException("Set Transport:ProjectId.");
                o.NamePrefix = prefix;
            }),
            _ => throw new InvalidOperationException("Set Transport:Kind to aws, azure or gcp."),
        };
    }

    /// <summary>True when the sample's anonymous query API is on (<c>Demo:QueryApi</c>, default true).</summary>
    public static bool DemoQueryApi(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !bool.TryParse(configuration["Demo:QueryApi"], out bool on) || on;
    }

    /// <summary>
    /// Sample only: every caller may read, validate and explain queries, without personal data. A real service grants
    /// permissions from the caller's claims and maps the query endpoints with <c>.RequireAuthorization("policy")</c>.
    /// </summary>
    public static SourceFlowBuilder WithDemoQueryApi(this SourceFlowBuilder sf)
    {
        return sf.WithQueryApi(o => o.Permissions = _ => QueryPermissions.Of(
        [
            QueryPermission.Adhoc, QueryPermission.Events, QueryPermission.ReadModels, QueryPermission.Aggregates,
            QueryPermission.Schema, QueryPermission.Validate, QueryPermission.Explain,
        ]));
    }

    /// <summary>
    /// Traces and metrics over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (the compose demo sends them to the Aspire
    /// dashboard): one trace follows a booking across both services. SourceFlow publishes one ActivitySource and one Meter.
    /// </summary>
    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService(configuration["OTEL_SERVICE_NAME"] ?? serviceName))
                .WithTracing(t => t.AddSource(SourceFlowTelemetry.ActivitySourceName))
                .WithMetrics(m => m.AddMeter(SourceFlowTelemetry.MeterName))
                .UseOtlpExporter();
        }

        return services;
    }
}
