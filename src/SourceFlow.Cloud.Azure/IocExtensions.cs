using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SourceFlow.Cloud.Azure.Infrastructure;
using SourceFlow.Cloud.Azure.Messaging.Commands;
using SourceFlow.Cloud.Azure.Messaging.Events;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.Azure;

public static class AzureIocExtensions
{
    /// <summary>
    /// Registers SourceFlow Azure services with Service Bus integration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Action to configure Azure options</param>
    /// <param name="configureBus">Action to configure bus routing</param>
    /// <param name="configureIdempotency">Optional action to configure idempotency service using fluent builder. If not provided, uses in-memory implementation.</param>
    /// <remarks>
    /// <para>By default, uses <see cref="InMemoryIdempotencyService"/> which is suitable for single-instance deployments.</para>
    /// <para>For multi-instance deployments, configure a SQL-based idempotency service using the fluent builder:</para>
    /// <code>
    /// services.UseSourceFlowAzure(
    ///     options => { options.FullyQualifiedNamespace = "myservicebus.servicebus.windows.net"; },
    ///     bus => bus.Send.Command&lt;CreateOrderCommand&gt;(q => q.Queue("orders")),
    ///     idempotency => idempotency.UseEFIdempotency(connectionString));
    /// </code>
    /// <para>Alternatively, pre-register the idempotency service before calling UseSourceFlowAzure:</para>
    /// <code>
    /// services.AddSourceFlowIdempotency(connectionString);
    /// services.UseSourceFlowAzure(
    ///     options => { options.FullyQualifiedNamespace = "myservicebus.servicebus.windows.net"; },
    ///     bus => bus.Send.Command&lt;CreateOrderCommand&gt;(q => q.Queue("orders")));
    /// </code>
    /// </remarks>
    public static void UseSourceFlowAzure(
        this IServiceCollection services,
        Action<AzureOptions> configureOptions,
        Action<BusConfigurationBuilder> configureBus,
        Action<IdempotencyConfigurationBuilder>? configureIdempotency = null)
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

        // 5. Register idempotency service using fluent builder
        if (configureIdempotency != null)
        {
            var idempotencyBuilder = new IdempotencyConfigurationBuilder();
            configureIdempotency(idempotencyBuilder);
            idempotencyBuilder.Build(services);
        }
        else
        {
            // Register in-memory idempotency service as default if not already registered
            services.TryAddScoped<IIdempotencyService, InMemoryIdempotencyService>();
        }

        // 6. Register bootstrapper as hosted service
        services.AddHostedService<AzureBusBootstrapper>();

        // 7. Register Azure dispatchers
        services.AddScoped<ICommandDispatcher, AzureServiceBusCommandDispatcher>();
        services.AddSingleton<IEventDispatcher, AzureServiceBusEventDispatcher>();

        // 8. Register Azure listeners as hosted services
        if (options.EnableCommandListener)
            services.AddHostedService<AzureServiceBusCommandListener>();

        if (options.EnableEventListener)
            services.AddHostedService<AzureServiceBusEventListener>();

        // 9. Register health check
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
