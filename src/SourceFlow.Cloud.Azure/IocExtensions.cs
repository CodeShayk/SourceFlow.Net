using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Cloud.Azure.Infrastructure;
using SourceFlow.Cloud.Azure.Messaging.Commands;
using SourceFlow.Cloud.Azure.Messaging.Events;
using SourceFlow.Cloud.Core.Configuration;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure;

public static class AzureIocExtensions
{
    public static void UseSourceFlowAzure(
        this IServiceCollection services,
        Action<AzureOptions> configureOptions,
        Action<BusConfigurationBuilder> configureBus)
    {
        // 1. Configure options
        services.Configure(configureOptions);
        var options = new AzureOptions();
        configureOptions(options);

        // 2. Register Azure Service Bus client (singleton, thread-safe)
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();

            var connectionString = config["SourceFlow:Azure:ServiceBus:ConnectionString"];
            var fullyQualifiedNamespace = config["SourceFlow:Azure:ServiceBus:FullyQualifiedNamespace"];

            if (!string.IsNullOrEmpty(connectionString))
            {
                return new ServiceBusClient(connectionString, new ServiceBusClientOptions
                {
                    RetryOptions = new ServiceBusRetryOptions
                    {
                        Mode = ServiceBusRetryMode.Exponential,
                        MaxRetries = 3,
                        Delay = TimeSpan.FromSeconds(1),
                        MaxDelay = TimeSpan.FromMinutes(1)
                    },
                    TransportType = ServiceBusTransportType.AmqpTcp
                });
            }
            else if (!string.IsNullOrEmpty(fullyQualifiedNamespace))
            {
                return new ServiceBusClient(
                    fullyQualifiedNamespace,
                    new DefaultAzureCredential(),
                    new ServiceBusClientOptions
                    {
                        RetryOptions = new ServiceBusRetryOptions
                        {
                            Mode = ServiceBusRetryMode.Exponential,
                            MaxRetries = 3,
                            Delay = TimeSpan.FromSeconds(1),
                            MaxDelay = TimeSpan.FromMinutes(1)
                        },
                        TransportType = ServiceBusTransportType.AmqpTcp
                    });
            }
            else
            {
                throw new InvalidOperationException(
                    "Either SourceFlow:Azure:ServiceBus:ConnectionString or SourceFlow:Azure:ServiceBus:FullyQualifiedNamespace must be configured");
            }
        });

        // 3. Register Azure Service Bus Administration client
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();

            var connectionString = config["SourceFlow:Azure:ServiceBus:ConnectionString"];
            var fullyQualifiedNamespace = config["SourceFlow:Azure:ServiceBus:FullyQualifiedNamespace"];

            if (!string.IsNullOrEmpty(connectionString))
            {
                return new ServiceBusAdministrationClient(connectionString);
            }
            else if (!string.IsNullOrEmpty(fullyQualifiedNamespace))
            {
                return new ServiceBusAdministrationClient(fullyQualifiedNamespace, new DefaultAzureCredential());
            }
            else
            {
                throw new InvalidOperationException(
                    "Either SourceFlow:Azure:ServiceBus:ConnectionString or SourceFlow:Azure:ServiceBus:FullyQualifiedNamespace must be configured");
            }
        });

        // 4. Build BusConfiguration from the fluent builder
        var busBuilder = new BusConfigurationBuilder();
        configureBus(busBuilder);
        var busConfig = busBuilder.Build();

        services.AddSingleton(busConfig);
        services.AddSingleton<ICommandRoutingConfiguration>(busConfig);
        services.AddSingleton<IEventRoutingConfiguration>(busConfig);
        services.AddSingleton<IBusBootstrapConfiguration>(busConfig);

        // 5. Register bootstrapper as hosted service
        services.AddHostedService<AzureBusBootstrapper>();

        // 6. Register Azure dispatchers
        services.AddScoped<ICommandDispatcher, AzureServiceBusCommandDispatcher>();
        services.AddSingleton<IEventDispatcher, AzureServiceBusEventDispatcher>();

        // 7. Register Azure listeners as hosted services
        if (options.EnableCommandListener)
            services.AddHostedService<AzureServiceBusCommandListener>();

        if (options.EnableEventListener)
            services.AddHostedService<AzureServiceBusEventListener>();

        // 8. Register health check
        services.AddHealthChecks()
            .AddCheck<AzureServiceBusHealthCheck>(
                "azure-servicebus",
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "azure", "servicebus", "messaging" });
    }
}

public class AzureOptions
{
    public string? ServiceBusConnectionString { get; set; }
    public bool EnableCommandRouting { get; set; } = true;
    public bool EnableEventRouting { get; set; } = true;
    public bool EnableCommandListener { get; set; } = true;
    public bool EnableEventListener { get; set; } = true;
}
