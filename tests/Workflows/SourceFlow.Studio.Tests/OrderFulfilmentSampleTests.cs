using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Dsl.Specs;
using SourceFlow.Studio.Dsl.Validation;
using SourceFlow.Studio.Workspace;

namespace SourceFlow.Studio.Tests;

/// <summary>
/// The OrderFulfilment sample (samples/OrderFulfilment, WP-S.13) as a workspace: its exported manifests are exact (the
/// WP-S.1 acceptance), its workflow validates against all four services with no diagnostic, enumerates the §5.1 path set,
/// and the specs Studio generated for it are current.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OrderFulfilmentSampleTests
{
    private static readonly string Sample = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "samples", "OrderFulfilment"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Id", "UT-MAN-16")]
    [Trait("Covers", "WFD-11;WFD-12")]
    public async Task Manifests_HaveExactEmitsAndRejects_ForEveryService()
    {
        DomainWorkspace workspace = await DomainWorkspace.LoadAsync(Sample, cancellationToken: Ct);

        workspace.Services.Select(s => s.Name).ShouldBe(["orders", "inventory", "payments", "shipping"]);
        Commands(workspace, "inventory").ShouldBe(
        [
            "inventory.release-stock: emits inventory.stock-released.v1; rejects none",
            "inventory.reserve-stock: emits inventory.stock-reserved.v1; rejects inventory.nothing_to_reserve, inventory.out_of_stock",
        ]);
        Commands(workspace, "payments").ShouldBe(
        [
            "payments.charge: emits payments.payment-captured.v1; rejects payments.declined, payments.fraud_suspected, payments.invalid_amount",
            "payments.refund: emits payments.refunded.v1; rejects none",
        ]);
        Commands(workspace, "shipping").ShouldBe(["shipping.create-shipment: emits shipping.shipment-created.v1; rejects shipping.address_invalid"]);
        Commands(workspace, "orders").ShouldContain("orders.place-order: emits orders.order-placed.v1; rejects orders.empty, orders.invalid_total");

        ServiceManifest orders = workspace.Services.Single(s => s.Name == "orders").Manifest;
        orders.CommandRoutes.Where(r => r.ReplyOnReject).Select(r => r.Command).ShouldBe(["inventory.reserve-stock.v1", "payments.charge.v1", "shipping.create-shipment.v1"]);
        orders.Workflows.ShouldHaveSingleItem().Name.ShouldBe("order-fulfilment");
    }

    [Fact]
    [Trait("Id", "UT-WF-71")]
    [Trait("Covers", "WFD-28")]
    public async Task Workflow_ValidatesAgainstTheWholeDomain_WithNoDiagnostic()
    {
        (DomainWorkspace workspace, WorkflowAnalysisResult analysis) = await AnalyzeAsync();

        workspace.Services.Single(s => s.Name == "orders").WorkflowFiles.ShouldHaveSingleItem().ShouldEndWith("order-fulfilment.sfflow.yaml");
        analysis.Diagnostics.ShouldBeEmpty();
        analysis.Workflow.ShouldNotBeNull().ProcessName.ShouldBe("order-fulfilment@1");
    }

    [Fact]
    [Trait("Id", "UT-WF-72")]
    [Trait("Covers", "WFD-33")]
    public async Task Workflow_HasTheDocExamplesPathSet()
    {
        (_, WorkflowAnalysisResult analysis) = await AnalyzeAsync();

        // The sample adds data (the address) and map fields to the doc 15 §5.1 example, not states or outcomes.
        PathSet paths = PathEnumerator.Enumerate(analysis.Workflow!);
        paths.Paths.Count.ShouldBe(437);
        paths.MinimalCover().Count.ShouldBe(6);
        paths.Paths.Select(p => p.End).Distinct().Order().ShouldBe([WorkflowStatus.Completed, WorkflowStatus.Compensated, WorkflowStatus.ManualIntervention]);
    }

    [Fact]
    [Trait("Id", "UT-WF-73")]
    [Trait("Covers", "WFD-34")]
    public async Task GeneratedSpecs_AreCurrent()
    {
        (DomainWorkspace workspace, WorkflowAnalysisResult analysis) = await AnalyzeAsync();
        WorkspaceService orders = workspace.Services.Single(s => s.Name == "orders");

        // The options the local host uses for the service's tests and module (sourceflow.domain.yaml).
        string expected = SpecGenerator.Generate(analysis.Workflow!, new WorkflowEnvironment(workspace.Manifests), new SpecOptions
        {
            Namespace = "Orders.Tests.Workflows",
            ModuleType = orders.ModuleType!,
            WorkflowType = "Orders.OrderFulfilmentWorkflowV1",
        });
        string file = Path.Combine(Sample, orders.TestsLocation!, "Workflows", "OrderFulfilmentV1PathTests.cs");

        File.ReadAllText(file).ReplaceLineEndings("\n").ShouldBe(expected, "regenerate the specs in Studio (Paths → Generate specs) after changing the workflow");
    }

    private static async Task<(DomainWorkspace Workspace, WorkflowAnalysisResult Analysis)> AnalyzeAsync()
    {
        DomainWorkspace workspace = await DomainWorkspace.LoadAsync(Sample, cancellationToken: Ct);
        string file = Path.Combine(workspace.Root, "Orders", "Workflows", "order-fulfilment.sfflow.yaml");
        return (workspace, WorkflowAnalysis.Analyze(await File.ReadAllTextAsync(file, Ct), new WorkflowEnvironment(workspace.Manifests)));
    }

    private static string[] Commands(DomainWorkspace workspace, string service)
    {
        return [.. workspace.Services.Single(s => s.Name == service).Manifest.Aggregates
            .SelectMany(a => a.Commands)
            .Select(c => $"{c.Type}: emits {Join(c.Emits)}; rejects {Join(c.Rejects)}")
            .Order(StringComparer.Ordinal)];

        static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(", ", values.Order(StringComparer.Ordinal));
    }
}
