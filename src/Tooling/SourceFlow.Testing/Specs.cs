namespace SourceFlow.Testing;

/// <summary>A specification failed.</summary>
public sealed class SpecificationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SpecificationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Given/When/Then for an aggregate's pure deciders (doc 11 §2): given past events, when a command is decided, then
/// expect events, a rejection or nothing. Runs the generated dispatch, so it tests exactly what production runs.
/// </summary>
/// <typeparam name="TAggregate">The aggregate.</typeparam>
/// <typeparam name="TId">Its id type.</typeparam>
public sealed class AggregateSpec<TAggregate, TId>
    where TAggregate : class, IAggregateRuntime<TAggregate, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    private readonly List<IEvent> _given = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private Decision? _decision;
    private TAggregate? _state;

    /// <summary>Past events.</summary>
    public AggregateSpec<TAggregate, TId> Given(params IEvent[] events)
    {
        _given.AddRange(events);
        return this;
    }

    /// <summary>Fixes the decision time.</summary>
    public AggregateSpec<TAggregate, TId> At(DateTimeOffset now)
    {
        _now = now;
        return this;
    }

    /// <summary>Decides a command against the folded state.</summary>
    public AggregateSpec<TAggregate, TId> When(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        TAggregate state = State;
        var context = new DecisionContext(_now, new StreamVersion(_given.Count), MessageId.Empty, CorrelationId.None, new Dictionary<string, string>());
        _decision = TAggregate.Decide(state, command, context);
        return this;
    }

    /// <summary>The state folded from the given events.</summary>
    public TAggregate State => _state ??= _given.Aggregate(TAggregate.Initial, (state, e) => state.Evolve(e));

    /// <summary>Expects exactly these events.</summary>
    public AggregateSpec<TAggregate, TId> Then(params IEvent[] expected)
    {
        Decision decision = Decided();
        if (decision.Kind != DecisionKind.Accepted)
        {
            throw new SpecificationException($"Expected events, but the decision was {decision}.");
        }

        IEvent[] actual = decision.Events.ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new SpecificationException($"Expected [{string.Join(", ", expected.Select(e => e.ToString()))}] but got [{string.Join(", ", actual.Select(e => e.ToString()))}].");
        }

        return this;
    }

    /// <summary>Expects a rejection with <paramref name="code"/>.</summary>
    public AggregateSpec<TAggregate, TId> ThenRejected(string code)
    {
        Decision decision = Decided();
        if (decision.Kind != DecisionKind.Rejected || decision.Error!.Code != code)
        {
            throw new SpecificationException($"Expected rejection '{code}', but the decision was {decision}.");
        }

        return this;
    }

    /// <summary>Expects no change.</summary>
    public AggregateSpec<TAggregate, TId> ThenNoOp()
    {
        Decision decision = Decided();
        if (decision.Kind != DecisionKind.NoOp)
        {
            throw new SpecificationException($"Expected NoOp, but the decision was {decision}.");
        }

        return this;
    }

    /// <summary>Expects the state after applying the produced events to satisfy <paramref name="assert"/>.</summary>
    public AggregateSpec<TAggregate, TId> ThenState(Func<TAggregate, bool> assert, string description = "state")
    {
        ArgumentNullException.ThrowIfNull(assert);
        TAggregate after = Decided().Events.ToArray().Aggregate(State, (state, e) => state.Evolve(e));
        if (!assert(after))
        {
            throw new SpecificationException($"Expected {description} to hold, but the state was {after}.");
        }

        return this;
    }

    private Decision Decided()
    {
        return _decision ?? throw new SpecificationException("Call When(command) first.");
    }
}

