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
        return new WorkflowSpec<TWorkflow>(new SourceFlowModelBuilder().AddModule<TModule>().Build());
    }
}

/// <summary>
/// Given/When/Then for a generated DSL workflow (doc 15 §6.1 "generate specs"). Messages are given by type name and JSON
/// data and built with the module's JSON metadata, as the runtime does; the clock follows the timers.
/// </summary>
/// <typeparam name="TWorkflow">The generated workflow.</typeparam>
public sealed class WorkflowSpec<TWorkflow>
    where TWorkflow : class, IProcessManagerRuntime<TWorkflow, WorkflowInstanceId>, IWorkflowState<TWorkflow>
{
    private readonly RuntimeModel _model;
    private readonly List<(ICommand Command, MessageId? Id)> _sent = [];
    private TWorkflow _state = TWorkflow.Initial;
    private WorkflowInstanceId _id;
    private Reaction? _reaction;
    private WorkflowTimer? _timer;
    private DateTimeOffset _due;
    private int _messages;

    internal WorkflowSpec(RuntimeModel model)
    {
        _model = model;
    }

    /// <summary>The current state.</summary>
    public TWorkflow State => _state;

    /// <summary>The simulated time.</summary>
    public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every command sent so far, with its message id.</summary>
    public IReadOnlyList<(ICommand Command, MessageId? Id)> Sent => _sent;

    /// <summary>The last reaction, or null when the message was ignored.</summary>
    public Reaction? LastReaction => _reaction;

    /// <summary>The start message.</summary>
    public WorkflowSpec<TWorkflow> Start(string type, string json)
    {
        IMessage message = Message(type, json);
        if (!TWorkflow.TryCorrelate(message, out _id))
        {
            throw new SpecificationException($"{type} doesn't correlate to an instance of {TWorkflow.ProcessName}.");
        }

        return Deliver(message);
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

        ICommand command = _sent[^1].Command;
        MessageTypeInfo commandType = _model.Registry.Get(command.GetType());
        if (!_model.Registry.TryGet(CommandReplies.RejectedTypeName(commandType.Name), commandType.Version, out MessageTypeInfo? reply))
        {
            throw new SpecificationException($"{TWorkflow.ProcessName} doesn't handle rejections of {commandType.CloudEventType} (no 'rejected:' failure on it).");
        }

        var json = new JsonObject
        {
            ["command"] = JsonSerializer.SerializeToNode(command, _model.Registry.Options.GetTypeInfo(command.GetType())),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message ?? code },
        };
        return Deliver((IMessage)json.Deserialize(reply.JsonTypeInfo)!);
    }

    /// <summary>The pending timer fires (a timeout or the end of a retry delay); the clock moves to its due time.</summary>
    public WorkflowSpec<TWorkflow> FireTimer()
    {
        WorkflowTimer timer = _timer ?? throw new SpecificationException("No timer is pending.");
        _timer = null;
        if (_due > Now)
        {
            Now = _due;
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
        string[] sent = [.. (_reaction?.Commands ?? []).Select(c => _model.Registry.Get(c.GetType()).CloudEventType)];
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
        if (!CloudEvents.TryParseType(type, out string name, out int version) || !_model.Registry.TryGet(name, version, out MessageTypeInfo? info))
        {
            throw new SpecificationException($"'{type}' is not a registered message type.");
        }

        return (IMessage)JsonSerializer.Deserialize(json, info.JsonTypeInfo)!;
    }

    private WorkflowSpec<TWorkflow> Deliver(IMessage message)
    {
        if (_id.IsDefault)
        {
            throw new SpecificationException("Start the workflow first.");
        }

        var messageId = new MessageId(MessageIds.CreateVersion5(MessageIds.SourceFlowNamespace, "spec/" + (++_messages).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Reaction reaction = _state.React(message, new ReactionContext<WorkflowInstanceId>(_id, Now, messageId, _state.State is null) { Serializer = _model.Registry.Options });
        if (reaction.IsIgnored)
        {
            _reaction = null;
            return this;
        }

        _reaction = reaction;
        _state = (TWorkflow?)reaction.NewState ?? _state;
        for (int i = 0; i < reaction.Commands.Count; i++)
        {
            _sent.Add((reaction.Commands[i], reaction.CommandIds[i]));
        }

        foreach (ScheduledMessage scheduled in reaction.Scheduled)
        {
            if (scheduled.Message is WorkflowTimer timer)
            {
                _timer = timer;
                _due = scheduled.DeliverAt;
            }
        }

        return this;
    }
}
