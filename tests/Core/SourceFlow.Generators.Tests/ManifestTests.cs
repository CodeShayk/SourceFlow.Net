using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SourceFlow.Testing;

namespace SourceFlow.Generators.Tests;

/// <summary>The static service manifest and SF0019 (doc 15 §3, WP-S.1).</summary>
[Trait("Category", "Unit")]
public sealed class ManifestTests
{
    private const string Decisions = Sources.Usings + """

        namespace Shop;

        [StronglyTypedId] public readonly partial record struct OrderId(Guid Value);

        [MessageType("shop.place", 1)] public sealed record Place(OrderId OrderId, decimal Total) : ICommand<Order>;
        [MessageType("shop.pay", 2)] public sealed record Pay(OrderId OrderId, decimal Amount) : ICommand<Order>;
        [MessageType("shop.ship", 1)] public sealed record Ship(OrderId OrderId) : ICommand<Order>;
        [MessageType("shop.cancel", 1)] public sealed record Cancel(OrderId OrderId) : ICommand<Order>;
        [MessageType("shop.placed", 1)] public sealed record Placed(OrderId OrderId, decimal Total) : IEvent;
        [MessageType("shop.flagged", 1)] public sealed record Flagged(OrderId OrderId) : IEvent;
        [MessageType("shop.paid", 1)] public sealed record Paid(OrderId OrderId) : IEvent;
        [MessageType("shop.shipped", 1)] public sealed record Shipped(OrderId OrderId) : IEvent;
        [MessageType("shop.cancelled", 1)] public sealed record Cancelled(OrderId OrderId) : IEvent;

        public static class OrderErrors
        {
            public const string TooLargeCode = "order.too_large";
            public static readonly Error Empty = new("order.empty", "The order is empty.");
            public static readonly Error TooLarge = new Error(TooLargeCode, "The order is too large.");
            public static Error Unpaid { get; } = new("order.unpaid", "Not paid.");
        }

        [Aggregate("shop-order")]
        public sealed partial record Order : IAggregate<Order, OrderId>
        {
            public static Order Initial { get; } = new();
            public int Stage { get; init; }

            public Decision Decide(Place c, DecisionContext ctx) => c.Total switch
            {
                <= 0 => Decision.Reject(OrderErrors.Empty),
                > 10_000 => Decision.Reject(OrderErrors.TooLarge),
                > 5_000 => Decision.Accept(new Placed(c.OrderId, c.Total), new Flagged(c.OrderId)),
                _ => Decision.Accept(new Placed(c.OrderId, c.Total)),
            };

            public Decision Decide(Pay c, DecisionContext ctx)
            {
                if (Stage == 0)
                {
                    return Decision.Reject(new Error("order.not_placed", "Not placed."));
                }

                return c.Amount > 0 ? Decision.Accept([new Paid(c.OrderId)]) : Decision.Reject(Error.InvalidCommand("amount"));
            }

            public Decision Decide(Ship c, DecisionContext ctx) =>
                Stage < 2 ? Decision.Reject(OrderErrors.Unpaid) : Decision.Accept(new Shipped(c.OrderId));

            public Decision Decide(Cancel c, DecisionContext ctx) =>
                Stage == 3 ? Decision.Reject(Error.VersionMismatch with { Message = "shipped" }) : Decision.Accept(new Cancelled(c.OrderId));

            private Order When(Placed e) => this with { Stage = 1 };
            private Order When(Paid e) => this with { Stage = 2 };
            private Order When(Shipped e) => this with { Stage = 3 };
        }
        """;

