using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Hosting;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Transport.InMemory;
using SourceFlow.Workflows;

namespace SourceFlow.Testing;

/// <summary>
/// The production process-manager path for the specs (doc 11 §2, CR-44): a runtime built from the module, in-memory
/// storage, and the process-manager runner. A message is correlated with <c>TryCorrelate</c> and the envelope correlation,
/// a workflow version starts only under the start rules, a completed instance ignores later messages, and the state is
/// written as JSON and read back between messages, as in a service.
/// </summary>
/// <remarks>
/// Two things differ from a service: commands without a local owner or a route go to a stand-in endpoint (routing is
/// deployment configuration a spec doesn't have), and a spec of a workflow version runs that version only, so the other
/// registered versions of the workflow don't take its instances.
/// </remarks>
internal sealed class ProcessSpecRuntime
{
    /// <summary>The endpoint that commands without a route are sent to.</summary>
    public const string Elsewhere = "spec-elsewhere";

    private readonly Type _processType;
    private readonly string _processName;
    private readonly List<Action<SourceFlowBuilder>> _modules = [];
    private Built? _built;

    private ProcessSpecRuntime(Type processType, string processName)
    {
        _processType = processType;
        _processName = processName;
    }

    public SourceFlowRuntime Runtime => Build().Runtime;

    public ProcessManagerDescriptor Process => Build().Process;

    public SpecClock Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>A runtime for the process manager <paramref name="processType"/> that <typeparamref name="TModule"/> registers.</summary>
    public static ProcessSpecRuntime Create<TModule>(Type processType, string processName)
        where TModule : ISourceFlowModule
    {
        var spec = new ProcessSpecRuntime(processType, processName);
        spec.AddModule<TModule>();
        return spec;
    }

    /// <summary>Registers another module, for example the contracts of the commands the process manager sends.</summary>
    public void AddModule<TModule>()
        where TModule : ISourceFlowModule
    {
        if (_built is not null)
        {
            throw new SpecificationException("Add modules before the first message.");
        }

        _modules.Add(sf => sf.AddModule<TModule>());
    }

    // Built on the first message, once every module is known.
    private Built Build()
    {
        if (_built is { } built)
        {
            return built;
        }

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSourceFlow(sf =>
        {
            sf.ServiceName = "spec";
            foreach (Action<SourceFlowBuilder> module in _modules)
            {
                module(sf);
            }

            sf.UseInMemoryStorage(new InMemoryStore(Clock));
            sf.UseInMemoryTransport(new InMemoryBroker(Clock));
            sf.Model.KeepProcessManagers(p => p.StateType == _processType || !OtherVersionOf(p, _processName));
        });

        SourceFlowRuntime runtime = services.BuildServiceProvider().GetRequiredService<SourceFlowRuntime>();
        runtime.UnroutedCommandEndpoint = Elsewhere;
        ProcessManagerDescriptor process = runtime.Model.ProcessManagers.FirstOrDefault(p => p.StateType == _processType)
            ?? throw new SpecificationException($"No registered module registers the process manager {_processType.Name}.");
        _built = new Built(runtime, process, new ProcessManagerRunner(runtime), runtime.Model.JsonTypeInfoFor(process.StateType));
        return _built;
    }

    private sealed record Built(SourceFlowRuntime Runtime, ProcessManagerDescriptor Process, ProcessManagerRunner Runner, JsonTypeInfo StateType);

    /// <summary>
    /// Delivers <paramref name="message"/> as the process manager's subscription or receive path would. Throws when
    /// production would never apply it to <paramref name="expectedInstance"/>: a type the process manager doesn't handle,
    /// a message that correlates to no instance, or to another one.
    /// </summary>
    public (string Instance, Reaction? Reaction) Deliver(IMessage message, CorrelationId correlation, MessageId messageId, MessageId? causation, string? expectedInstance)
    {
        ArgumentNullException.ThrowIfNull(message);
        string type = Runtime.Model.Registry.TryGet(message.GetType(), out MessageTypeInfo? info) ? info.CloudEventType : message.GetType().Name;
        if (!ProcessManagerRunner.Handles(Process, message.GetType()))
        {
            throw new SpecificationException($"{Process.Name} doesn't handle {type}: a service never delivers it to the process manager.");
        }

        MessageEnvelope envelope = Runtime.CreateEnvelope(message, messageId: messageId, correlation: correlation) with { CausationId = causation };
        ReactOutcome outcome = Run(async ct =>
        {
            await using IStorageSession session = await Runtime.Storage.Sessions.BeginAsync(MessageEnvelope.DefaultTenant, ct).ConfigureAwait(false);
            ReactOutcome result = await Build().Runner.ReactCoreAsync(session, Process, message, envelope, ct).ConfigureAwait(false);
            await session.CommitAsync(ct).ConfigureAwait(false);
            Runtime.Committed(session);
            return result;
        });

        if (!outcome.Correlated)
        {
            throw new SpecificationException($"{type} doesn't correlate to an instance of {Process.Name}: a service would drop it. " +
                "A starting message must carry the instance id (CorrelateBy, or the workflow's correlate path).");
        }

        if (expectedInstance is not null && outcome.InstanceId != expectedInstance)
        {
            throw new SpecificationException($"{type} correlates to instance '{outcome.InstanceId}' of {Process.Name}, not to this spec's '{expectedInstance}'.");
        }

        return (outcome.InstanceId!, outcome.Reaction);
    }

    /// <summary>The stored state of an instance, read back from its JSON, and whether it completed; null before it starts.</summary>
    public (object State, bool Completed)? Load(string instance)
    {
        ProcessState? stored = Run(async ct =>
        {
            await using IStorageSession session = await Runtime.Storage.Sessions.BeginAsync(MessageEnvelope.DefaultTenant, ct).ConfigureAwait(false);
            return await session.LoadProcessStateAsync(Process.Name, instance, ct).ConfigureAwait(false);
        });
        return stored is null ? null : (JsonSerializer.Deserialize(stored.State.Span, Build().StateType)!, stored.Completed);
    }

    // The in-memory store completes synchronously; the specs' API is synchronous.
    private static T Run<T>(Func<CancellationToken, Task<T>> work)
    {
        return Task.Run(() => work(CancellationToken.None)).GetAwaiter().GetResult();
    }

    // Another registered version of the same DSL workflow.
    private static bool OtherVersionOf(ProcessManagerDescriptor candidate, string processName)
    {
        return candidate.IsWorkflow
            && WorkflowRuntime.TryParseProcessName(candidate.Name, out string? name, out _)
            && WorkflowRuntime.TryParseProcessName(processName, out string? own, out _)
            && name == own;
    }
}

/// <summary>A clock the specs move forward.</summary>
internal sealed class SpecClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
