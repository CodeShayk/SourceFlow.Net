using SourceFlow.Studio.Dsl.Json;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Workspace;

namespace SourceFlow.Studio.Tests;

/// <summary>Service manifests and the business domain workspace (doc 15 §3–4, WP-S.2).</summary>
[Trait("Category", "Unit")]
public sealed class WorkspaceTests : IDisposable
{
    private const string Payments = """
        {
          "manifestVersion": 1,
          "service": "payments",
          "endpoint": "payments",
          "sourceflowVersion": "3.0.0",
          "messages": [
            { "type": "payments.charge", "version": 1, "kind": "command", "clrType": "Payments.Charge", "aggregate": "payment",
              "fields": [ { "name": "orderId", "type": "uuid" }, { "name": "amount", "type": "number" }, { "name": "card", "type": "string", "nullable": true, "personalData": true } ] },
            { "type": "payments.payment-captured", "version": 1, "kind": "event", "fields": [ { "name": "paymentId", "type": "uuid" } ] }
          ],
          "aggregates": [
            { "name": "payment", "clrType": "Payments.Payment",
              "commands": [ { "type": "payments.charge", "version": 1, "emits": ["payments.payment-captured.v1"], "rejects": ["payments.declined", "payments.fraud_suspected"] } ],
              "events": ["payments.payment-captured.v1"] }
          ],
          "processManagers": [ { "name": "refund-retry", "clrType": "Payments.RefundRetry", "handles": ["payments.refund-failed.v1"], "sends": ["payments.refund.v1"], "schedules": [] } ],
          "projections": [ { "name": "payment-summary", "version": 2, "clrType": "Payments.Summary", "handles": ["payments.payment-captured.v1"] } ],
          "publishes": [ { "domainEvent": "payments.payment-captured.v1", "as": "payments.payment-captured-v1.v1", "topic": "payments.events" } ],
          "subscribes": [ { "topic": "orders.events", "only": ["orders.order-placed.v1"], "where": "source = 'orders'" } ],
          "commandRoutes": [ { "command": "inventory.reserve-stock.v1", "to": "inventory", "replyOnReject": true } ],
          "workflows": [ { "name": "refund", "version": 1, "file": "workflows/refund.sfflow.yaml" } ],
          "schemas": "catalog://payments"
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sourceflow-workspace-" + Path.GetRandomFileName());

    public WorkspaceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    [Trait("Id", "UT-MAN-12")]
    [Trait("Covers", "WFD-10")]
    public async Task Workspace_ResolvesRelativePaths_AndListsWorkflowFiles()
    {
        Write("services/payments/artifacts/sourceflow.manifest.json", Payments);
        Write("services/orders/artifacts/sourceflow.manifest.json", Manifest("orders"));
        Write("services/orders/workflows/order-fulfilment.sfflow.yaml", "sourceflow.workflow: 1\n");
        Write("services/orders/workflows/returns/refund.sfflow.yaml", "sourceflow.workflow: 1\n");
        Write("services/orders/workflows/notes.md", "not a workflow");
        Write("other-repo/shipping.json", Manifest("shipping"));
        Write("domain/sourceflow.domain.yaml", """
            # The commerce domain.
            domain: commerce
            description: Order to delivery
            services:
              - name: orders
                manifest: ../services/orders/artifacts/sourceflow.manifest.json
                workflows: ../services/orders/workflows
              - name: payments
                manifest: ../services/payments/artifacts/sourceflow.manifest.json
              - name: shipping
                manifest: ../other-repo/shipping.json
            """);

        DomainWorkspace workspace = await DomainWorkspace.LoadAsync(Path.Combine(_root, "domain"), cancellationToken: TestContext.Current.CancellationToken);

        workspace.Domain.ShouldBe("commerce");
        workspace.Description.ShouldBe("Order to delivery");
        workspace.Path.ShouldBe(Path.Combine(_root, "domain", DomainWorkspace.FileName));
        workspace.Services.Select(s => s.Name).ShouldBe(["orders", "payments", "shipping"]);
        workspace.Service("orders")!.WorkflowFiles.ShouldBe(
        [
            "../services/orders/workflows/order-fulfilment.sfflow.yaml",
            "../services/orders/workflows/returns/refund.sfflow.yaml",
        ]);
        workspace.Service("payments")!.WorkflowsLocation.ShouldBeNull();
        workspace.Service("payments")!.WorkflowFiles.ShouldBeEmpty();
        workspace.Service("payments")!.Manifest.Aggregates.Single().Name.ShouldBe("payment");
        workspace.Manifests.Count().ShouldBe(3);
        workspace.Service("billing").ShouldBeNull();
    }

    [Theory]
    [Trait("Id", "UT-MAN-13")]
    [Trait("Covers", "WFD-10")]
    [InlineData("domain: d\nservices:\n  - name: payments\n    manifest: missing.json\n", "manifest 'missing.json' not found")]
    [InlineData("domain: d\nservices:\n  - name: payments\n    manifest: p.json\n    colour: red\n", "(5,5): unknown key 'colour'")]
    [InlineData("domain: d\nowner: me\nservices: []\n", "(2,1): unknown key 'owner'")]
    [InlineData("domain: d\nservices:\n  - name: payments\n    manifest: p.json\n  - name: payments\n    manifest: p.json\n", "service 'payments' is listed twice")]
    [InlineData("domain: d\nservices: []\n", "'services' lists no service")]
    [InlineData("services:\n  - name: payments\n    manifest: p.json\n", "'domain' is required")]
    [InlineData("domain: d\nservices:\n  - name: payments\n", "needs a 'name' and a 'manifest'")]
    [InlineData("domain: d\nservices:\n  - name: payments\n    manifest: https://example.invalid/payments.json\n", "remote manifests are off")]
    [InlineData("domain: d\nservices:\n  - name: billing\n    manifest: p.json\n", "is the manifest of service 'payments'")]
    [InlineData("domain: d\nservices:\n  - name: payments\n    manifest: bad.json\n", "manifest 'bad.json' is invalid")]
    [InlineData("domain: [d\n", "sourceflow.domain.yaml(")]
    public async Task Workspace_ReportsInvalidFilesAndManifests(string yaml, string expected)
    {
        Write("p.json", Payments);
        Write("bad.json", """{ "manifestVersion": 2 }""");
        Write(DomainWorkspace.FileName, yaml);

        WorkspaceException error = await Should.ThrowAsync<WorkspaceException>(() =>
            DomainWorkspace.LoadAsync(Path.Combine(_root, DomainWorkspace.FileName), cancellationToken: TestContext.Current.CancellationToken));

        error.Message.ShouldContain(expected);
    }

    [Fact]
    [Trait("Id", "UT-MAN-13")]
    public async Task Workspace_FetchesRemoteManifests_OnlyThroughAConfiguredFetcher()
    {
        Write(DomainWorkspace.FileName, "domain: d\nservices:\n  - name: payments\n    manifest: https://manifests.example/payments.json\n");
        var fetched = new List<Uri>();
        var options = new WorkspaceLoadOptions
        {
            FetchRemote = (uri, _) =>
            {
                fetched.Add(uri);
                return Task.FromResult(Payments);
            },
        };

        DomainWorkspace workspace = await DomainWorkspace.LoadAsync(_root, options, TestContext.Current.CancellationToken);

        fetched.ShouldBe([new Uri("https://manifests.example/payments.json")]);
        workspace.Service("payments")!.Manifest.Service.ShouldBe("payments");
        await Should.ThrowAsync<WorkspaceException>(() => DomainWorkspace.LoadAsync(Path.Combine(_root, "nowhere.yaml"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Id", "UT-MAN-14")]
    [Trait("Covers", "WFD-11")]
    public void Manifest_ParsesEverySection()
    {
        ServiceManifest manifest = ServiceManifest.Parse(Payments);

        manifest.Service.ShouldBe("payments");
        manifest.Endpoint.ShouldBe("payments");
        manifest.SourceFlowVersion.ShouldBe("3.0.0");
        manifest.Schemas.ShouldBe("catalog://payments");
        ManifestMessage charge = manifest.FindMessage("payments.charge.v1")!;
        charge.Kind.ShouldBe("command");
        charge.Aggregate.ShouldBe("payment");
        charge.Field("card").ShouldBe(new ManifestField("card", "string", Nullable: true, PersonalData: true));
        charge.Field("nope").ShouldBeNull();
        manifest.TryGetCommand("payments.charge.v1", out ManifestAggregate? aggregate, out ManifestCommand? command).ShouldBeTrue();
        aggregate!.Name.ShouldBe("payment");
        command!.Emits.ShouldBe(["payments.payment-captured.v1"]);
        command.Rejects.ShouldBe(["payments.declined", "payments.fraud_suspected"]);
        manifest.TryGetCommand("payments.refund.v1", out _, out _).ShouldBeFalse();
        manifest.ProcessManagers.Single().Sends.ShouldBe(["payments.refund.v1"]);
        manifest.Projections.Single().Version.ShouldBe(2);
        manifest.Publishes.Single().ShouldBe(new ManifestPublication("payments.payment-captured.v1", "payments.payment-captured-v1.v1", "payments.events"));
        manifest.Subscribes.Single().Only.ShouldBe(["orders.order-placed.v1"]);
        manifest.Subscribes.Single().Where.ShouldBe("source = 'orders'");
        manifest.CommandRoutes.Single().ShouldBe(new ManifestCommandRoute("inventory.reserve-stock.v1", "inventory", ReplyOnReject: true));
        manifest.Workflows.Single().ShouldBe(new ManifestWorkflow("refund", 1, "workflows/refund.sfflow.yaml"));

        // A static manifest (from the generator's attribute) has no service or routing.
        ServiceManifest staticPart = ServiceManifest.Parse("""{"manifestVersion":1,"assembly":"Payments","sourceflowVersion":"3.0.0","messages":[],"aggregates":[],"processManagers":[],"projections":[]}""");
        staticPart.Service.ShouldBeNull();
        staticPart.Assembly.ShouldBe("Payments");
        staticPart.CommandRoutes.ShouldBeEmpty();

        MessageRef.Of("payments.charge", 2).ShouldBe("payments.charge.v2");
        MessageRef.TryParse("payments.charge.v2", out string name, out int version).ShouldBeTrue();
        (name, version).ShouldBe(("payments.charge", 2));
        MessageRef.TryParse("payments.charge", out _, out _).ShouldBeFalse();
    }

    [Theory]
    [Trait("Id", "UT-MAN-14")]
    [InlineData("""{ "manifestVersion": 2 }""", "Unsupported manifestVersion 2")]
    [InlineData("""{ "service": "x" }""", "manifestVersion (missing)")]
    [InlineData("""[1, 2]""", "is a JSON object")]
    [InlineData("""{ "manifestVersion": 1, "manifestVersion": 1 }""", "duplicate property 'manifestVersion'")]
    [InlineData("""{ "manifestVersion": 1, "aggregates": [ { "clrType": "X" } ] }""", "has no 'name'")]
    [InlineData("""{ "manifestVersion": 1, """, "line 1")]
    [InlineData("""{ "manifestVersion": 1 } x""", "after the value")]
    [InlineData("""{ "manifestVersion": 01 }""", "expected '}'")]
    [InlineData("""{ "a": "\q" }""", "invalid escape")]
    [InlineData("""{ "a": tru }""", "invalid literal")]
    public void Manifest_RejectsInvalidInput(string json, string expected)
    {
        Should.Throw<FormatException>(() => ServiceManifest.Parse(json)).Message.ShouldContain(expected);
    }

    [Fact]
    [Trait("Id", "UT-MAN-14")]
    public void JsonLite_ReadsEveryValueKind()
    {
        JsonValue value = JsonLite.Parse("""
            { "s": "a\"b\\c\/d\né", "i": -12, "d": 1.5e2, "t": true, "f": false, "n": null, "a": [1, [], {}], "o": { "x": 0 } }
            """);

        value["s"]!.AsString.ShouldBe("a\"b\\c/d\né");
        value["i"]!.AsInteger.ShouldBe(-12);
        value["d"]!.AsNumber.ShouldBe(150);
        value["d"]!.AsInteger.ShouldBeNull();
        value["t"]!.AsBoolean.ShouldBe(true);
        value["f"]!.AsBoolean.ShouldBe(false);
        value["n"]!.AsString.ShouldBeNull();
        value["a"]!.Items.Count.ShouldBe(3);
        value["o"]!.Members.Single().Key.ShouldBe("x");
        value["missing"].ShouldBeNull();
        Should.Throw<FormatException>(() => JsonLite.Parse(new string('[', 70) + new string(']', 70))).Message.ShouldContain("nested too deeply");
    }

    private static string Manifest(string service)
    {
        return Payments.Replace("\"payments\"", "\"" + service + "\"", StringComparison.Ordinal);
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