    private const string Opaque = Sources.Usings + """

        namespace Opaque;

        [StronglyTypedId] public readonly partial record struct ItemId(Guid Value);

        [MessageType("opaque.a", 1)] public sealed record A(ItemId ItemId) : ICommand<Item>;
        [MessageType("opaque.b", 1)] public sealed record B(ItemId ItemId, string Code) : ICommand<Item>;
        [MessageType("opaque.c", 1)] public sealed record C(ItemId ItemId) : ICommand<Item>;
        [MessageType("opaque.d", 1)] public sealed record D(ItemId ItemId) : ICommand<Item>;
        [MessageType("opaque.e", 1)] public sealed record E(ItemId ItemId) : ICommand<Item>;
        [MessageType("opaque.happened", 1)] public sealed record Happened(ItemId ItemId) : IEvent;

        [Aggregate("opaque-item")]
        public sealed partial record Item : IAggregate<Item, ItemId>
        {
            public static Item Initial { get; } = new();

            // Events built in a helper: unresolved.
            public Decision Decide(A c, DecisionContext ctx) => Decision.Accept(Make(c.ItemId));

            // A computed code: unresolved.
            public Decision Decide(B c, DecisionContext ctx) => Decision.Reject(new Error(c.Code, "computed"));

            // A decision from a helper: unresolved.
            public Decision Decide(C c, DecisionContext ctx) => Helper(c.ItemId);

            // Declared: the declarations fill the manifest and silence SF0019.
            [Emits(typeof(Happened))]
            public Decision Decide(D c, DecisionContext ctx) => Decision.Accept(Make(c.ItemId));

            [Emits(typeof(Happened))]
            [Rejects("opaque.declared")]
            public Decision Decide(E c, DecisionContext ctx) => Helper(c.ItemId);

            private static IEvent Make(ItemId id) => new Happened(id);
            private static Decision Helper(ItemId id) => Decision.Accept(new Happened(id));
            private Item When(Happened e) => this;
        }
        """;

    private const string Handled = Sources.Usings + """

        using System.Threading;
        using System.Threading.Tasks;
        namespace Handled;

        [StronglyTypedId] public readonly partial record struct CardId(string Value);

        [MessageType("card.charge", 1)]
        public sealed record Charge(CardId CardId, decimal Amount, [property: PersonalData] string Holder, string? Note, [property: System.Text.Json.Serialization.JsonPropertyName("ref")] int Reference, DateTimeOffset At, IReadOnlyList<string> Tags) : ICommand<Card>;
        [MessageType("card.charged", 1)] public sealed record Charged(CardId CardId, decimal Amount) : IEvent;

        [Aggregate("card")]
        public sealed partial record Card : IAggregate<Card, CardId>
        {
            public static Card Initial { get; } = new();
            private Card When(Charged e) => this;
        }

        public sealed class ChargeHandler : ICommandHandler<Charge, Card>
        {
            public ValueTask<Decision> HandleAsync(Charge command, Card state, DecisionContext context, CancellationToken cancellationToken) =>
                ValueTask.FromResult(command.Amount <= 0
                    ? Decision.Reject(new Error("card.amount_invalid", "Invalid amount."))
                    : Decision.Accept(new Charged(command.CardId, command.Amount)));
        }
        """;

    private const string Projections = Sources.Usings + """

        using System.Threading;
        using System.Threading.Tasks;
        namespace Views;

        [StronglyTypedId] public readonly partial record struct NoteId(Guid Value);
        [MessageType("note.written", 1)] public sealed record Written(NoteId NoteId) : IEvent;
        [MessageType("note.erased", 1)] public sealed record Erased(NoteId NoteId) : IEvent;
        public sealed record NoteView(NoteId Id);
        public sealed class Db { }

        [Projection("notes", Version = 4)]
        public sealed partial class Notes : IDocumentProjection<NoteView, NoteId>
        {
            public NoteView When(Written e, NoteView? current, EventContext ctx) => new(e.NoteId);
            public NoteView? When(Erased e, NoteView? current, EventContext ctx) => null;
        }

        [Projection("note-lines")]
        public sealed partial class Lines : ISessionProjection<Db>
        {
            public ValueTask When(Written e, Db db, EventContext ctx, CancellationToken ct) => default;
        }
        """;

