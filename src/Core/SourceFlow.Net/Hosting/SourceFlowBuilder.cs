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

    /// <summary>How transport resources are provisioned at startup (ADR-017).</summary>
    public ProvisioningMode Provisioning { get; set; } = ProvisioningMode.CreateIfMissing;

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

    internal Dictionary<Type, SubscriptionFailurePolicy> ProjectionPolicies { get; } = [];

    internal List<MiddlewareRegistration> Middleware { get; } = [];

    internal ErrorClassifier ErrorRules { get; } = new();

    internal List<Action<SourceFlowOptions>> OptionActions { get; } = [];

    internal Func<IServiceProvider, StoragePorts>? StorageFactory { get; private set; }

    internal Func<IServiceProvider, ITransport>? TransportFactory { get; private set; }

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
            return new StoragePorts(s, s, s, s, s, s, s, s, s, s) { Retention = s, Nodes = s, Schemas = s, DocumentAdmin = s, Aliases = s };
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
