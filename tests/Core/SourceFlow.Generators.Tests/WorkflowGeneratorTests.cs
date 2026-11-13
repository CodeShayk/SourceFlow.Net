using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using SourceFlow.Testing;

namespace SourceFlow.Generators.Tests;

/// <summary>GEN-56..64 (plan 10 §3.1): DSL workflows compiled by the generator (WP-S.5).</summary>
[Trait("Category", "Unit")]
public sealed partial class WorkflowGeneratorTests
{
    private const string Domain = Sources.Usings + """

        namespace Orders;

        [StronglyTypedId] public readonly partial record struct OrderId(Guid Value);
        [MessageType("orders.order-placed", 1)] public sealed record OrderPlaced(OrderId OrderId, decimal Total) : IEvent;
        [MessageType("orders.approve-order", 1)] public sealed record ApproveOrder(OrderId OrderId, string Note) : ICommand<Order>;
        [MessageType("orders.order-approved", 1)] public sealed record OrderApproved(OrderId OrderId) : IEvent;

        [Aggregate("order")]
        public sealed partial record Order : IAggregate<Order, OrderId>
        {
            public static Order Initial { get; } = new();
            public Decision Decide(ApproveOrder c, DecisionContext ctx) =>
                c.Note.Length == 0 ? Decision.Reject(new Error("orders.no_note", "A note is needed.")) : Decision.Accept(new OrderApproved(c.OrderId));
            private Order When(OrderPlaced e) => this;
            private Order When(OrderApproved e) => this;
        }

        [SourceFlowModule] public sealed partial class OrdersModule;
        """;

    private const string Hooks = """

        namespace Orders
        {
            public sealed partial record ApproveOrderWorkflowV1 : SourceFlow.Workflows.IWorkflowHooks
            {
                private static partial object? Note(System.Collections.Generic.IReadOnlyDictionary<string, object?> data) => "auto";
            }
        }
        """;

    private const string Workflow = """
        sourceflow.workflow: 1
        name: approve-order
        version: 1
        owner: orders
        start: { on: orders.order-placed.v1, correlate: data.orderId }
        data:
          orderId: { from: start.data.orderId }
        steps:
          - id: approve
            send: { command: orders.approve-order.v1, map: { orderId: $data.orderId, note: { $hook: Note } } }
            await:
              success: orders.order-approved.v1
              failure:
                - rejected: orders.no_note
                  then: manual
            timeout: { after: PT1H, then: manual }
        """;

    private static readonly Dictionary<string, string> Properties = new()
    {
        ["build_property.ProjectDir"] = "/repo/orders/",
        ["build_property.RootNamespace"] = "Orders",
    };

    private static GeneratorRun Run(string workflow = Workflow, string? extraSource = Hooks, Dictionary<string, string>? properties = null, params (string Path, string Text)[] more)
    {
        string[] sources = extraSource is null ? [Domain] : [Domain, extraSource];
        return GeneratorHarness.RunWithFiles(sources, [("/repo/orders/workflows/approve-order.sfflow.yaml", workflow), .. more], properties ?? Properties);
    }

