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
    [Trait("Id", "GEN-29")]
    [Trait("Covers", "ADR-007")]
    public void SF0006_CommandWithoutDecideOrHandler()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + """
            [MessageType("t.decided", 1)] public sealed record Decided(Aid A) : ICommand<Agg>;
            [MessageType("t.handled", 1)] public sealed record Handled(Aid A) : ICommand<Agg>;
            [MessageType("t.orphan", 1)] public sealed record Orphan(Aid A) : ICommand<Agg>;
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public Decision Decide(Decided c, DecisionContext ctx) => Decision.NoOp;
            }
            public sealed class HandledHandler : ICommandHandler<Handled, Agg>
            {
                public System.Threading.Tasks.ValueTask<Decision> HandleAsync(Handled c, Agg s, DecisionContext ctx, System.Threading.CancellationToken ct) => default;
            }
            """);

        diagnostics.Where(d => d.Id == "SF0006").ShouldHaveSingleItem().GetMessage().ShouldContain("Orphan");
    }

    [Fact]
    [Trait("Id", "GEN-34")]
    [Trait("Covers", "DC-1")]
    public void SF0010_TypeMissingFromTheJsonContext()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + """
            [MessageType("t.listed", 1)] public sealed record Listed(Aid A) : IEvent;
            [MessageType("t.forgotten", 1)] public sealed record Forgotten(Aid A) : IEvent;
            [System.Text.Json.Serialization.JsonSerializable(typeof(Listed))]
            public sealed partial class Ctx : System.Text.Json.Serialization.JsonSerializerContext;
            [SourceFlowModule(typeof(Ctx))] public sealed partial class Mod;
            """);

        Diagnostic missing = diagnostics.Where(d => d.Id == "SF0010").ShouldHaveSingleItem();
        missing.GetMessage().ShouldContain("Forgotten");
        missing.Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    private const string Aggregate = """
        [MessageType("t.open", 1)] public sealed record Open(Aid A) : ICommand<Agg>;
        [MessageType("t.opened", 1)] public sealed record Opened(Aid A) : IEvent;
        [MessageType("t.noted", 1)] public sealed record Noted(Aid A) : IEvent;
        """;

    [Fact]
    [Trait("Id", "GEN-28")]
    public void SF0005_EventFromDecideWithoutWhen_IsAWarning()
    {
        ImmutableArray<Diagnostic> missing = Analyze(Id + Aggregate + """
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public Decision Decide(Open c, DecisionContext ctx) => Decision.Accept(new Opened(c.A), new Noted(c.A));
                private Agg When(Opened e) => this;
            }
            """);
        ImmutableArray<Diagnostic> complete = Analyze(Id + Aggregate + """
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public Decision Decide(Open c, DecisionContext ctx) => Decision.Accept(new Opened(c.A), new Noted(c.A));
                private Agg When(Opened e) => this;
                private Agg When(Noted e) => this;
            }
            """);

        Diagnostic warning = missing.Where(d => d.Id == "SF0005").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldContain("Noted");
        complete.ShouldNotContain(d => d.Id == "SF0005");
    }

    [Fact]
    [Trait("Id", "GEN-36")]
    [Trait("Covers", "ADR-007")]
    public void SF0014_CommandOwnedAndRouted_IsAnError()
    {
        const string Owned = """
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public Decision Decide(Open c, DecisionContext ctx) => Decision.Accept(new Opened(c.A));
                private Agg When(Opened e) => this;
            }
            [MessageType("t.remote", 1)] public sealed record Remote(Aid A) : ICommand<Other>;
            [Aggregate("other")] public sealed partial record Other : IAggregate<Other, Aid> { public static Other Initial { get; } = new(); }
            """;

        ImmutableArray<Diagnostic> conflict = Analyze(Id + Aggregate + Owned + """
            public static class Routes
            {
                public static void Configure(SourceFlow.Runtime.RoutingBuilder r) => r.Command<Open>().To("accounts");
            }
            """);
        ImmutableArray<Diagnostic> remoteOnly = Analyze(Id + Aggregate + Owned + """
            public static class Routes
            {
                public static void Configure(SourceFlow.Runtime.RoutingBuilder r) => r.Command<Remote>().To("other-service");
            }
            """);

        Diagnostic error = conflict.Where(d => d.Id == "SF0014").ShouldHaveSingleItem();
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.GetMessage().ShouldContain("Agg");
        remoteOnly.ShouldNotContain(d => d.Id == "SF0014");
    }

    [Fact]
    [Trait("Id", "GEN-47")]
    [Trait("Covers", "Q-R1")]
    public void SF0015_QueryServiceInAnAggregate_IsAnError()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + Aggregate + """
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                public static SourceFlow.Query.IQueryService? Queries { get; set; }
                public Decision Decide(Open c, DecisionContext ctx)
                {
                    _ = Queries!.ValidateAsync("from readmodel \"x\"");
                    return Decision.Accept(new Opened(c.A));
                }

                private Agg When(Opened e) => this;
            }
            """);

        Diagnostic error = diagnostics.Where(d => d.Id == "SF0015").ShouldHaveSingleItem();
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.GetMessage().ShouldContain("IQueryService");
    }

    [Fact]
    [Trait("Id", "GEN-48")]
    [Trait("Covers", "Q-R1")]
    public void SF0016_QueryServiceInACommandHandler_IsAWarning()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + Aggregate + """
            [Aggregate("agg")] public sealed partial record Agg : IAggregate<Agg, Aid>
            {
                public static Agg Initial { get; } = new();
                private Agg When(Opened e) => this;
            }

            public sealed class OpenHandler(SourceFlow.Query.IQueryService queries) : ICommandHandler<Open, Agg>
            {
                public async System.Threading.Tasks.ValueTask<Decision> HandleAsync(Open c, Agg state, DecisionContext ctx, System.Threading.CancellationToken ct)
                {
                    await queries.ValidateAsync("from readmodel \"x\"", cancellationToken: ct);
                    return Decision.Accept(new Opened(c.A));
                }
            }
            """);

        Diagnostic warning = diagnostics.Where(d => d.Id == "SF0016").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        diagnostics.ShouldNotContain(d => d.Id == "SF0015");
    }

    private const string DomainQueryResult = """
        public sealed record Row(string Id, decimal Balance);
        """;

    [Fact]
    [Trait("Id", "GEN-49")]
    [Trait("Covers", "Q-B4")]
    public void SF0018_AParameterWithoutAMember_IsAnError()
    {
        ImmutableArray<Diagnostic> bad = Analyze(DomainQueryResult + """
            [DomainQuery("t.low", 1)]
            public sealed partial record Low(decimal Below) : IDomainQuery<Row>
            {
                public const string Sfql = "from readmodel \"r\" | where balance < @below and holder <> @holder and note <> '@literal'";
            }
            """);
        ImmutableArray<Diagnostic> good = Analyze(DomainQueryResult + """
            [DomainQuery("t.low", 1)]
            public sealed partial record Low(decimal Below) : IDomainQuery<Row>
            {
                public const string Sfql = "from readmodel \"r\" | where balance < @below";
            }
            """);

        Diagnostic error = bad.Where(d => d.Id == "SF0018").ShouldHaveSingleItem();
        error.GetMessage().ShouldContain("@holder");
        good.ShouldNotContain(d => d.Id == "SF0018");
    }

    [Theory]
    [Trait("Id", "GEN-50")]
    [Trait("Covers", "Q-B4")]
    [InlineData("""[DomainQuery("t.q", 1)] public sealed partial record Q(int A) : IDomainQuery<Row>;""", "const string Sfql")]
    [InlineData("""[DomainQuery("t.q", 1)] public sealed record Q(int A) : IDomainQuery<Row> { public const string Sfql = "from readmodel \"r\""; }""", "partial")]
    [InlineData("""[DomainQuery("t.q", 1)] public sealed partial record Q(int A) { public const string Sfql = "from readmodel \"r\""; }""", "IDomainQuery")]
    public void SF0018_TheShapeOfADomainQuery(string source, string problem)
    {
        Analyze(DomainQueryResult + source).Where(d => d.Id == "SF0018").ShouldHaveSingleItem().GetMessage().ShouldContain(problem);
    }

    [Fact]
    [Trait("Id", "UT-PII-21")]
    [Trait("Covers", "doc 10 4")]
    public void SF0011_PersonalDataWithAnUnknownSubject_IsAnError()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + """
            [MessageType("t.registered", 1)] public sealed record Registered(Aid A, string Customer,
                [property: PersonalData(Subject = "Client")] string Name,
                [property: PersonalData(Subject = nameof(Customer))] string Email,
                [property: PersonalData] string Phone) : IEvent;
            """);

        Diagnostic error = diagnostics.Where(d => d.Id == "SF0011").ShouldHaveSingleItem();
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.GetMessage().ShouldContain("Registered.Name");
    }

    [Fact]
    [Trait("Id", "UT-PII-22")]
    [Trait("Covers", "doc 10 4")]
    public void SF0013_AsIsPublicationOfPersonalData_IsAWarning()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + """
            [MessageType("t.personal", 1)] public sealed record Personal(Aid A, [property: PersonalData] string Name) : IEvent;
            [MessageType("t.plain", 1)] public sealed record Plain(Aid A) : IEvent;
            public static class Routes
            {
                public static void Configure(SourceFlow.Runtime.RoutingBuilder r)
                {
                    r.Publish<Personal>().AsIs().To("people");
                    r.Publish<Plain>().AsIs().To("plain");
                }
            }
            """);

        Diagnostic warning = diagnostics.Where(d => d.Id == "SF0013").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldContain("Personal");
    }

    [Fact]
    [Trait("Id", "UT-PII-23")]
    [Trait("Covers", "doc 10 4")]
    public void SF0017_QueryablePersonalData_IsAWarning()
    {
        ImmutableArray<Diagnostic> diagnostics = Analyze(Id + """
            [MessageType("t.indexed", 1)] public sealed record Indexed(Aid A, [property: PersonalData, Queryable] string Email, [property: Queryable] string Country) : IEvent;
            """);

        Diagnostic warning = diagnostics.Where(d => d.Id == "SF0017").ShouldHaveSingleItem();
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldContain("Indexed.Email");
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
