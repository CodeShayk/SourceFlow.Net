using System.Text.Json.Serialization;

namespace SourceFlow.Benchmarks;

[StronglyTypedId]
public readonly partial record struct BenchId(Guid Value);

[MessageType("bench.increment", 1)]
public sealed record Increment(BenchId Id) : ICommand<Counter>;

[MessageType("bench.probe", 1)]
public sealed record Probe(BenchId Id) : ICommand<Counter>;

[MessageType("bench.snap-increment", 1)]
public sealed record SnapIncrement(BenchId Id) : ICommand<SnapCounter>;

[MessageType("bench.snap-probe", 1)]
public sealed record SnapProbe(BenchId Id) : ICommand<SnapCounter>;

[MessageType("bench.incremented", 1)]
public sealed record Incremented(BenchId Id, long Count) : IEvent;

/// <summary>A counter without snapshots: loads fold the whole stream.</summary>
[Aggregate("bench-counter")]
public sealed partial record Counter : IAggregate<Counter, BenchId>
{
    public static Counter Initial { get; } = new();

    public long Count { get; init; }

    public Decision Decide(Increment c, DecisionContext ctx)
    {
        return Decision.Accept(new Incremented(c.Id, Count + 1));
    }

    // Load and decide without appending: isolates load cost (PERF-04).
    public Decision Decide(Probe c, DecisionContext ctx)
    {
        return Decision.NoOp;
    }

    private Counter When(Incremented e)
    {
        return this with { Count = e.Count };
    }
}

/// <summary>The same counter with a snapshot every 100 events.</summary>
[Aggregate("bench-snap", SnapshotEvery = 100)]
public sealed partial record SnapCounter : IAggregate<SnapCounter, BenchId>
{
    public static SnapCounter Initial { get; } = new();

    public long Count { get; init; }

    public Decision Decide(SnapIncrement c, DecisionContext ctx)
    {
        return Decision.Accept(new Incremented(c.Id, Count + 1));
    }

    public Decision Decide(SnapProbe c, DecisionContext ctx)
    {
        return Decision.NoOp;
    }

    private SnapCounter When(Incremented e)
    {
        return this with { Count = e.Count };
    }
}

public sealed record Total(BenchId Id, long Count);

/// <summary>A document projection for the rebuild and latency measurements (PERF-05, PERF-08).</summary>
[Projection("bench-totals")]
public sealed partial class TotalsProjection : IDocumentProjection<Total, BenchId>
{
    public Total When(Incremented e, Total? current, EventContext ctx)
    {
        return new Total(e.Id, e.Count);
    }
}

[JsonSerializable(typeof(Increment))]
[JsonSerializable(typeof(Probe))]
[JsonSerializable(typeof(SnapIncrement))]
[JsonSerializable(typeof(SnapProbe))]
[JsonSerializable(typeof(Incremented))]
[JsonSerializable(typeof(Counter))]
[JsonSerializable(typeof(SnapCounter))]
[JsonSerializable(typeof(Total))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class BenchJsonContext : JsonSerializerContext;

[SourceFlowModule(typeof(BenchJsonContext))]
public sealed partial class BenchModule;
