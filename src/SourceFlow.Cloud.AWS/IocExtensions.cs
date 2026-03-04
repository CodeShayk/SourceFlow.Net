using Amazon.SQS;
using Amazon.SimpleNotificationService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using SourceFlow.Cloud.AWS.Configuration;
using SourceFlow.Cloud.AWS.Infrastructure;
using SourceFlow.Cloud.AWS.Messaging.Commands;
using SourceFlow.Cloud.AWS.Messaging.Events;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.AWS;

public static class IocExtensions
{
    /// <summary>
    /// Registers SourceFlow AWS services. Routing is configured exclusively through the
    /// fluent <see cref="BusConfigurationBuilder"/> — no appsettings routing is used.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configureOptions">Action to configure AWS options</param>
    /// <param name="configureBus">Action to configure bus routing</param>
    /// <param name="configureIdempotency">Optional action to configure idempotency service using fluent builder. If not provided, uses in-memory implementation.</param>
    /// <remarks>
    /// <para>By default, uses <see cref="InMemoryIdempotencyService"/> which is suitable for single-instance deployments.</para>
    /// <para>For multi-instance deployments, configure a SQL-based idempotency service using the fluent builder:</para>
    /// <code>
    /// services.UseSourceFlowAws(
    ///     options => { options.Region = RegionEndpoint.USEast1; },
    ///     bus => bus.Send.Command&lt;CreateOrderCommand&gt;(q => q.Queue("orders.fifo")),
    ///     idempotency => idempotency.UseEFIdempotency(connectionString));
    /// </code>
    /// <para>Alternatively, pre-register the idempotency service before calling UseSourceFlowAws:</para>
    /// <code>
    /// services.AddSourceFlowIdempotency(connectionString);
    /// services.UseSourceFlowAws(
    ///     options => { options.Region = RegionEndpoint.USEast1; },
    ///     bus => bus.Send.Command&lt;CreateOrderCommand&gt;(q => q.Queue("orders.fifo")));
    /// </code>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.UseSourceFlowAws(
    ///     options => { options.Region = RegionEndpoint.USEast1; },
    ///     bus => bus
    ///         .Send
    ///             .Command&lt;CreateOrderCommand&gt;(q =&gt; q.Queue("orders.fifo"))
    ///             .Command&lt;UpdateOrderCommand&gt;(q =&gt; q.Queue("orders.fifo"))
    ///         .Raise.Event&lt;OrderCreatedEvent&gt;(t =&gt; t.Topic("order-events"))
    ///         .Listen.To
    ///             .CommandQueue("orders.fifo")
    ///         .Subscribe.To
    ///             .Topic("order-events"),
    ///     idempotency => idempotency.UseEFIdempotency(connectionString));
    /// </code>
    /// </example>
    public static void UseSourceFlowAws(
        this IServiceCollection services,
        Action<AwsOptions> configureOptions,
        Action<BusConfigurationBuilder> configureBus,
        Action<IdempotencyConfigurationBuilder>? configureIdempotency = null)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        ArgumentNullException.ThrowIfNull(configureBus);

        // 1. Configure options
        var options = new AwsOptions();
        configureOptions(options);
        services.AddSingleton(options);

        // 2. Register AWS clients
        services.AddAWSService<IAmazonSQS>();
        services.AddAWSService<IAmazonSimpleNotificationService>();

        // 3. Build and register BusConfiguration as singleton for all routing interfaces
        var busBuilder = new BusConfigurationBuilder();
        configureBus(busBuilder);
        var busConfiguration = busBuilder.Build();

        services.AddSingleton(busConfiguration);
        services.AddSingleton<ICommandRoutingConfiguration>(busConfiguration);
        services.AddSingleton<IEventRoutingConfiguration>(busConfiguration);
        services.AddSingleton<IBusBootstrapConfiguration>(busConfiguration);

        // 4. Register idempotency service using fluent builder
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

        // 5. Register AWS dispatchers
        services.AddScoped<ICommandDispatcher, AwsSqsCommandDispatcher>();
        services.AddSingleton<IEventDispatcher, AwsSnsEventDispatcher>();

        // 6. Register bootstrapper first so queues/topics are resolved before listeners start
        services.AddHostedService<AwsBusBootstrapper>();

        // 7. Register AWS listeners as hosted services
        services.AddHostedService<AwsSqsCommandListener>();
        services.AddHostedService<AwsSnsEventListener>();

        // 8. Register health check
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, AwsHealthCheck>(
            provider => new AwsHealthCheck(
                provider.GetRequiredService<IAmazonSQS>(),
                provider.GetRequiredService<IAmazonSimpleNotificationService>(),
                provider.GetRequiredService<ICommandRoutingConfiguration>(),
                provider.GetRequiredService<IEventRoutingConfiguration>())));
    }
}
