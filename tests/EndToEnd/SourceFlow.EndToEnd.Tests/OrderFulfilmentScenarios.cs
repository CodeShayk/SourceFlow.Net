using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderFulfilment.NextRelease;
using Orders;
using Payments;
using SourceFlow.Cloud.Tests.Aws;
using SourceFlow.Cloud.Tests.Azure;
using SourceFlow.Cloud.Tests.Gcp;
using SourceFlow.Diagnostics;
using SourceFlow.Hosting;
using SourceFlow.Runtime;
using SourceFlow.Storage.Tests.PostgreSql;
using SourceFlow.Testing;
using SourceFlow.Workflows;
using OrderId = Orders.OrderId;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>
/// The OrderFulfilment sample's order-fulfilment workflow between its four services (plan 10 §3.2, WF-E2E-01..12): the
/// doc 15 §5.1 paths (success, business retries, compensation in reverse order, timeouts, manual intervention and resume,
/// the review guard, forward recovery after the pivot), versions side by side, duplicate delivery, and crashes.
/// </summary>
/// <remarks>
/// WF-E2E-01..03 run on PostgreSQL over every transport; WF-E2E-04..09 on the in-memory store and on PostgreSQL (in-memory
/// broker); WF-E2E-10 on PostgreSQL; WF-E2E-11 on the three cloud emulators; WF-E2E-12 (chaos, nightly) on PostgreSQL.
/// The cloud legs move to the real cloud with <c>SOURCEFLOW_REAL_CLOUD</c> (nightly).
/// </remarks>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "EndToEnd")]
[Trait("Category", "Workflow")]
public sealed class OrderFulfilmentScenarios(PostgreSqlServer postgreSql, LocalStackServer localStack, PubSubEmulatorServer pubSub, ServiceBusEmulatorServer serviceBus)
{
    private const string Placed = "orders.order-placed.v1";
    private const string Fulfilled = "orders.order-fulfilled.v1";
    private const string Cancelled = "orders.order-cancelled.v1";
    private const string Reserved = "inventory.stock-reserved.v1";
    private const string Released = "inventory.stock-released.v1";
    private const string Captured = "payments.payment-captured.v1";
    private const string Refunded = "payments.refunded.v1";
    private const string Shipped = "shipping.shipment-created.v1";

    // The orders service's clock only has to pass a timer's due time; a second more avoids an edge.
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private Servers Servers => new(postgreSql, localStack, pubSub, serviceBus);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ WF-E2E-01: the happy path

    [Fact]
    [Trait("Id", "WF-E2E-01")]
    [Trait("Requires", "Docker")]
    [Trait("Covers", "WFD-29;WFD-30")]
    public Task HappyPath_Completes_InMemory() => HappyPathAsync(Broker.InMemory);

