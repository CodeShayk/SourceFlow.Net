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
        return AddSourceFlow(services, configure, SourceFlowDesignTime.FromEnvironment());
    }

    /// <summary>
    /// Adds a SourceFlow service; with <paramref name="designTime"/>, in design-time mode (doc 15 §3.2): the manifest is
    /// written when the host is built, and the host's background service is not registered.
    /// </summary>
    internal static IServiceCollection AddSourceFlow(this IServiceCollection services, Action<SourceFlowBuilder> configure, DesignTimeExport? designTime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SourceFlowBuilder(services);
        configure(builder);
        if (string.IsNullOrWhiteSpace(builder.ServiceName))
        {
            throw new InvalidOperationException("SourceFlowBuilder.ServiceName is required.");
        }

        if (builder.SecuritySettings?.PersonalDataEncryption == true)
        {
            builder.Model.AddModule<Security.PersonalDataModule>();   // the PersonalDataErased system event (doc 10 §4)
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
        services.AddSingleton(new SourceFlowServiceName(builder.ServiceName));
        services.AddSingleton(builder.ErrorRules);
        ProvisioningMode? provisioning = builder.Provisioning;
        SourceFlowRoles roles = builder.Roles;
        services.AddSingleton(sp => new HostSettings(roles, provisioning ?? DefaultProvisioning(sp.GetService<IHostEnvironment>(), sp.GetRequiredService<ITransport>())));
        services.AddSingleton<SourceFlowMetrics>();
        services.AddSingleton<IMessageSerializer, SystemTextJsonMessageSerializer>();
        services.AddSingleton(new MessagePipeline(builder.Middleware));
        Func<IServiceProvider, StoragePorts> storage = builder.StorageFactory ?? throw new InvalidOperationException("No storage is configured. Call UseInMemoryStorage() or a storage package's Use* method.");
        if (builder.SecuritySettings?.KeyProvider is { } keyFactory)
        {
            services.AddSingleton(keyFactory);   // one provider instance for payload protection and personal data
        }

        if (builder.SecuritySettings?.PersonalDataEncryption == true)
        {
            // Crypto-shredding (doc 10 §4): [PersonalData] members are encrypted per subject in the store.
            _ = builder.SecuritySettings.KeyProvider
                ?? throw new InvalidOperationException("Personal data encryption needs a key provider: call UseAwsKms, UseAzureKeyVault, UseGoogleKms or UseKeyProvider in sf.Security(...).");
            services.AddSingleton(sp => new Security.PersonalDataProtector(
                sp.GetRequiredService<Security.IKeyProvider>(),
                sp.GetRequiredService<StoragePorts>().PersonalData ?? throw new InvalidOperationException("Personal data encryption needs a storage adapter that keeps data keys (IPersonalDataKeyStore)."),
                sp.GetRequiredService<RuntimeModel>(),
                sp.GetService<TimeProvider>()));
            services.AddSingleton<Security.IPersonalDataEraser>(sp => new Security.PersonalDataEraser(
                sp.GetRequiredService<SourceFlowRuntime>(), sp.GetRequiredService<Security.PersonalDataProtector>(), sp.GetRequiredService<StoragePorts>().PersonalData!));
        }

        if (builder.SecuritySettings?.Protection is { } protection)
        {
            // Payload protection (doc 10 §3): outbox rows for protected destinations are encrypted at commit.
            protection.Validate();
            _ = builder.SecuritySettings.KeyProvider
                ?? throw new InvalidOperationException("Payload protection needs a key provider: call UseAwsKms, UseAzureKeyVault, UseGoogleKms or UseKeyProvider in sf.Security(...).");
            services.AddSingleton(sp => new Security.PayloadProtector(sp.GetRequiredService<Security.IKeyProvider>(), protection, sp.GetService<TimeProvider>()));
            Func<IServiceProvider, StoragePorts> unprotected = storage;
            storage = sp =>
            {
                StoragePorts ports = unprotected(sp);
                ITransport transport = sp.GetRequiredService<ITransport>();
                return ports with { Sessions = new Security.ProtectingSessionFactory(ports.Sessions, sp.GetRequiredService<Security.PayloadProtector>(), () => transport.Capabilities.MaxBodyBytes) };
            };
        }

        services.AddSingleton(storage);
        services.AddSingleton(builder.TransportFactory ?? (sp => new Transport.InMemory.InMemoryBroker(sp.GetService<TimeProvider>()).CreateTransport()));
        services.AddSingleton(sp =>
        {
            var runtime = ActivatorUtilities.CreateInstance<SourceFlowRuntime>(sp);
            runtime.Faults = sp.GetService<IFaultInjector>();
            runtime.Protector = sp.GetService<Security.PayloadProtector>();
            runtime.PersonalData = sp.GetService<Security.PersonalDataProtector>();
            runtime.InlineProjections = [.. model.Projections.Where(p => builder.ProjectionModes.GetValueOrDefault(p.ProjectionType, ProjectionMode.Async) == ProjectionMode.Inline)];
            runtime.AsyncProjections = [.. model.Projections.Where(p => builder.ProjectionModes.GetValueOrDefault(p.ProjectionType, ProjectionMode.Async) == ProjectionMode.Async)];
            runtime.ProjectionPolicies = builder.ProjectionPolicies.ToDictionary();
            string[] missing = [.. model.Projections.Select(p => p.MissingDependency(sp)).OfType<string>()];
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, missing));
            }

            // Blue/green (doc 06 §6): document projections, and session projections whose store keeps generations apart.
            runtime.Generations.SetParticipants(runtime.AsyncProjections.Where(p => p is DocumentProjectionDescriptor || p.GenerationStore(sp) is not null));
            return runtime;
        });
        services.AddSingleton<IReadModelResolver>(sp => sp.GetRequiredService<SourceFlowRuntime>().Generations);
        services.TryAddScoped<Storage.ProjectionTarget>();
        services.AddSingleton<CommandProcessor>();
        services.AddSingleton<ICommandSender, CommandSender>();
        services.AddSingleton<OutboxRelay>();
        services.AddSingleton<ReceiveEndpoint>();
        services.AddSingleton<SubscriptionEngine>();
        services.AddSingleton<SourceFlowMonitor>();
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
            sp.GetRequiredService<SourceFlowMonitor>(),
            sp.GetService<Query.IQueryService>() as Query.QueryService));
        if (designTime is not null)
        {
            // Design-time mode: nothing starts; the hook exports when the host is built (WP-S.2).
            services.AddSingleton(designTime);
            DesignTimeHook.Install();
            return services;
        }

        services.AddHostedService(sp => sp.GetRequiredService<SourceFlowHost>());
        return services;
    }

    /// <summary>
    /// The provisioning mode when none is configured (ADR-017): CreateIfMissing in Development, for the in-memory transport
    /// (nothing outside the process to provision) and without a host environment; Validate in every other environment.
    /// </summary>
    public static ProvisioningMode DefaultProvisioning(IHostEnvironment? environment, ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return environment is null || environment.IsDevelopment() || transport is Transport.InMemory.InMemoryTransport
            ? ProvisioningMode.CreateIfMissing
            : ProvisioningMode.Validate;
    }

    /// <summary>
    /// Adds the SourceFlow health checks (doc 08 §5) as <c>{prefix}.ready</c>, <c>.store</c>, <c>.transport</c>,
    /// <c>.outbox</c>, <c>.subscriptions</c>, <c>.leases</c> and <c>.keys</c>. Ready, store and transport carry the <c>ready</c> tag;
    /// every check carries <c>sourceflow</c>. Map liveness without them, so a dependency outage does not restart the service:
    /// <c>app.MapHealthChecks("/health/live", new() { Predicate = _ =&gt; false })</c> and
    /// <c>app.MapHealthChecks("/health/ready", new() { Predicate = c =&gt; c.Tags.Contains("ready") })</c>.
    /// </summary>
    public static IHealthChecksBuilder AddSourceFlow(this IHealthChecksBuilder builder, string prefix = "sourceflow")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        foreach (string check in SourceFlowHealthChecks.All)
        {
            string[] tags = check is SourceFlowHealthChecks.Ready or SourceFlowHealthChecks.Store or SourceFlowHealthChecks.Transport
                ? [SourceFlowHealthChecks.Tag, SourceFlowHealthChecks.ReadyTag]
                : [SourceFlowHealthChecks.Tag];
            builder.Add(new HealthCheckRegistration($"{prefix}.{check}",
                sp => new SourceFlowHealthCheck(check, sp.GetRequiredService<SourceFlowHost>(), sp.GetRequiredService<SourceFlowMonitor>()),
                HealthStatus.Unhealthy, tags));
        }

        return builder;
    }
}

internal sealed record HostSettings(SourceFlowRoles Roles, ProvisioningMode Provisioning);
