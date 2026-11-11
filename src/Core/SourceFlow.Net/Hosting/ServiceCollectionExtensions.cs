using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Transport;

namespace SourceFlow.Hosting;

/// <summary>Registers SourceFlow in a host.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a SourceFlow service. Modules, routing and projections are validated here and options at host start
    /// (fail fast, doc 01 §9).
    /// </summary>
    public static IServiceCollection AddSourceFlow(this IServiceCollection services, Action<SourceFlowBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SourceFlowBuilder(services);
        configure(builder);
        if (string.IsNullOrWhiteSpace(builder.ServiceName))
        {
            throw new InvalidOperationException("SourceFlowBuilder.ServiceName is required.");
        }

        RuntimeModel model = builder.Model.Build();
        RoutingTable routing = builder.RoutingRules.Build(builder.ServiceName, model);
        foreach (Type projection in builder.ProjectionModes.Keys)
        {
            if (!model.Projections.Any(p => p.ProjectionType == projection))
            {
                throw new InvalidOperationException($"Projection {projection.Name} is configured but not registered by any module.");
            }
        }

        builder.Model.RegisterServices(services);
        services.AddOptions<SourceFlowOptions>()
            .Configure(o =>
            {
                o.ServiceName = builder.ServiceName;
                foreach (Action<SourceFlowOptions> action in builder.OptionActions)
                {
                    action(o);
                }
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SourceFlowOptions>, SourceFlowOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SourceFlowOptions>, SourceFlowOptionsTimingValidator>());

        services.AddRedaction(builder.Redaction ?? (r => r.SetRedactor<Microsoft.Extensions.Compliance.Redaction.ErasingRedactor>(SourceFlowTaxonomy.All)));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.TryAddSingleton<IActorAccessor, NullActorAccessor>();
        services.TryAddSingleton<ITenantResolver, NullTenantResolver>();
        services.AddSingleton(model);
        services.AddSingleton(routing);
        services.AddSingleton(builder.ErrorRules);
        services.AddSingleton(new HostSettings(builder.Roles, builder.Provisioning));
        services.AddSingleton<SourceFlowMetrics>();
        services.AddSingleton<IMessageSerializer, SystemTextJsonMessageSerializer>();
        services.AddSingleton(new MessagePipeline(builder.Middleware));
        services.AddSingleton(builder.StorageFactory ?? throw new InvalidOperationException("No storage is configured. Call UseInMemoryStorage() or a storage package's Use* method."));
        services.AddSingleton(builder.TransportFactory ?? (sp => new Transport.InMemory.InMemoryBroker(sp.GetService<TimeProvider>()).CreateTransport()));
        services.AddSingleton(sp =>
        {
            var runtime = ActivatorUtilities.CreateInstance<SourceFlowRuntime>(sp);
            runtime.InlineProjections = [.. model.Projections.Where(p => builder.ProjectionModes.GetValueOrDefault(p.ProjectionType, ProjectionMode.Async) == ProjectionMode.Inline)];
            runtime.AsyncProjections = [.. model.Projections.Where(p => builder.ProjectionModes.GetValueOrDefault(p.ProjectionType, ProjectionMode.Async) == ProjectionMode.Async)];
            runtime.ProjectionPolicies = builder.ProjectionPolicies.ToDictionary();
            string[] missing = [.. model.Projections.Select(p => p.MissingDependency(sp)).OfType<string>()];
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, missing));
            }

            return runtime;
        });
        services.AddSingleton<CommandProcessor>();
        services.AddSingleton<ICommandSender, CommandSender>();
        services.AddSingleton<OutboxRelay>();
        services.AddSingleton<ReceiveEndpoint>();
        services.AddSingleton<SubscriptionEngine>();
        services.AddSingleton<IDeadLetterManager, DeadLetterManager>();
        services.AddSingleton<ISubscriptionAdmin, SubscriptionAdmin>();
        services.AddSingleton<PayloadRedaction>();
        services.AddSingleton<Query.IAggregateReader, Query.AggregateReader>();
        services.TryAddSingleton(Query.QueryPolicySettings.Default);
        services.AddSingleton<Query.IQueryService>(sp => new Query.QueryService(
            sp.GetRequiredService<SourceFlowRuntime>(),
            sp.GetServices<Query.IQueryEngine>(),
            sp.GetRequiredService<Query.QueryPolicySettings>()));
        services.AddSingleton(sp => new SourceFlowHost(
            sp.GetRequiredService<SourceFlowRuntime>(),
            sp.GetRequiredService<OutboxRelay>(),
            sp.GetRequiredService<ReceiveEndpoint>(),
            sp.GetRequiredService<SubscriptionEngine>(),
            sp.GetRequiredService<HostSettings>(),
            sp.GetService<Query.IQueryService>() as Query.QueryService));
        services.AddHostedService(sp => sp.GetRequiredService<SourceFlowHost>());
        return services;
    }

    /// <summary>Adds the SourceFlow readiness check (started, store and transport reachable).</summary>
    public static IHealthChecksBuilder AddSourceFlow(this IHealthChecksBuilder builder, string name = "sourceflow")
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name, sp => sp.GetRequiredService<SourceFlowHost>(), HealthStatus.Unhealthy, ["ready"]));
    }
}

internal sealed record HostSettings(SourceFlowRoles Roles, ProvisioningMode Provisioning);