    [Fact]
    [Trait("Id", "WF-E2E-01")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    public Task HappyPath_Completes_Aws() => HappyPathAsync(Broker.Aws);

    [Fact]
    [Trait("Id", "WF-E2E-01")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    public Task HappyPath_Completes_Gcp() => HappyPathAsync(Broker.Gcp);

    [Fact]
    [Trait("Id", "WF-E2E-01")]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    public Task HappyPath_Completes_Azure() => HappyPathAsync(Broker.Azure);

    // ================================================================ WF-E2E-02: declined twice, then captured

    [Fact]
    [Trait("Id", "WF-E2E-02")]
    [Trait("Requires", "Docker")]
    [Trait("Covers", "WFD-30")]
    public Task DeclinedTwice_ThenCaptured_Completes_InMemory() => DeclinedTwiceThenCapturedAsync(Broker.InMemory);

    [Fact]
    [Trait("Id", "WF-E2E-02")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    public Task DeclinedTwice_ThenCaptured_Completes_Aws() => DeclinedTwiceThenCapturedAsync(Broker.Aws);

    [Fact]
    [Trait("Id", "WF-E2E-02")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    public Task DeclinedTwice_ThenCaptured_Completes_Gcp() => DeclinedTwiceThenCapturedAsync(Broker.Gcp);

    [Fact]
    [Trait("Id", "WF-E2E-02")]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    public Task DeclinedTwice_ThenCaptured_Completes_Azure() => DeclinedTwiceThenCapturedAsync(Broker.Azure);

    // ================================================================ WF-E2E-03: declined four times, compensated

    [Fact]
    [Trait("Id", "WF-E2E-03")]
    [Trait("Requires", "Docker")]
    [Trait("Covers", "WFD-30;WFD-31")]
    public Task DeclinedFourTimes_IsCompensated_InMemory() => DeclinedFourTimesAsync(Broker.InMemory);

    [Fact]
    [Trait("Id", "WF-E2E-03")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    public Task DeclinedFourTimes_IsCompensated_Aws() => DeclinedFourTimesAsync(Broker.Aws);

    [Fact]
    [Trait("Id", "WF-E2E-03")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    public Task DeclinedFourTimes_IsCompensated_Gcp() => DeclinedFourTimesAsync(Broker.Gcp);

    [Fact]
    [Trait("Id", "WF-E2E-03")]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    public Task DeclinedFourTimes_IsCompensated_Azure() => DeclinedFourTimesAsync(Broker.Azure);

    // ================================================================ WF-E2E-04..09: in-memory store and PostgreSQL

    [Fact]
    [Trait("Id", "WF-E2E-04")]
    [Trait("Covers", "WFD-31")]
    public Task OutOfStock_IsCompensated_WithNothingToUndo_InMemory() => OutOfStockAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "WF-E2E-04")]
    [Trait("Requires", "Docker")]
    public Task OutOfStock_IsCompensated_WithNothingToUndo_PostgreSql() => OutOfStockAsync(Store.PostgreSql);

    [Fact]
    [Trait("Id", "WF-E2E-05")]
    [Trait("Covers", "WFD-30;WFD-31")]
    public Task PaymentTimesOutThreeTimes_IsCompensated_InMemory() => PaymentTimesOutAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "WF-E2E-05")]
    [Trait("Requires", "Docker")]
    public Task PaymentTimesOutThreeTimes_IsCompensated_PostgreSql() => PaymentTimesOutAsync(Store.PostgreSql);

    [Fact]
    [Trait("Id", "WF-E2E-06")]
    [Trait("Covers", "WFD-31;WFD-37")]
    public Task FailingCompensation_NeedsAnOperator_InMemory() => FailingCompensationAsync(Store.InMemory, resume: false);

    [Fact]
    [Trait("Id", "WF-E2E-06")]
    [Trait("Requires", "Docker")]
    public Task FailingCompensation_NeedsAnOperator_PostgreSql() => FailingCompensationAsync(Store.PostgreSql, resume: false);

    [Fact]
    [Trait("Id", "WF-E2E-07")]
    [Trait("Covers", "WFD-31;WFD-37")]
    public Task OperatorResume_FinishesTheCompensation_InMemory() => FailingCompensationAsync(Store.InMemory, resume: true);

    [Fact]
    [Trait("Id", "WF-E2E-07")]
    [Trait("Requires", "Docker")]
    public Task OperatorResume_FinishesTheCompensation_PostgreSql() => FailingCompensationAsync(Store.PostgreSql, resume: true);

    [Fact]
    [Trait("Id", "WF-E2E-08")]
    [Trait("Covers", "WFD-30")]
    public Task LargeOrder_IsReviewed_ThenShipped_InMemory() => LargeOrderAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "WF-E2E-08")]
    [Trait("Requires", "Docker")]
    public Task LargeOrder_IsReviewed_ThenShipped_PostgreSql() => LargeOrderAsync(Store.PostgreSql);

    [Fact]
    [Trait("Id", "WF-E2E-09")]
    [Trait("Covers", "WFD-31")]
    public Task InvalidAddress_AfterThePivot_IsRecoveredForward_InMemory() => InvalidAddressAsync(Store.InMemory);

