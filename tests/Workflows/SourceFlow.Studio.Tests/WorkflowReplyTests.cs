using System.Text.Json.Nodes;
using SourceFlow.Studio.Tests.Fixture;
using SourceFlow.Testing;
using SourceFlow.Workflows;

namespace SourceFlow.Studio.Tests;

/// <summary>UT-PM-19..20 (WP-C.11, CR-32, CR-33): rejection replies reach the generated workflow, and only for the attempt that sent them.</summary>
[Trait("Category", "Unit")]
public sealed class WorkflowReplyTests
{
    private const string Order = "12345678-1234-5678-9abc-def012345678";

    private static WorkflowSpec<OrderFulfilmentWorkflowV1> Started()
    {
        var data = new JsonObject { ["orderId"] = Order, ["total"] = 100m, ["lines"] = new JsonArray("sku-1"), ["customerEmail"] = "a@b.c", ["priority"] = false };
        return WorkflowSpec.For<OrderFulfilmentWorkflowV1, FixtureModule>().Start("orders.order-placed.v1", data.ToJsonString());
    }

    [Fact]
    [Trait("Id", "UT-PM-19")]
    [Trait("Covers", "doc 15 5.3")]
    public void ARejectedCompensation_IsAFailedAttempt_RetriedOnItsSchedule()
    {
        WorkflowSpec<OrderFulfilmentWorkflowV1> spec = Started()
            .Succeed("inventory.stock-reserved.v1", new JsonObject { ["orderId"] = Order }.ToJsonString())
            .Reject("payments.fraud_suspected")                 // charge-payment fails: compensate reserve-stock
            .ThenSends("inventory.release-stock.v1")
            .Reject("inventory.not_reserved")                   // the compensation itself is rejected
            .ThenStatus(WorkflowStatus.Compensating);

        WorkflowTimer retry = spec.Timers[^1];
        retry.Kind.ShouldBe("retry", "release-stock's retry: 5, from 30 s");
        retry.Step.ShouldBe("compensate:reserve-stock");
        spec.FireTimer().ThenSends("inventory.release-stock.v1");
    }

    [Fact]
    [Trait("Id", "UT-PM-20")]
    [Trait("Covers", "doc 15 5.5")]
    public void ALateRejectionOfAnEarlierAttempt_IsIgnored()
    {
        WorkflowSpec<OrderFulfilmentWorkflowV1> spec = Started()
            .FireTimer()                                        // reserve-stock times out: attempt 2 is sent
            .ThenSends("inventory.reserve-stock.v1");
        spec.Sent.Count.ShouldBe(2);

        spec.RejectAt(0, "inventory.out_of_stock");             // attempt 1's rejection arrives late
        spec.LastReaction.ShouldBeNull("it is not the current attempt's reply");
        spec.ThenStatus(WorkflowStatus.Running);

        spec.Reject("inventory.out_of_stock")                   // attempt 2's own rejection counts
            .ThenStatus(WorkflowStatus.Compensated);
    }
}