/// <summary>Given/When/Then for a process manager's pure reactions (doc 11 §2).</summary>
/// <typeparam name="TProcess">The process manager.</typeparam>
/// <typeparam name="TId">Its id type.</typeparam>
public sealed class ProcessManagerSpec<TProcess, TId>
    where TProcess : class, IProcessManagerRuntime<TProcess, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    private readonly TId _id;
    private TProcess _state = TProcess.Initial;
    private bool _started;
    private bool _completed;
    private Reaction? _reaction;
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a spec for one instance.</summary>
    public ProcessManagerSpec(TId id)
    {
        _id = id;
    }

    /// <summary>The current state.</summary>
    public TProcess State => _state;

    /// <summary>True when the instance completed.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Past messages, applied in order.</summary>
    public ProcessManagerSpec<TProcess, TId> Given(params IMessage[] messages)
    {
        foreach (IMessage message in messages)
        {
            Apply(message);
        }

        return this;
    }

    /// <summary>Moves the reaction clock forward.</summary>
    public ProcessManagerSpec<TProcess, TId> WhenTimeAdvances(TimeSpan by)
    {
        _now += by;
        return this;
    }

    /// <summary>Reacts to a message.</summary>
    public ProcessManagerSpec<TProcess, TId> When(IMessage message)
    {
        _reaction = Apply(message);
        return this;
    }

    /// <summary>Expects exactly these commands to be sent.</summary>
    public ProcessManagerSpec<TProcess, TId> ThenSends(params ICommand[] commands)
    {
        Reaction reaction = Reacted();
        if (!reaction.Commands.SequenceEqual(commands))
        {
            throw new SpecificationException($"Expected sends [{string.Join(", ", commands.Select(c => c.ToString()))}] but got [{string.Join(", ", reaction.Commands.Select(c => c.ToString()))}].");
        }

        return this;
    }

    /// <summary>Expects a message to be scheduled at <paramref name="at"/>.</summary>
    public ProcessManagerSpec<TProcess, TId> ThenSchedules(IMessage message, DateTimeOffset at)
    {
        if (!Reacted().Scheduled.Contains(new ScheduledMessage(message, at)))
        {
            throw new SpecificationException($"Expected {message} to be scheduled at {at:O}.");
        }

        return this;
    }

    /// <summary>Expects the instance to be complete.</summary>
    public ProcessManagerSpec<TProcess, TId> ThenCompleted()
    {
        if (!_completed)
        {
            throw new SpecificationException("Expected the process to be completed.");
        }

        return this;
    }

    /// <summary>Expects the message to be ignored.</summary>
    public ProcessManagerSpec<TProcess, TId> ThenIgnored()
    {
        if (!Reacted().IsIgnored)
        {
            throw new SpecificationException("Expected the message to be ignored.");
        }

        return this;
    }

    private Reaction Apply(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_completed)
        {
            return Reaction.Ignore;
        }

        Reaction reaction = _state.React(message, new ReactionContext<TId>(_id, _now, MessageId.Empty, !_started));
        if (!reaction.IsIgnored)
        {
            _started = true;
            _state = (TProcess?)reaction.NewState ?? _state;
            _completed = reaction.Completes;
        }

        return reaction;
    }

    private Reaction Reacted()
    {
        return _reaction ?? throw new SpecificationException("Call When(message) first.");
    }
}

/// <summary>Given/Then for a document projection (doc 11 §2).</summary>
/// <typeparam name="TProjection">The projection.</typeparam>
/// <typeparam name="TDocument">The document.</typeparam>
/// <typeparam name="TKey">The key.</typeparam>
public sealed class ProjectionSpec<TProjection, TDocument, TKey>
    where TProjection : class, IDocumentProjectionRuntime<TProjection, TDocument, TKey>, new()
    where TDocument : class
    where TKey : notnull
{
    private readonly TProjection _projection = new();
    private readonly Dictionary<string, TDocument> _documents = new(StringComparer.Ordinal);
    private long _position;

    /// <summary>Applies events from one stream, in order.</summary>
    public ProjectionSpec<TProjection, TDocument, TKey> Given(StreamId stream, params IEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        long version = 0;
        foreach (IEvent e in events)
        {
            version++;
            _position++;
            var context = new EventContext(MessageId.Empty, stream, new StreamVersion(version), new GlobalPosition((ulong)_position, 0),
                DateTimeOffset.UnixEpoch, CorrelationId.None, null, null);
            if (!TProjection.TryGetKey(e, context, out TKey key))
            {
                continue;
            }

            string id = TProjection.FormatKey(key);
            TDocument? next = _projection.Apply(e, _documents.GetValueOrDefault(id), context);
            if (next is null)
            {
                _documents.Remove(id);
            }
            else
            {
                _documents[id] = next;
            }
        }

        return this;
    }

    /// <summary>The document for <paramref name="key"/>, or null.</summary>
    public TDocument? Document(TKey key)
    {
        return _documents.GetValueOrDefault(TProjection.FormatKey(key));
    }

    /// <summary>Expects the document for <paramref name="key"/> to equal <paramref name="expected"/>.</summary>
    public ProjectionSpec<TProjection, TDocument, TKey> Then(TKey key, TDocument? expected)
    {
        TDocument? actual = Document(key);
        if (!Equals(actual, expected))
        {
            throw new SpecificationException($"Expected document {expected} but got {actual}.");
        }

        return this;
    }
}