    [Fact]
    [Trait("Id", "WF-E2E-09")]
    [Trait("Requires", "Docker")]
    public Task InvalidAddress_AfterThePivot_IsRecoveredForward_PostgreSql() => InvalidAddressAsync(Store.PostgreSql);

    // ================================================================ scenarios

    // An order is reserved, charged and shipped; the workflow marks it fulfilled. Nothing was retried or undone.
    private async Task HappyPathAsync(Broker broker)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, Store.PostgreSql, broker);
        var order = OrderId.New();

        await system.PlaceAsync(order, 120m);

        WorkflowInstanceSummary done = await system.WaitAsync(order, s => s.Status == "Completed", "the order to be fulfilled");
        done.PendingCompensations.ShouldBe(2, "reserve-stock and charge-payment pushed their compensations, which never ran");
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, Fulfilled], "the workflow marked the order fulfilled", system);
        (await system.ReservationEventsAsync(order)).ShouldBe([Reserved]);
        (await system.PaymentEventsAsync(order)).ShouldBe([Captured]);
        (await system.ShipmentEventsAsync(order)).ShouldBe([Shipped]);
        system.Gateway.Attempts(order).ShouldBe(1);
    }

    // The issuer declines twice; the workflow retries after its fixed backoff (10 minutes on the orders clock) and the
    // third charge is captured.
    private async Task DeclinedTwiceThenCapturedAsync(Broker broker)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, Store.PostgreSql, broker);
        var order = OrderId.New();
        system.Gateway.Script(order, GatewayOutcome.Declined, GatewayOutcome.Declined);

        await system.PlaceAsync(order, 120m);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            await system.WaitForPhaseAsync(order, "charge-payment", "Retrying", attempt + 1, $"decline {attempt} to be retried later");
            system.Gateway.Attempts(order).ShouldBe(attempt, "the backoff hasn't passed: no charge yet");
            system.Advance(TimeSpan.FromMinutes(10) + Second);
        }

        await system.WaitAsync(order, s => s.Status == "Completed", "the third charge to be captured");
        system.Gateway.Attempts(order).ShouldBe(3);
        system.Gateway.Captures(order).ShouldBe(1);
        (await system.PaymentEventsAsync(order)).ShouldBe([Captured]);
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, Fulfilled], "the order to be fulfilled", system);
    }

    // Declined four times: the retries (3) are used up, the step fails, and what was done is undone in reverse order.
    // The charge never succeeded, so there's nothing to refund: the stock is released, then the order cancelled.
    private async Task DeclinedFourTimesAsync(Broker broker)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, Store.PostgreSql, broker);
        var order = OrderId.New();
        system.Gateway.Script(order, GatewayOutcome.Declined, GatewayOutcome.Declined, GatewayOutcome.Declined, GatewayOutcome.Declined);

        await system.PlaceAsync(order, 120m);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await system.WaitForPhaseAsync(order, "charge-payment", "Retrying", attempt + 1, $"decline {attempt} to be retried later");
            system.Advance(TimeSpan.FromMinutes(10) + Second);
        }

        WorkflowInstanceSummary done = await system.WaitAsync(order, s => s.Status == "Compensated", "the order to be compensated");
        done.FailureCode.ShouldBe(PaymentErrors.Declined);
        done.FailureStep.ShouldBe("charge-payment");
        system.Gateway.Attempts(order).ShouldBe(4);
        (await system.PaymentEventsAsync(order)).ShouldBeEmpty("nothing was captured, so nothing is refunded");
        (await system.ReservationEventsAsync(order)).ShouldBe([Reserved, Released]);
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, Cancelled], "the order to be cancelled", system);
    }

    // The first step fails: nothing was done yet, so the compensation stack is empty and the order is cancelled at once.
    private async Task OutOfStockAsync(Store store)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, store, Broker.InMemory);
        var order = OrderId.New();
        system.Stock.OutOfStock("sku-lamp");

        await system.PlaceAsync(order, 80m, ["sku-teapot", "sku-lamp"]);

        WorkflowInstanceSummary done = await system.WaitAsync(order, s => s.Status == "Compensated", "the order to be compensated");
        done.FailureCode.ShouldBe(Inventory.InventoryErrors.OutOfStock);
        done.PendingCompensations.ShouldBe(0);
        (await system.ReservationEventsAsync(order)).ShouldBeEmpty();
        system.Gateway.Attempts(order).ShouldBe(0, "the workflow never got to the payment");
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, Cancelled], "the order to be cancelled", system);
    }

    // Payments is down: every charge is lost to the outage, and the workflow only sees its timeouts (2 minutes, retried
    // twice). The third timeout fails the step; the reserved stock is released and the order cancelled.
    private async Task PaymentTimesOutAsync(Store store)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, store, Broker.InMemory);
        var order = OrderId.New();
        system.Outage(PaymentsService.Name);

        await system.PlaceAsync(order, 120m);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await system.WaitForPhaseAsync(order, "charge-payment", "Awaiting", attempt, $"charge attempt {attempt} to be sent");
            system.Advance(TimeSpan.FromMinutes(2) + Second);
        }

        WorkflowInstanceSummary done = await system.WaitAsync(order, s => s.Status == "Compensated", "the order to be compensated");
        done.FailureStep.ShouldBe("charge-payment");
        system.Gateway.Attempts(order).ShouldBe(0, "payments never handled a charge");
        (await system.ReservationEventsAsync(order)).ShouldBe([Reserved, Released]);
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, Cancelled], "the order to be cancelled", system);
    }

    // Fraud is suspected after the stock was reserved, and Inventory is down: releasing the stock times out (15 minutes)
    // through all its retries (5, with backoff), so the workflow stops in ManualIntervention, keeps the release on its
    // stack and counts the alert metric once. With resume, Inventory is back and the operator resumes the instance: the
    // release is sent again and the compensation finishes.
    private async Task FailingCompensationAsync(Store store, bool resume)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, store, Broker.InMemory);
        using var alerts = new ManualInterventions(system.Orders);
        var order = OrderId.New();
        system.Gateway.Script(order, GatewayOutcome.FraudSuspected);
        system.Outage(PaymentsService.Name);

        await system.PlaceAsync(order, 120m);
        await system.WaitForPhaseAsync(order, "charge-payment", "Awaiting", 1, "the charge to be sent while payments is down");
        system.Outage(Inventory.InventoryService.Name);   // from now on, inventory loses what it receives
        system.Outage(PaymentsService.Name, down: false);
        system.Advance(TimeSpan.FromMinutes(2) + Second);   // the lost charge times out and is sent again: fraud is suspected

        // While compensating, the instance stays on the step it undoes; a retry waits with the next attempt's number.
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            await system.WaitForPhaseAsync(order, "reserve-stock", "Awaiting", attempt, $"release attempt {attempt} to be sent", "Compensating");
            system.Advance(TimeSpan.FromMinutes(15) + Second);
            if (attempt < 6)
            {
                await system.WaitForPhaseAsync(order, "reserve-stock", "Retrying", attempt + 1, $"release attempt {attempt} to time out", "Compensating");
                system.Advance(TimeSpan.FromMinutes(10) + Second);   // the longest backoff (the cap)
            }
        }

        WorkflowInstanceSummary manual = await system.WaitAsync(order, s => s.Status == "ManualIntervention", "the instance to need an operator");
        manual.FailureCode.ShouldBe(PaymentErrors.FraudSuspected);
        manual.PendingCompensations.ShouldBe(1, "the release is still to do");
        (await alerts.WaitAsync(1)).ShouldBe(1, "the alert metric counts the instance once");
        (await system.OrderEventsAsync(order)).ShouldBe([Placed], "the order stays open for the operator");

        if (!resume)
        {
            return;
        }

        system.Outage(Inventory.InventoryService.Name, down: false);
        (int code, string output, string error) = await system.CliAsync("workflow", "resume", "order-fulfilment@1", order.ToString());
        code.ShouldBe(0, error);
        output.ShouldContain("resume");

        WorkflowInstanceSummary done = await system.WaitAsync(order, s => s.Status == "Compensated", "the resumed compensation to finish");
        done.PendingCompensations.ShouldBe(0);
        (await system.ReservationEventsAsync(order)).ShouldBe([Reserved, Released]);
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, Cancelled], "the order to be cancelled", system);
        alerts.Count.ShouldBe(1, "resuming doesn't alert again");
    }

    // A large order waits for a reviewer (the guard $data.amount > 5000); approved, it ships.
    private async Task LargeOrderAsync(Store store)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, store, Broker.InMemory);
        var order = OrderId.New();
        var small = OrderId.New();

        await system.PlaceAsync(order, 6000m);
        await system.PlaceAsync(small, 50m);
        await system.WaitAsync(order, s => s.Step == "review-large-order", "the large order to wait for its review");
        await system.WaitAsync(small, s => s.Status == "Completed", "the small order to skip the review");
        (await system.ShipmentEventsAsync(order)).ShouldBeEmpty("not shipped before the review");

        await system.SendAsync(new ApproveOrder(order));

        await system.WaitAsync(order, s => s.Status == "Completed", "the approved order to ship");
        (await system.ShipmentEventsAsync(order)).ShouldBe([Shipped]);
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, "orders.order-approved.v1", Fulfilled], "the order to be fulfilled", system);
    }

    // After the pivot (create-shipment), a failure can't be undone: the carrier rejects the address, the workflow waits
    // for the customer to correct it (await-address-fix), captures the new address and creates the shipment again.
    private async Task InvalidAddressAsync(Store store)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, store, Broker.InMemory);
        var order = OrderId.New();

        await system.PlaceAsync(order, 120m, address: "Springfield");
        await system.WaitAsync(order, s => s.Step == "await-address-fix", "the shipment to wait for an address");

        await system.SendAsync(new CorrectAddress(order, OrderFulfilmentSystem.Address));

        WorkflowInstanceSummary done = await system.WaitAsync(order, s => s.Status == "Completed", "the corrected order to ship");
        done.FailureCode.ShouldBeNull("forward recovery: nothing failed");
        (await system.ShipmentEventsAsync(order)).ShouldBe([Shipped]);
        (await system.ReservationEventsAsync(order)).ShouldBe([Reserved], "nothing was released");
        (await system.PaymentEventsAsync(order)).ShouldBe([Captured], "nothing was refunded");
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, "orders.address-corrected.v1", Fulfilled], "the order to be fulfilled", system);
    }

    // ================================================================ WF-E2E-10: versions side by side

    [Fact]
    [Trait("Id", "WF-E2E-10")]
    [Trait("Requires", "Docker")]
    [Trait("Covers", "WFD-35")]
    public async Task Version2_StartsNewOrders_WhileVersion1Finishes_AndVersion1CantBeRemovedUntilThen()
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, Store.PostgreSql, Broker.InMemory);
        var inFlight = OrderId.New();
        await system.PlaceAsync(inFlight, 6000m);
        await system.WaitAsync(inFlight, s => s.Step == "review-large-order", "version 1 to wait for the review");
        await system.StopAsync(system.Orders);

        // The next release without version 1: refused while version 1 has an instance.
        Exception removed = await Should.ThrowAsync<Exception>(() => system.StartOrdersAsync(sf => sf.AddOrdersService<Without<OrdersModule, OrderFulfilmentWorkflowV1>>().AddModule<NextReleaseModule>()));
        removed.ShouldBeOfType<WorkflowVersionRemovedException>(removed.ToString()).Message.ShouldContain("order-fulfilment@1 has 1 active instance(s)");

        // The next release with both versions.
        await system.StartOrdersAsync(sf => sf.AddOrdersService().AddModule<NextReleaseModule>());
        var fresh = OrderId.New();
        await system.PlaceAsync(fresh, 6000m);
        await system.SendAsync(new ApproveOrder(inFlight));

        (await system.WaitAsync(fresh, s => s.Status == "Completed", "version 2 to ship without a review", version: 2)).Workflow.ShouldBe("order-fulfilment");
        (await system.SummaryAsync(fresh, version: 1)).ShouldBeNull("new orders start on the latest version only");
        await system.WaitAsync(inFlight, s => s.Status == "Completed", "the in-flight order to finish on version 1");
        (await system.SummaryAsync(inFlight, version: 2)).ShouldBeNull();

        // Version 1 has no instances left: it can go.
        await system.StopAsync(system.Orders);
        IHost after = await system.StartOrdersAsync(sf => sf.AddOrdersService<Without<OrdersModule, OrderFulfilmentWorkflowV1>>().AddModule<NextReleaseModule>());
        after.Services.GetRequiredService<SourceFlowRuntime>().Model.ProcessManagers.Select(p => p.Name).ShouldBe(["order-fulfilment@2"]);
    }

    // ================================================================ WF-E2E-11: duplicate delivery everywhere

    [Fact]
    [Trait("Id", "WF-E2E-11")]
    [Trait("Cloud", "Aws")]
    [Trait("Requires", "LocalStack")]
    [Trait("Covers", "WFD-NFR-3;GAP-19")]
    public Task DuplicateDeliveries_TransitionOnce_Aws() => DuplicateDeliveriesAsync(Broker.Aws);

    [Fact]
    [Trait("Id", "WF-E2E-11")]
    [Trait("Cloud", "Gcp")]
    [Trait("Requires", "PubSubEmulator")]
    public Task DuplicateDeliveries_TransitionOnce_Gcp() => DuplicateDeliveriesAsync(Broker.Gcp);

    [Fact]
    [Trait("Id", "WF-E2E-11")]
    [Trait("Cloud", "Azure")]
    [Trait("Requires", "ServiceBusEmulator")]
    public Task DuplicateDeliveries_TransitionOnce_Azure() => DuplicateDeliveriesAsync(Broker.Azure);

    // Every service sends and publishes everything twice: each command, reply and awaited event arrives twice. The
    // owners' inboxes and the deterministic command ids absorb them: one charge, one shipment, one transition each.
    private async Task DuplicateDeliveriesAsync(Broker broker)
    {
        await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, Store.PostgreSql, broker, new OrderFulfilmentOptions { Duplicates = true });
        using var transitions = new Transitions(system.Orders);
        var order = OrderId.New();
        var declined = OrderId.New();
        system.Gateway.Script(declined, GatewayOutcome.Declined);

        await system.PlaceAsync(order, 120m);
        await system.WaitAsync(order, s => s.Status == "Completed", "the order to be fulfilled");
        await QuietAsync();
        transitions.Counts().ShouldBe(new Dictionary<string, int> { ["success"] = 3, ["complete"] = 1 }, "each awaited event moved the instance once");

        await system.PlaceAsync(declined, 130m);
        await system.WaitForPhaseAsync(declined, "charge-payment", "Retrying", 2, "the declined charge to wait for its retry");
        system.Advance(TimeSpan.FromMinutes(10) + Second);
        await system.WaitAsync(declined, s => s.Status == "Completed", "the retried order to be fulfilled");
        await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, Fulfilled], "the order to be fulfilled once", system);
        await QuietAsync();

        system.Gateway.Attempts(order).ShouldBe(1, "the charge was delivered twice and decided once");
        system.Gateway.Attempts(declined).ShouldBe(2, "the retry is a new command, decided once too");
        (await system.ShipmentEventsAsync(order)).ShouldBe([Shipped]);
        (await system.ReservationEventsAsync(order)).ShouldBe([Reserved]);
        (await system.OrderEventsAsync(order)).ShouldBe([Placed, Fulfilled]);
        (await system.OrderEventsAsync(declined)).ShouldBe([Placed, Fulfilled]);
        foreach (string type in (string[])[Reserved, Captured, Shipped])
        {
            // The workflow's inbox drops the copy (the transitions above); the pipeline still sees both deliveries.
            system.Received[("orders", type)].Deliveries.ShouldBeGreaterThanOrEqualTo(4, $"orders received {type} twice per order");
        }

        system.Received[("payments", "payments.charge.v1")].Duplicates.ShouldBeGreaterThan(0, "the command processor reports the copy as a duplicate");
    }

    // ================================================================ WF-E2E-12: crashes mid-workflow (chaos, nightly)

    [Fact]
    [Trait("Id", "WF-E2E-12")]
    [Trait("Category", "Chaos")]
    [Trait("Requires", "Docker")]
    [Trait("Covers", "ADR-006;WFD-NFR-3")]
    public async Task CrashAtEachFaultPoint_TheNextNodeFinishesTheWorkflowOnce()
    {
        // Each point at a hit inside the workflow. The orders node commits two commands (the order placed by the test, then
        // mark-fulfilled from on.completed), a reaction per transition, and relays each command it sends.
        (string Point, int Hit)[] crashes =
        [
            (FaultPoints.ReactionBeforeCommit, 2),            // reacting to stock-reserved: the charge isn't sent
            (FaultPoints.RelayAfterPublishBeforeDelete, 2),   // the charge was sent, its outbox row stays: sent again
            (FaultPoints.ReceiverBeforeComplete, 1),          // stock-reserved was handled, not completed: redelivered
            (FaultPoints.SessionBeforeCommit, 2),             // mark-fulfilled doesn't commit
            (FaultPoints.SessionAfterCommitBeforeAck, 2),     // mark-fulfilled committed, not acknowledged
        ];

        foreach ((string point, int hit) in crashes)
        {
            var faults = new ChaosInjector().CrashAt(point, hit);
            await using OrderFulfilmentSystem system = await OrderFulfilmentSystem.StartAsync(Servers, Store.PostgreSql, Broker.InMemory, new OrderFulfilmentOptions { OrdersFaults = faults });
            var order = OrderId.New();
            IHost crashed = system.Orders;

            await system.PlaceAsync(order, 120m);
            (await faults.Triggered.WaitAsync(TimeSpan.FromSeconds(60), Ct).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : $"no crash: {point} reached {faults.Hits(point)} time(s)", Ct, TaskContinuationOptions.None, TaskScheduler.Default)).ShouldBe(point);
            await system.StopAsync(crashed, kill: true);
            await system.StartOrdersAsync();

            await system.WaitAsync(order, s => s.Status == "Completed", $"the next node to finish the workflow after a crash at {point}");
            await EventuallyEqualAsync(() => system.OrderEventsAsync(order), [Placed, Fulfilled], $"one fulfilment after a crash at {point}", system);
            await QuietAsync();
            system.Gateway.Attempts(order).ShouldBe(1, $"one charge after a crash at {point}");
            (await system.ShipmentEventsAsync(order)).ShouldBe([Shipped], $"one shipment after a crash at {point}");
            (await system.ReservationEventsAsync(order)).ShouldBe([Reserved]);
            (await system.OrderEventsAsync(order)).ShouldBe([Placed, Fulfilled]);
        }
    }

    // ================================================================ helpers

    private static async Task EventuallyEqualAsync(Func<Task<IReadOnlyList<string>>> read, string[] expected, string what, OrderFulfilmentSystem system)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(60);
        IReadOnlyList<string> actual = await read();
        while (!actual.SequenceEqual(expected))
        {
            if (TimeProvider.System.GetUtcNow() > deadline)
            {
                actual.ShouldBe(expected, $"{what} {await system.DiagnoseAsync()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            actual = await read();
        }
    }

    // Room for late duplicates to arrive, so asserting "once" means something.
    private static Task QuietAsync()
    {
        return Task.Delay(TimeSpan.FromSeconds(3), TimeProvider.System, Ct);
    }

    /// <summary>The <c>sourceflow.workflow.manual_intervention</c> counter of one host.</summary>
    private sealed class ManualInterventions : IDisposable
    {
        private readonly MeterListener _listener = new();
        private int _count;

        public ManualInterventions(IHost host)
        {
            Meter meter = host.Services.GetRequiredService<SourceFlowRuntime>().Metrics.Meter;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter == meter && instrument.Name == SourceFlowTelemetry.Instruments.WorkflowManualIntervention)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, (int)value));
            _listener.Start();
        }

        public int Count => Volatile.Read(ref _count);

        public async Task<int> WaitAsync(int atLeast)
        {
            DateTimeOffset deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(10);
            while (Count < atLeast && TimeProvider.System.GetUtcNow() < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
            }

            return Count;
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    /// <summary>The <c>sourceflow.workflow.transitions</c> of one host, counted by kind.</summary>
    private sealed class Transitions : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

        public Transitions(IHost host)
        {
            Meter meter = host.Services.GetRequiredService<SourceFlowRuntime>().Metrics.Meter;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter == meter && instrument.Name == SourceFlowTelemetry.Instruments.WorkflowTransitions)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (tag.Key == SourceFlowTelemetry.Tags.TransitionKind && tag.Value is string kind)
                    {
                        _counts.AddOrUpdate(kind, (int)value, (_, n) => n + (int)value);
                    }
                }
            });
            _listener.Start();
        }

        public Dictionary<string, int> Counts() => new(_counts, StringComparer.Ordinal);

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}

