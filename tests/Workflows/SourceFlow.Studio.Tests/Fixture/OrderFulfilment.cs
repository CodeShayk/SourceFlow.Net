using System.Text.Json.Serialization;

namespace SourceFlow.Studio.Tests.Fixture;

// The doc 15 §5.1 domain, so the generator compiles order-fulfilment.sfflow.yaml (next to this file) into
// OrderFulfilmentWorkflowV1 in this assembly (WP-S.5). Field names match the golden manifests. Local aggregates own the
// commands of the happy path, so one SourceFlowTestHost can run the workflow end to end; the compensation commands have
// no owner here (their steps are exercised through WorkflowSpec).

[StronglyTypedId] public readonly partial record struct OrderId(Guid Value);

[MessageType("orders.place-order", 1)]
public sealed record PlaceOrder(OrderId OrderId, decimal Total, IReadOnlyList<string> Lines, string CustomerEmail, bool Priority) : ICommand<Order>;

[MessageType("orders.order-placed", 1)]
public sealed record OrderPlaced(OrderId OrderId, decimal Total, IReadOnlyList<string> Lines, string CustomerEmail, bool Priority) : IEvent;

[MessageType("orders.order-approved", 1)]
public sealed record OrderApproved(OrderId OrderId) : IEvent;

[MessageType("orders.order-rejected-by-review", 1)]
public sealed record OrderRejectedByReview(OrderId OrderId, string Reason) : IEvent;

[MessageType("orders.address-corrected", 1)]
public sealed record AddressCorrected(OrderId OrderId) : IEvent;

[MessageType("orders.mark-fulfilled", 1)]
public sealed record MarkFulfilled(OrderId OrderId) : ICommand<Order>;

[MessageType("orders.order-fulfilled", 1)]
public sealed record OrderFulfilled(OrderId OrderId) : IEvent;

[MessageType("orders.cancel-order", 1)]
public sealed record CancelOrder(OrderId OrderId, string Reason) : ICommand<Order>;

[MessageType("orders.order-cancelled", 1)]
public sealed record OrderCancelled(OrderId OrderId, string Reason) : IEvent;

[MessageType("inventory.reserve-stock", 1)]
public sealed record ReserveStock(OrderId OrderId, IReadOnlyList<string> Lines) : ICommand<Stock>;

[MessageType("inventory.release-stock", 1)]
public sealed record ReleaseStock(OrderId OrderId) : ICommand;

[MessageType("inventory.stock-reserved", 1)]
public sealed record StockReserved(OrderId OrderId) : IEvent;

[MessageType("inventory.stock-released", 1)]
public sealed record StockReleased(OrderId OrderId) : IEvent;

[MessageType("payments.charge", 1)]
public sealed record Charge(OrderId OrderId, decimal Amount) : ICommand<Payment>;

[MessageType("payments.refund", 1)]
public sealed record Refund(Guid PaymentId, decimal Amount) : ICommand;

[MessageType("payments.payment-captured", 1)]
public sealed record PaymentCaptured(Guid PaymentId, OrderId OrderId, decimal Amount, string CardHolder) : IEvent;

[MessageType("payments.refunded", 1)]
public sealed record Refunded(Guid PaymentId, OrderId OrderId) : IEvent;

[MessageType("shipping.create-shipment", 1)]
public sealed record CreateShipment(OrderId OrderId, IReadOnlyList<string> Lines, string? Note) : ICommand<Shipment>;

[MessageType("shipping.shipment-created", 1)]
public sealed record ShipmentCreated(OrderId OrderId, Guid ShipmentId) : IEvent;

/// <summary>Orders: places and fulfils orders.</summary>
[Aggregate("fixture-order")]
public sealed partial record Order : IAggregate<Order, OrderId>
{
    public static Order Initial { get; } = new();

    public bool Placed { get; init; }

    public Decision Decide(PlaceOrder c, DecisionContext ctx) =>
        Placed ? Decision.NoOp : Decision.Accept(new OrderPlaced(c.OrderId, c.Total, c.Lines, c.CustomerEmail, c.Priority));

