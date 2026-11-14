using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SourceFlow.Testing;

namespace SourceFlow.Generators.Tests;

/// <summary>GEN-01..20, 37..40 (plan 05 §2).</summary>
[Trait("Category", "Unit")]
public sealed class GeneratorTests
{
    private static readonly Lazy<GeneratorRun> Demo = new(() => GeneratorHarness.Run(Sources.Account));

    [Fact]
    [Trait("Id", "GEN-01")]
    [Trait("Covers", "ADR-004")]
    public void Aggregate_EmitsRuntimeWithNameFromAttribute()
    {
        string source = Demo.Value.Source("Demo.Account.Aggregate.g.cs");

        source.ShouldContain("IAggregateRuntime<Account, global::Demo.AccountId>");
        source.ShouldContain("public const string AggregateName = \"demo-account\";");
        source.ShouldContain("SnapshotEvery => 50;");
    }

    [Fact]
    [Trait("Id", "GEN-02")]
    public void Aggregate_DecidedCommands_ListsEveryDecide()
    {
        Demo.Value.Source("Demo.Account.Aggregate.g.cs")
            .ShouldContain("__sourceFlowDecided = [typeof(global::Demo.Deposit), typeof(global::Demo.Open), typeof(global::Demo.Transfer)];");
    }

    [Fact]
    [Trait("Id", "GEN-03")]
    [Trait("Covers", "ADR-003")]
    public void Aggregate_DecideSwitch_DispatchesAndThrowsForUnknown()
    {
        string source = Demo.Value.Source("Demo.Account.Aggregate.g.cs");

        source.ShouldContain("global::Demo.Open c => state.Decide(c, context),");
        source.ShouldContain("_ => throw new global::SourceFlow.UnhandledMessageException(command.GetType()),");
    }

    [Fact]
    [Trait("Id", "GEN-04")]
    public void Aggregate_Evolve_CallsWhen_AndIsTolerant()
    {
        string source = Demo.Value.Source("Demo.Account.Aggregate.g.cs");

        source.ShouldContain("global::Demo.Opened e => When(e),");
        source.ShouldNotContain("global::Demo.Audited e");
        source.ShouldContain("_ => this,");
    }

    [Fact]
    [Trait("Id", "GEN-05")]
    [Trait("Covers", "DM-R3")]
    public void Aggregate_TargetOf_UsesTheSingleIdMember()
    {
        Demo.Value.Source("Demo.Account.Aggregate.g.cs").ShouldContain("global::Demo.Deposit c => c.AccountId,");
    }

    [Fact]
    [Trait("Id", "GEN-06")]
    [Trait("Covers", "DM-R3")]
    public void Aggregate_TargetOf_AggregateIdDisambiguates()
    {
        Demo.Value.Source("Demo.Account.Aggregate.g.cs").ShouldContain("global::Demo.Transfer c => c.From,");
    }

