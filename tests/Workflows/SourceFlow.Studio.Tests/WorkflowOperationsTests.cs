using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Storage;
using SourceFlow.Storage.InMemory;
using SourceFlow.Studio.Dsl.Json;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Tests.Fixture;
using SourceFlow.Testing;
using SourceFlow.Tool;
using SourceFlow.Workflows;
using SimStatus = SourceFlow.Studio.Dsl.Simulation.WorkflowStatus;

namespace SourceFlow.Studio.Tests;

/// <summary>
/// Runtime support for DSL workflows (WP-S.6, doc 15 §5.6, §7): transition metrics and spans, the workflow-instances read
/// model with its gauge and domain query, the version-removal guard, the operator CLI, and the order-fulfilment paths
/// WF-E2E-09 (forward recovery after the pivot) and WF-E2E-10 (versions side by side) in an in-memory host.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowOperationsTests
{
    private const string Workflow = "order-fulfilment";
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly WorkflowDefinition Definition = WorkflowReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixture", "order-fulfilment.sfflow.yaml"))).Workflow!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ transitions (engine)

    [Fact]
    [Trait("Id", "UT-WF-61")]
    [Trait("Covers", "WFD-37")]
    public void Engine_RecordsEachTransitionOnce_WithItsKind()
    {
        Kinds(Start(100m), Ok(), Ok(Captured()), Ok()).ShouldBe(["success", "success", "success", "complete"], "the review is skipped: no transition");
        Kinds(Start(100m), SimulationInput.Reject("inventory.out_of_stock")).ShouldBe(["failure"], "nothing to compensate");
        Kinds(Start(100m), SimulationInput.FireTimer()).ShouldBe(["timeout", "retry"]);
        Kinds(Start(100m), Ok(), SimulationInput.Reject("payments.declined")).ShouldBe(["success", "retry"], "a business retry is not a failure");
        Kinds(Start(100m), Ok(), SimulationInput.Reject("payments.fraud_suspected"), Ok()).ShouldBe(["success", "failure", "compensate"]);
        Kinds(Start(6000m), Ok(), Ok(Captured()), SimulationInput.FireTimer()).ShouldBe(["success", "success", "timeout", "failure", "manual"]);

        SimulationTransition.Kinds.ShouldBe(WorkflowTransition.Kinds, "the engine and the runtime agree on the kind tag values");
    }

    // ================================================================ metrics and spans (host)

    [Fact]
    [Trait("Id", "UT-WF-62")]
    [Trait("Covers", "WFD-37")]
    public async Task Host_CountsTransitions_OncePerCommittedTransition_WithLowCardinalityTags()
    {
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>();
        using var metrics = new Metrics(host);
        var order = OrderId.New();

        await host.SendAsync(new PlaceOrder(order, 120m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        metrics.Counts(SourceFlowTelemetry.Instruments.WorkflowTransitions, SourceFlowTelemetry.Tags.TransitionKind)
            .ShouldBe(new Dictionary<string, long> { ["success"] = 3, ["complete"] = 1 });
        metrics.Measurements(SourceFlowTelemetry.Instruments.WorkflowTransitions).ShouldAllBe(m =>
            m.Tags.Count == 3 && (string)m.Tags["workflow"]! == Workflow && (string)m.Tags["version"]! == "1");
        metrics.Measurements(SourceFlowTelemetry.Instruments.WorkflowManualIntervention).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "UT-WF-63")]
    [Trait("Covers", "WFD-37")]
    public async Task ManualIntervention_IsCountedOnce_AndAlertedOn()
    {
        var time = new FakeTimeProvider(T0);
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(time);
        using var metrics = new Metrics(host);

        OrderId order = await ToManualAsync(host, time);

        metrics.Measurements(SourceFlowTelemetry.Instruments.WorkflowManualIntervention).ShouldHaveSingleItem().Tags
            .ShouldBe(new Dictionary<string, object?> { ["workflow"] = Workflow, ["version"] = "1" }, ignoreOrder: true);
        (await Summary(host, order)).Status.ShouldBe("ManualIntervention");
        Observability.Alerts.ShouldContain(a => a.Name == "SourceFlowWorkflowManualIntervention"
            && a.Expression.Contains("sourceflow_workflow_manual_intervention_total", StringComparison.Ordinal) && a.Severity == "page");
    }

    [Fact]
    [Trait("Id", "UT-WF-70")]
    [Trait("Covers", "WFD-37")]
    public async Task Transitions_AreSpans_UnderTheReactSpan()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceFlowTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>();
        var order = OrderId.New();
        string instance = WorkflowInstanceId.From(order.ToString()).ToStreamSegment();

        await host.SendAsync(new PlaceOrder(order, 120m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        Activity[] mine = [.. spans.Where(s => s.DisplayName.StartsWith("workflow ", StringComparison.Ordinal) && (string?)s.Parent?.GetTagItem(SourceFlowTelemetry.Tags.ProcessId) == instance)];
        mine.Select(s => s.DisplayName).ShouldBe(
            ["workflow order-fulfilment reserve-stock", "workflow order-fulfilment charge-payment", "workflow order-fulfilment create-shipment", "workflow order-fulfilment create-shipment"],
            ignoreOrder: true);
        Activity shipped = mine.First(s => (string?)s.GetTagItem(SourceFlowTelemetry.Tags.WorkflowTransition) == "complete");
        shipped.Parent!.DisplayName.ShouldBe("react order-fulfilment@1");
        shipped.GetTagItem(SourceFlowTelemetry.Tags.WorkflowStep).ShouldBe("create-shipment");
        shipped.GetTagItem(SourceFlowTelemetry.Tags.WorkflowAttempt).ShouldBe(1);
    }

    // ================================================================ read model, gauge and domain query

    [Fact]
    [Trait("Id", "UT-WF-64")]
    [Trait("Covers", "WFD-37")]
    public async Task InstanceDocument_FollowsTheInstance_AndFeedsTheActiveGauge()
    {
        var time = new FakeTimeProvider(T0);
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(time, monitor: true);
        using var metrics = new Metrics(host);
        var done = OrderId.New();
        await host.SendAsync(new PlaceOrder(done, 120m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        OrderId stuck = await ToManualAsync(host, time);

        WorkflowInstanceSummary completed = await Summary(host, done);
        completed.ShouldSatisfyAllConditions(
            s => s.Status.ShouldBe("Completed"),
            s => s.Step.ShouldBeNull(),
            s => s.StartedAt.ShouldBe(T0),
            s => s.IsActive.ShouldBeFalse());
        WorkflowInstanceSummary manual = await Summary(host, stuck);
        manual.ShouldSatisfyAllConditions(
            s => s.Step.ShouldBe("review-large-order"),
            s => s.FailureCode.ShouldBe("timeout"),
            s => s.PendingCompensations.ShouldBe(2, "reserve-stock and charge-payment would be undone"),
            s => s.UpdatedAt.ShouldBeGreaterThan(s.StartedAt));

        (await host.Store.CountActiveWorkflowsAsync(Ct)).ShouldBe([new WorkflowStatusCount(Workflow, 1, "ManualIntervention", 1)]);
        (await metrics.GaugeAsync(SourceFlowTelemetry.Instruments.WorkflowActive, m => (string?)m.Tags.GetValueOrDefault("status") == "ManualIntervention")).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            m => m.Value.ShouldBe(1),
            m => m.Tags.ShouldBe(new Dictionary<string, object?> { ["workflow"] = Workflow, ["version"] = "1", ["status"] = "ManualIntervention" }, ignoreOrder: true));
    }

    [Fact]
    [Trait("Id", "UT-WF-65")]
    [Trait("Covers", "WFD-37")]
    public async Task WorkflowInstancesQuery_BindsAtStartup_AndReturnsTheInstances()
    {
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>();
        var order = OrderId.New();
        await host.SendAsync(new PlaceOrder(order, 120m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        host.Runtime.Model.DomainQueries.ShouldContain(q => q.Name == "orders.workflow-instances" && q.Version == 1, "SF0018: checked against the catalog at startup");
        WorkflowInstancesQuery.QueryName.ShouldBe("orders.workflow-instances", "the workflows' owner names the service");
        IReadOnlyList<WorkflowInstanceSummary> rows = await host.Services.GetRequiredService<IQueryService>().ExecuteAsync(new WorkflowInstancesQuery(Workflow), cancellationToken: Ct);

        rows.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Instance.ShouldBe(order.ToString()),
            r => r.WorkflowVersion.ShouldBe(1),
            r => r.Status.ShouldBe("Completed"),
            r => r.ProcessName.ShouldBe("order-fulfilment@1"));
        (await host.Services.GetRequiredService<IQueryService>().ExecuteAsync(new WorkflowInstancesQuery("other"), cancellationToken: Ct)).ShouldBeEmpty();
    }

    // ================================================================ version guard

    [Fact]
    [Trait("Id", "UT-WF-66")]
    [Trait("Covers", "WFD-35")]
    public async Task Startup_IsBlocked_WhileARemovedVersionHasActiveInstances()
    {
        var store = new InMemoryStore(TimeProvider.System);
        await Seed(store, "order-fulfilment@2", "wf_01", completed: false);
        await Seed(store, "order-fulfilment@2", "wf_02", completed: true);
        await Seed(store, "other-flow@1", "wf_03", completed: false);

        WorkflowVersionRemovedException blocked = await Should.ThrowAsync<WorkflowVersionRemovedException>(() => Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(store: store));
        blocked.Message.ShouldContain("order-fulfilment@2 has 1 active instance(s)");
        blocked.Message.ShouldNotContain("other-flow", Case.Sensitive, "another workflow's instances are not this service's concern");

        await using (await Host<FixtureModule>(store: store))
        {
            // version 2 registered again: it starts
        }

        await Seed(store, "order-fulfilment@2", "wf_01", completed: true, version: 1);
        await using SourceFlowTestHost removed = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(store: store);
        removed.Runtime.Model.ProcessManagers.ShouldNotContain(p => p.Name == "order-fulfilment@2", "removable once every instance finished");
    }

    // ================================================================ CLI

    [Fact]
    [Trait("Id", "UT-WF-67")]
    [Trait("Covers", "WFD-35;WFD-37")]
    public async Task Cli_ListsAndShowsInstances_AndSaysWhetherAVersionCanBeRemoved()
    {
        var time = new FakeTimeProvider(T0);
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(time);
        var done = OrderId.New();
        await host.SendAsync(new PlaceOrder(done, 120m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        OrderId stuck = await ToManualAsync(host, time);

        (int code, string output, _) = await Cli(host, "workflow", "list", "--active");
        code.ShouldBe(0);
        output.ShouldContain(stuck.ToString());
        output.ShouldNotContain(done.ToString());
        output.ShouldContain("ManualIntervention");
        output.ShouldContain("1 instance(s)");

        (_, string all, _) = await Cli(host, "workflow", "list", "--name", Workflow, "--version", "1");
        all.ShouldContain("2 instance(s)");
        all.ShouldContain("order-fulfilment@1: 1 active instance(s) across tenants; keep it until they finish");
        (await Cli(host, "workflow", "list", "--name", Workflow, "--version", "2")).Output.ShouldContain("order-fulfilment@2: no active instances in any tenant; it can be removed.");

        (int shown, string show, _) = await Cli(host, "workflow", "show", "order-fulfilment@1", stuck.ToString());
        shown.ShouldBe(0);
        show.ShouldContain("status:        ManualIntervention");
        show.ShouldContain("step:          review-large-order (attempt 1)");
        show.ShouldContain("failure:       timeout at review-large-order");
        (await Cli(host, "workflow", "show", "order-fulfilment@1", "nope")).ShouldSatisfyAllConditions(r => r.Code.ShouldBe(1), r => r.Error.ShouldContain("No instance 'nope'"));
        (await Cli(host, "workflow", "show", "order-fulfilment", stuck.ToString())).Error.ShouldContain("name@version");
    }

    [Fact]
    [Trait("Id", "UT-WF-68")]
    [Trait("Covers", "WFD-35")]
    public async Task Cli_Resume_SendsWorkflowControl_ThroughTheScheduler()
    {
        var time = new FakeTimeProvider(T0);
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(time);
        OrderId order = await ToManualAsync(host, time);

        (int code, string output, _) = await Cli(host, "workflow", "resume", "order-fulfilment@1", order.ToString(), "--step", "create-shipment", "--note", "approved by phone");
        code.ShouldBe(0);
        output.ShouldBe("Sent resume to order-fulfilment@1 " + order + Environment.NewLine);
        await host.WaitForIdleAsync();

        (await Summary(host, order)).Status.ShouldBe("Completed");
        (await host.EventsAsync(StreamId.For("fixture-shipment", order.ToStreamSegment()))).ShouldHaveSingleItem().ShouldBeOfType<ShipmentCreated>();
        (await Cli(host, "workflow", "resume", "order-fulfilment@1", order.ToString())).ShouldSatisfyAllConditions(
            r => r.Code.ShouldBe(1),
            r => r.Error.ShouldContain("is Completed: 'resume' needs ManualIntervention"));
    }

    [Fact]
    [Trait("Id", "UT-WF-69")]
    [Trait("Covers", "WFD-35")]
    public async Task Cli_RetryStep_AndAbort_ActOnTheInstance()
    {
        var time = new FakeTimeProvider(T0);
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(time);
        OrderId retried = await ToManualAsync(host, time);
        OrderId aborted = await ToManualAsync(host, time);

        (await Cli(host, "workflow", "retry-step", "order-fulfilment@1", retried.ToString())).Code.ShouldBe(0);
        (await Cli(host, "workflow", "abort", "order-fulfilment@1", aborted.ToString(), "--reason", "customer cancelled")).Code.ShouldBe(0);
        await host.WaitForIdleAsync();

        (await Summary(host, retried)).ShouldSatisfyAllConditions(s => s.Status.ShouldBe("Running"), s => s.Step.ShouldBe("review-large-order"), s => s.Attempt.ShouldBe(2));
        (await Summary(host, aborted)).Status.ShouldBe("Aborted");
        (await Cli(host, "workflow", "abort", "order-fulfilment@1", aborted.ToString(), "--reason", "again")).Error.ShouldContain("only an unfinished instance can be aborted");
        (await Cli(host, "workflow", "abort", "order-fulfilment@1", retried.ToString())).Code.ShouldNotBe(0, "--reason is required (audited)");
    }

    // ================================================================ end to end (in-memory)

    [Fact]
    [Trait("Id", "WF-E2E-09")]
    [Trait("Category", "Workflow")]
    [Trait("Covers", "WFD-22;WFD-25")]
    public async Task AfterThePivot_AnInvalidAddress_RecoversForward_WithoutCompensation()
    {
        await using SourceFlowTestHost host = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>();
        using var metrics = new Metrics(host);
        var order = OrderId.New();

        await host.SendAsync(new PlaceOrder(order, 120m, ["sku-1", "bad-address"], "a@b.c", Priority: false), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        (await Summary(host, order)).ShouldSatisfyAllConditions(s => s.Status.ShouldBe("Running"), s => s.Step.ShouldBe("await-address-fix"));

        await host.SendAsync(new FixAddress(order), cancellationToken: Ct);
        await host.SendAsync(new CorrectAddress(order), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        (await Summary(host, order)).Status.ShouldBe("Completed");
        (await host.EventsAsync(StreamId.For("fixture-shipment", order.ToStreamSegment()))).Select(e => e.GetType().Name)
            .ShouldBe(["ShipmentAddressFixed", "ShipmentCreated"], "the re-entered step was sent again with a new id, so the owner's inbox ran it");
        (await host.EventsAsync(StreamId.For("fixture-order", order.ToStreamSegment()))).Select(e => e.GetType().Name)
            .ShouldBe(["OrderPlaced", "AddressCorrected", "OrderFulfilled"], "forward recovery: nothing cancelled");
        metrics.Counts(SourceFlowTelemetry.Instruments.WorkflowTransitions, SourceFlowTelemetry.Tags.TransitionKind).ShouldNotContainKey("compensate");
    }

    [Fact]
    [Trait("Id", "WF-E2E-10")]
    [Trait("Category", "Workflow")]
    [Trait("Covers", "WFD-35")]
    public async Task Version2_StartsNewOrders_WhileVersion1Finishes_AndVersion1CantBeRemovedUntilThen()
    {
        var store = new InMemoryStore(TimeProvider.System);
        var inFlight = OrderId.New();
        await using (SourceFlowTestHost before = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(store: store))
        {
            await before.SendAsync(new PlaceOrder(inFlight, 6000m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
            await before.WaitForIdleAsync();
            (await Summary(before, inFlight)).Step.ShouldBe("review-large-order", "version 1 waits for the review");
        }

        (await Should.ThrowAsync<WorkflowVersionRemovedException>(() => Host<FixtureWithout<OrderFulfilmentWorkflowV1>>(store: store)))
            .Message.ShouldContain("order-fulfilment@1 has 1 active instance(s)");

        await using (SourceFlowTestHost both = await Host<FixtureModule>(store: store))
        {
            var fresh = OrderId.New();
            await both.SendAsync(new PlaceOrder(fresh, 6000m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
            await both.SendAsync(new ApproveOrder(inFlight), cancellationToken: Ct);
            await both.WaitForIdleAsync();

            (await Summary(both, fresh, version: 2)).Status.ShouldBe("Completed", "version 2 has no review step");
            (await both.Runtime.Storage.Documents.GetAsync(MessageEnvelope.DefaultTenant, WorkflowInstanceSummary.Collection, WorkflowInstanceSummary.DocumentId("order-fulfilment@1", fresh.ToString()), Ct))
                .ShouldBeNull("new instances start on the latest version only");
            (await Summary(both, inFlight)).Status.ShouldBe("Completed", "the in-flight instance finished on version 1");
            (await both.Runtime.Storage.Documents.GetAsync(MessageEnvelope.DefaultTenant, WorkflowInstanceSummary.Collection, WorkflowInstanceSummary.DocumentId("order-fulfilment@2", inFlight.ToString()), Ct))
                .ShouldBeNull();
        }

        await using SourceFlowTestHost after = await Host<FixtureWithout<OrderFulfilmentWorkflowV1>>(store: store);
        after.Runtime.Model.ProcessManagers.Select(p => p.Name).ShouldContain("order-fulfilment@2");
    }

    [Fact]
    [Trait("Id", "UT-PM-21")]
    [Trait("Covers", "doc 15 5.6")]
    public async Task ANodeThatDoesntKnowVersion1_StillLeavesItsInstancesAlone()
    {
        // A rolling deploy: a node still on version 1 starts an order; a node already on version 2 (which doesn't register
        // version 1, so can't see its state) reads the same start event. The shared claim keeps it to one instance.
        var store = new InMemoryStore(TimeProvider.System);
        var order = OrderId.New();
        await using (SourceFlowTestHost v1 = await Host<FixtureWithout<OrderFulfilmentWorkflowV2>>(store: store))
        {
            await v1.SendAsync(new PlaceOrder(order, 120m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
            await v1.WaitForIdleAsync();
            (await Summary(v1, order)).Status.ShouldBe("Completed");
        }

        await using SourceFlowTestHost v2 = await SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "orders";
            sf.AddModule<FixtureWithout<OrderFulfilmentWorkflowV1>>();
            sf.ProcessManager<OrderFulfilmentWorkflowV2>(p => p.StartFrom(SourceFlow.Hosting.SubscriptionStart.Beginning));   // it reads the past start event
        }, store: store, cancellationToken: Ct);
        var fresh = OrderId.New();
        await v2.SendAsync(new PlaceOrder(fresh, 120m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        await v2.WaitForIdleAsync();

        (await Summary(v2, fresh, version: 2)).Status.ShouldBe("Completed", "a new order starts on version 2");
        await using IStorageSession session = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        string segment = WorkflowInstanceId.From(order.ToString()).ToStreamSegment();
        (await session.LoadProcessStateAsync("order-fulfilment@2", segment, Ct)).ShouldBeNull("version 1 claimed that order");
        System.Text.Encoding.UTF8.GetString((await session.LoadProcessStateAsync("workflow-claim:order-fulfilment", segment, Ct)).ShouldNotBeNull().State.Span)
            .ShouldContain("order-fulfilment@1");
        System.Text.Encoding.UTF8.GetString((await session.LoadProcessStateAsync("workflow-claim:order-fulfilment", WorkflowInstanceId.From(fresh.ToString()).ToStreamSegment(), Ct)).ShouldNotBeNull().State.Span)
            .ShouldContain("order-fulfilment@2");
    }

    // ================================================================ helpers

    private static Task<SourceFlowTestHost> Host<TModule>(TimeProvider? time = null, InMemoryStore? store = null, bool monitor = false)
        where TModule : ISourceFlowModule
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "orders";
            sf.AddModule<TModule>();
            if (monitor)
            {
                sf.Configure(o => o.MonitorInterval = TimeSpan.FromMilliseconds(100));
            }
        }, time, store: store, cancellationToken: Ct);
    }

    // A large order waits for its review; the review times out after a day: manual intervention.
    private static async Task<OrderId> ToManualAsync(SourceFlowTestHost host, FakeTimeProvider time)
    {
        var order = OrderId.New();
        await host.SendAsync(new PlaceOrder(order, 6000m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        await host.WaitForIdleAsync();
        time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        await host.WaitForIdleAsync();
        (await Summary(host, order)).Status.ShouldBe("ManualIntervention");
        return order;
    }

    private static async Task<WorkflowInstanceSummary> Summary(SourceFlowTestHost host, OrderId order, int version = 1)
    {
        StoredDocument document = (await host.Runtime.Storage.Documents.GetAsync(MessageEnvelope.DefaultTenant, WorkflowInstanceSummary.Collection,
            WorkflowInstanceSummary.DocumentId($"{Workflow}@{version}", order.ToString()), Ct)).ShouldNotBeNull();
        return WorkflowRuntime.DeserializeSummary(document.Data.Span);
    }

    private static async Task Seed(InMemoryStore store, string process, string instance, bool completed, long version = 0)
    {
        await using IStorageSession session = await store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        session.SaveProcessState(process, instance, version, "{\"state\":null}"u8.ToArray(), completed);
        await session.CommitAsync(Ct);
    }

    private static async Task<(int Code, string Output, string Error)> Cli(SourceFlowTestHost host, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await SourceFlowCli.RunAsync(args, host.Services, output, error, Ct);
        return (code, output.ToString(), error.ToString());
    }

    private static SimulationResult Start(decimal total)
    {
        var data = new JsonObject { ["orderId"] = "order-7", ["total"] = total, ["lines"] = new JsonArray("sku-1"), ["customerEmail"] = "a@b.c", ["priority"] = false };
        return WorkflowSimulator.Start(Definition, (Dictionary<string, object?>)SimulationStateJson.FromJson(JsonLite.Parse(data.ToJsonString()))!, T0);
    }

    private static SimulationInput Ok(IReadOnlyDictionary<string, object?>? data = null) => SimulationInput.Succeed(data);

    private static Dictionary<string, object?> Captured() => new() { ["paymentId"] = "p-1" };

    private static string[] Kinds(SimulationResult start, params SimulationInput[] inputs)
    {
        var kinds = new List<string>(start.Transitions.Select(t => t.Kind));
        SimulationResult current = start;
        foreach (SimulationInput input in inputs)
        {
            current = WorkflowSimulator.Step(current.State, input);
            kinds.AddRange(current.Transitions.Select(t => t.Kind));
        }

        current.State.Status.ShouldNotBe(SimStatus.Aborted);
        return [.. kinds];
    }

    /// <summary>Listens to one host's workflow instruments.</summary>
    private sealed class Metrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Name, long Value, Dictionary<string, object?> Tags)> _measurements = new();

        public Metrics(SourceFlowTestHost host)
        {
            Meter meter = host.Runtime.Metrics.Meter;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter == meter && instrument.Name.StartsWith("sourceflow.workflow.", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, value, tags, _) => Add(i, value, tags));
            _listener.SetMeasurementEventCallback<int>((i, value, tags, _) => Add(i, value, tags));
            _listener.Start();
        }

        public IReadOnlyList<(string Name, long Value, Dictionary<string, object?> Tags)> Measurements(string instrument)
        {
            return [.. _measurements.Where(m => m.Name == instrument)];
        }

        public Dictionary<string, long> Counts(string instrument, string tag)
        {
            return Measurements(instrument).GroupBy(m => (string)m.Tags[tag]!).ToDictionary(g => g.Key, g => g.Sum(m => m.Value));
        }

        // Samples the observable gauges until the monitor's latest reading has a measurement that satisfies until.
        public async Task<IReadOnlyList<(string Name, long Value, Dictionary<string, object?> Tags)>> GaugeAsync(
            string instrument, Func<(string Name, long Value, Dictionary<string, object?> Tags), bool> until)
        {
            for (int i = 0; i < 500; i++)
            {
                _measurements.Clear();
                _listener.RecordObservableInstruments();
                if (Measurements(instrument) is { Count: > 0 } found && found.Any(until))
                {
                    return found;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
            }

            return [];
        }

        public void Dispose()
        {
            _listener.Dispose();
        }

        private void Add(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            _measurements.Enqueue((instrument.Name, value, map));
        }
    }
}
