namespace SourceFlow;

/// <summary>A process manager state type: a <c>sealed partial record</c> with pure <c>React</c> methods (ADR-020).</summary>
/// <typeparam name="TSelf">The process manager type.</typeparam>
/// <typeparam name="TId">The instance id type.</typeparam>
public interface IProcessManager<TSelf, TId>
    where TSelf : class, IProcessManager<TSelf, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    /// <summary>The state before the first message.</summary>
    static abstract TSelf Initial { get; }
}

/// <summary>The dispatch members the generator adds to a process manager. Not implemented by hand.</summary>
/// <typeparam name="TSelf">The process manager type.</typeparam>
/// <typeparam name="TId">The instance id type.</typeparam>
public interface IProcessManagerRuntime<TSelf, TId> : IProcessManager<TSelf, TId>
    where TSelf : class, IProcessManagerRuntime<TSelf, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    /// <summary>The process type name.</summary>
    static abstract string ProcessName { get; }

    /// <summary>Every message type with a <c>React</c> method.</summary>
    static abstract IReadOnlyList<Type> HandledMessages { get; }

    /// <summary>
    /// Correlates a message through its <c>CorrelateBy</c> method. Messages without one correlate by the envelope
    /// correlation id, which the runtime sets to the instance id on everything the instance sends.
    /// </summary>
    static abstract bool TryCorrelate(IMessage message, out TId id);

    /// <summary>Dispatches <paramref name="message"/> to the matching <c>React</c> method.</summary>
    Reaction React(IMessage message, ReactionContext<TId> context);
}

/// <summary>Inputs to a reaction, fixed for the whole reaction.</summary>
/// <typeparam name="TId">The instance id type.</typeparam>
/// <param name="Id">The process instance id.</param>
/// <param name="Now">The reaction time, from <see cref="TimeProvider"/>.</param>
/// <param name="MessageId">The id of the message being handled.</param>
/// <param name="IsNew">True when this message starts the instance.</param>
public readonly record struct ReactionContext<TId>(TId Id, DateTimeOffset Now, MessageId MessageId, bool IsNew)
    where TId : struct, IStronglyTypedId<TId>;

/// <summary>A message to deliver at a later time (a durable timer, doc 04 §8).</summary>
/// <param name="Message">The message, usually an <see cref="ITimeout"/>.</param>
/// <param name="DeliverAt">The earliest delivery time.</param>
public sealed record ScheduledMessage(IMessage Message, DateTimeOffset DeliverAt);

/// <summary>
/// The pure result of a process manager's <c>React</c>: commands to send, timers to schedule, a new state, or completion.
/// Entry points are static (<see cref="Send"/>, <see cref="Become"/>, <see cref="Complete"/>, <see cref="Ignore"/>);
/// further effects chain with the <c>And…</c> and <see cref="Schedule"/> instance methods (DC-3).
/// </summary>
public sealed class Reaction
{
    private static readonly Reaction IgnoreInstance = new([], [], null, completes: false, ignored: true);

    private readonly ICommand[] _commands;
    private readonly ScheduledMessage[] _scheduled;

    private Reaction(ICommand[] commands, ScheduledMessage[] scheduled, object? newState, bool completes, bool ignored)
    {
        _commands = commands;
        _scheduled = scheduled;
        NewState = newState;
        Completes = completes;
        IsIgnored = ignored;
    }

    /// <summary>Nothing happens; the state is unchanged.</summary>
    public static Reaction Ignore => IgnoreInstance;

    /// <summary>The commands to send, in order.</summary>
    public IReadOnlyList<ICommand> Commands => _commands;

    /// <summary>The messages to schedule.</summary>
    public IReadOnlyList<ScheduledMessage> Scheduled => _scheduled;

    /// <summary>The new state, or null to keep the current one.</summary>
    public object? NewState { get; }

    /// <summary>True when the instance completes.</summary>
    public bool Completes { get; }

    /// <summary>True for <see cref="Ignore"/>.</summary>
    public bool IsIgnored { get; }

    /// <summary>Sends a command.</summary>
    public static Reaction Send(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new Reaction([command], [], null, completes: false, ignored: false);
    }

    /// <summary>Moves to a new state.</summary>
    public static Reaction Become(object newState)
    {
        ArgumentNullException.ThrowIfNull(newState);
        return new Reaction([], [], newState, completes: false, ignored: false);
    }

    /// <summary>Completes the instance.</summary>
    public static Reaction Complete()
    {
        return new Reaction([], [], null, completes: true, ignored: false);
    }

    /// <summary>Also sends a command.</summary>
    public Reaction AndSend(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new Reaction([.. _commands, command], _scheduled, NewState, Completes, ignored: false);
    }

    /// <summary>Also schedules <paramref name="message"/> for delivery at <paramref name="deliverAt"/>.</summary>
    public Reaction Schedule(IMessage message, DateTimeOffset deliverAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new Reaction(_commands, [.. _scheduled, new ScheduledMessage(message, deliverAt)], NewState, Completes, ignored: false);
    }

    /// <summary>Also moves to a new state.</summary>
    public Reaction AndBecome(object newState)
    {
        ArgumentNullException.ThrowIfNull(newState);
        return new Reaction(_commands, _scheduled, newState, Completes, ignored: false);
    }

    /// <summary>Also completes the instance.</summary>
    public Reaction AndComplete()
    {
        return new Reaction(_commands, _scheduled, NewState, completes: true, ignored: false);
    }
}
