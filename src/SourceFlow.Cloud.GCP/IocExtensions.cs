using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using SourceFlow.Cloud.Configuration;
using SourceFlow.Cloud.GCP.Configuration;
using SourceFlow.Cloud.GCP.Infrastructure;
using SourceFlow.Cloud.GCP.Messaging.Commands;
using SourceFlow.Cloud.GCP.Messaging.Events;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;

namespace SourceFlow.Cloud.GCP;

public static class IocExtensions
{
    /// <summary>
    /// Registers SourceFlow Google Cloud services with Pub/Sub integration. Routing is configured
    /// exclusively through the fluent <see cref="BusConfigurationBuilder"/> — no appsettings routing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure GCP options (ProjectId is required).</param>
    /// <param name="configureBus">Action to configure bus routing.</param>
    /// <param name="configureIdempotency">Optional idempotency configuration. Defaults to the in-memory service.</param>
    /// <remarks>
    /// A command "queue" maps to a Pub/Sub topic plus a pull subscription; an event "topic" maps to a
    /// Pub/Sub topic plus a pull subscription per subscriber. The <see cref="GcpBusBootstrapper"/>
    /// provisions these at startup. Set <c>PUBSUB_EMULATOR_HOST</c> to target the Pub/Sub emulator.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.UseSourceFlowGcp(
    ///     options => { options.ProjectId = "my-project"; },
    ///     bus => bus
    ///         .Send.Command&lt;CreateOrderCommand&gt;(q =&gt; q.Queue("orders"))
    ///         .Raise.Event&lt;OrderCreatedEvent&gt;(t =&gt; t.Topic("order-events"))
    ///         .Listen.To.CommandQueue("orders")
    ///         .Subscribe.To.Topic("order-events"));
    /// </code>
    /// </example>
    public static void UseSourceFlowGcp(
        this IServiceCollection services,
        Action<GcpOptions> configureOptions,
        Action<BusConfigurationBuilder> configureBus,
        Action<IdempotencyConfigurationBuilder>? configureIdempotency = null)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);
        ArgumentNullException.ThrowIfNull(configureBus);

        // 1. Configure options
        var options = new GcpOptions();
        configureOptions(options);
        services.AddSingleton(options);

        // 2. Register Pub/Sub API clients (honour PUBSUB_EMULATOR_HOST when set)
        services.TryAddSingleton(_ => PubSubClientFactory.CreatePublisher());
        services.TryAddSingleton(_ => PubSubClientFactory.CreateSubscriber());

        // 3. Build and register BusConfiguration for all routing interfaces
        var busBuilder = new BusConfigurationBuilder();
        configureBus(busBuilder);
        var busConfiguration = busBuilder.Build();

        services.AddSingleton(busConfiguration);
        services.AddSingleton<ICommandRoutingConfiguration>(busConfiguration);
        services.AddSingleton<IEventRoutingConfiguration>(busConfiguration);
        services.AddSingleton<IBusBootstrapConfiguration>(busConfiguration);

        // 4. Register idempotency service
        if (configureIdempotency != null)
        {
            var idempotencyBuilder = new IdempotencyConfigurationBuilder();
            configureIdempotency(idempotencyBuilder);
            idempotencyBuilder.Build(services);
        }
        else
        {
            // In-memory idempotency must be a singleton so the dedup store persists across messages.
            services.TryAddSingleton<InMemoryIdempotencyService>();
            services.TryAddSingleton<IIdempotencyService>(sp => sp.GetRequiredService<InMemoryIdempotencyService>());
            services.AddHostedService<InMemoryIdempotencyCleanupService>();
        }

        // 5. Register GCP dispatchers
        services.AddScoped<ICommandDispatcher, PubSubCommandDispatcher>();
        services.AddSingleton<IEventDispatcher, PubSubEventDispatcher>();

        // 6. Register bootstrapper first so topics/subscriptions are resolved before listeners start
        services.AddHostedService<GcpBusBootstrapper>();

        // 7. Register listeners as hosted services
        if (options.EnableCommandListener)
            services.AddHostedService<PubSubCommandListener>();

        if (options.EnableEventListener)
            services.AddHostedService<PubSubEventListener>();

        // 8. Register health check
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, GcpHealthCheck>(
            provider => new GcpHealthCheck(
                provider.GetRequiredService<PublisherServiceApiClient>(),
                provider.GetRequiredService<GcpOptions>())));
    }
}
