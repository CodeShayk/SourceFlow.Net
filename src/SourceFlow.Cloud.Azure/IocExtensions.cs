using Azure.Messaging.ServiceBus;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Cloud.Azure.Configuration;
using SourceFlow.Cloud.Azure.Infrastructure;
using SourceFlow.Cloud.Azure.Messaging.Commands;
using SourceFlow.Cloud.Azure.Messaging.Events;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure;

public static class AzureIocExtensions
{
    public static void UseSourceFlowAzure(
        this IServiceCollection services,
        Action<AzureOptions> configureOptions)
    {
        // 1. Configure options
        services.Configure(configureOptions);
        var options = new AzureOptions();
        configureOptions(options);

        // 2. Register Azure Service Bus client (singleton, thread-safe)
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();

            // Support both connection string and managed identity
            var connectionString = config["SourceFlow:Azure:ServiceBus:ConnectionString"];
            var fullyQualifiedNamespace = config["SourceFlow:Azure:ServiceBus:FullyQualifiedNamespace"];

            if (!string.IsNullOrEmpty(connectionString))
            {
                // Use connection string
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
                // Use managed identity with DefaultAzureCredential
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

        // 3. Register routing configurations
        services.AddSingleton<IAzureCommandRoutingConfiguration,
            ConfigurationBasedAzureCommandRouting>();
        services.AddSingleton<IAzureEventRoutingConfiguration,
            ConfigurationBasedAzureEventRouting>();

        // 4. Register Azure dispatchers
        services.AddScoped<ICommandDispatcher, AzureServiceBusCommandDispatcher>();
        services.AddSingleton<IEventDispatcher, AzureServiceBusEventDispatcher>();

        // 5. Register Azure listeners as hosted services
        if (options.EnableCommandListener)
            services.AddHostedService<AzureServiceBusCommandListener>();

        if (options.EnableEventListener)
            services.AddHostedService<AzureServiceBusEventListener>();

        // 6. Register health check
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