using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Simulation;

namespace SourceFlow.Studio.Tests;

/// <summary>UT-WF-77..79 (WP-C.10, CR-19, CR-20): timer identity and due time, and the pivot as a point of no return.</summary>
[Trait("Category", "Unit")]
public sealed class TimerAndPivotTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, object?> Order = new()
    {
        ["orderId"] = "order-7", ["total"] = 6000m, ["lines"] = new List<object?> { "sku-1" }, ["customerEmail"] = "a@b.c", ["priority"] = false,
    };

    // A post-pivot step whose timeout says 'fail' (WF-V07 refuses it; the engine must too, for definitions validated before).
    private const string PastThePivot = """
        sourceflow.workflow: 1
        name: ship-order
        version: 1
        owner: orders
        start: { on: orders.order-placed.v1, correlate: data.orderId }
        data:
          orderId: { from: start.data.orderId }
        steps:
          - id: reserve
            send: { command: inventory.reserve-stock.v1, map: { orderId: $data.orderId } }
            await: { success: inventory.stock-reserved.v1 }
            timeout: { after: PT5M, then: fail }
            compensate:
              send: { command: inventory.release-stock.v1, map: { orderId: $data.orderId } }
              await: { success: inventory.stock-released.v1 }
              timeout: { after: PT15M }
          - id: ship
            pivot: true
            send: { command: shipping.create-shipment.v1, map: { orderId: $data.orderId } }
            await: { success: shipping.shipment-created.v1 }
            timeout: { after: PT1H, then: manual }
          - id: confirm
            send: { command: orders.approve-order.v1, map: { orderId: $data.orderId } }
            await: { success: orders.order-approved.v1 }
            timeout: { after: PT1H, then: fail }
        """;

    private static WorkflowDefinition Example => WorkflowReader.Read(Golden.Workflow("01-order-fulfilment")).Workflow!;

    private static SimulationResult Run(SimulationResult result, params SimulationInput[] inputs)
    {
        foreach (SimulationInput input in inputs)
        {
            result = WorkflowSimulator.Step(result.State, input);
        }

        return result;
    }

    [Fact]
    [Trait("Id", "UT-WF-77")]
    [Trait("Covers", "doc 15 5.1;doc 15 5.3")]
    public void APaymentRejectedWithinFiveMinutes_GivesReleaseStockItsOwnFifteenMinuteTimeout()
    {
        SimulationResult started = WorkflowSimulator.Start(Example, Order, T0);
        SimulationTimer reserveTimeout = started.State.Timer!;   // reserve-stock's 5-minute timeout, cancelled on success
        SimulationResult compensating = Run(started,
            SimulationInput.Receive("inventory.stock-reserved.v1", new Dictionary<string, object?> { ["orderId"] = "order-7" }),
            SimulationInput.RejectedFor("payments.charge.v1", "payments.fraud_suspected", "fraud"));

        compensating.State.Status.ShouldBe(WorkflowStatus.Compensating);
        SimulationTimer compensation = compensating.State.Timer.ShouldNotBeNull();
        compensation.Kind.ShouldBe(SimulationTimer.CompensateTimeout);
        compensation.Step.ShouldBe("compensate:reserve-stock");
        compensation.Due.ShouldBe(compensating.State.Now.AddMinutes(15), "release-stock's own timeout");

        // The cancelled step timeout is still delivered at T0 + 5 min: it is not the compensation's.
        SimulationResult late = Run(compensating, SimulationInput.Timer(reserveTimeout.Kind, reserveTimeout.Step, reserveTimeout.Attempt, reserveTimeout.Due));
        late.Effects.Single().Kind.ShouldBe(SimulationEffect.Ignored);
        late.State.Status.ShouldBe(WorkflowStatus.Compensating);
        late.State.Timer.ShouldBe(compensation);
    }

    [Fact]
    [Trait("Id", "UT-WF-78")]
    [Trait("Covers", "doc 15 5.3")]
    public void ACancelledStepTimer_DeliveredLate_IsIgnored_AndSoIsOneWithTheWrongDueTime()
    {
        SimulationResult started = WorkflowSimulator.Start(Example, Order, T0);
        SimulationTimer reserveTimeout = started.State.Timer!;
        SimulationResult charging = Run(started, SimulationInput.Receive("inventory.stock-reserved.v1", new Dictionary<string, object?> { ["orderId"] = "order-7" }));
        SimulationTimer pending = charging.State.Timer.ShouldNotBeNull();

        Run(charging, SimulationInput.Timer(reserveTimeout.Kind, reserveTimeout.Step, reserveTimeout.Attempt, reserveTimeout.Due))
            .Effects.Single().Kind.ShouldBe(SimulationEffect.Ignored, "the step it belonged to is done");
        Run(charging, SimulationInput.Timer(pending.Kind, pending.Step, pending.Attempt, pending.Due.AddMinutes(-1)))
            .Effects.Single().Kind.ShouldBe(SimulationEffect.Ignored, "same identity, but not the timer that is pending");
        Run(charging, SimulationInput.Timer(pending.Kind, pending.Step, pending.Attempt, pending.Due))
            .State.Attempt.ShouldBe(2, "the pending timer counts");
    }

    [Fact]
    [Trait("Id", "UT-WF-79")]
    [Trait("Covers", "doc 15 2;WF-V07")]
    public void AfterThePivot_CompensationIsRefused_ExceptForAnAuditedOperatorAbort()
    {
        WorkflowDefinition workflow = WorkflowReader.Read(PastThePivot).Workflow!;
        SimulationResult shipped = Run(WorkflowSimulator.Start(workflow, new Dictionary<string, object?> { ["orderId"] = "o-1" }, T0),
            SimulationInput.Receive("inventory.stock-reserved.v1", new Dictionary<string, object?> { ["orderId"] = "o-1" }),
            SimulationInput.Receive("shipping.shipment-created.v1", new Dictionary<string, object?> { ["orderId"] = "o-1" }));
        shipped.State.PivotPassed.ShouldBeTrue();

        SimulationResult failed = Run(shipped, SimulationInput.FireTimer());   // confirm times out: 'then: fail'
        failed.State.Status.ShouldBe(WorkflowStatus.ManualIntervention, "no compensation after the point of no return");
        failed.Sent.ShouldBeEmpty("release-stock is not sent");

        Run(failed, SimulationInput.Resume("reserve")).Effects.ShouldContain(e => e.Kind == SimulationEffect.Ignored && e.Text.Contains("before the pivot"));

        SimulationResult aborted = Run(failed, SimulationInput.Abort("customer cancelled after shipping", compensate: true));
        aborted.Effects.ShouldContain(e => e.Kind == SimulationEffect.Audit);
        aborted.State.Status.ShouldBe(WorkflowStatus.Compensating);
        aborted.Sent.Single().Command.ShouldBe("inventory.release-stock.v1");
    }
}
