using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Pipeline;
using SourceFlow.Runtime;
using SourceFlow.Storage.InMemory;
using SourceFlow.Transport;
using SourceFlow.Transport.InMemory;

namespace SourceFlow.Hosting;

/// <summary>Which background roles this instance runs (doc 01 §9). API-only instances send but do not consume.</summary>
[Flags]
public enum SourceFlowRoles
{
    /// <summary>Only sends commands (no background work).</summary>
    Api = 0,

    /// <summary>Receives from the service endpoint.</summary>
    Receiver = 1,

    /// <summary>Relays the outbox.</summary>
    Relay = 2,

    /// <summary>Runs subscriptions (projections, process managers, event handlers).</summary>
    Subscriptions = 4,

    /// <summary>Everything.</summary>
    All = Receiver | Relay | Subscriptions,
}

/// <summary>How a projection runs (ADR-016).</summary>
public enum ProjectionMode
{
    /// <summary>In the command transaction (read-your-writes).</summary>
    Inline = 0,

    /// <summary>In a subscription (eventually consistent, partitionable).</summary>
    Async = 1,
}

/// <summary>
/// Composes a SourceFlow service: modules, the storage and transport slots (extensibility SPIs), routing,
/// projections, middleware and error rules. Everything is validated when the host starts (fail fast).
/// </summary>
public sealed class SourceFlowBuilder
{
    internal SourceFlowBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The service name (envelope source, endpoint, inbox consumer). Required.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>The background roles of this instance (default: all).</summary>
    public SourceFlowRoles Roles { get; set; } = SourceFlowRoles.All;

    /// <summary>
    /// How transport resources are provisioned at startup (ADR-017, doc 07 §5). Null (the default) chooses by environment:
    /// <see cref="ProvisioningMode.CreateIfMissing"/> in Development, with the in-memory transport, or without a host
    /// environment; <see cref="ProvisioningMode.Validate"/> otherwise, so production needs describe permissions only and
    /// resources come from infrastructure as code (<c>dotnet sourceflow topology export</c>).
    /// </summary>
    public ProvisioningMode? Provisioning { get; set; }

    internal SourceFlowModelBuilder Model { get; } = new();

    internal RoutingBuilder RoutingRules { get; } = new();

    internal Dictionary<Type, ProjectionMode> ProjectionModes { get; } = [];

    internal Action<Microsoft.Extensions.Compliance.Redaction.IRedactionBuilder>? Redaction { get; private set; }