    public Decision Decide(MarkFulfilled c, DecisionContext ctx) => Decision.Accept(new OrderFulfilled(c.OrderId));

    public Decision Decide(CancelOrder c, DecisionContext ctx) => Decision.Accept(new OrderCancelled(c.OrderId, c.Reason));

    private Order When(OrderPlaced e) => this with { Placed = true };

    private Order When(OrderCancelled e) => this;

    private Order When(OrderFulfilled e) => this;
}

/// <summary>Inventory: reserves stock unless an order asks for the line "none".</summary>
[Aggregate("fixture-stock")]
public sealed partial record Stock : IAggregate<Stock, OrderId>
{
    public static Stock Initial { get; } = new();

    public Decision Decide(ReserveStock c, DecisionContext ctx) =>
        c.Lines.Contains("none") ? Decision.Reject(new Error("inventory.out_of_stock", "Out of stock.")) : Decision.Accept(new StockReserved(c.OrderId));

    private Stock When(StockReserved e) => this;
}

/// <summary>Payments: declines charges over 100,000 and suspects fraud on exactly 13.</summary>
[Aggregate("fixture-payment")]
public sealed partial record Payment : IAggregate<Payment, OrderId>
{
    public static Payment Initial { get; } = new();

    public Decision Decide(Charge c, DecisionContext ctx) => c.Amount switch
    {
        > 100_000 => Decision.Reject(new Error("payments.declined", "Declined.")),
        13 => Decision.Reject(new Error("payments.fraud_suspected", "Fraud suspected.")),
        _ => Decision.Accept(new PaymentCaptured(c.OrderId.Value, c.OrderId, c.Amount, "card-holder")),
    };

    private Payment When(PaymentCaptured e) => this;
}

/// <summary>Shipping: creates shipments unless a line is "bad-address".</summary>
[Aggregate("fixture-shipment")]
public sealed partial record Shipment : IAggregate<Shipment, OrderId>
{
    public static Shipment Initial { get; } = new();

    public Decision Decide(CreateShipment c, DecisionContext ctx) =>
        c.Lines.Contains("bad-address") ? Decision.Reject(new Error("shipping.address_invalid", "Invalid address.")) : Decision.Accept(new ShipmentCreated(c.OrderId, c.OrderId.Value));

    private Shipment When(ShipmentCreated e) => this;
}

/// <summary>The fixture module: registers the messages, the aggregates and, generated, the workflow.</summary>
[SourceFlowModule(typeof(FixtureJsonContext))]
public sealed partial class FixtureModule;

/// <summary>JSON metadata (DC-1), with the rejection replies the workflow handles.</summary>
[JsonSerializable(typeof(PlaceOrder))]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(OrderApproved))]
[JsonSerializable(typeof(OrderRejectedByReview))]
[JsonSerializable(typeof(AddressCorrected))]
[JsonSerializable(typeof(MarkFulfilled))]
[JsonSerializable(typeof(OrderFulfilled))]
[JsonSerializable(typeof(CancelOrder))]
[JsonSerializable(typeof(OrderCancelled))]
[JsonSerializable(typeof(ReserveStock))]
[JsonSerializable(typeof(ReleaseStock))]
[JsonSerializable(typeof(StockReserved))]
[JsonSerializable(typeof(StockReleased))]
[JsonSerializable(typeof(Charge))]
[JsonSerializable(typeof(Refund))]
[JsonSerializable(typeof(PaymentCaptured))]
[JsonSerializable(typeof(Refunded))]
[JsonSerializable(typeof(CreateShipment))]
[JsonSerializable(typeof(ShipmentCreated))]
[JsonSerializable(typeof(CommandRejected<ReserveStock>))]
[JsonSerializable(typeof(CommandRejected<Charge>))]
[JsonSerializable(typeof(CommandRejected<CreateShipment>))]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(Stock))]
[JsonSerializable(typeof(Payment))]
[JsonSerializable(typeof(Shipment))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class FixtureJsonContext : JsonSerializerContext;
