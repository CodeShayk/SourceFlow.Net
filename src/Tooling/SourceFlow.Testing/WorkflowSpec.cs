using System.Text.Json;
using System.Text.Json.Nodes;
using SourceFlow.Messaging;
using SourceFlow.Runtime;
using SourceFlow.Workflows;

namespace SourceFlow.Testing;

/// <summary>Creates <see cref="WorkflowSpec{TWorkflow}"/>s.</summary>
public static class WorkflowSpec
{
    /// <summary>A spec for a generated DSL workflow registered by <typeparamref name="TModule"/>.</summary>
    public static WorkflowSpec<TWorkflow> For<TWorkflow, TModule>()
        where TWorkflow : class, IProcessManagerRuntime<TWorkflow, WorkflowInstanceId>, IWorkflowState<TWorkflow>
        where TModule : ISourceFlowModule
    {
        return new WorkflowSpec<TWorkflow>(ProcessSpecRuntime.Create<TModule>(typeof(TWorkflow), TWorkflow.ProcessName));
    }
}

/// <summary>
/// Given/When/Then for a generated DSL workflow (doc 15 §6.1 "generate specs"). Messages are given by type name and JSON
/// data and built with the module's JSON metadata, as the runtime does; the clock follows the timers.
/// </summary>
/// <remarks>
/// Every message goes through the process-manager runner over in-memory storage (doc 11 §2, CR-44): the start message
/// must correlate through the workflow's correlate path, later messages correlate as replies to its commands (the
/// envelope correlation is the instance), the start rules apply, and the state is read back from its JSON each time. A
/// spec runs one workflow version: the module's other versions of the workflow are left out.
/// </remarks>
/// <typeparam name="TWorkflow">The generated workflow.</typeparam>
public sealed class WorkflowSpec<TWorkflow>
    where TWorkflow : class, IProcessManagerRuntime<TWorkflow, WorkflowInstanceId>, IWorkflowState<TWorkflow>
{
    private readonly ProcessSpecRuntime _spec;
    private readonly List<(ICommand Command, MessageId? Id)> _sent = [];
    private TWorkflow _state = TWorkflow.Initial;
    private WorkflowInstanceId _id;
    private string? _instance;
    private Reaction? _reaction;
    private readonly List<(WorkflowTimer Timer, DateTimeOffset Due)> _timers = [];
    private int _messages;

    internal WorkflowSpec(ProcessSpecRuntime spec)
    {
        _spec = spec;
    }

    private RuntimeModel Model => _spec.Runtime.Model;

    /// <summary>Registers another module before the start, for example the contracts of the commands the workflow sends.</summary>
    public WorkflowSpec<TWorkflow> WithModule<TModule>()
        where TModule : ISourceFlowModule
    {
        _spec.AddModule<TModule>();
        return this;
    }

    /// <summary>The current state.</summary>
    public TWorkflow State => _state;

    /// <summary>The simulated time.</summary>
    public DateTimeOffset Now
    {
        get => _spec.Clock.Now;
        private set => _spec.Clock.Now = value;
    }

    /// <summary>Every command sent so far, with its message id.</summary>
    public IReadOnlyList<(ICommand Command, MessageId? Id)> Sent => _sent;

    /// <summary>The last reaction, or null when the message was ignored.</summary>
    public Reaction? LastReaction => _reaction;

    /// <summary>
    /// Every timer scheduled and not fired yet, oldest first. Cancelled timers stay, as in production: the broker delivers
    /// them at their due time and the engine ignores them (doc 15 §5.3).
    /// </summary>
    public IReadOnlyList<WorkflowTimer> Timers => [.. _timers.Select(t => t.Timer)];

    /// <summary>The start message: it must correlate through the workflow's correlate path, as in a service.</summary>
    public WorkflowSpec<TWorkflow> Start(string type, string json)
    {
        if (_instance is not null)
        {
            throw new SpecificationException("The workflow has started already.");
        }

        return Deliver(Message(type, json), start: true);
    }

    /// <summary>The awaited success event (or any event) arrives.</summary>
    public WorkflowSpec<TWorkflow> Succeed(string type, string json)
    {
        return Deliver(Message(type, json));
    }

    /// <summary>A failure event arrives.</summary>
    public WorkflowSpec<TWorkflow> Fail(string type, string json)
    {
        return Deliver(Message(type, json));
    }

    /// <summary>The last command sent is rejected with <paramref name="code"/>.</summary>
    public WorkflowSpec<TWorkflow> Reject(string code, string? message = null)
    {
        if (_sent.Count == 0)
        {
            throw new SpecificationException("Nothing was sent to reject.");
        }

        return RejectAt(_sent.Count - 1, code, message);
    }

    /// <summary>
    /// The command at <paramref name="sentIndex"/> in <see cref="Sent"/> is rejected: for an earlier attempt's command, a
    /// late reply. The reply carries the command's id as its causation, as in production (doc 15 §5.5).
    /// </summary>
    public WorkflowSpec<TWorkflow> RejectAt(int sentIndex, string code, string? message = null)
    {
        if (sentIndex < 0 || sentIndex >= _sent.Count)
        {
            throw new SpecificationException($"There is no sent command #{sentIndex}; {_sent.Count} were sent.");
        }

        (ICommand command, MessageId? commandId) = _sent[sentIndex];
        MessageTypeInfo commandType = Model.Registry.Get(command.GetType());
        if (!Model.Registry.TryGet(CommandReplies.RejectedTypeName(commandType.Name), commandType.Version, out MessageTypeInfo? reply))
        {
            throw new SpecificationException($"{TWorkflow.ProcessName} doesn't handle rejections of {commandType.CloudEventType} (no 'rejected:' failure on it).");
        }

        var json = new JsonObject
        {
            ["command"] = JsonSerializer.SerializeToNode(command, Model.Registry.Options.GetTypeInfo(command.GetType())),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message ?? code },
        };
        return Deliver((IMessage)json.Deserialize(reply.JsonTypeInfo)!, causation: commandId);
    }

    /// <summary>The last timer scheduled fires (a timeout or the end of a retry delay); the clock moves to its due time.</summary>
    public WorkflowSpec<TWorkflow> FireTimer()
    {
        if (_timers.Count == 0)
        {
            throw new SpecificationException("No timer is pending.");
        }

        return Fire(_timers.Count - 1);
    }

    /// <summary>
    /// A specific scheduled timer fires, even one the workflow has since cancelled or replaced (a stale timer, delivered
    /// late): the clock moves to its due time if later. The latest timer with that identity is used.
    /// </summary>
    public WorkflowSpec<TWorkflow> FireTimer(string kind, string step, int attempt)
    {
        int index = _timers.FindLastIndex(t => t.Timer.Kind == kind && t.Timer.Step == step && t.Timer.Attempt == attempt);
        if (index < 0)
        {
            throw new SpecificationException($"No {kind} timer for {step}#{attempt} was scheduled; scheduled: {string.Join(", ", _timers.Select(t => t.Timer.Kind + " " + t.Timer.Step + "#" + t.Timer.Attempt))}.");
        }

        return Fire(index);
    }

    private WorkflowSpec<TWorkflow> Fire(int index)
    {
        (WorkflowTimer timer, DateTimeOffset due) = _timers[index];
        _timers.RemoveAt(index);
        if (due > Now)
        {
            Now = due;
        }

        return Deliver(timer);
    }

    /// <summary>An operator action (<see cref="WorkflowControl"/>).</summary>
    public WorkflowSpec<TWorkflow> Control(string action, string? step = null, string? reason = null, bool compensate = false)
    {
        return Deliver(new WorkflowControl(TWorkflow.ProcessName, _id.Value, action, step, reason, compensate));
    }

    /// <summary>Expects the last message to have sent exactly these command types (<c>{name}.v{version}</c>), in order.</summary>
    public WorkflowSpec<TWorkflow> ThenSends(params string[] types)
    {
        string[] sent = [.. (_reaction?.Commands ?? []).Select(c => Model.Registry.Get(c.GetType()).CloudEventType)];
        if (!sent.SequenceEqual(types))
        {
            throw new SpecificationException($"Expected sends [{string.Join(", ", types)}] but got [{string.Join(", ", sent)}].");
        }

        return this;
    }

    /// <summary>Expects the instance's status.</summary>
    public WorkflowSpec<TWorkflow> ThenStatus(WorkflowStatus status)
    {
        WorkflowStatus? actual = Status();
        if (actual != status)
        {
            throw new SpecificationException($"Expected {TWorkflow.ProcessName} to be {status} but it is {actual?.ToString() ?? "not started"}.");
        }

        return this;
    }

    /// <summary>The instance's status, or null before the start.</summary>
    public WorkflowStatus? Status()
    {
        if (_state.State is not { } state)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(state);
        return Enum.Parse<WorkflowStatus>(document.RootElement.GetProperty("status").GetString()!);
    }

    private IMessage Message(string type, string json)
    {
        if (!CloudEvents.TryParseType(type, out string name, out int version) || !Model.Registry.TryGet(name, version, out MessageTypeInfo? info))
        {
            throw new SpecificationException($"'{type}' is not a registered message type.");
        }

        return (IMessage)JsonSerializer.Deserialize(json, info.JsonTypeInfo)!;
    }

    private WorkflowSpec<TWorkflow> Deliver(IMessage message, MessageId? causation = null, bool start = false)
    {
        bool starting = _instance is null;
        if (starting != start)
        {
            throw new SpecificationException(starting ? "Start the workflow first." : "The workflow has started already.");
        }

        var messageId = new MessageId(MessageIds.CreateVersion5(MessageIds.SourceFlowNamespace, "spec/" + (++_messages).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        CorrelationId correlation = starting ? CorrelationId.None : new CorrelationId(_instance!);
        (string instance, Reaction? reaction) = _spec.Deliver(message, correlation, messageId, causation, _instance);
        if (starting)
        {
            _instance = instance;
            _id = WorkflowInstanceId.Parse(instance);
        }

        if (_spec.Load(instance) is { } stored)
        {
            _state = (TWorkflow)stored.State;
        }
        else if (starting)
        {
            _instance = null;   // it didn't start (for example start.when was false)
            _id = default;
        }

        _reaction = reaction;
        if (reaction is null)
        {
            return this;
        }

        for (int i = 0; i < reaction.Commands.Count; i++)
        {
            _sent.Add((reaction.Commands[i], reaction.CommandIds[i]));
        }

        foreach (ScheduledMessage scheduled in reaction.Scheduled)
        {
            if (scheduled.Message is WorkflowTimer timer)
            {
                _timers.Add((timer, scheduled.DeliverAt));
            }
        }

        return this;
    }
}
