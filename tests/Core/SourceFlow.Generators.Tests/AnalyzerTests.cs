using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace SourceFlow.Generators.Tests;

/// <summary>GEN-21..33 (analyzer rules SF0001–SF0009).</summary>
[Trait("Category", "Unit")]
public sealed class AnalyzerTests
{
    private const string Id = """
        [StronglyTypedId] public readonly partial record struct Aid(Guid Value);
        """;

    [Fact]
    [Trait("Id", "GEN-21")]
    [Trait("Covers", "ADR-005")]
    public void SF0001_MessageWithoutMessageType_ReportedOncePerType()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            public sealed partial record Missing(int X) : IEvent;
            public sealed partial record Missing;
            """);

        diagnostics.Count(d => d.Id == "SF0001").ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "GEN-22")]
    public void SF0001_AbstractMessage_Ignored()
    {
        Analyze("public abstract record Base : IEvent;").ShouldNotContain(d => d.Id == "SF0001");
    }

    [Fact]
    [Trait("Id", "GEN-23")]
    [Trait("Covers", "ADR-005")]
    public void SF0002_DuplicateNameAndVersion()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze("""
            [MessageType("dup.e", 1)] public sealed record A(int X) : IEvent;
            [MessageType("dup.e", 1)] public sealed record B(int X) : IEvent;
            [MessageType("dup.e", 2)] public sealed record C(int X) : IEvent;
            """);

        diagnostics.Count(d => d.Id == "SF0002").ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "GEN-24")]
    [Trait("Covers", "DM-R3")]
    public void SF0003_CommandWithoutIdMember()
    {
        Analyze(Id + """
            [MessageType("t.c", 1)] public sealed record C(int X) : ICommand<Agg>;
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid> { public static Agg Initial { get; } = new(); }
            """).ShouldContain(d => d.Id == "SF0003" && d.GetMessage().Contains("no member"));
    }

    [Fact]
    [Trait("Id", "GEN-25")]
    [Trait("Covers", "DM-R3")]
    public void SF0003_TwoIdMembersWithoutAggregateId()
    {
        ImmutableArray<Diagnostic> ambiguous = Analyze(Id + """
            [MessageType("t.c", 1)] public sealed record C(Aid A, Aid B) : ICommand<Agg>;
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid> { public static Agg Initial { get; } = new(); }
            """);
        ImmutableArray<Diagnostic> marked = Analyze(Id + """
            [MessageType("t.c", 1)] public sealed record C([property: AggregateId] Aid A, Aid B) : ICommand<Agg>;
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid> { public static Agg Initial { get; } = new(); }
            """);

        ambiguous.ShouldContain(d => d.Id == "SF0003");
        marked.ShouldNotContain(d => d.Id == "SF0003");
    }

    [Fact]
    [Trait("Id", "GEN-26")]
    [Trait("Covers", "ADR-003")]
    public void SF0004_WhenAssigningState_IsAnError()
    {
        Analyze(Id + """
            [MessageType("t.e", 1)] public sealed record E(Aid A) : IEvent;
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                private static int counter;
                public static Agg Initial { get; } = new();
                private Agg When(E e) { counter++; return this; }
            }
            """).ShouldContain(d => d.Id == "SF0004");
    }

    [Fact]
    [Trait("Id", "GEN-27")]
    public void SF0004_PureWithExpression_NoDiagnostic()
    {
        Analyze(Id + """
            [MessageType("t.e", 1)] public sealed record E(Aid A) : IEvent;
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public int Count { get; init; }
                private Agg When(E e) => this with { Count = Count + 1 };
            }
            """).ShouldNotContain(d => d.Id == "SF0004");
    }

    [Fact]
    [Trait("Id", "GEN-30")]
    [Trait("Covers", "ADR-003")]
    public void SF0007_AsyncDecide_IsAnError()
    {
        Analyze(Id + """
            [MessageType("t.c", 1)] public sealed record C(Aid A) : ICommand<Agg>;
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public System.Threading.Tasks.Task<Decision> Decide(C c, DecisionContext ctx) => System.Threading.Tasks.Task.FromResult(Decision.NoOp);
            }
            """).ShouldContain(d => d.Id == "SF0007");
    }

    [Fact]
    [Trait("Id", "GEN-31")]
    public void SF0008_NonRecordOrNonPartialAggregate()
    {
        Analyze(Id + """
            [Aggregate("agg")] public sealed class Agg : IAggregate<Agg, Aid> { public static Agg Initial { get; } = new(); }
            """).ShouldContain(d => d.Id == "SF0008");
    }

    [Fact]
    [Trait("Id", "GEN-32")]
    public void SF0008_SetAccessorAndNestedType()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + """
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public int Mutable { get; set; }
            }
            public static class Outer
            {
                [Aggregate("nested")] public sealed partial record Inner : IAggregate<Inner, Aid> { public static Inner Initial { get; } = new(); }
            }
            """);

        diagnostics.ShouldContain(d => d.Id == "SF0008" && d.GetMessage().Contains("Mutable"));
        diagnostics.ShouldContain(d => d.Id == "SF0008" && d.GetMessage().Contains("nested"));
    }

    [Fact]
    [Trait("Id", "GEN-33")]
    [Trait("Covers", "ADR-003")]
    public void SF0009_NonDeterministicApisInPureMethods()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + """
            [MessageType("t.c", 1)] public sealed record C(Aid A) : ICommand<Agg>;
            [MessageType("t.e", 1)] public sealed record E(Aid A, DateTime At) : IEvent;
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public Decision Decide(C c, DecisionContext ctx) => Decision.Accept(new E(c.A, DateTime.UtcNow));
                private Agg When(E e) => Guid.NewGuid() == Guid.Empty ? this : this with { };
            }
            public static class Elsewhere { public static DateTime Fine() => DateTime.UtcNow; }
            """);

        diagnostics.Count(d => d.Id == "SF0009").ShouldBe(2);
    }

    [Fact]
    public void BankSample_HasNoDiagnostics()
    {
        Analyze(Id).ShouldBeEmpty();
    }

    private static ImmutableArray<Diagnostic> Analyze(string body)
    {
        return GeneratorHarness.Analyze(Sources.Usings + "\nnamespace T;\n" + body);
    }
}
