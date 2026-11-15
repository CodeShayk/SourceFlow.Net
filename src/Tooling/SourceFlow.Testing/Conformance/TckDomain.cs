using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using SourceFlow.Runtime;

namespace SourceFlow.Testing.Conformance;

// The transport conformance kit's end-to-end domain (TCK-18, 24, 25, 45): a tally that adds amounts, so the kit can run
// the real core (inbox, outbox, receive pipeline, dead-letter ingestor) over any transport.

/// <summary>A tally id.</summary>
[StronglyTypedId]
public readonly partial record struct TckTallyId(Guid Value);

/// <summary>Adds to a tally.</summary>
[MessageType("tck.tally.add", 1)]
public sealed record TckAdd(TckTallyId TallyId, int Amount) : ICommand<TckTally>;

/// <summary>An amount was added.</summary>
[MessageType("tck.tally.added", 1)]
public sealed record TckAdded(TckTallyId TallyId, int Amount, int Total) : IEvent;

/// <summary>The tally aggregate.</summary>
[Aggregate("tck-tally")]
public sealed partial record TckTally : IAggregate<TckTally, TckTallyId>
{
    /// <summary>The empty tally.</summary>
    public static TckTally Initial { get; } = new();

    /// <summary>The total.</summary>
    public int Total { get; init; }

    /// <summary>Adds the amount.</summary>
    public Decision Decide(TckAdd c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Decision.Accept(new TckAdded(c.TallyId, c.Amount, Total + c.Amount));
    }

    private TckTally When(TckAdded e)
    {
        return this with { Total = e.Total };
    }
}

/// <summary>Records which service handled which event (one entry per handling), so the kit can count side effects.</summary>
public sealed class TckObservations
{
    private readonly ConcurrentQueue<(string Service, MessageId EventId)> _handled = new();

    /// <summary>Every handling so far.</summary>
    public IReadOnlyList<(string Service, MessageId EventId)> Handled => [.. _handled];

    internal void Add(string service, MessageId eventId)
    {
        _handled.Enqueue((service, eventId));
    }
}

/// <summary>An event handler with a visible side effect (it records each handling).</summary>
public sealed class TckObserver(TckObservations observations, SourceFlowRuntime runtime) : IEventHandler<TckAdded>
{
    /// <inheritdoc />
    public ValueTask HandleAsync(TckAdded @event, EventContext context, CancellationToken cancellationToken)
    {
        observations.Add(runtime.ServiceName, context.EventId);
        return ValueTask.CompletedTask;
    }
}

/// <summary>The kit's module (generated registration).</summary>
[SourceFlowModule(typeof(TckJsonContext))]
public sealed partial class TckModule;

/// <summary>Source-generated JSON metadata of the kit's domain.</summary>
[JsonSerializable(typeof(TckAdd))]
[JsonSerializable(typeof(TckAdded))]
[JsonSerializable(typeof(TckTally))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class TckJsonContext : JsonSerializerContext;