    /// <summary>
    /// Configures the redactors for the SourceFlow taxonomy (doc 10 §5). Without a call, <see cref="SourceFlowTaxonomy.PersonalData"/>
    /// and <see cref="SourceFlowTaxonomy.Secret"/> are erased (the Production default). Development can use an HMAC
    /// redactor (correlatable, not reversible): <c>r.SetHmacRedactor(o =&gt; { o.Key = …; o.KeyId = 1; }, SourceFlowTaxonomy.PersonalData)</c>.
    /// </summary>
    public SourceFlowBuilder UseRedaction(Action<Microsoft.Extensions.Compliance.Redaction.IRedactionBuilder> configure)
    {
        Redaction = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>
    /// Configures security (doc 10): the key provider and payload protection. Protected messages are encrypted in the
    /// outbox and on the wire, and decrypted on receive.
    /// </summary>
    public SourceFlowBuilder Security(Action<Security.SecurityBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        SecuritySettings ??= new Security.SecurityBuilder(Services);
        configure(SecuritySettings);
        return this;
    }

    internal Security.SecurityBuilder? SecuritySettings { get; private set; }

    /// <summary>
    /// Registers the v2 data migration (doc 12 §3): the v2 database and one <c>IV2Import</c> per entity type, run with
    /// <c>dotnet sourceflow migrate v2 plan|scaffold|import|archive|verify</c> inside the service, or through
    /// <see cref="Migration.V2Migration"/>.
    /// </summary>
    public SourceFlowBuilder MigrateFromV2(Action<Migration.V2MigrationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var settings = new Migration.V2MigrationBuilder();
        configure(settings);
        if (settings.Source is null)
        {
            throw new ArgumentException("MigrateFromV2 needs the v2 database: call From(new V2Source(…)).", nameof(configure));
        }

        Services.AddSingleton(sp => new Migration.V2Migration(settings, sp.GetRequiredService<SourceFlowRuntime>(), sp));
        return this;
    }

    internal Dictionary<Type, SubscriptionFailurePolicy> ProjectionPolicies { get; } = [];

    internal Dictionary<Type, int> ProjectionPartitions { get; } = [];

    internal List<MiddlewareRegistration> Middleware { get; } = [];

    internal ErrorClassifier ErrorRules { get; } = new();

    internal List<Action<SourceFlowOptions>> OptionActions { get; } = [];

    internal Func<IServiceProvider, StoragePorts>? StorageFactory { get; private set; }

    internal Func<IServiceProvider, ITransport>? TransportFactory { get; private set; }

    /// <summary>Sets <see cref="ServiceName"/>: the service's endpoint name, telemetry service and inbox consumer prefix.</summary>
    public SourceFlowBuilder WithServiceName(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ServiceName = serviceName;
        return this;
    }

    /// <summary>
    /// Sets <see cref="Roles"/>: what this node runs besides the API (receivers, the outbox relay, subscriptions). For
    /// example <see cref="SourceFlowRoles.Api"/> for a node that only accepts commands and queries.
    /// </summary>
    public SourceFlowBuilder WithRoles(SourceFlowRoles roles)
    {
        Roles = roles;
        return this;
    }

    /// <summary>Sets <see cref="Provisioning"/>: how transport resources are provisioned at startup (ADR-017).</summary>
    public SourceFlowBuilder WithProvisioning(ProvisioningMode provisioning)
    {
        Provisioning = provisioning;
        return this;
    }

    /// <summary>
    /// Adds the SourceFlow health checks (doc 08 §5) to the service's health checks: <c>{prefix}.ready</c>, <c>.store</c>,
    /// <c>.transport</c>, <c>.outbox</c>, <c>.subscriptions</c>, <c>.leases</c> and <c>.keys</c>; the first three carry the
    /// <c>ready</c> tag. Map them with <c>MapSourceFlowHealthChecks()</c> (SourceFlow.AspNetCore) or <c>MapHealthChecks</c>.
    /// </summary>
    public SourceFlowBuilder WithHealthChecks(string prefix = "sourceflow")
    {
        Services.AddHealthChecks().AddSourceFlow(prefix);
        return this;
    }

    /// <summary>
    /// Applies <paramref name="configure"/> when <paramref name="condition"/> holds, so a feature can be switched on from
    /// configuration without leaving the chain:
    /// <c>sf.When(configuration.GetValue&lt;bool&gt;("Features:QueryApi"), s =&gt; s.WithQueryApi())</c>.
    /// </summary>
    public SourceFlowBuilder When(bool condition, Action<SourceFlowBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (condition)
        {
            configure(this);
        }

        return this;
    }

    /// <summary>Adds a generated module (no assembly scanning).</summary>
    public SourceFlowBuilder AddModule<TModule>()
        where TModule : ISourceFlowModule
    {
        Model.AddModule<TModule>();
        return this;
    }

    /// <summary>Configures options.</summary>
    public SourceFlowBuilder Configure(Action<SourceFlowOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        OptionActions.Add(configure);
        return this;
    }

    /// <summary>Configures routing (doc 04 §3).</summary>
    public SourceFlowBuilder Routing(Action<RoutingBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(RoutingRules);
        return this;
    }

    /// <summary>Configures projection modes; unconfigured projections run async.</summary>
    public SourceFlowBuilder Projections(Action<ProjectionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new ProjectionsBuilder(this));
        return this;
    }

    /// <summary>Adds middleware for the given stages, outermost first (ADR-012).</summary>
    public SourceFlowBuilder UseMiddleware(Func<IServiceProvider, IMessageMiddleware> factory, PipelineStage stages = PipelineStage.All, Func<MessageContext, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Middleware.Add(new MiddlewareRegistration(factory, stages, filter));
        return this;
    }

    /// <summary>Changes how exceptions are classified (doc 04 §9.1).</summary>
    public SourceFlowBuilder Errors(Action<ErrorClassifier> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(ErrorRules);
        return this;
    }