/// <summary>
/// A module without one process manager: a deployment that never had a workflow version, or removed it (doc 15 §5.6).
/// </summary>
internal sealed class Without<TModule, TProcess> : ISourceFlowModule
    where TModule : ISourceFlowModule
    where TProcess : class
{
    public static string ModuleName => TModule.ModuleName;

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        TModule.Register(new Filter(builder));
    }

    private sealed class Filter(ISourceFlowModuleBuilder inner) : ISourceFlowModuleBuilder
    {
        public void AddJsonTypeInfoResolver(System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver resolver) => inner.AddJsonTypeInfoResolver(resolver);

        public void AddJsonConverter(System.Text.Json.Serialization.JsonConverter converter) => inner.AddJsonConverter(converter);

        public void AddMessage<TMessage>(string name, int version)
            where TMessage : class, IMessage => inner.AddMessage<TMessage>(name, version);

        public void AddUpcaster<TFrom, TTo, TUpcaster>()
            where TFrom : class, IMessage
            where TTo : class, IMessage
            where TUpcaster : class, IUpcaster<TFrom, TTo>, new() => inner.AddUpcaster<TFrom, TTo, TUpcaster>();

        public void AddAggregate<TAggregate, TId>()
            where TAggregate : class, IAggregateRuntime<TAggregate, TId>
            where TId : struct, IStronglyTypedId<TId> => inner.AddAggregate<TAggregate, TId>();

        public void AddCommandHandler<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TCommand, TAggregate>()
            where THandler : class, ICommandHandler<TCommand, TAggregate>
            where TCommand : class, ICommand<TAggregate>
            where TAggregate : class => inner.AddCommandHandler<THandler, TCommand, TAggregate>();

        public void AddProcessManager<TProcessManager, TId>()
            where TProcessManager : class, IProcessManagerRuntime<TProcessManager, TId>
            where TId : struct, IStronglyTypedId<TId>
        {
            if (typeof(TProcessManager) != typeof(TProcess))
            {
                inner.AddProcessManager<TProcessManager, TId>();
            }
        }

        public void AddProjection<TProjection, TDocument, TKey>()
            where TProjection : class, IDocumentProjectionRuntime<TProjection, TDocument, TKey>, new()
            where TDocument : class
            where TKey : notnull => inner.AddProjection<TProjection, TDocument, TKey>();

        public void AddSessionProjection<TProjection, TUnitOfWork>()
            where TProjection : class, ISessionProjectionRuntime<TProjection, TUnitOfWork>, new()
            where TUnitOfWork : class => inner.AddSessionProjection<TProjection, TUnitOfWork>();

        public void AddEventHandler<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] THandler, TEvent>()
            where THandler : class, IEventHandler<TEvent>
            where TEvent : class, IMessage => inner.AddEventHandler<THandler, TEvent>();

        public void AddDomainQuery<TQuery, TResult>()
            where TQuery : class, IDomainQueryRuntime<TQuery, TResult>
            where TResult : class => inner.AddDomainQuery<TQuery, TResult>();
    }
}