    private static readonly Lazy<JsonObject> Demo = new(() => Manifest(GeneratorHarness.Run(Sources.Account)));

    [Fact]
    [Trait("Id", "UT-MAN-01")]
    [Trait("Covers", "WFD-11")]
    public void Manifest_ListsAggregatesCommandsAndHandledEvents()
    {
        JsonObject manifest = Demo.Value;

        manifest["manifestVersion"]!.GetValue<int>().ShouldBe(1);
        manifest["assembly"]!.GetValue<string>().ShouldBe("TestAssembly");
        manifest["sourceflowVersion"]!.GetValue<string>().ShouldBe(typeof(ICommand).Assembly.GetName().Version!.ToString(3));
        JsonObject account = Single(manifest["aggregates"]!, "demo-account");
        account["clrType"]!.GetValue<string>().ShouldBe("Demo.Account");
        Types(account["commands"]!).ShouldBe(["demo.deposit", "demo.open", "demo.transfer"]);
        Strings(account["events"]!).ShouldBe(["demo.deposited.v1", "demo.opened.v1"]);
        Strings(Command(account, "demo.open")["emits"]!).ShouldBe(["demo.opened.v1"]);
        Strings(Command(account, "demo.transfer")["emits"]!).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-MAN-02")]
    [Trait("Covers", "WFD-12")]
    public void Emits_FromEventsCreatedInDecisionAccept()
    {
        JsonObject order = Single(Manifest(GeneratorHarness.Run(Decisions))["aggregates"]!, "shop-order");

        Strings(Command(order, "shop.place")["emits"]!).ShouldBe(["shop.flagged.v1", "shop.placed.v1"]);   // params, switch arms
        Strings(Command(order, "shop.pay")["emits"]!).ShouldBe(["shop.paid.v1"]);                           // collection expression
        Strings(Command(order, "shop.ship")["emits"]!).ShouldBe(["shop.shipped.v1"]);                       // conditional
        Command(order, "shop.pay")["version"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    [Trait("Id", "UT-MAN-03")]
    [Trait("Covers", "WFD-12")]
    public void Rejects_ResolvedToConstantErrorCodes()
    {
        JsonObject order = Single(Manifest(GeneratorHarness.Run(Decisions))["aggregates"]!, "shop-order");

        Strings(Command(order, "shop.place")["rejects"]!).ShouldBe(["order.empty", "order.too_large"]);      // static fields, const code
        Strings(Command(order, "shop.pay")["rejects"]!).ShouldBe(["order.not_placed", "sourceflow.invalid_command"]);
        Strings(Command(order, "shop.ship")["rejects"]!).ShouldBe(["order.unpaid"]);                         // static property
        Strings(Command(order, "shop.cancel")["rejects"]!).ShouldBe(["sourceflow.version_mismatch"]);        // built-in, with-expression
        GeneratorHarness.Analyze(Decisions).Where(d => d.Id == "SF0019").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-MAN-04")]
    [Trait("Covers", "WFD-12")]
    public void SF0019_WarnsWhenAnalysisCannotDecide()
    {
        Diagnostic[] warnings = [.. GeneratorHarness.Analyze(Opaque).Where(d => d.Id == "SF0019").OrderBy(d => d.Location.SourceSpan.Start)];

        warnings.Length.ShouldBe(3);
        warnings.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Warning);
        warnings[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("'Item.Decide' for 'A'");
        warnings[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("[Emits(typeof(...))]");
        warnings[1].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("'B'");
        warnings[1].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("[Rejects(\"code\")]");
        warnings[2].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("'C'");
        warnings[2].GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("helper");
    }

    [Fact]
    [Trait("Id", "UT-MAN-05")]
    [Trait("Covers", "WFD-12")]
    public void EmitsAndRejectsAttributes_FillTheManifestAndSilenceSF0019()
    {
        JsonObject item = Single(Manifest(GeneratorHarness.Run(Opaque))["aggregates"]!, "opaque-item");

        Strings(Command(item, "opaque.d")["emits"]!).ShouldBe(["opaque.happened.v1"]);
        Strings(Command(item, "opaque.e")["emits"]!).ShouldBe(["opaque.happened.v1"]);
        Strings(Command(item, "opaque.e")["rejects"]!).ShouldBe(["opaque.declared"]);
        Strings(Command(item, "opaque.a")["emits"]!).ShouldBeEmpty();
        GeneratorHarness.Analyze(Opaque).Where(d => d.Id == "SF0019")
            .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
            .ShouldAllBe(m => !m.Contains("'D'") && !m.Contains("'E'"));
    }

    [Fact]
    [Trait("Id", "UT-MAN-06")]
    [Trait("Covers", "WFD-11")]
    public void ProcessManagers_ListHandlesSendsAndSchedules()
    {
        const string Source = Sources.Usings + """

            namespace Flow;

            [StronglyTypedId] public readonly partial record struct JobId(Guid Value);
            [StronglyTypedId] public readonly partial record struct RunId(Guid Value);
            [MessageType("job.run", 1)] public sealed record Run(RunId RunId) : ICommand<Runner>;
            [MessageType("job.stop", 1)] public sealed record Stop(RunId RunId) : ICommand<Runner>;
            [MessageType("job.started", 1)] public sealed record Started(JobId JobId, RunId RunId) : IEvent;
            [MessageType("job.expired", 1)] public sealed record Expired(JobId JobId) : ITimeout;
            [Aggregate("runner")]
            public sealed partial record Runner : IAggregate<Runner, RunId>
            {
                public static Runner Initial { get; } = new();
                public Decision Decide(Run c, DecisionContext ctx) => Decision.NoOp;
                public Decision Decide(Stop c, DecisionContext ctx) => Decision.NoOp;
            }

            [ProcessManager("job-flow")]
            public sealed partial record JobFlow : IProcessManager<JobFlow, JobId>
            {
                public static JobFlow Initial { get; } = new();
                public RunId Run { get; init; }
                public static JobId CorrelateBy(Started e) => e.JobId;
                public static JobId CorrelateBy(Expired e) => e.JobId;
                public Reaction React(Started e, ReactionContext<JobId> ctx) =>
                    Reaction.Send(new Run(e.RunId)).Schedule(new Expired(e.JobId), ctx.Now.AddMinutes(5)).AndBecome(this with { Run = e.RunId });
                public Reaction React(Expired e, ReactionContext<JobId> ctx) => Reaction.Send(new Stop(Run)).AndComplete();
                public Reaction React(CommandRejected<Run> e, ReactionContext<JobId> ctx) => Reaction.Complete();
            }
            """;

        JsonObject process = Single(Manifest(GeneratorHarness.Run(Source))["processManagers"]!, "job-flow");

        Strings(process["handles"]!).ShouldBe(["job.expired.v1", "job.run.rejected.v1", "job.started.v1"]);
        Strings(process["sends"]!).ShouldBe(["job.run.v1", "job.stop.v1"]);
        Strings(process["schedules"]!).ShouldBe(["job.expired.v1"]);
    }

    [Fact]
    [Trait("Id", "UT-MAN-07")]
    [Trait("Covers", "WFD-11")]
    public void Projections_ListNameVersionAndHandledEvents()
    {
        JsonNode projections = Manifest(GeneratorHarness.Run(Projections))["projections"]!;

        JsonObject notes = Single(projections, "notes");
        notes["version"]!.GetValue<int>().ShouldBe(4);
        Strings(notes["handles"]!).ShouldBe(["note.erased.v1", "note.written.v1"]);
        JsonObject lines = Single(projections, "note-lines");
        lines["version"]!.GetValue<int>().ShouldBe(1);
        Strings(lines["handles"]!).ShouldBe(["note.written.v1"]);
    }

    [Fact]
    [Trait("Id", "UT-MAN-08")]
    [Trait("Covers", "WFD-11")]
    public void CommandHandlers_JoinTheirAggregate_AndMessagesDescribeTheirFields()
    {
        JsonObject manifest = Manifest(GeneratorHarness.Run(Handled));

        JsonObject card = Single(manifest["aggregates"]!, "card");
        Strings(Command(card, "card.charge")["emits"]!).ShouldBe(["card.charged.v1"]);
        Strings(Command(card, "card.charge")["rejects"]!).ShouldBe(["card.amount_invalid"]);
        GeneratorHarness.Analyze(Handled).Where(d => d.Id == "SF0019").ShouldBeEmpty();

        JsonObject charge = manifest["messages"]!.AsArray().Select(m => m!.AsObject()).Single(m => m["type"]!.GetValue<string>() == "card.charge");
        charge["kind"]!.GetValue<string>().ShouldBe("command");
        charge["aggregate"]!.GetValue<string>().ShouldBe("card");
        string[] fields = [.. charge["fields"]!.AsArray().Select(f => f!.ToJsonString())];
        fields.ShouldBe(
        [
            """{"name":"cardId","type":"string"}""",
            """{"name":"amount","type":"number"}""",
            """{"name":"holder","type":"string","personalData":true}""",
            """{"name":"note","type":"string","nullable":true}""",
            """{"name":"ref","type":"integer"}""",
            """{"name":"at","type":"date-time"}""",
            """{"name":"tags","type":"array"}""",
        ]);
        manifest["messages"]!.AsArray().Single(m => m!["type"]!.GetValue<string>() == "card.charged")!["kind"]!.GetValue<string>().ShouldBe("event");
    }

    [Fact]
    [Trait("Id", "GEN-51")]
    [Trait("Covers", "WFD-11")]
    public void ManifestAttribute_IsEmittedAndCompiles_OnlyWhenThereIsSomethingToDescribe()
    {
        GeneratorRun run = GeneratorHarness.Run(Decisions);

        run.HasSource("SourceFlowManifest.g.cs").ShouldBeTrue();
        run.Source("SourceFlowManifest.g.cs").ShouldContain("[assembly: global::SourceFlow.SourceFlowManifestAttribute(");
        run.Output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree?.FilePath.EndsWith("SourceFlowManifest.g.cs", StringComparison.Ordinal) == true)
            .ShouldBeEmpty();
        GeneratorHarness.Run("namespace Empty { public static class Nothing { } }").HasSource("SourceFlowManifest.g.cs").ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "GEN-52")]
    [Trait("Covers", "WFD-11")]
    public void ManifestJson_Snapshot()
    {
        JsonObject manifest = Manifest(GeneratorHarness.Run(Sources.Account, Decisions));
        manifest["sourceflowVersion"] = "<version>";

        Approvals.AssertMatches(
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            "Manifest.Demo");
    }

    [Fact]
    [Trait("Id", "GEN-53")]
    [Trait("Covers", "ADR-004")]
    public void Incrementality_UnrelatedEdit_KeepsManifestStepsCached()
    {
        GeneratorRun first = GeneratorHarness.Run(Sources.Account, Decisions);
        Compilation edited = first.Input.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Other { public static class Unrelated { public const int X = 1; } }", GeneratorHarness.ParseOptions, cancellationToken: TestContext.Current.CancellationToken));

        GeneratorDriverRunResult second = first.Driver.RunGenerators(edited, TestContext.Current.CancellationToken).GetRunResult();

        second.Results[0].TrackedSteps
            .Where(step => step.Key is "ManifestMessages" or "ManifestAggregates" or "ManifestProcessManagers" or "ManifestProjections" or "ManifestAssembly")
            .SelectMany(step => step.Value)
            .SelectMany(run => run.Outputs)
            .Select(output => output.Reason)
            .ShouldAllBe(reason => reason == IncrementalStepRunReason.Cached || reason == IncrementalStepRunReason.Unchanged);
    }

    [Fact]
    [Trait("Id", "GEN-54")]
    [Trait("Covers", "WFD-NFR-3")]
    public void ManifestJson_IsDeterministic_RegardlessOfSourceOrder()
    {
        string forward = Json(GeneratorHarness.Run(Sources.Account, Decisions, Projections));
        string reversed = Json(GeneratorHarness.Run(Projections, Decisions, Sources.Account));

        reversed.ShouldBe(forward);
    }

    [Fact]
    [Trait("Id", "GEN-55")]
    [Trait("Covers", "WFD-12")]
    public void BankSample_ProducesExactEmitsAndRejects_WithNoSF0019()
    {
        string root = RepositoryRoot();
        string[] sources = new[] { "Bank.Contracts", "Bank.Accounts", "Bank.Transfers", "Bank.Notifications" }
            .SelectMany(project => Directory.GetFiles(Path.Combine(root, "samples", "Bank", project), "*.cs", SearchOption.TopDirectoryOnly))
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .Prepend("global using System;")
            .ToArray();

        GeneratorHarness.Analyze(sources).Where(d => d.Id == "SF0019").ShouldBeEmpty();
        JsonObject manifest = Manifest(GeneratorHarness.Run(sources));
        JsonObject account = Single(manifest["aggregates"]!, "bank-account");
        Strings(Command(account, "bank.account.open")["emits"]!).ShouldBe(["bank.account.opened.v1"]);
        Strings(Command(account, "bank.account.open")["rejects"]!).ShouldBe(["account.already_open", "account.initial_deposit_required"]);
        Strings(Command(account, "bank.account.withdraw")["emits"]!).ShouldBe(["bank.account.withdrawn.v1"]);
        Strings(Command(account, "bank.account.withdraw")["rejects"]!).ShouldBe(["account.amount_must_be_positive", "account.insufficient_funds", "account.not_open"]);
        Strings(Command(account, "bank.account.close")["rejects"]!).ShouldBe(["account.not_open"]);
        JsonObject transfer = Single(manifest["aggregates"]!, "bank-transfer");
        Strings(Command(transfer, "bank.transfer.request")["rejects"]!).ShouldBe(["transfer.amount_must_be_positive", "transfer.same_account"]);
        Strings(Single(manifest["processManagers"]!, "money-transfer")["sends"]!)
            .ShouldBe(["bank.account.deposit.v1", "bank.account.withdraw.v1", "bank.transfer.complete.v1", "bank.transfer.fail.v1"]);
    }

    internal static JsonObject Manifest(GeneratorRun run)
    {
        return JsonNode.Parse(Json(run))!.AsObject();
    }

    private static string Json(GeneratorRun run)
    {
        AttributeData attribute = run.Output.Assembly.GetAttributes().Single(a => a.AttributeClass?.Name == "SourceFlowManifestAttribute");
        return (string)attribute.ConstructorArguments[0].Value!;
    }

    private static JsonObject Single(JsonNode array, string name)
    {
        return array.AsArray().Select(n => n!.AsObject()).Single(n => n["name"]!.GetValue<string>() == name);
    }

    private static JsonObject Command(JsonObject aggregate, string type)
    {
        return aggregate["commands"]!.AsArray().Select(n => n!.AsObject()).Single(n => n["type"]!.GetValue<string>() == type);
    }

    private static string[] Types(JsonNode commands)
    {
        return [.. commands.AsArray().Select(n => n!["type"]!.GetValue<string>())];
    }

    private static string[] Strings(JsonNode array)
    {
        return [.. array.AsArray().Select(n => n!.GetValue<string>())];
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "SourceFlow.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Repository root not found.");
    }
}