    /// <summary>The persistence slot: registers the storage ports of an adapter.</summary>
    public SourceFlowBuilder UseStorage(Func<IServiceProvider, StoragePorts> factory)
    {
        StorageFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>The cloud/broker slot: registers a transport.</summary>
    public SourceFlowBuilder UseTransport(Func<IServiceProvider, ITransport> factory)
    {
        TransportFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>Uses the in-memory store (development and tests). Pass a store to share it.</summary>
    public SourceFlowBuilder UseInMemoryStorage(InMemoryStore? store = null)
    {
        return UseStorage(sp =>
        {
            InMemoryStore s = store ?? new InMemoryStore(sp.GetService<TimeProvider>());
            return new StoragePorts(s, s, s, s, s, s, s, s, s, s) { Retention = s, Nodes = s, Schemas = s, DocumentAdmin = s, Aliases = s, Monitor = s, PersonalData = s };
        });
    }

    /// <summary>Uses the in-memory broker. Pass a broker to connect several services in one process.</summary>
    public SourceFlowBuilder UseInMemoryTransport(InMemoryBroker? broker = null)
    {
        return UseTransport(sp => (broker ?? new InMemoryBroker(sp.GetService<TimeProvider>())).CreateTransport());
    }
}

/// <summary>Chooses projection modes.</summary>
public sealed class ProjectionsBuilder
{
    private readonly SourceFlowBuilder _builder;

    internal ProjectionsBuilder(SourceFlowBuilder builder)
    {
        _builder = builder;
    }

    /// <summary>Selects a registered projection.</summary>
    public ProjectionModeBuilder Add<TProjection>()
        where TProjection : class
    {
        _builder.ProjectionModes[typeof(TProjection)] = ProjectionMode.Async;
        return new ProjectionModeBuilder(_builder, typeof(TProjection));
    }

    /// <summary>
    /// Leaves out a projection a module registers, for example the previous version when this service carries a new version
    /// of it: the new version builds its own generation and takes over reads once it has caught up (doc 06 §6).
    /// </summary>
    public ProjectionsBuilder Remove<TProjection>()
        where TProjection : class
    {
        _builder.Model.RemoveProjection<TProjection>();
        _builder.ProjectionModes.Remove(typeof(TProjection));
        return this;
    }
}

/// <summary>Sets one projection's mode.</summary>
public sealed class ProjectionModeBuilder
{
    private readonly SourceFlowBuilder _builder;
    private readonly Type _projection;

    internal ProjectionModeBuilder(SourceFlowBuilder builder, Type projection)
    {
        _builder = builder;
        _projection = projection;
    }

    /// <summary>Runs in the command transaction (read-your-writes; same database required).</summary>
    public ProjectionModeBuilder Inline()
    {
        _builder.ProjectionModes[_projection] = ProjectionMode.Inline;
        return this;
    }

    /// <summary>Runs in a subscription.</summary>
    public ProjectionModeBuilder Async()
    {
        _builder.ProjectionModes[_projection] = ProjectionMode.Async;
        return this;
    }

    /// <summary>
    /// What an async projection does with an event that keeps failing (doc 06 §7): <see cref="SubscriptionFailurePolicy.Stop"/>
    /// (the default for projections) or <see cref="SubscriptionFailurePolicy.Park"/>.
    /// </summary>
    public ProjectionModeBuilder OnFailure(SubscriptionFailurePolicy policy)
    {
        _builder.ProjectionPolicies[_projection] = policy;
        return this;
    }

    /// <summary>
    /// The async projection's partition count (a power of two), overriding <see cref="Runtime.SourceFlowOptions.SubscriptionPartitions"/>
    /// for this subscription (doc 06 §3, CR-18). It must match the count stored for the subscription: a node configured
    /// otherwise refuses to run it; change it with <c>projection repartition</c>.
    /// </summary>
    public ProjectionModeBuilder Partitions(int partitions)
    {
        if (partitions < 1 || (partitions & (partitions - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitions), partitions, "The partition count must be a positive power of two (doc 06 §3).");
        }

        _builder.ProjectionPartitions[_projection] = partitions;
        return this;
    }
}

/// <summary>What a subscription does with an event that keeps failing (doc 06 §7, GAP-07).</summary>
public enum SubscriptionFailurePolicy
{
    /// <summary>
    /// Retry with backoff (1 s doubling to 5 min); the partition halts at the event while others continue, and
    /// <c>sourceflow.subscription.stalled</c> is 1 until the code is fixed or an operator skips the event (audited).
    /// The default for projections, where correctness matters more than freshness.
    /// </summary>
    Stop = 0,

    /// <summary>After the retry budget, park the event in dead letters and move past it. The default for process managers and handlers.</summary>
    Park = 1,
}
