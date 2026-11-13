using System.Text.Json;
using System.Text.Json.Nodes;
using CsCheck;
using Microsoft.Extensions.Time.Testing;
using SourceFlow.Messaging;
using SourceFlow.Storage;
using SourceFlow.Studio.Dsl.Json;
using SourceFlow.Studio.Dsl.Model;
using SourceFlow.Studio.Dsl.Reading;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Tests.Fixture;
using SourceFlow.Testing;
using SourceFlow.Workflows;
using SimStatus = SourceFlow.Studio.Dsl.Simulation.WorkflowStatus;

namespace SourceFlow.Studio.Tests;

/// <summary>
/// The generated workflow (WP-S.5): the doc 15 §5.1 workflow compiled from Fixture/order-fulfilment.sfflow.yaml into
/// OrderFulfilmentWorkflowV1, run in a host and compared with the simulator (WF-DIFF-01..05).
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowRuntimeTests
{
    private static readonly WorkflowDefinition Definition = WorkflowReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixture", "order-fulfilment.sfflow.yaml"))).Workflow!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ in a host

    [Fact]
    [Trait("Id", "GEN-63")]
    [Trait("Covers", "WFD-29")]
    public async Task GeneratedWorkflow_RunsTheHappyPath_InAHost()
    {
        await using SourceFlowTestHost host = await Host();
        var order = OrderId.New();

        await host.SendAsync(new PlaceOrder(order, 120m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        (await Events(host, "fixture-order", order)).Select(e => e.GetType().Name).ShouldBe(["OrderPlaced", "OrderFulfilled"], "mark-fulfilled was sent on completion");
        (await Events(host, "fixture-stock", order)).ShouldHaveSingleItem().ShouldBeOfType<StockReserved>();
        (await Events(host, "fixture-payment", order)).ShouldHaveSingleItem().ShouldBeOfType<PaymentCaptured>();
        (await Events(host, "fixture-shipment", order)).ShouldHaveSingleItem().ShouldBeOfType<ShipmentCreated>();
        ProcessState state = await State(host, order);
        state.Completed.ShouldBeTrue();
        JsonDocument.Parse(state.State.ToArray()).RootElement.GetProperty("state").GetProperty("status").GetString().ShouldBe("Completed");
    }

    [Fact]
    [Trait("Id", "GEN-63")]
    public async Task GeneratedWorkflow_CompensatesInAHost_WhenStockIsOut()
    {
        await using SourceFlowTestHost host = await Host();
        var order = OrderId.New();

        await host.SendAsync(new PlaceOrder(order, 120m, ["none"], "a@b.c", Priority: false), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        ProcessState state = await State(host, order);
        state.Completed.ShouldBeTrue();
        JsonElement engine = JsonDocument.Parse(state.State.ToArray()).RootElement.GetProperty("state");
        engine.GetProperty("status").GetString().ShouldBe("Compensated", "the rejection reply reached the workflow and nothing needed undoing");
        engine.GetProperty("failure").GetProperty("code").GetString().ShouldBe("inventory.out_of_stock");
        (await Events(host, "fixture-order", order)).Last().ShouldBe(new OrderCancelled(order, "inventory.out_of_stock"), "on.compensated maps $failure.code");
    }

    [Fact]
    [Trait("Id", "GEN-63")]
    public async Task GeneratedWorkflow_RetriesADecline_OnItsTimer()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using SourceFlowTestHost host = await Host(time);
        var order = OrderId.New();

        await host.SendAsync(new PlaceOrder(order, 200_000m, ["sku-1"], "a@b.c", Priority: false), cancellationToken: Ct);
        await host.WaitForIdleAsync();

        JsonElement engine = JsonDocument.Parse((await State(host, order)).State.ToArray()).RootElement.GetProperty("state");
        engine.GetProperty("status").GetString().ShouldBe("Running");
        engine.GetProperty("step").GetString().ShouldBe("charge-payment");
        engine.GetProperty("phase").GetString().ShouldBe("Retrying", "payments.declined has 3 retries every 10 minutes");
        engine.GetProperty("timer").GetProperty("kind").GetString().ShouldBe("retry");
    }

    // ================================================================ WF-DIFF: the generated workflow ≡ the simulator

    [Fact]
    [Trait("Id", "WF-DIFF-01")]
    [Trait("Covers", "WFD-NFR-2")]
    public void HappyPath_MatchesTheSimulator()
    {
        var run = new Differential();
        run.Start(total: 6000m);
        run.Apply(Choice.Success);   // stock reserved
        run.Apply(Choice.Success);   // payment captured
        run.Apply(Choice.Success);   // large order approved
        run.Apply(Choice.Success);   // shipment created
        run.Status.ShouldBe(SimStatus.Completed);
    }

    [Fact]
    [Trait("Id", "WF-DIFF-02")]
    [Trait("Category", "Property")]
    [Trait("Covers", "WFD-NFR-2")]
    public void RandomOutcomeSequences_MatchTheSimulator()
    {
        Gen.Select(Gen.Bool, Gen.Int[0, 1_000].Array[1, 40])
            .Sample((large, choices) =>
            {
                var run = new Differential();
                run.Start(total: large ? 6000m : 100m);
                foreach (int choice in choices)
                {
                    if (run.Terminal || run.Status == SimStatus.ManualIntervention)
                    {
                        break;
                    }

                    run.ApplyRandom(choice);
                }
            }, iter: 10_000);
    }

    [Fact]
    [Trait("Id", "WF-DIFF-03")]
    public void OperatorActions_MatchTheSimulator()
    {
        var resumed = new Differential();
        resumed.Start(total: 6000m);
        resumed.Apply(Choice.Success);
        resumed.Apply(Choice.Success);
        resumed.Apply(Choice.Timer);   // review times out: manual
        resumed.Status.ShouldBe(SimStatus.ManualIntervention);
        resumed.Control(WorkflowControl.ResumeAction, step: "create-shipment");
        resumed.Apply(Choice.Success);
        resumed.Status.ShouldBe(SimStatus.Completed);

        var aborted = new Differential();
        aborted.Start(total: 100m);
        aborted.Apply(Choice.Success);
        aborted.Control(WorkflowControl.AbortAction, reason: "customer cancelled", compensate: true);
        aborted.Apply(Choice.Success);
        aborted.Status.ShouldBe(SimStatus.Aborted);

        var retried = new Differential();
        retried.Start(total: 6000m);
        retried.Apply(Choice.Success);
        retried.Apply(Choice.Success);
        retried.Apply(Choice.Timer);
        retried.Control(WorkflowControl.RetryStepAction);
        retried.Status.ShouldBe(SimStatus.Running);
    }

    [Fact]
    [Trait("Id", "WF-DIFF-04")]
    public void StaleTimersAndUnrelatedMessages_AreIgnoredByBoth()
    {
        var run = new Differential();
        run.Start(total: 100m);
        run.Apply(Choice.Timer);                                  // reserve-stock times out: re-sent as attempt 2
        run.Stale("timeout", "reserve-stock", 1).ShouldBeTrue();  // the attempt-1 timeout is stale
        run.Unrelated("payments.payment-captured.v1").ShouldBeTrue("an event the instance doesn't wait for now");
        run.Apply(Choice.Success);
        run.Status.ShouldBe(SimStatus.Running);
    }

    [Fact]
    [Trait("Id", "WF-DIFF-05")]
    public void SendIds_AreDeterministicPerAttempt()
    {
        var first = new Differential();
        first.Start(total: 100m);
        var second = new Differential();
        second.Start(total: 100m);

        first.Sent.Single().Id.ShouldBe(second.Sent.Single().Id, "the same instance and attempt: the same id, deduplicated by the owner's inbox");
        first.Sent.Single().Id.ShouldBe(new MessageId(WorkflowIds.Send(OrderFulfilmentWorkflowV1.ProcessName, first.Instance, "reserve-stock", 1)));
        first.Apply(Choice.Timer);
        first.Sent[^1].Id.ShouldBe(new MessageId(WorkflowIds.Send(OrderFulfilmentWorkflowV1.ProcessName, first.Instance, "reserve-stock", 2)));
    }

    // ================================================================ helpers

    // Version 1 only: with version 2 registered too, new orders would start on version 2 (doc 15 §5.6).
    private static Task<SourceFlowTestHost> Host(TimeProvider? time = null)
    {
        return SourceFlowTestHost.StartAsync(sf =>
        {
            sf.ServiceName = "orders";
            sf.AddModule<FixtureWithout<OrderFulfilmentWorkflowV2>>();
        }, time, cancellationToken: Ct);
    }

    private static Task<IReadOnlyList<IEvent>> Events(SourceFlowTestHost host, string aggregate, OrderId order)
    {
        return host.EventsAsync(StreamId.For(aggregate, order.ToStreamSegment()));
    }

    private static async Task<ProcessState> State(SourceFlowTestHost host, OrderId order)
    {
        await using IStorageSession session = await host.Store.BeginAsync(MessageEnvelope.DefaultTenant, Ct);
        return (await session.LoadProcessStateAsync(OrderFulfilmentWorkflowV1.ProcessName, WorkflowInstanceId.From(order.ToString()).ToStreamSegment(), Ct)).ShouldNotBeNull();
    }

    private enum Choice
    {
        Success,
        Timer,
    }

    /// <summary>Drives the simulator and the generated workflow with the same inputs and compares them after every move.</summary>
    private sealed class Differential
    {
        private readonly WorkflowSpec<OrderFulfilmentWorkflowV1> _spec = WorkflowSpec.For<OrderFulfilmentWorkflowV1, FixtureModule>();
        private SimulationState? _sim;
        private string _order = string.Empty;

        public string Instance => _order;

        public SimStatus Status => _sim!.Status;

        public bool Terminal => _sim!.IsTerminal;

        public IReadOnlyList<(ICommand Command, MessageId? Id)> Sent => _spec.Sent;

        public void Start(decimal total)
        {
            _order = new Guid(0x1234_5678, 0x1234, 0x5678, 0x9a, 0xbc, 0xde, 0xf0, 0x12, 0x34, 0x56, 0x78).ToString("D");
            var data = new JsonObject { ["orderId"] = _order, ["total"] = total, ["lines"] = new JsonArray("sku-1"), ["customerEmail"] = "a@b.c", ["priority"] = false };
            SimulationResult result = WorkflowSimulator.Start(Definition, Values(data), _spec.Now);
            _spec.Start("orders.order-placed.v1", data.ToJsonString());
            Compare(result);
        }

        public void Apply(Choice choice)
        {
            Apply(choice == Choice.Timer ? -1 : 0);
        }

        // 0: the awaited success; 1..n: a declared failure; -1 (or no failures left): the pending timer.
        public void ApplyRandom(int choice)
        {
            WorkflowStep? step = _sim!.Step is { } id ? Definition.Step(id) : null;
            int failures = _sim.Status == SimStatus.Running ? step?.Await?.Failures.Count ?? 0 : 0;
            int options = 1 + failures + (_sim.Timer is null ? 0 : 1);
            int pick = choice % options;
            Apply(pick <= failures ? pick : -1);
        }

        public void Control(string action, string? step = null, string? reason = null, bool compensate = false)
        {
            SimulationInput input = action switch
            {
                WorkflowControl.ResumeAction => SimulationInput.Resume(step),
                WorkflowControl.AbortAction => SimulationInput.Abort(reason!, compensate),
                _ => SimulationInput.RetryStep(),
            };
            SimulationResult result = WorkflowSimulator.Step(_sim!, input);
            _spec.Control(action, step, reason, compensate);
            Compare(result);
        }

        public bool Stale(string kind, string step, int attempt)
        {
            SimulationResult result = WorkflowSimulator.Step(_sim!, SimulationInput.Timer(kind, step, attempt));
            int before = _spec.Sent.Count;
            Deliver(new WorkflowTimer(OrderFulfilmentWorkflowV1.ProcessName, _order, kind, step, attempt));
            Compare(result);
            return result.Effects.All(e => e.Kind == SimulationEffect.Ignored) && _spec.LastReaction is null && _spec.Sent.Count == before;
        }

        public bool Unrelated(string type)
        {
            var data = new JsonObject { ["paymentId"] = Guid.Empty.ToString("D"), ["orderId"] = _order, ["amount"] = 1m, ["cardHolder"] = "x" };
            SimulationResult result = WorkflowSimulator.Step(_sim!, SimulationInput.Receive(type, Values(data)));
            _spec.Succeed(type, data.ToJsonString());
            Compare(result);
            return _spec.LastReaction is null;
        }

        private void Apply(int pick)
        {
            WorkflowStep step = Definition.Step(_sim!.Step!)!;
            SimulationResult result;
            if (pick < 0)
            {
                result = WorkflowSimulator.Step(_sim, SimulationInput.FireTimer());
                _spec.FireTimer();
            }
            else if (pick == 0)
            {
                string type = _sim.Status == SimStatus.Compensating ? step.Compensate!.Await!.Success.On : step.Await!.Success.On;
                JsonObject data = EventData(type);
                result = WorkflowSimulator.Step(_sim, SimulationInput.Receive(type, Values(data)));
                _spec.Succeed(type, data.ToJsonString());
            }
            else
            {
                FailureSpec failure = step.Await!.Failures[pick - 1];
                if (failure.Rejected is { } code)
                {
                    result = WorkflowSimulator.Step(_sim, SimulationInput.RejectedFor(step.Send!.Command, code, code));
                    _spec.Reject(code);
                }
                else
                {
                    JsonObject data = EventData(failure.On!);
                    result = WorkflowSimulator.Step(_sim, SimulationInput.Receive(failure.On!, Values(data)));
                    _spec.Fail(failure.On!, data.ToJsonString());
                }
            }

            Compare(result);
        }

        private void Deliver(WorkflowTimer timer)
        {
            // A stale timer: delivered through the operator path of the spec (the same React).
            typeof(WorkflowSpec<OrderFulfilmentWorkflowV1>).GetMethod("Deliver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(_spec, [timer]);
        }

        private JsonObject EventData(string type)
        {
            return type switch
            {
                "payments.payment-captured.v1" => new JsonObject { ["paymentId"] = "9b2f1f0e-7d1c-4b1a-9f3e-2a1b3c4d5e6f", ["orderId"] = _order, ["amount"] = 6000m, ["cardHolder"] = "card-holder" },
                "payments.refunded.v1" => new JsonObject { ["paymentId"] = "9b2f1f0e-7d1c-4b1a-9f3e-2a1b3c4d5e6f", ["orderId"] = _order },
                "shipping.shipment-created.v1" => new JsonObject { ["orderId"] = _order, ["shipmentId"] = _order },
                "orders.order-rejected-by-review.v1" => new JsonObject { ["orderId"] = _order, ["reason"] = "no" },
                _ => new JsonObject { ["orderId"] = _order },
            };
        }

        // The simulator's state and outbox must equal the generated workflow's after every move.
        private void Compare(SimulationResult result)
        {
            _sim = result.State;
            string? json = _spec.State.State;
            json.ShouldNotBeNull();
            SimulationState generated = SimulationStateJson.Read(Definition, json);
            string expected = SimulationStateJson.Write(result.State);
            SimulationStateJson.Write(generated).ShouldBe(expected);

            SentCommand[] simulated = [.. result.Sent];
            (ICommand Command, MessageId? Id)[] sent = _spec.LastReaction is null ? [] : [.. _spec.Sent.TakeLast(_spec.LastReaction.Commands.Count)];
            sent.Length.ShouldBe(simulated.Length);
            for (int i = 0; i < sent.Length; i++)
            {
                sent[i].Id.ShouldBe(new MessageId(simulated[i].MessageId));
                JsonNode actual = JsonSerializer.SerializeToNode(sent[i].Command, Options.GetTypeInfo(sent[i].Command.GetType()))!;
                foreach (KeyValuePair<string, object?> field in simulated[i].Fields)
                {
                    SimulationStateJson.ToJson(field.Value).ShouldBe(SimulationStateJson.ToJson(SimulationStateJson.FromJson(JsonLite.Parse(actual[field.Key]?.ToJsonString() ?? "null"))), simulated[i].Command + "." + field.Key);
                }
            }
        }

        private static JsonSerializerOptions Options { get; } = new SourceFlow.Runtime.SourceFlowModelBuilder().AddModule<FixtureModule>().Build().Registry.Options;

        private static Dictionary<string, object?> Values(JsonObject data)
        {
            return (Dictionary<string, object?>)SimulationStateJson.FromJson(JsonLite.Parse(data.ToJsonString()))!;
        }
    }
}
