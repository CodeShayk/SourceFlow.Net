using System.Text.Json.Nodes;
using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Diagnostics;
using SourceFlow.Studio.Dsl.Manifests;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Dsl.Specs;
using SourceFlow.Studio.Dsl.Validation;

namespace SourceFlow.Studio.Tests;

/// <summary>UT-WF-80 (WP-C.11, CR-35): one reading of a nested <c>start.correlate</c> path, everywhere.</summary>
[Trait("Category", "Unit")]
public sealed class NestedCorrelationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // doc 15 §5.1's workflow, correlated by the customer's copy of the order id, nested in the start message.
    private static string Nested(string path = "data.customer.orderId") =>
        Golden.Workflow("01-order-fulfilment").Replace("correlate: data.orderId", "correlate: " + path, StringComparison.Ordinal);

    // The golden domain, with orders.order-placed.v1 carrying { customer: { orderId, email } } as well.
    private static WorkflowEnvironment Environment()
    {
        string path = Path.Combine(Golden.Root, "domain", "orders.manifest.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonObject placed = manifest["messages"]!.AsArray().Select(m => m!.AsObject()).Single(m => (string?)m["type"] == "orders.order-placed");
        placed["fields"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "customer",
            ["type"] = "object",
            ["fields"] = new JsonArray(new JsonObject { ["name"] = "orderId", ["type"] = "uuid" }, new JsonObject { ["name"] = "email", ["type"] = "string", ["personalData"] = true }),
        });
        ServiceManifest orders = ServiceManifest.Parse(manifest.ToJsonString());
        return new WorkflowEnvironment(Golden.Environment.Manifests.Where(m => m.Service != "orders").Append(orders));
    }

    [Fact]
    [Trait("Id", "UT-WF-80")]
    [Trait("Covers", "doc 15 5.4")]
    public void ANestedCorrelatePath_IsReadTheSameWayEverywhere_AndKeepsItsTimers()
    {
        // The helper: the start message is read along the path; awaited events carry the last segment.
        CorrelationPath.IsValid("data.customer.orderId").ShouldBeTrue();
        CorrelationPath.IsValid("customer.orderId").ShouldBeFalse("a path starts with data.");
        CorrelationPath.IsValid("data.customer..orderId").ShouldBeFalse();
        CorrelationPath.InstanceKey("data.customer.orderId").ShouldBe("orderId");

        // The reader and the validator: the nested field exists in the manifest; a missing one is WF-V04.
        WorkflowEnvironment environment = Environment();
        WorkflowAnalysis.Analyze(Nested(), environment).Diagnostics.ShouldBeEmpty();
        WorkflowAnalysis.Analyze(Nested("data.customer.missing"), environment).Diagnostics
            .ShouldContain(d => d.Id == WorkflowRules.Correlation && d.Message.Contains("'customer.missing'", StringComparison.Ordinal));

        // The simulator: the instance is the nested value, and the timers it schedules carry that instance.
        WorkflowDefinition workflow = WorkflowReader.Read(Nested()).Workflow!;
        var start = new Dictionary<string, object?>
        {
            ["orderId"] = "order-7", ["total"] = 100m, ["lines"] = new List<object?> { "sku-1" }, ["customerEmail"] = "a@b.c", ["priority"] = false,
            ["customer"] = new Dictionary<string, object?> { ["orderId"] = "customer-copy-7", ["email"] = "a@b.c" },
        };
        SimulationResult started = WorkflowSimulator.Start(workflow, start, T0);
        started.State.Instance.ShouldBe("customer-copy-7");
        started.State.Timer.ShouldNotBeNull("reserve-stock's timeout");
        SimulationResult timedOut = WorkflowSimulator.Step(started.State, SimulationInput.FireTimer());
        timedOut.State.Instance.ShouldBe("customer-copy-7");
        timedOut.State.Attempt.ShouldBe(2, "the timer reached its instance: the step is sent again");

        // The path explorer: its placeholder start data puts the instance at the nested path.
        PathSet paths = PathEnumerator.Enumerate(workflow);
        paths.Paths.ShouldNotBeEmpty();
        foreach (WorkflowPath path in paths.Paths)
        {
            PathEnumerator.Replay(workflow, path)[0].State.Instance.ShouldBe("instance-1", path.ToString());
        }

        // The spec generator: the start message's sample carries the instance under customer.orderId.
        string specs = SpecGenerator.Generate(workflow, environment);
        specs.ShouldContain("\"customer\":{\"orderId\":");
    }
}
