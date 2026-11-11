namespace SourceFlow;

/// <summary>An aggregate state type: a <c>sealed partial record</c> with pure <c>Decide</c> and <c>When</c> methods (ADR-003).</summary>
/// <typeparam name="TSelf">The aggregate type.</typeparam>
/// <typeparam name="TId">The aggregate's strongly typed id.</typeparam>
public interface IAggregate<TSelf, TId>
    where TSelf : class, IAggregate<TSelf, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    /// <summary>The state before the first event.</summary>
    static abstract TSelf Initial { get; }
}

/// <summary>The dispatch members the generator adds to an aggregate (doc 03 §4.2). Not implemented by hand.</summary>
/// <typeparam name="TSelf">The aggregate type.</typeparam>
/// <typeparam name="TId">The aggregate's strongly typed id.</typeparam>
public interface IAggregateRuntime<TSelf, TId> : IAggregate<TSelf, TId>
    where TSelf : class, IAggregateRuntime<TSelf, TId>
    where TId : struct, IStronglyTypedId<TId>
{
    /// <summary>The aggregate (stream type) name.</summary>
    static abstract string AggregateName { get; }

    /// <summary>Snapshot interval; 0 disables snapshots.</summary>
    static abstract int SnapshotEvery { get; }

    /// <summary>The snapshot schema version; bump it to invalidate snapshots when <c>When</c> logic changes.</summary>
    static abstract int SnapshotVersion { get; }

    /// <summary>Commands with a <c>Decide</c> method on the aggregate.</summary>
    static abstract IReadOnlyList<Type> DecidedCommands { get; }

    /// <summary>Every command type that targets this aggregate in the compilation.</summary>
    static abstract IReadOnlyList<Type> TargetingCommands { get; }

    /// <summary>Dispatches <paramref name="command"/> to the matching <c>Decide</c> method.</summary>
    static abstract Decision Decide(TSelf state, ICommand command, in DecisionContext context);

    /// <summary>Extracts the target aggregate id of <paramref name="command"/> (DM-R3).</summary>
    static abstract TId TargetOf(ICommand command);

    /// <summary>Applies an event; unknown events leave the state unchanged.</summary>
    TSelf Evolve(IEvent @event);
}

/// <summary>Decides a command when <c>Decide</c> needs I/O inputs. Must be read-only and safe to re-run (doc 03 §5.3).</summary>
/// <typeparam name="TCommand">The command type.</typeparam>
/// <typeparam name="TAggregate">The target aggregate type.</typeparam>
public interface ICommandHandler<in TCommand, TAggregate>
    where TCommand : ICommand<TAggregate>
    where TAggregate : class
{
    /// <summary>Returns the decision for <paramref name="command"/> against <paramref name="state"/>.</summary>
    ValueTask<Decision> HandleAsync(TCommand command, TAggregate state, DecisionContext context, CancellationToken cancellationToken);
}

/// <summary>Converts a stored older event version to the next version (doc 03 §2.1).</summary>
/// <typeparam name="TFrom">The older version.</typeparam>
/// <typeparam name="TTo">The newer version.</typeparam>
public interface IUpcaster<in TFrom, out TTo>
    where TFrom : class, IMessage
    where TTo : class, IMessage
{
    /// <summary>Upcasts one event.</summary>
    TTo Upcast(TFrom old);
}
