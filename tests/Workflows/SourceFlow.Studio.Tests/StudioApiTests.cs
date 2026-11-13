using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Api;
using SourceFlow.Studio.Dsl.Export;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Dsl.Specs;

namespace SourceFlow.Studio.Tests;

/// <summary>
/// The Studio core's JSON API (WP-S.7, ADR-025): what SourceFlow.Studio.Wasm exports. These run it in .NET; the Vitest
/// suite (studio/core-client, WASM-01..08) runs the same API compiled to WebAssembly against the same golden files.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StudioApiTests
{
    private static readonly string Repository = Path.GetFullPath(Path.Combine(Golden.Root, "..", ".."));
    private static readonly string ExportsSource = Path.Combine(Repository, "src", "Workflows", "SourceFlow.Studio.Wasm", "Exports.cs");
    private static readonly string Declaration = Path.Combine(Repository, "studio", "core-client", "src", "generated", "exports.d.ts");

    [Fact]
    [Trait("Id", "UT-WASM-01")]
    [Trait("Covers", "WFD-NFR-2")]
    public void EveryApiMethod_IsExported_AndTheTypeScriptDeclarationIsGeneratedFromTheExports()
    {
        string[] api = [.. typeof(StudioApi).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string) && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(string))
            .OrderBy(m => m.MetadataToken)
            .Select(m => m.Name)];
        IReadOnlyList<Export> exports = Exports();

        exports.Select(e => e.Name).ShouldBe(api, "one [JSExport] per StudioApi method, in the same order");
        exports.ShouldAllBe(e => e.Delegates, "each export only calls StudioApi's method of the same name");
        api.ShouldBe(["Version", "Parse", "Validate", "SimulateStep", "EnumeratePaths", "ToMermaid", "ToBpmn", "GenerateSpecs"]);

        string generated = TypeScript(exports);
        string? committed = File.Exists(Declaration) ? File.ReadAllText(Declaration).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
        if (committed != generated && Environment.GetEnvironmentVariable(Golden.ApproveVariable) is "1" or "true")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Declaration)!);
            File.WriteAllText(Declaration, generated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        committed.ShouldBe(generated, $"studio/core-client/src/generated/exports.d.ts is out of date: run the tests with {Golden.ApproveVariable}=1.");
    }

    [Theory]
    [Trait("Id", "UT-WASM-02")]
    [Trait("Covers", "WFD-28;WFD-NFR-2")]
    [MemberData(nameof(Cases))]
    public void Validate_GivesTheGoldenDiagnostics(string @case)
    {
        JsonNode result = Ok(StudioApi.Validate(Request(new JsonObject { ["yaml"] = Golden.Workflow(@case), ["manifests"] = Manifests() })));

        result["diagnostics"]!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).ShouldBe(
            File.ReadAllText(Path.Combine(Golden.Root, "cases", @case, "expected.diagnostics.json")).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n'));
        WorkflowAnalysisResult analysis = WorkflowAnalysis.Analyze(Golden.Workflow(@case), Golden.Environment);
        result["valid"]!.GetValue<bool>().ShouldBe(analysis.Workflow is not null && !analysis.HasErrors);
    }

    [Fact]
    [Trait("Id", "UT-WASM-03")]
    [Trait("Covers", "WFD-33")]
    public void SimulateStep_DrivesTheSimulator_ThroughTheOpaqueState()
    {
        string yaml = Golden.Workflow("01-order-fulfilment");
        var data = new JsonObject { ["orderId"] = "o-1", ["total"] = 100, ["lines"] = new JsonArray("sku"), ["customerEmail"] = "a@b.c", ["priority"] = false };

        JsonNode started = Ok(StudioApi.SimulateStep(Request(new JsonObject { ["yaml"] = yaml, ["input"] = new JsonObject { ["kind"] = "start", ["data"] = data }, ["now"] = "2026-01-01T00:00:00Z" })));
        started["status"]!.GetValue<string>().ShouldBe("Running");
        started["step"]!.GetValue<string>().ShouldBe("reserve-stock");
        JsonNode send = started["effects"]!.AsArray().Single(e => e!["kind"]!.GetValue<string>() == "send")!;
        send["command"]!["type"]!.GetValue<string>().ShouldBe("inventory.reserve-stock.v1");
        send["command"]!["messageId"]!.GetValue<string>().ShouldBe(WorkflowIds.Send("order-fulfilment@1", "o-1", "reserve-stock", 1).ToString("D"));
        send["command"]!["fields"]!["orderId"]!.GetValue<string>().ShouldBe("o-1");
        started["timer"]!["kind"]!.GetValue<string>().ShouldBe("timeout");

        JsonNode next = Step(yaml, started, new JsonObject { ["kind"] = "succeed" });
        next["step"]!.GetValue<string>().ShouldBe("charge-payment");
        next["transitions"]!.AsArray().Single()!["kind"]!.GetValue<string>().ShouldBe("success");
        JsonNode retried = Step(yaml, next, new JsonObject { ["kind"] = "reject", ["code"] = "payments.declined" });
        retried["phase"]!.GetValue<string>().ShouldBe("Retrying");
        JsonNode fired = Step(yaml, retried, new JsonObject { ["kind"] = "timer" });
        fired["attempt"]!.GetValue<int>().ShouldBe(2);
        JsonNode fraud = Step(yaml, fired, new JsonObject { ["kind"] = "rejected", ["command"] = "payments.charge.v1", ["code"] = "payments.fraud_suspected" });
        fraud["status"]!.GetValue<string>().ShouldBe("Compensating");
        fraud["compensationStack"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldBe(["reserve-stock"]);
        fraud["failure"]!["code"]!.GetValue<string>().ShouldBe("payments.fraud_suspected");
        JsonNode undone = Step(yaml, fraud, new JsonObject { ["kind"] = "receive", ["type"] = "inventory.stock-released.v1" });
        undone["status"]!.GetValue<string>().ShouldBe("Compensated");
        undone["terminal"]!.GetValue<bool>().ShouldBeTrue();
        Step(yaml, undone, new JsonObject { ["kind"] = "resume" })["effects"]![0]!["kind"]!.GetValue<string>().ShouldBe("ignored");

        // Operators, guards and failure events.
        JsonNode large = Ok(StudioApi.SimulateStep(Request(new JsonObject
        {
            ["yaml"] = yaml,
            ["input"] = new JsonObject { ["kind"] = "start", ["data"] = data.DeepClone() },
            ["guards"] = new JsonObject { ["review-large-order"] = true },
        })));
        large = Step(yaml, Step(yaml, large, new JsonObject { ["kind"] = "succeed" }), new JsonObject { ["kind"] = "succeed", ["data"] = new JsonObject { ["paymentId"] = "p" } }, guards: true);
        large["step"]!.GetValue<string>().ShouldBe("review-large-order", "the forced guard ran the review");
        JsonNode manual = Step(yaml, large, new JsonObject { ["kind"] = "timer", ["timer"] = "timeout", ["step"] = "review-large-order", ["attempt"] = 1 });
        manual["status"]!.GetValue<string>().ShouldBe("ManualIntervention");
        Step(yaml, manual, new JsonObject { ["kind"] = "retry-step" })["attempt"]!.GetValue<int>().ShouldBe(2);
        Step(yaml, manual, new JsonObject { ["kind"] = "resume", ["step"] = "create-shipment" })["step"]!.GetValue<string>().ShouldBe("create-shipment");
        Step(yaml, manual, new JsonObject { ["kind"] = "abort", ["reason"] = "x", ["compensate"] = true })["status"]!.GetValue<string>().ShouldBe("Compensating");
        Step(yaml, Step(yaml, manual, new JsonObject { ["kind"] = "resume" }), new JsonObject { ["kind"] = "fail", ["event"] = "orders.order-rejected-by-review.v1" })["status"]!.GetValue<string>().ShouldBe("Compensating");
    }

    [Fact]
    [Trait("Id", "UT-WASM-04")]
    [Trait("Covers", "WFD-34;WFD-30;WFD-36")]
    public void PathsExportsAndSpecs_EqualTheLibrary()
    {
        string yaml = Golden.Workflow("01-order-fulfilment");
        var workflow = WorkflowReader.Read(yaml).Workflow!;
        PathSet expected = PathEnumerator.Enumerate(workflow);

        JsonNode paths = Ok(StudioApi.EnumeratePaths(Request(new JsonObject { ["yaml"] = yaml })));
        paths["paths"]!.AsArray().Select(p => p!["text"]!.GetValue<string>()).ShouldBe(expected.Paths.Select(p => p.ToString()));
        paths["edges"]!.AsArray().Count.ShouldBe(expected.Edges.Count);
        paths["minimalCover"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(expected.MinimalCover().Select(p => p.Id));
        paths["truncated"]!.GetValue<bool>().ShouldBeFalse();
        Ok(StudioApi.EnumeratePaths(Request(new JsonObject { ["yaml"] = yaml, ["maxPaths"] = 3, ["maxStepEntries"] = 1, ["maxMoves"] = 50 })))["truncated"]!.GetValue<bool>().ShouldBeTrue();

        Ok(StudioApi.ToMermaid(Request(new JsonObject { ["yaml"] = yaml })))["text"]!.GetValue<string>().ShouldBe(MermaidExporter.Export(workflow));
        Ok(StudioApi.ToBpmn(Request(new JsonObject { ["yaml"] = yaml })))["text"]!.GetValue<string>().ShouldBe(BpmnExporter.Export(workflow));
        string specs = Ok(StudioApi.GenerateSpecs(Request(new JsonObject
        {
            ["yaml"] = yaml,
            ["manifests"] = Manifests(),
            ["namespace"] = "Orders.Tests",
            ["moduleType"] = "Orders.OrdersModule",
            ["workflowType"] = "Orders.OrderFulfilmentWorkflowV1",
        })))["text"]!.GetValue<string>();
        specs.ShouldBe(File.ReadAllText(Path.Combine(Golden.Root, "cases", "01-order-fulfilment", "expected.specs.cs")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Ok(StudioApi.GenerateSpecs(Request(new JsonObject { ["yaml"] = yaml, ["manifests"] = Manifests(), ["pathIds"] = new JsonArray("P01") })))["text"]!.GetValue<string>()
            .Split("[Fact").Length.ShouldBe(2, "only the selected path");
        Ok(StudioApi.GenerateSpecs(Request(new JsonObject { ["yaml"] = yaml, ["manifests"] = Manifests(), ["allPaths"] = true })))["text"]!.GetValue<string>()
            .Split("[Fact").Length.ShouldBe(expected.Paths.Count + 1);
    }

    [Fact]
    [Trait("Id", "UT-WASM-05")]
    [Trait("Covers", "WFD-NFR-2")]
    public void Failures_AreEnvelopes_NeverExceptions()
    {
        Error(StudioApi.Parse("[]")).ShouldBe("The request must be a JSON object.");
        Error(StudioApi.Parse("{")).ShouldNotBeNullOrEmpty();
        Error(StudioApi.Validate("{}")).ShouldBe("'yaml' is required.");
        Error(StudioApi.Validate(Request(new JsonObject { ["yaml"] = "x: 1", ["manifests"] = new JsonArray(1) }))).ShouldContain("manifest JSON strings");
        Error(StudioApi.SimulateStep(Request(new JsonObject { ["yaml"] = Golden.Workflow("02-minimal") }))).ShouldBe("'input' is required.");
        Error(StudioApi.SimulateStep(Request(new JsonObject { ["yaml"] = Golden.Workflow("02-minimal"), ["input"] = new JsonObject { ["kind"] = "succeed" } }))).ShouldBe("'state' is required after the start.");
        Error(StudioApi.SimulateStep(Request(new JsonObject { ["yaml"] = Golden.Workflow("02-minimal"), ["input"] = new JsonObject() }))).ShouldBe("'input.kind' is required.");
        Error(StudioApi.SimulateStep(Request(new JsonObject { ["yaml"] = Golden.Workflow("02-minimal"), ["input"] = new JsonObject { ["kind"] = "start", ["data"] = new JsonObject() }, ["now"] = "soon" })))
            .ShouldContain("ISO-8601");

        JsonNode started = Ok(StudioApi.SimulateStep(Request(new JsonObject { ["yaml"] = Golden.Workflow("02-minimal"), ["input"] = new JsonObject { ["kind"] = "start", ["data"] = new JsonObject() } })));
        Error(StudioApi.SimulateStep(Request(new JsonObject { ["yaml"] = Golden.Workflow("02-minimal"), ["state"] = started["state"]!.GetValue<string>(), ["input"] = new JsonObject { ["kind"] = "jump" } })))
            .ShouldBe("Unknown input kind 'jump'.");
        Error(StudioApi.SimulateStep(Request(new JsonObject { ["yaml"] = Golden.Workflow("02-minimal"), ["state"] = started["state"]!.GetValue<string>(), ["input"] = new JsonObject { ["kind"] = "fail" } })))
            .ShouldBe("'input.event' is required for fail.");

        JsonNode broken = JsonNode.Parse(StudioApi.ToMermaid(Request(new JsonObject { ["yaml"] = Golden.Workflow("03-s01-invalid-yaml") })))!;
        broken["ok"]!.GetValue<bool>().ShouldBeFalse();
        broken["diagnostics"]!.AsArray().ShouldNotBeEmpty("the reader's diagnostics explain why");
        Ok(StudioApi.Version(string.Empty))["api"]!.GetValue<int>().ShouldBe(StudioApi.ApiVersion);
        Ok(StudioApi.Parse(Request(new JsonObject { ["yaml"] = "- not a mapping" })))["workflow"].ShouldBeNull();
        Ok(StudioApi.Parse(Request(new JsonObject { ["yaml"] = Golden.Workflow("02-minimal") })))["workflow"]!["name"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
    }

    // ================================================================ helpers

    public static TheoryData<string> Cases()
    {
        return Golden.Cases();
    }

    private static string Request(JsonObject request)
    {
        return request.ToJsonString();
    }

    private static JsonNode Ok(string response)
    {
        JsonNode envelope = JsonNode.Parse(response)!;
        envelope["ok"]!.GetValue<bool>().ShouldBeTrue(response);
        return envelope["result"]!;
    }

    private static string Error(string response)
    {
        JsonNode envelope = JsonNode.Parse(response)!;
        envelope["ok"]!.GetValue<bool>().ShouldBeFalse(response);
        return envelope["error"]!.GetValue<string>();
    }

    private static JsonNode Step(string yaml, JsonNode previous, JsonObject input, bool guards = false)
    {
        var request = new JsonObject { ["yaml"] = yaml, ["state"] = previous["state"]!.GetValue<string>(), ["input"] = input };
        if (guards)
        {
            request["guards"] = new JsonObject { ["review-large-order"] = true };
        }

        return Ok(StudioApi.SimulateStep(Request(request)));
    }

    // The golden domain's manifests, in sourceflow.domain.yaml order (as DomainWorkspace loads them).
    private static JsonArray Manifests()
    {
        string domain = Path.Combine(Golden.Root, "domain");
        return new JsonArray([.. File.ReadAllLines(Path.Combine(domain, "sourceflow.domain.yaml"))
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("manifest:", StringComparison.Ordinal))
            .Select(l => (JsonNode)JsonValue.Create(File.ReadAllText(Path.Combine(domain, l["manifest:".Length..].Trim())))!)]);
    }

    private sealed record Export(string Name, string Summary, bool Delegates);

    private static List<Export> Exports()
    {
        CompilationUnitSyntax root = CSharpSyntaxTree.ParseText(File.ReadAllText(ExportsSource), cancellationToken: TestContext.Current.CancellationToken).GetCompilationUnitRoot(TestContext.Current.CancellationToken);
        var exports = new List<Export>();
        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString() == "JSExport")))
        {
            method.ReturnType.ToString().ShouldBe("string", method.Identifier.Text);
            method.ParameterList.Parameters.Select(p => p.Type!.ToString() + " " + p.Identifier.Text).ShouldBe(["string request"], method.Identifier.Text);
            string summary = string.Join(" ", method.GetLeadingTrivia().ToFullString().Split('\n')
                .Select(l => l.Trim().TrimStart('/').Trim())
                .Where(l => l.Length > 0)
                .Select(l => l.Replace("<summary>", string.Empty, StringComparison.Ordinal).Replace("</summary>", string.Empty, StringComparison.Ordinal).Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('[')));
            bool delegates = method.ExpressionBody?.Expression.ToString() == $"StudioApi.{method.Identifier.Text}(request)";
            exports.Add(new Export(method.Identifier.Text, summary, delegates));
        }

        return exports;
    }

    private static string TypeScript(IEnumerable<Export> exports)
    {
        var text = new StringBuilder()
            .Append("// <auto-generated/>\n")
            .Append("// Generated from the [JSExport] signatures in src/Workflows/SourceFlow.Studio.Wasm/Exports.cs by StudioApiTests\n")
            .Append("// (tests/Workflows/SourceFlow.Studio.Tests, UT-WASM-01). Do not edit: run the tests with SOURCEFLOW_APPROVE=1.\n")
            .Append('\n')
            .Append("/** The exports of SourceFlow.Studio.Wasm.dll (`SourceFlow.Studio.Wasm.Exports`): one JSON request in, one JSON envelope out. */\n")
            .Append("export interface StudioExports {\n");
        foreach (Export export in exports)
        {
            text.Append("  /** ").Append(export.Summary).Append(" */\n")
                .Append("  ").Append(export.Name).Append("(request: string): string;\n");
        }

        return text.Append("}\n\n/** The name of an export. */\nexport type ExportName = keyof StudioExports;\n").ToString();
    }
}
