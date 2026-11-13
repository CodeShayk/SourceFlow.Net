using System.Text.Json.Serialization;

namespace SourceFlow.Studio.Tests.Fixture;

// The doc 15 §5.1 domain, so the generator compiles order-fulfilment.sfflow.yaml (next to this file) into
// OrderFulfilmentWorkflowV1 in this assembly (WP-S.5), and order-fulfilment-v2.sfflow.yaml into OrderFulfilmentWorkflowV2
// (WP-S.6: versions side by side). Field names match the golden manifests. Local aggregates own the
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

[MessageType("orders.correct-address", 1)]
public sealed record CorrectAddress(OrderId OrderId) : ICommand<Order>;

[MessageType("orders.approve-order", 1)]
public sealed record ApproveOrder(OrderId OrderId) : ICommand<Order>;

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

[MessageType("shipping.fix-address", 1)]
public sealed record FixAddress(OrderId OrderId) : ICommand<Shipment>;

[MessageType("shipping.shipment-address-fixed", 1)]
public sealed record ShipmentAddressFixed(OrderId OrderId) : IEvent;

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

    public Decision Decide(CorrectAddress c, DecisionContext ctx) => Decision.Accept(new AddressCorrected(c.OrderId));

    public Decision Decide(ApproveOrder c, DecisionContext ctx) => Decision.Accept(new OrderApproved(c.OrderId));

    private Order When(OrderPlaced e) => this with { Placed = true };

    private Order When(OrderCancelled e) => this;

    private Order When(OrderFulfilled e) => this;

    private Order When(AddressCorrected e) => this;

    private Order When(OrderApproved e) => this;
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

/// <summary>Shipping: creates shipments unless a line is "bad-address" and the address was not fixed.</summary>
[Aggregate("fixture-shipment")]
public sealed partial record Shipment : IAggregate<Shipment, OrderId>
{
    public static Shipment Initial { get; } = new();

    public bool AddressFixed { get; init; }

    public Decision Decide(CreateShipment c, DecisionContext ctx) =>
        c.Lines.Contains("bad-address") && !AddressFixed ? Decision.Reject(new Error("shipping.address_invalid", "Invalid address.")) : Decision.Accept(new ShipmentCreated(c.OrderId, c.OrderId.Value));

    public Decision Decide(FixAddress c, DecisionContext ctx) => Decision.Accept(new ShipmentAddressFixed(c.OrderId));

    private Shipment When(ShipmentCreated e) => this;

    private Shipment When(ShipmentAddressFixed e) => this with { AddressFixed = true };
}

/// <summary>The fixture module: registers the messages, the aggregates and, generated, the workflow.</summary>
[SourceFlowModule(typeof(FixtureJsonContext))]
public sealed partial class FixtureModule;

/// <summary>
/// The fixture module without one workflow version: a deployment that never had it, or removed it (doc 15 §5.6). The
/// generated module registers every version in this assembly; this leaves <typeparamref name="TWorkflow"/> out.
/// </summary>
public sealed class FixtureWithout<TWorkflow> : ISourceFlowModule
    where TWorkflow : class
{
    public static string ModuleName => "FixtureModule";

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        FixtureModule.Register(new Without(builder));
    }

    private sealed class Without(ISourceFlowModuleBuilder inner) : ISourceFlowModuleBuilder
    {
        public void AddJsonTypeInfoResolver(System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver resolver) => inner.AddJsonTypeInfoResolver(resolver);

        public void AddJsonConverter(JsonConverter converter) => inner.AddJsonConverter(converter);

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

        public void AddProcessManager<TProcess, TId>()
            where TProcess : class, IProcessManagerRuntime<TProcess, TId>
            where TId : struct, IStronglyTypedId<TId>
        {
            if (typeof(TProcess) != typeof(TWorkflow))
            {
                inner.AddProcessManager<TProcess, TId>();
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

/// <summary>JSON metadata (DC-1), with the rejection replies the workflow handles.</summary>
[JsonSerializable(typeof(PlaceOrder))]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(OrderApproved))]
[JsonSerializable(typeof(OrderRejectedByReview))]
[JsonSerializable(typeof(AddressCorrected))]
[JsonSerializable(typeof(CorrectAddress))]
[JsonSerializable(typeof(ApproveOrder))]
[JsonSerializable(typeof(FixAddress))]
[JsonSerializable(typeof(ShipmentAddressFixed))]
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