    [Fact]
    [Trait("Id", "GEN-56")]
    [Trait("Covers", "WFD-29")]
    public void Workflow_GeneratesAProcessManager_Snapshot()
    {
        GeneratorRun run = Run();

        run.GeneratorDiagnostics.ShouldBeEmpty(string.Join("\n", run.GeneratorDiagnostics));
        string source = run.Source("ApproveOrderWorkflowV1.Workflow.g.cs");
        source.ShouldContain("[global::SourceFlow.ProcessManager(\"approve-order@1\")]");
        source.ShouldContain("public sealed partial record ApproveOrderWorkflowV1 : global::SourceFlow.IProcessManagerRuntime<ApproveOrderWorkflowV1, global::SourceFlow.Workflows.WorkflowInstanceId>");
        source.ShouldContain("global::SourceFlow.CommandRejected<global::Orders.ApproveOrder> m => Process.Rejected(this, \"orders.approve-order.v1\", m.Error.Code, m.Error.Message, context),");
        Approvals.AssertMatches(source, "Workflow.Generated");
        run.Output.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree?.FilePath.Contains("Workflow", StringComparison.Ordinal) == true)
            .ShouldBeEmpty("the generated workflow and the injected engine compile");
    }

    [Fact]
    [Trait("Id", "GEN-57")]
    [Trait("Covers", "WFD-28")]
    public void ValidationErrors_AreSF0020_AtTheYamlPosition()
    {
        string broken = Workflow
            .Replace("    timeout: { after: PT1H, then: manual }", "    retries: 3", StringComparison.Ordinal)
            .Replace("then: manual\n", "then: { goto: nowhere }\n", StringComparison.Ordinal);

        GeneratorRun run = Run(broken);

        Diagnostic[] errors = [.. run.GeneratorDiagnostics.Where(d => d.Id == "SF0020").OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)];
        errors.Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Split(':')[0]).ShouldBe(["WF-V06", "WF-V11", "WF-S02"]);
        FileLinePositionSpan unknown = errors[2].Location.GetLineSpan();
        unknown.Path.ShouldBe("/repo/orders/workflows/approve-order.sfflow.yaml");
        (unknown.StartLinePosition.Line + 1, unknown.StartLinePosition.Character + 1).ShouldBe((16, 5));
        errors.ShouldAllBe(d => d.Severity == DiagnosticSeverity.Error);
        run.HasSource("ApproveOrderWorkflowV1.Workflow.g.cs").ShouldBeFalse("an invalid workflow generates nothing");

        GeneratorRun warned = Run(Workflow.Replace("  orderId: { from: start.data.orderId }", "  orderId: { from: start.data.orderId }\n  noted: { type: string, personal: false }", StringComparison.Ordinal));
        warned.GeneratorDiagnostics.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "GEN-58")]
    [Trait("Covers", "WFD-29")]
    public void MissingHook_IsSF0021_AndImplementedHooksAreDispatched()
    {
        GeneratorRun missing = Run(extraSource: null);

        Diagnostic error = missing.GeneratorDiagnostics.Single(d => d.Id == "SF0021");
        error.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ShouldContain("'Note'");
        (error.Location.GetLineSpan().StartLinePosition.Line + 1).ShouldBe(10);
        missing.HasSource("ApproveOrderWorkflowV1.Workflow.g.cs").ShouldBeFalse();

        string source = Run().Source("ApproveOrderWorkflowV1.Workflow.g.cs");
        source.ShouldContain("\"Note\" => Note(data),");
        source.ShouldContain("private static partial object? Note(global::System.Collections.Generic.IReadOnlyDictionary<string, object?> data);");
    }

    [Fact]
    [Trait("Id", "GEN-59")]
    [Trait("Covers", "WFD-29")]
    public void BuildProps_AddWorkflowsAsAdditionalFiles()
    {
        XDocument props = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Core", "SourceFlow.Net", "build", "SourceFlow.Net.props"));

        props.Descendants("AdditionalFiles").Single().Attribute("Include")!.Value.ShouldBe("**/*.sfflow.yaml");
        props.Descendants("CompilerVisibleProperty").Select(p => p.Attribute("Include")!.Value).ShouldBe(["RootNamespace", "ProjectDir", "SourceFlowServiceName"]);
        GeneratorHarness.RunWithFiles([Domain], [("/repo/orders/notes.yaml", Workflow)]).HasSource("ApproveOrderWorkflowV1.Workflow.g.cs").ShouldBeFalse("only *.sfflow.yaml files are workflows");
    }

    [Fact]
    [Trait("Id", "GEN-60")]
    [Trait("Covers", "WFD-35")]
    public void Versions_SideBySide_AndDuplicatesAreRejected()
    {
        string second = Workflow.Replace("version: 1", "version: 2", StringComparison.Ordinal).Replace("note: { $hook: Note }", "note: 'v2'", StringComparison.Ordinal);

        GeneratorRun run = Run(more: ("/repo/orders/workflows/approve-order-v2.sfflow.yaml", second));

        run.HasSource("ApproveOrderWorkflowV1.Workflow.g.cs").ShouldBeTrue();
        run.HasSource("ApproveOrderWorkflowV2.Workflow.g.cs").ShouldBeTrue();
        string registration = run.Source("Orders.OrdersModule.Workflows.g.cs");
        registration.ShouldContain("builder.AddProcessManager<global::Orders.ApproveOrderWorkflowV1, global::SourceFlow.Workflows.WorkflowInstanceId>();");
        registration.ShouldContain("builder.AddProcessManager<global::Orders.ApproveOrderWorkflowV2, global::SourceFlow.Workflows.WorkflowInstanceId>();");

        GeneratorRun duplicate = Run(more: ("/repo/orders/workflows/copy.sfflow.yaml", Workflow));
        duplicate.GeneratorDiagnostics.Count(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).StartsWith("WF-V15", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    [Trait("Id", "GEN-61")]
    [Trait("Covers", "WFD-11;WFD-35")]
    public void Manifest_ListsTheWorkflow_WithItsFingerprint()
    {
        GeneratorRun run = Run();

        AttributeData attribute = run.Output.Assembly.GetAttributes().Single(a => a.AttributeClass?.Name == "SourceFlowManifestAttribute");
        JsonNode entry = JsonNode.Parse((string)attribute.ConstructorArguments[0].Value!)!["workflows"]![0]!;
        entry["name"]!.GetValue<string>().ShouldBe("approve-order");
        entry["version"]!.GetValue<int>().ShouldBe(1);
        entry["file"]!.GetValue<string>().ShouldBe("workflows/approve-order.sfflow.yaml");
        entry["fingerprint"]!.GetValue<string>().ShouldMatch("^sha256:[0-9a-f]{16}$");
        string registration = run.Source("Orders.OrdersModule.Workflows.g.cs");
        registration.ShouldContain("global::SourceFlow.Workflows.WorkflowRuntime.RegisterMessages(builder);");
        registration.ShouldContain("builder.AddJsonTypeInfoResolver(global::SourceFlow.Workflows.WorkflowRuntime.StateResolver<global::Orders.ApproveOrderWorkflowV1>());");
        registration.ShouldContain("builder.AddMessage<global::SourceFlow.CommandRejected<global::Orders.ApproveOrder>>(\"orders.approve-order.rejected\", 1);");
    }

    [Fact]
    [Trait("Id", "GEN-62")]
    [Trait("Covers", "WFD-NFR-1;WFD-NFR-2")]
    public void Engine_IsInjectedOnce_AsInternalTypes_OnlyWithWorkflows()
    {
        GeneratorRun run = Run(more: ("/repo/orders/workflows/v2.sfflow.yaml", Workflow.Replace("version: 1", "version: 2", StringComparison.Ordinal)));

        SyntaxTree[] engine = [.. run.Result.GeneratedTrees.Where(t => Path.GetFileName(t.FilePath).StartsWith("SourceFlow.Workflows.Engine.", StringComparison.Ordinal))];
        engine.Select(t => Path.GetFileName(t.FilePath)).Order(StringComparer.Ordinal).ShouldBe(
        [
            "SourceFlow.Workflows.Engine.Guard.g.cs", "SourceFlow.Workflows.Engine.IsoDuration.g.cs", "SourceFlow.Workflows.Engine.JsonLite.g.cs",
            "SourceFlow.Workflows.Engine.SimulationStateJson.g.cs", "SourceFlow.Workflows.Engine.WorkflowBridge.g.cs", "SourceFlow.Workflows.Engine.WorkflowIds.g.cs",
            "SourceFlow.Workflows.Engine.WorkflowJson.g.cs", "SourceFlow.Workflows.Engine.WorkflowModel.g.cs", "SourceFlow.Workflows.Engine.WorkflowSimulator.g.cs",
        ]);
        foreach (SyntaxTree tree in engine)
        {
            string text = tree.ToString();
            text.ShouldNotContain("SourceFlow.Studio.Dsl");
            PublicType().IsMatch(text).ShouldBeFalse(Path.GetFileName(tree.FilePath) + " declares no public type");
        }

        GeneratorHarness.RunWithFiles([Domain], []).Result.GeneratedTrees.ShouldNotContain(t => t.FilePath.Contains("Workflows.Engine", StringComparison.Ordinal), "services without workflows get none of it");

        GeneratorRun other = Run(properties: new Dictionary<string, string>(Properties) { ["build_property.SourceFlowServiceName"] = "payments" });
        other.GeneratorDiagnostics.ShouldContain(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("compiled by 'payments'"));
    }

    [Fact]
    [Trait("Id", "GEN-64")]
    [Trait("Covers", "WFD-29")]
    public void YamlDotNet_IsBundledNextToTheGenerator()
    {
        string root = RepositoryRoot();
        XDocument core = XDocument.Load(Path.Combine(root, "src", "Core", "SourceFlow.Net", "SourceFlow.Net.csproj"));
        string[] analyzers = [.. core.Descendants("None").Where(n => (string?)n.Attribute("PackagePath") == "analyzers/dotnet/cs").Select(n => n.Attribute("Include")!.Value)];
        analyzers.ShouldContain(a => a.EndsWith("SourceFlow.Generators.dll", StringComparison.Ordinal));
        analyzers.ShouldContain(a => a.Contains("yamldotnet/$(YamlDotNetVersion)/lib/netstandard2.0/YamlDotNet.dll", StringComparison.Ordinal));
        core.Descendants("None").ShouldContain(n => (string?)n.Attribute("PackagePath") == "build/SourceFlow.Net.props");

        string packages = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        packages.ShouldContain("<PackageVersion Include=\"YamlDotNet\" Version=\"$(YamlDotNetVersion)\" />");
        typeof(SourceFlowGenerator).Assembly.GetReferencedAssemblies().ShouldContain(a => a.Name == "YamlDotNet");
        File.Exists(Path.Combine(Path.GetDirectoryName(typeof(SourceFlowGenerator).Assembly.Location)!, "YamlDotNet.dll")).ShouldBeTrue("the generator's dependency resolves from its own folder");
    }

    [Fact]
    [Trait("Id", "GEN-65")]
    [Trait("Covers", "WFD-37")]
    public void WorkflowInstancesQuery_IsGeneratedAndRegistered_NamedForTheService()
    {
        GeneratorRun run = Run();

        string registration = run.Source("Orders.OrdersModule.Workflows.g.cs");
        registration.ShouldContain("builder.AddDomainQuery<WorkflowInstancesQuery, global::SourceFlow.Workflows.WorkflowInstanceSummary>();");
        registration.ShouldContain("public sealed record WorkflowInstancesQuery(string Workflow) : global::SourceFlow.IDomainQueryRuntime<WorkflowInstancesQuery, global::SourceFlow.Workflows.WorkflowInstanceSummary>");
        registration.ShouldContain("public const string QueryName = \"orders.workflow-instances\";", Case.Sensitive, "the workflows' owner names the service");
        registration.ShouldContain("static string global::SourceFlow.IDomainQueryRuntime<WorkflowInstancesQuery, global::SourceFlow.Workflows.WorkflowInstanceSummary>.QueryText => global::SourceFlow.Workflows.WorkflowRuntime.InstancesQueryText;");
        run.CompilationErrors().ShouldBeEmpty();

        Run(properties: new Dictionary<string, string>(Properties) { ["build_property.SourceFlowServiceName"] = "orders" })
            .Source("Orders.OrdersModule.Workflows.g.cs").ShouldContain("\"orders.workflow-instances\"");
        GeneratorHarness.RunWithFiles([Domain], []).Result.GeneratedTrees.ShouldNotContain(t => t.FilePath.EndsWith("Workflows.g.cs", StringComparison.Ordinal), "no workflows: no query");
    }

    [GeneratedRegex(@"(?m)^\s*public (?:(?:static|sealed|abstract|readonly|partial)\s+)*(?:class|record|struct|enum|interface)\b")]
    private static partial Regex PublicType();

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
