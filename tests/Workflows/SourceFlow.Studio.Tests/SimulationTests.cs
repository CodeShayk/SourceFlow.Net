using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SourceFlow.Messaging;
using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Export;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Dsl.Specs;

namespace SourceFlow.Studio.Tests;

/// <summary>UT-WF-41..60 (plan 10 §3.1): the simulator (doc 15 §5.3), the path enumerator, the exporters and the spec generator.</summary>
[Trait("Category", "Unit")]
public sealed class SimulationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, object?> Order = new()
    {
        ["orderId"] = "order-7", ["total"] = 6000m, ["lines"] = new List<object?> { "sku-1" }, ["customerEmail"] = "a@b.c", ["priority"] = false,
    };

    private static WorkflowDefinition Example => WorkflowReader.Read(Golden.Workflow("01-order-fulfilment")).Workflow!;

    private static SimulationResult Started(IReadOnlyDictionary<string, object?>? data = null) => WorkflowSimulator.Start(Example, data ?? Order, T0);

    private static SimulationResult Run(SimulationResult result, params SimulationInput[] inputs)
    {
        foreach (SimulationInput input in inputs)
        {
            result = WorkflowSimulator.Step(result.State, input);
        }

        return result;
    }

    private static Dictionary<string, object?> Captured(string paymentId) => new() { ["paymentId"] = paymentId };

    // ================================================================ the transition table (UT-WF-41..52)

    [Fact]
    [Trait("Id", "UT-WF-41")]
    [Trait("Covers", "WFD-33")]
    public void Start_BindsData_AndEntersTheFirstStep()
    {
        SimulationResult result = Started();

        result.State.Status.ShouldBe(WorkflowStatus.Running);
        result.State.Instance.ShouldBe("order-7");
        result.State.Data["amount"].ShouldBe(6000m);
        result.State.Step.ShouldBe("reserve-stock");
        result.State.Attempt.ShouldBe(1);
        SentCommand sent = result.Sent.Single();
        sent.Command.ShouldBe("inventory.reserve-stock.v1");
        sent.Fields.Select(f => f.Key).ShouldBe(["orderId", "lines"]);
        sent.MessageId.ShouldBe(WorkflowIds.Send("order-fulfilment@1", "order-7", "reserve-stock", 1));
        result.State.Timer.ShouldBe(new SimulationTimer(SimulationTimer.Timeout, "reserve-stock", 1, T0.AddMinutes(5)));
        result.Effects.Select(e => e.Kind).ShouldBe([SimulationEffect.Status, SimulationEffect.Send, SimulationEffect.Schedule]);
        Should.Throw<ArgumentNullException>(() => WorkflowSimulator.Start(null!, Order, T0));
        Should.Throw<ArgumentNullException>(() => WorkflowSimulator.Step(null!, SimulationInput.Succeed()));
    }

    [Fact]
    [Trait("Id", "UT-WF-42")]
    public void Success_Captures_CancelsTheTimer_PushesTheCompensation_AndMovesOn()
    {
        SimulationResult result = Run(Started(), SimulationInput.Succeed(), SimulationInput.Succeed(Captured("pay-1")));

        result.State.Data["paymentId"].ShouldBe("pay-1");
        result.State.CompensationStack.ShouldBe(["reserve-stock", "charge-payment"]);
        result.State.Step.ShouldBe("review-large-order");
        result.Effects.Select(e => e.Kind).ShouldBe([SimulationEffect.Capture, SimulationEffect.Cancel, SimulationEffect.Schedule]);
        result.Sent.ShouldBeEmpty("an await-only step sends nothing");
    }

    [Fact]
    [Trait("Id", "UT-WF-43")]
    public void Rejection_WithRetry_WaitsTheBackoff_ThenResendsWithTheNextAttemptId()
    {
        SimulationResult declined = Run(Started(), SimulationInput.Succeed(), SimulationInput.Reject("payments.declined"));

        declined.State.Phase.ShouldBe(WorkflowPhase.Retrying);
        declined.State.Attempt.ShouldBe(2);
        declined.State.Timer.ShouldBe(new SimulationTimer(SimulationTimer.Retry, "charge-payment", 2, T0.AddMinutes(10)));
        declined.State.Retries["failure:0"].ShouldBe(1);

        SimulationResult resent = Run(declined, SimulationInput.FireTimer());
        resent.State.Now.ShouldBe(T0.AddMinutes(10));
        resent.Sent.Single().MessageId.ShouldBe(WorkflowIds.Send("order-fulfilment@1", "order-7", "charge-payment", 2));
        resent.State.Phase.ShouldBe(WorkflowPhase.Awaiting);
        Run(declined, SimulationInput.Succeed()).Effects.Single().Kind.ShouldBe(SimulationEffect.Ignored, "an outcome while waiting to retry is ignored");
    }

    [Fact]
    [Trait("Id", "UT-WF-44")]
    [Trait("Covers", "WFD-24")]
    public void RetriesExhausted_Fail_CompensatesInReverseOrder()
    {
        SimulationResult result = Run(Started(), SimulationInput.Succeed(), SimulationInput.Succeed(Captured("pay-1")),
            SimulationInput.FailWith("orders.order-rejected-by-review.v1"));

        result.State.Status.ShouldBe(WorkflowStatus.Compensating);
        result.State.Step.ShouldBe("charge-payment", "the most recent first");
        result.Sent.Single().ShouldSatisfyAllConditions(
            c => c.Command.ShouldBe("payments.refund.v1"),
            c => c.Fields.First().Value.ShouldBe("pay-1"),
            c => c.MessageId.ShouldBe(WorkflowIds.Compensation("order-fulfilment@1", "order-7", "charge-payment", 1)));
        result.State.Failure!.Code.ShouldBe("orders.order-rejected-by-review.v1");

        result = Run(result, SimulationInput.Succeed());
        result.State.Step.ShouldBe("reserve-stock");
        result.Sent.Single().Command.ShouldBe("inventory.release-stock.v1");

        result = Run(result, SimulationInput.Succeed());
        result.State.Status.ShouldBe(WorkflowStatus.Compensated);
        result.State.CompensationStack.ShouldBeEmpty();
        result.Sent.Single().ShouldSatisfyAllConditions(
            c => c.Command.ShouldBe("orders.cancel-order.v1"),
            c => c.Fields.Single(f => f.Key == "reason").Value.ShouldBe("orders.order-rejected-by-review.v1"));

        // Exhausting a business retry: declined three times, then a fourth → fail.
        SimulationResult declined = Run(Started(), SimulationInput.Succeed(),
            SimulationInput.Reject("payments.declined"), SimulationInput.FireTimer(),
            SimulationInput.Reject("payments.declined"), SimulationInput.FireTimer(),
            SimulationInput.Reject("payments.declined"), SimulationInput.FireTimer(),
            SimulationInput.Reject("payments.declined"));
        declined.State.Status.ShouldBe(WorkflowStatus.Compensating);
        declined.State.Step.ShouldBe("reserve-stock", "charge-payment never succeeded, so only reserve-stock is undone");
    }

    [Fact]
    [Trait("Id", "UT-WF-45")]
    public void CompensationRetry_TimesOut_RetriesWithBackoff_ThenSucceeds()
    {
        SimulationResult result = Run(Started(), SimulationInput.Succeed(), SimulationInput.Reject("payments.fraud_suspected"));
        result.State.Step.ShouldBe("reserve-stock");
        result.State.Timer!.Due.ShouldBe(T0.AddMinutes(15));

        result = Run(result, SimulationInput.FireTimer());
        result.State.Phase.ShouldBe(WorkflowPhase.Retrying);
        result.State.Timer!.ShouldSatisfyAllConditions(t => t.Kind.ShouldBe(SimulationTimer.Retry), t => t.Due.ShouldBe(T0.AddMinutes(15).AddSeconds(30)));

        result = Run(result, SimulationInput.FireTimer());
        result.Sent.Single().MessageId.ShouldBe(WorkflowIds.Compensation("order-fulfilment@1", "order-7", "reserve-stock", 2));
        Run(result, SimulationInput.Succeed()).State.Status.ShouldBe(WorkflowStatus.Compensated);
    }

    [Fact]
    [Trait("Id", "UT-WF-46")]
    public void CompensationExhausted_GoesToManualIntervention_KeepingTheStack()
    {
        SimulationResult result = Run(Started(), SimulationInput.Succeed(), SimulationInput.Reject("payments.fraud_suspected"));
        for (int i = 0; i < 5; i++)
        {
            result = Run(result, SimulationInput.FireTimer(), SimulationInput.FireTimer());   // timeout, then the retry delay
        }

        result = Run(result, SimulationInput.FireTimer());   // the sixth attempt times out: no retries left

        result.State.Status.ShouldBe(WorkflowStatus.ManualIntervention);
        result.State.CompensationStack.ShouldBe(["reserve-stock"]);
        result.State.CompensationPending.ShouldBeTrue();
        result.Effects.ShouldContain(e => e.Kind == SimulationEffect.Alert);
    }

    [Fact]
    [Trait("Id", "UT-WF-47")]
    public void ThenManual_StopsInManualIntervention_KeepingThePosition()
    {
        SimulationResult result = Run(Started(), SimulationInput.Succeed(), SimulationInput.Succeed(Captured("p")), SimulationInput.FireTimer());

        result.State.Status.ShouldBe(WorkflowStatus.ManualIntervention);
        result.State.Step.ShouldBe("review-large-order");
        result.State.Failure!.Code.ShouldBe("timeout");
        result.State.CompensationPending.ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-WF-48")]
    public void StaleTimeouts_AreIgnored()
    {
        SimulationResult result = Run(Started(), SimulationInput.FireTimer());   // reserve-stock timed out: immediate re-send (no delay)
        result.State.Attempt.ShouldBe(2);
        result.Sent.Single().MessageId.ShouldBe(WorkflowIds.Send("order-fulfilment@1", "order-7", "reserve-stock", 2));

        SimulationResult stale = Run(result, SimulationInput.Timer(SimulationTimer.Timeout, "reserve-stock", 1));
        stale.Effects.Single().ShouldSatisfyAllConditions(e => e.Kind.ShouldBe(SimulationEffect.Ignored), e => e.Text.ShouldContain("stale"));
        stale.State.Attempt.ShouldBe(2);
        Run(result, SimulationInput.Timer(SimulationTimer.Timeout, "reserve-stock", 2)).State.Attempt.ShouldBe(3, "the current attempt's timeout counts");
    }

    [Fact]
    [Trait("Id", "UT-WF-49")]
    public void GotoAfterThePivot_RecoversForward_WithoutCompensation()
    {
        SimulationResult result = Run(Started(), SimulationInput.Succeed(), SimulationInput.Succeed(Captured("p")), SimulationInput.Succeed(),
            SimulationInput.Reject("shipping.address_invalid"));

        result.State.Step.ShouldBe("await-address-fix");
        result.State.Status.ShouldBe(WorkflowStatus.Running);

        result = Run(result, SimulationInput.Succeed());
        result.State.Step.ShouldBe("create-shipment");
        result.Sent.Single().MessageId.ShouldBe(WorkflowIds.Send("order-fulfilment@1", "order-7", "create-shipment", 2),
            "a re-entered step continues its attempt numbering: a new id, so the owner's inbox runs the re-send");
        result.State.Attempt.ShouldBe(2);

        result = Run(result, SimulationInput.Succeed());
        result.State.Status.ShouldBe(WorkflowStatus.Completed);
        result.Sent.Single().ShouldSatisfyAllConditions(c => c.Command.ShouldBe("orders.mark-fulfilled.v1"), c => c.MessageId.ShouldBe(WorkflowIds.Terminal("order-fulfilment@1", "order-7", "completed")));
    }

    [Fact]
    [Trait("Id", "UT-WF-50")]
    public void GuardedStep_IsSkippedWhenFalse()
    {
        var small = new Dictionary<string, object?>(Order) { ["total"] = 100m };

        SimulationResult result = Run(WorkflowSimulator.Start(Example, small, T0), SimulationInput.Succeed(), SimulationInput.Succeed(Captured("p")));

        result.Effects.ShouldContain(e => e.Kind == SimulationEffect.Skip && e.Text == "review-large-order");
        result.State.Step.ShouldBe("create-shipment");
        WorkflowSimulator.Start(Example, small, T0, new SimulationOptions { Guard = _ => true }).State.Data["amount"].ShouldBe(100m);
    }

    [Fact]
    [Trait("Id", "UT-WF-51")]
    public void Resume_AndRetryStep_ContinueFromManualIntervention()
    {
        SimulationResult manual = Run(Started(), SimulationInput.Succeed(), SimulationInput.Succeed(Captured("p")), SimulationInput.FireTimer());

        SimulationResult resumed = Run(manual, SimulationInput.Resume());
        resumed.State.ShouldSatisfyAllConditions(s => s.Status.ShouldBe(WorkflowStatus.Running), s => s.Step.ShouldBe("review-large-order"),
            s => s.Attempt.ShouldBe(2, "a resumed step is a new attempt (new send id, new timer)"));
        Run(manual, SimulationInput.Resume("create-shipment")).Sent.Single().Command.ShouldBe("shipping.create-shipment.v1");
        Run(manual, SimulationInput.Resume("nowhere")).State.Status.ShouldBe(WorkflowStatus.ManualIntervention);
        Run(manual, SimulationInput.RetryStep()).State.Attempt.ShouldBe(2);
        Run(Started(), SimulationInput.Resume()).Effects.Single().Kind.ShouldBe(SimulationEffect.Ignored, "resume needs ManualIntervention");

        // A failed compensation resumes compensating.
        SimulationResult stuck = Run(Started(), SimulationInput.Succeed(), SimulationInput.Reject("payments.fraud_suspected"));
        for (int i = 0; i < 5; i++)
        {
            stuck = Run(stuck, SimulationInput.FireTimer(), SimulationInput.FireTimer());
        }

        stuck = Run(stuck, SimulationInput.FireTimer());
        SimulationResult again = Run(stuck, SimulationInput.Resume());
        again.State.Status.ShouldBe(WorkflowStatus.Compensating);
        again.Sent.Single().Command.ShouldBe("inventory.release-stock.v1");
        Run(again, SimulationInput.Succeed()).State.Status.ShouldBe(WorkflowStatus.Compensated);
        Run(stuck, SimulationInput.RetryStep()).State.Attempt.ShouldBe(7);
    }

    [Fact]
    [Trait("Id", "UT-WF-52")]
    public void Abort_UnhandledOutcomes_AndTerminalInstances()
    {
        SimulationResult running = Run(Started(), SimulationInput.Succeed(), SimulationInput.Succeed(Captured("p")));

        SimulationResult aborted = Run(running, SimulationInput.Abort("customer asked"));
        aborted.State.Status.ShouldBe(WorkflowStatus.Aborted);
        aborted.Sent.ShouldBeEmpty("no compensation unless asked");
        Run(aborted, SimulationInput.Succeed()).Effects.Single().Kind.ShouldBe(SimulationEffect.Ignored, "terminal instances ignore everything");

        SimulationResult undone = Run(running, SimulationInput.Abort("fraud", compensate: true), SimulationInput.Succeed(), SimulationInput.Succeed());
        undone.State.Status.ShouldBe(WorkflowStatus.Aborted, "an abort with compensation ends Aborted, not Compensated");
        undone.Sent.ShouldBeEmpty("on.compensated is not sent for an abort");

        SimulationResult unexpected = Run(Started(), SimulationInput.Reject("inventory.warehouse_closed"));
        unexpected.State.Status.ShouldBe(WorkflowStatus.ManualIntervention, "an undeclared rejection needs an operator");
        unexpected.State.Failure!.Code.ShouldBe("inventory.warehouse_closed");

        WorkflowDefinition guarded = WorkflowReader.Read(Golden.Workflow("27-local-steps")).Workflow!;
        SimulationResult ignored = WorkflowSimulator.Start(guarded, new Dictionary<string, object?> { ["orderId"] = "o", ["total"] = 0m }, T0);
        ignored.Effects.Single().Text.ShouldContain("start.when is false");
    }

    // ================================================================ paths (UT-WF-53..56)

    [Fact]
    [Trait("Id", "UT-WF-53")]
    [Trait("Covers", "WFD-34")]
    public void Paths_BoundLoopsAndRetries()
    {
        PathSet paths = PathEnumerator.Enumerate(Example);

        paths.Paths.Count.ShouldBe(437);
        paths.Truncated.ShouldBeFalse();
        paths.Paths.ShouldAllBe(p => p.Moves.Count(m => m.Step == "create-shipment" && m.Outcome == "success") <= 1);
        paths.Paths.Max(p => p.Moves.Select((m, i) => (m, i)).Count(x => x.m.Step == "create-shipment" && (x.i == 0 || p.Moves[x.i - 1].Step != "create-shipment"))).ShouldBe(2, "a step is entered at most twice");
        int declined = paths.Paths.Select(p => p.Moves.Count(m => m.Edge == "charge-payment:rejected:payments.declined")).Max();
        declined.ShouldBe(4, "retry loops at 0, 1 and max (3 retries: 4 rejections)");
        paths.Paths.ShouldContain(p => p.Moves.Count(m => m.Edge == "charge-payment:rejected:payments.declined") == 1);
        PathEnumerator.Enumerate(Example, new PathOptions { MaxPaths = 10 }).ShouldSatisfyAllConditions(s => s.Paths.Count.ShouldBe(10), s => s.Truncated.ShouldBeTrue());
    }

    [Fact]
    [Trait("Id", "UT-WF-54")]
    public void Paths_EndInEveryReachableStatus_AndReplayFaithfully()
    {
        PathSet paths = PathEnumerator.Enumerate(Example);

        paths.Paths.Select(p => p.End).Distinct().Order().ShouldBe([WorkflowStatus.Completed, WorkflowStatus.Compensated, WorkflowStatus.ManualIntervention]);
        foreach (WorkflowPath path in paths.Paths.Take(50).Concat(paths.Paths.TakeLast(50)))
        {
            PathEnumerator.Replay(Example, path).Last().State.Status.ShouldBe(path.End, path.ToString());
        }

        paths.Paths[0].Id.ShouldBe("P01");
        paths.Paths[0].ToString().ShouldStartWith("P01 reserve-stock: success → ");
    }

    [Fact]
    [Trait("Id", "UT-WF-55")]
    public void Coverage_MarksEveryEdge_AndTheMinimalCoverKeepsIt()
    {
        PathSet paths = PathEnumerator.Enumerate(Example);

        paths.Uncovered.ShouldBeEmpty();
        paths.Edges.ShouldContain("review-large-order:skip");
        paths.Edges.ShouldContain("compensate:charge-payment:retry");
        paths.Covering("create-shipment:rejected:shipping.address_invalid").ShouldNotBeEmpty();
        IReadOnlyList<WorkflowPath> cover = paths.MinimalCover();
        cover.Count.ShouldBeLessThan(20);
        paths.Edges.ShouldAllBe(e => cover.Any(p => p.Moves.Any(m => m.Covers(e))));
        cover.Select(p => p.End).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    [Trait("Id", "UT-WF-56")]
    public void Paths_OfSmallWorkflows()
    {
        PathSet minimal = PathEnumerator.Enumerate(WorkflowReader.Read(Golden.Workflow("02-minimal")).Workflow!);
        minimal.Paths.Select(p => p.ToString()).ShouldBe(["P01 approve: success ⇒ Completed", "P02 approve: timeout ⇒ ManualIntervention"]);
        PathEnumerator.Enumerate(WorkflowReader.Read(Golden.Workflow("27-local-steps")).Workflow!).Paths.Count.ShouldBe(3, "start.when is assumed to hold");
        Should.Throw<ArgumentNullException>(() => PathEnumerator.Enumerate(null!));
    }

    // ================================================================ exporters and specs (UT-WF-57..60)

    [Theory]
    [Trait("Id", "UT-WF-57")]
    [Trait("Covers", "WFD-30;WFD-NFR-4")]
    [MemberData(nameof(ValidCases))]
    public void Mermaid_MatchesTheGoldenFile(string @case)
    {
        string mermaid = MermaidExporter.Export(WorkflowReader.Read(Golden.Workflow(@case)).Workflow!);

        mermaid.ShouldStartWith("---\ntitle: ");
        mermaid.ShouldContain("stateDiagram-v2\n");
        Golden.AssertMatches(@case, "expected.mmd", mermaid);
    }

    [Theory]
    [Trait("Id", "UT-WF-58")]
    [Trait("Covers", "WFD-36")]
    [MemberData(nameof(ValidCases))]
    public void Bpmn_IsAConsistentBpmn20Model(string @case)
    {
        string bpmn = BpmnExporter.Export(WorkflowReader.Read(Golden.Workflow(@case)).Workflow!);

        XDocument document = XDocument.Parse(bpmn);
        XNamespace ns = BpmnExporter.Namespace;
        document.Root!.Name.ShouldBe(ns + "definitions");
        string[] ids = [.. document.Descendants().Select(e => (string?)e.Attribute("id")).OfType<string>()];
        ids.Distinct().Count().ShouldBe(ids.Length, "ids are unique");
        string[] references = [.. document.Descendants().SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "sourceRef" or "targetRef" or "attachedToRef" or "messageRef" or "default")).Select(a => a.Value)];
        references.ShouldAllBe(r => ids.Contains(r));
        XElement process = document.Root.Element(ns + "process")!;
        process.Elements(ns + "startEvent").Count().ShouldBe(1);
        process.Elements(ns + "sequenceFlow").ShouldAllBe(f => process.Elements().Any(e => (string?)e.Attribute("id") == (string)f.Attribute("targetRef")!));
        Golden.AssertMatches(@case, "expected.bpmn", bpmn);
    }

    [Fact]
    [Trait("Id", "UT-WF-59")]
    [Trait("Covers", "WFD-34")]
    public void Specs_AreGeneratedPerPath_AndParseAsCSharp()
    {
        WorkflowDefinition workflow = Example;
        string specs = SpecGenerator.Generate(workflow, Golden.Environment, new SpecOptions { Namespace = "Orders.Tests", ModuleType = "Orders.OrdersModule", WorkflowType = "Orders.OrderFulfilmentWorkflowV1" });

        SyntaxTree tree = CSharpSyntaxTree.ParseText(specs, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: TestContext.Current.CancellationToken);
        tree.GetDiagnostics(TestContext.Current.CancellationToken).ShouldBeEmpty();
        specs.ShouldContain("WorkflowSpec.For<Orders.OrderFulfilmentWorkflowV1, Orders.OrdersModule>()");
        int tests = specs.Split("[Fact").Length - 1;
        tests.ShouldBe(PathEnumerator.Enumerate(workflow).MinimalCover().Count);
        specs.ShouldNotContain("Skip =", Case.Sensitive, "the solver finds data for every guard decision");
        Golden.AssertMatches("01-order-fulfilment", "expected.specs.cs", specs);

        SpecGenerator.WorkflowTypeName(workflow).ShouldBe("OrderFulfilmentWorkflowV1");
        SpecGenerator.Pascal("await-address-fix").ShouldBe("AwaitAddressFix");
        string selected = SpecGenerator.Generate(workflow, Golden.Environment, new SpecOptions { PathIds = ["P01", "P02"] });
        (selected.Split("[Fact").Length - 1).ShouldBe(2);
        (SpecGenerator.Generate(WorkflowReader.Read(Golden.Workflow("02-minimal")).Workflow!, Golden.Environment, new SpecOptions { AllPaths = true }).Split("[Fact").Length - 1).ShouldBe(2);
    }

    [Fact]
    [Trait("Id", "UT-WF-60")]
    [Trait("Covers", "WFD-29")]
    public void WorkflowIds_MatchTheRuntimesUuidV5()
    {
        WorkflowIds.Namespace.ShouldBe(MessageIds.SourceFlowNamespace);
        string name = WorkflowIds.Name("order-fulfilment@1", "order-7", "reserve-stock", 2);
        WorkflowIds.Send("order-fulfilment@1", "order-7", "reserve-stock", 2).ShouldBe(MessageIds.CreateVersion5(MessageIds.SourceFlowNamespace, name));
        WorkflowIds.Version5(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "python.org").ShouldBe(new Guid("886313e1-3b8a-5372-9b90-0c9aee199e5d"), "RFC 9562 test vector");
        WorkflowIds.Compensation("w@1", "i", "s", 1).ShouldNotBe(WorkflowIds.Send("w@1", "i", "s", 1));
    }

    public static TheoryData<string> ValidCases()
    {
        return new TheoryData<string>(Golden.Cases().Select(row => (string)row.Data).Where(c => !WorkflowAnalysis.Analyze(Golden.Workflow(c), Golden.Environment).HasErrors));
    }
}