    [Fact]
    [Trait("Id", "GEN-07")]
    public void Aggregate_PrivateWhen_IsCallable_AndOutputCompiles()
    {
        Demo.Value.CompilationErrors().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "GEN-08")]
    public void Aggregate_GlobalNamespace_IsEmittedWithoutNamespaceBlock()
    {
        string source = Sources.Usings + """

            [StronglyTypedId] public readonly partial record struct Gid(Guid Value);
            [MessageType("g.do", 1)] public sealed record Do(Gid Id) : ICommand<G>;
            [Aggregate("g")] public sealed partial record G : IAggregate<G, Gid>
            {
                public static G Initial { get; } = new();
                public Decision Decide(Do c, DecisionContext ctx) => Decision.NoOp;
            }
            """;

        GeneratorRun run = GeneratorHarness.Run(source);

        run.Source("G.Aggregate.g.cs").ShouldNotContain("namespace ");
        run.CompilationErrors().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "GEN-09")]
    [Trait("Covers", "ADR-016")]
    public void Projection_EmitsApplySwitch_HandledEvents_AndVersion()
    {
        string source = Demo.Value.Source("Demo.SummaryProjection.Projection.g.cs");

        source.ShouldContain("ProjectionVersion => 3;");
        source.ShouldContain("global::Demo.Opened e => When(e, current, context),");
        source.ShouldContain("__sourceFlowHandled = [typeof(global::Demo.Deposited), typeof(global::Demo.Opened)];");
    }

    [Fact]
    [Trait("Id", "GEN-10")]
    public void Projection_KeyFromIdMember_AndFallbackToAggregateKey()
    {
        string source = Demo.Value.Source("Demo.SummaryProjection.Projection.g.cs");

        source.ShouldContain("case global::Demo.Opened e: key = e.AccountId; return true;");
        source.ShouldContain("default: return global::Demo.AccountId.TryParse(context.AggregateKey, out key);");
        source.ShouldContain("FormatKey(global::Demo.AccountId key) => key.ToStreamSegment();");
    }

    [Fact]
    [Trait("Id", "GEN-11")]
    [Trait("Covers", "ADR-020")]
    public void ProcessManager_ReactSwitch_DefaultsToIgnore()
    {
        string source = Demo.Value.Source("Demo.TransferProcess.ProcessManager.g.cs");

        source.ShouldContain("global::Demo.TransferStarted m => React(m, context),");
        source.ShouldContain("global::Demo.TransferTimedOut m => React(m, context),");
        source.ShouldContain("_ => global::SourceFlow.Reaction.Ignore,");
    }

    [Fact]
    [Trait("Id", "GEN-12")]
    [Trait("Covers", "ADR-020")]
    public void ProcessManager_CorrelateBy_IsUsedForStartingMessages()
    {
        string source = Demo.Value.Source("Demo.TransferProcess.ProcessManager.g.cs");

        source.ShouldContain("case global::Demo.TransferStarted m: id = CorrelateBy(m); return true;");
        source.ShouldNotContain("case global::Demo.Deposited m:");
    }

    [Fact]
    [Trait("Id", "GEN-13")]
    public void ProcessManager_CorrelateByText_IsParsedAsTheId()
    {
        GeneratorRun run = GeneratorHarness.Run(Sources.Usings + """

            namespace Text;

            [StronglyTypedId] public readonly partial record struct CaseId(Guid Value);
            [MessageType("text.opened", 1)] public sealed record CaseOpened(string Reference) : IEvent;
            [MessageType("text.closed", 1)] public sealed record CaseClosed(CaseId Id) : IEvent;

            [ProcessManager("case")]
            public sealed partial record CaseProcess : IProcessManager<CaseProcess, CaseId>
            {
                public static CaseProcess Initial { get; } = new();
                public static string CorrelateBy(CaseOpened e) => e.Reference;
                public static CaseId CorrelateBy(CaseClosed e) => e.Id;
                public Reaction React(CaseOpened e, ReactionContext<CaseId> ctx) => Reaction.Ignore;
                public Reaction React(CaseClosed e, ReactionContext<CaseId> ctx) => Reaction.Complete();
            }
            """);

        string source = run.Source("Text.CaseProcess.ProcessManager.g.cs");
        source.ShouldContain("case global::Text.CaseOpened m: return global::Text.CaseId.TryParse(CorrelateBy(m), out id);");
        source.ShouldContain("case global::Text.CaseClosed m: id = CorrelateBy(m); return true;");
        run.CompilationErrors().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "GEN-14")]
    [Trait("Covers", "ADR-005")]
    public void Module_RegistersMessagesSortedByName()
    {
        string source = Demo.Value.Source("Demo.DemoModule.Module.g.cs");

        int deposit = source.IndexOf("\"demo.deposit\"", StringComparison.Ordinal);
        int open = source.IndexOf("\"demo.open\"", StringComparison.Ordinal);
        deposit.ShouldBeGreaterThan(0);
        open.ShouldBeGreaterThan(deposit);
        source.ShouldContain("builder.AddMessage<global::Demo.TransferTimedOut>(\"demo.transfer-timeout\", 1);");
    }

    [Fact]
    [Trait("Id", "GEN-15")]
    [Trait("Covers", "ADR-004;GAP-16")]
    public void Module_RegistersEverythingInTheCompilation()
    {
        string source = Demo.Value.Source("Demo.DemoModule.Module.g.cs");

        source.ShouldContain("builder.AddAggregate<global::Demo.Account, global::Demo.AccountId>();");
        source.ShouldContain("builder.AddProjection<global::Demo.SummaryProjection, global::Demo.Summary, global::Demo.AccountId>();");
        source.ShouldContain("builder.AddProcessManager<global::Demo.TransferProcess, global::Demo.TransferId>();");
        source.ShouldContain("builder.AddEventHandler<global::Demo.AuditHandler, global::Demo.Audited>();");
        source.ShouldContain("builder.AddJsonConverter(new global::Demo.AccountId.SourceFlowJsonConverter());");
    }

    [Fact]
    [Trait("Id", "GEN-16")]
    [Trait("Covers", "DC-1")]
    public void Module_AddsJsonResolver_WhenContextGiven()
    {
        string source = Sources.Usings + """

            namespace M;
            [MessageType("m.e", 1)] public sealed record E(int X) : IEvent;
            [System.Text.Json.Serialization.JsonSerializable(typeof(E))]
            public sealed partial class Ctx : System.Text.Json.Serialization.JsonSerializerContext;
            [SourceFlowModule(typeof(Ctx))] public sealed partial class Mod;
            """;

        GeneratorHarness.Run(source).Source("M.Mod.Module.g.cs").ShouldContain("builder.AddJsonTypeInfoResolver(global::M.Ctx.Default);");
    }

    [Fact]
    [Trait("Id", "GEN-17")]
    public void NonPartialAggregate_IsNotRegistered()
    {
        string source = Sources.Usings + """

            namespace N;
            [StronglyTypedId] public readonly partial record struct Id(Guid Value);
            [Aggregate("n")] public sealed record NotPartial : IAggregate<NotPartial, Id> { public static NotPartial Initial { get; } = new(); }
            [SourceFlowModule] public sealed partial class Mod;
            """;

        GeneratorRun run = GeneratorHarness.Run(source);

        run.HasSource("N.NotPartial.Aggregate.g.cs").ShouldBeFalse();
        run.Source("N.Mod.Module.g.cs").ShouldNotContain("AddAggregate");
    }

    [Fact]
    [Trait("Id", "GEN-18")]
    [Trait("Covers", "ADR-015")]
    public void StronglyTypedId_GuidBacking_EmitsVersion7FactoryAndParsing()
    {
        string source = Demo.Value.Source("Demo.AccountId.Id.g.cs");

        source.ShouldContain("global::System.Guid.CreateVersion7((time ?? throw new global::System.ArgumentNullException(nameof(time))).GetUtcNow())");
        source.ShouldContain("public static bool TryParse(string? value, out AccountId id)");
        source.ShouldContain("ToString(\"N\"");
    }

    [Fact]
    [Trait("Id", "GEN-19")]
    [Trait("Covers", "ADR-015")]
    public void StronglyTypedId_JsonConverter_WritesScalar()
    {
        Demo.Value.Source("Demo.AccountId.Id.g.cs").ShouldContain("writer.WriteStringValue(value.ToString());");
    }

    [Fact]
    [Trait("Id", "GEN-20")]
    [Trait("Covers", "ADR-015")]
    public void StronglyTypedId_StringBacking_HonoursMaxLength()
    {
        string source = Sources.Usings + """

            namespace S;
            [StronglyTypedId(Backing = IdBacking.String, MaxLength = 12)] public readonly partial record struct Iban(string Value);
            """;

        GeneratorRun run = GeneratorHarness.Run(source);

        run.Source("S.Iban.Id.g.cs").ShouldContain("public const int MaxLength = 12;");
        run.CompilationErrors().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "GEN-78")]
    [Trait("Covers", "Q-B4")]
    public void DomainQuery_Policy_IsEmittedOnlyWhenDeclared()
    {
        GeneratorRun run = GeneratorHarness.Run(Sources.Usings + """
            namespace Q;
            public sealed record Row(string Id, decimal Balance);

            [DomainQuery("t.guarded", 1, Policy = "statements")]
            public sealed partial record Guarded(decimal Below) : IDomainQuery<Row>
            {
                public const string Sfql = "from readmodel \"r\" | where balance < @below";
            }

            [DomainQuery("t.open", 1)]
            public sealed partial record Open(decimal Below) : IDomainQuery<Row>
            {
                public const string Sfql = "from readmodel \"r\" | where balance < @below";
            }
            """);

        run.Source("Q.Guarded.DomainQuery.g.cs").ShouldContain("QueryPolicy => \"statements\";");
        run.Source("Q.Open.DomainQuery.g.cs").ShouldNotContain("QueryPolicy");
        run.CompilationErrors().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "GEN-37")]
    public void GeneratedSources_CompileWithWarningsAsErrors()
    {
        Compilation strict = Demo.Value.Output.WithOptions(((CSharpCompilationOptions)Demo.Value.Output.Options)
            .WithGeneralDiagnosticOption(ReportDiagnostic.Error)
            .WithNullableContextOptions(NullableContextOptions.Enable));

        strict.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "GEN-38")]
    [Trait("Covers", "ADR-004")]
    public void Incrementality_UnrelatedEdit_ProducesNoNewOutputs()
    {
        GeneratorRun first = Demo.Value;
        Compilation edited = first.Input.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Other { public static class Unrelated { public const int X = 1; } }", GeneratorHarness.ParseOptions, cancellationToken: TestContext.Current.CancellationToken));

        GeneratorDriverRunResult second = first.Driver.RunGenerators(edited, TestContext.Current.CancellationToken).GetRunResult();

        IEnumerable<IncrementalStepRunReason> reasons = second.Results[0].TrackedSteps
            .Where(step => step.Key is "Ids" or "Messages" or "Aggregates" or "Projections" or "ProcessManagers" or "Modules")
            .SelectMany(step => step.Value)
            .SelectMany(run => run.Outputs)
            .Select(output => output.Reason);
        reasons.ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged);
    }

    [Fact]
    [Trait("Id", "GEN-39")]
    public void Snapshot_BankSampleGeneratedOutput()
    {
        string root = RepositoryRoot();
        string[] sources = Directory.GetFiles(Path.Combine(root, "tests", "Fixtures", "Bank", "Bank.Accounts"), "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .Prepend("global using System;")
            .ToArray();

        GeneratorRun run = GeneratorHarness.Run(sources);
        string combined = string.Join("\n", run.Result.GeneratedTrees
            .OrderBy(t => Path.GetFileName(t.FilePath), StringComparer.Ordinal)
            .Select(t => "//// " + Path.GetFileName(t.FilePath) + "\n" + t.ToString()));

        Approvals.AssertMatches(combined, "BankAccounts.Generated");
    }

    [Theory]
    [Trait("Id", "GEN-40")]
    [InlineData("[Aggregate(\"x\")] public sealed partial record X : IAggregate<X, ")]
    [InlineData("[MessageType(] public sealed record M : IEvent;")]
    [InlineData("[ProcessManager(\"p\")] public sealed partial record P : IProcessManager<P, int> { public Reaction React(")]
    [InlineData("[Projection] public partial class Q : IDocumentProjection<")]
    [InlineData("[StronglyTypedId] public readonly partial record struct Bad(DateTime Value);")]
    public void Generator_DoesNotCrashOnIncompleteCode(string fragment)
    {
        GeneratorRun run = GeneratorHarness.Run(Sources.Usings + "\nnamespace F;\n" + fragment);

        run.Result.Results[0].Exception.ShouldBeNull();
    }

    private const string Ledger = Sources.Usings + """

        using System.Threading;
        using System.Threading.Tasks;
        namespace Ledgers;
        [StronglyTypedId] public readonly partial record struct LedgerId(Guid Value);
        [MessageType("ledger.opened", 1)] public sealed record Opened(LedgerId LedgerId) : IEvent;
        [MessageType("ledger.posted", 1)] public sealed record Posted(LedgerId LedgerId, decimal Amount) : IEvent;
        public sealed class LedgerDb { }

        [Projection("ledger-lines", Version = 2)]
        public sealed partial class LedgerProjection : ISessionProjection<LedgerDb>
        {
            public ValueTask When(Opened e, LedgerDb db, EventContext ctx, CancellationToken ct) => default;
            public Task When(Posted e, LedgerDb db, EventContext ctx, CancellationToken ct) => Task.CompletedTask;
            public ValueTask When(Posted e, LedgerDb db) => default;
            public int When(Opened e, LedgerDb db, EventContext ctx, CancellationToken ct, int extra) => extra;
        }

        [SourceFlowModule] public sealed partial class LedgerModule;
        """;

    [Fact]
    [Trait("Id", "GEN-41")]
    [Trait("Covers", "doc 05 8.1")]
    public void SessionProjection_EmitsAsyncDispatch_HandledEvents_AndModuleRegistration()
    {
        GeneratorRun run = GeneratorHarness.Run(Ledger);
        string source = run.Source("Ledgers.LedgerProjection.SessionProjection.g.cs");

        source.ShouldContain("ISessionProjectionRuntime<LedgerProjection, global::Ledgers.LedgerDb>");
        source.ShouldContain("ProjectionVersion => 2;");
        source.ShouldContain("__sourceFlowHandled = [typeof(global::Ledgers.Opened), typeof(global::Ledgers.Posted)];");
        source.ShouldContain("global::Ledgers.Opened e => When(e, unitOfWork, context, cancellationToken),");
        source.ShouldContain("global::Ledgers.Posted e => new global::System.Threading.Tasks.ValueTask(When(e, unitOfWork, context, cancellationToken)),");
        run.Source("Ledgers.LedgerModule.Module.g.cs").ShouldContain("builder.AddSessionProjection<global::Ledgers.LedgerProjection, global::Ledgers.LedgerDb>();");
        run.CompilationErrors().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "GEN-42")]
    public void SessionProjection_IsNotADocumentProjection()
    {
        GeneratorRun run = GeneratorHarness.Run(Ledger);

        run.HasSource("Ledgers.LedgerProjection.Projection.g.cs").ShouldBeFalse();
        run.Source("Ledgers.LedgerModule.Module.g.cs").ShouldNotContain("builder.AddProjection<");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SourceFlow.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
