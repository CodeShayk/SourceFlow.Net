using System.Text.Json.Serialization;
using Inventory;
using Payments;
using Shipping;
using SourceFlow;
using SourceFlow.Hosting;

namespace Orders;

/// <summary>The order id (UUIDv7). It is also the order-fulfilment instance id.</summary>
[StronglyTypedId]
public readonly partial record struct OrderId(Guid Value);

/// <summary>Where an order is in its life.</summary>
public enum OrderStatus
{
    /// <summary>Not placed yet.</summary>
    None = 0,

    /// <summary>Placed; the order-fulfilment workflow is running.</summary>
    Placed = 1,

    /// <summary>Shipped and done.</summary>
    Fulfilled = 2,

    /// <summary>Cancelled; whatever was done for it was undone.</summary>
    Cancelled = 3,
}

/// <summary>The rejection codes of the Orders service.</summary>
public static class OrderErrors
{
    /// <summary>An order needs at least one line.</summary>
    public const string Empty = "orders.empty";

    /// <summary>The total must be positive.</summary>
    public const string InvalidTotal = "orders.invalid_total";

    /// <summary>The command needs a placed order that is still open.</summary>
    public const string NotOpen = "orders.not_open";
}

/// <summary>Places an order: lines (SKUs), the total and the delivery address.</summary>
[MessageType("orders.place-order", 1)]
public sealed record PlaceOrder(OrderId OrderId, IReadOnlyList<string> Lines, decimal Total, string ShippingAddress) : ICommand<Order>;

/// <summary>A reviewer approves a large order.</summary>
[MessageType("orders.approve-order", 1)]
public sealed record ApproveOrder(OrderId OrderId) : ICommand<Order>;

/// <summary>A reviewer rejects a large order.</summary>
[MessageType("orders.reject-order", 1)]
public sealed record RejectOrder(OrderId OrderId, string Reason) : ICommand<Order>;

/// <summary>The customer corrects the delivery address.</summary>
[MessageType("orders.correct-address", 1)]
public sealed record CorrectAddress(OrderId OrderId, string Address) : ICommand<Order>;

/// <summary>Marks the order fulfilled (sent by the workflow when it completes).</summary>
[MessageType("orders.mark-fulfilled", 1)]
public sealed record MarkFulfilled(OrderId OrderId) : ICommand<Order>;

/// <summary>Cancels the order (sent by the workflow once everything was undone).</summary>
[MessageType("orders.cancel-order", 1)]
public sealed record CancelOrder(OrderId OrderId, string Reason) : ICommand<Order>;

/// <summary>The order was placed: it starts the order-fulfilment workflow.</summary>
[MessageType("orders.order-placed", 1)]
public sealed record OrderPlaced(OrderId OrderId, IReadOnlyList<string> Lines, decimal Total, string ShippingAddress) : IEvent;

/// <summary>A reviewer approved the order.</summary>
[MessageType("orders.order-approved", 1)]
public sealed record OrderApproved(OrderId OrderId) : IEvent;

/// <summary>A reviewer rejected the order.</summary>
[MessageType("orders.order-rejected-by-review", 1)]
public sealed record OrderRejectedByReview(OrderId OrderId, string Reason) : IEvent;

/// <summary>The delivery address was corrected.</summary>
[MessageType("orders.address-corrected", 1)]
public sealed record AddressCorrected(OrderId OrderId, string Address) : IEvent;

/// <summary>The order was fulfilled.</summary>
[MessageType("orders.order-fulfilled", 1)]
public sealed record OrderFulfilled(OrderId OrderId) : IEvent;

/// <summary>The order was cancelled.</summary>
[MessageType("orders.order-cancelled", 1)]
public sealed record OrderCancelled(OrderId OrderId, string Reason) : IEvent;

/// <summary>The order aggregate: pure deciders (ADR-003). The order-fulfilment workflow does the rest.</summary>
[Aggregate("order")]
public sealed partial record Order : IAggregate<Order, OrderId>
{
    /// <summary>The state before the first event.</summary>
    public static Order Initial { get; } = new();

    /// <summary>Where the order is.</summary>
    public OrderStatus Status { get; init; }

    /// <summary>The delivery address.</summary>
    public string? ShippingAddress { get; init; }

    /// <summary>Places the order once.</summary>
    public Decision Decide(PlaceOrder c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (Status != OrderStatus.None)
        {
            return Decision.NoOp;
        }

        if (c.Lines.Count == 0)
        {
            return Decision.Reject(new Error(OrderErrors.Empty, "An order needs at least one line."));
        }

        return c.Total <= 0
            ? Decision.Reject(new Error(OrderErrors.InvalidTotal, "The total must be positive."))
            : Decision.Accept(new OrderPlaced(c.OrderId, c.Lines, c.Total, c.ShippingAddress));
    }

    /// <summary>Approves an open order.</summary>
    public Decision Decide(ApproveOrder c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Status == OrderStatus.Placed
            ? Decision.Accept(new OrderApproved(c.OrderId))
            : Decision.Reject(new Error(OrderErrors.NotOpen, "Only an open order can be reviewed."));
    }

    /// <summary>Rejects an open order.</summary>
    public Decision Decide(RejectOrder c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Status == OrderStatus.Placed
            ? Decision.Accept(new OrderRejectedByReview(c.OrderId, c.Reason))
            : Decision.Reject(new Error(OrderErrors.NotOpen, "Only an open order can be reviewed."));
    }

    /// <summary>Corrects the address of an open order.</summary>
    public Decision Decide(CorrectAddress c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Status == OrderStatus.Placed
            ? Decision.Accept(new AddressCorrected(c.OrderId, c.Address))
            : Decision.Reject(new Error(OrderErrors.NotOpen, "Only an open order's address can be corrected."));
    }

    /// <summary>Marks the order fulfilled, once.</summary>
    public Decision Decide(MarkFulfilled c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Status == OrderStatus.Placed ? Decision.Accept(new OrderFulfilled(c.OrderId)) : Decision.NoOp;
    }

    /// <summary>Cancels the order, once.</summary>
    public Decision Decide(CancelOrder c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Status == OrderStatus.Placed ? Decision.Accept(new OrderCancelled(c.OrderId, c.Reason)) : Decision.NoOp;
    }

    private Order When(OrderPlaced e)
    {
        return this with { Status = OrderStatus.Placed, ShippingAddress = e.ShippingAddress };
    }

    private Order When(OrderApproved e)
    {
        return this;
    }

    private Order When(OrderRejectedByReview e)
    {
        return this;
    }

    private Order When(AddressCorrected e)
    {
        return this with { ShippingAddress = e.Address };
    }

    private Order When(OrderFulfilled e)
    {
        return this with { Status = OrderStatus.Fulfilled };
    }

    private Order When(OrderCancelled e)
    {
        return this with { Status = OrderStatus.Cancelled };
    }
}

/// <summary>The Orders domain: the order aggregate and, generated from Workflows/*.sfflow.yaml, the order-fulfilment workflow.</summary>
[SourceFlowModule(typeof(OrdersJsonContext))]
public sealed partial class OrdersDomainModule;

/// <summary>
/// The Orders service's module: its domain and the contracts of the services its workflow talks to (the commands it
/// sends and the events it awaits). It is the module the workspace names for the workflow's specs
/// (<c>sourceflow.domain.yaml</c>: <c>module</c>), so a spec runs the workflow exactly as the service does.
/// </summary>
public sealed class OrdersModule : ISourceFlowModule
{
    /// <inheritdoc />
    public static string ModuleName => nameof(OrdersModule);

    /// <inheritdoc />
    public static void Register(ISourceFlowModuleBuilder builder)
    {
        OrdersDomainModule.Register(builder);
        InventoryContractsModule.Register(builder);
        PaymentsContractsModule.Register(builder);
        ShippingContractsModule.Register(builder);
    }
}

/// <summary>How the Orders service is set up: what a host, the design-time export and the tests share.</summary>
public static class OrdersService
{
    /// <summary>The service name and endpoint.</summary>
    public const string Name = "orders";

    /// <summary>
    /// Adds the Orders service: its module (with the other services' contracts), the routes of their commands (with a
    /// rejection reply for the ones the workflow handles), and subscriptions to their events.
    /// </summary>
    public static SourceFlowBuilder AddOrdersService(this SourceFlowBuilder sf)
    {
        return sf.AddOrdersService<OrdersModule>();
    }

    /// <summary>As <see cref="AddOrdersService(SourceFlowBuilder)"/>, with <typeparamref name="TModule"/> in place of <see cref="OrdersModule"/>.</summary>
    public static SourceFlowBuilder AddOrdersService<TModule>(this SourceFlowBuilder sf)
        where TModule : ISourceFlowModule
    {
        ArgumentNullException.ThrowIfNull(sf);
        sf.ServiceName = Name;
        sf.AddModule<TModule>();
        sf.Routing(r =>
        {
            r.Command<ReserveStock>().To(InventoryService.Name).ReplyOnReject();
            r.Command<ReleaseStock>().To(InventoryService.Name);
            r.Command<Charge>().To(PaymentsService.Name).ReplyOnReject();
            r.Command<Refund>().To(PaymentsService.Name);
            r.Command<CreateShipment>().To(ShippingService.Name).ReplyOnReject();
            r.Subscribe(InventoryTopics.Events);
            r.Subscribe(PaymentsTopics.Events);
            r.Subscribe(ShippingTopics.Events);
        });
        return sf;
    }
}

/// <summary>JSON metadata for the Orders service (DC-1), with the rejection replies the workflow handles.</summary>
[JsonSerializable(typeof(PlaceOrder))]
[JsonSerializable(typeof(ApproveOrder))]
[JsonSerializable(typeof(RejectOrder))]
[JsonSerializable(typeof(CorrectAddress))]
[JsonSerializable(typeof(MarkFulfilled))]
[JsonSerializable(typeof(CancelOrder))]
[JsonSerializable(typeof(OrderPlaced))]
[JsonSerializable(typeof(OrderApproved))]
[JsonSerializable(typeof(OrderRejectedByReview))]
[JsonSerializable(typeof(AddressCorrected))]
[JsonSerializable(typeof(OrderFulfilled))]
[JsonSerializable(typeof(OrderCancelled))]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(Inventory.OrderId), TypeInfoPropertyName = "InventoryOrderId")]
[JsonSerializable(typeof(Payments.OrderId), TypeInfoPropertyName = "PaymentsOrderId")]
[JsonSerializable(typeof(Shipping.OrderId), TypeInfoPropertyName = "ShippingOrderId")]
[JsonSerializable(typeof(CommandRejected<ReserveStock>))]
[JsonSerializable(typeof(CommandRejected<Charge>))]
[JsonSerializable(typeof(CommandRejected<CreateShipment>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class OrdersJsonContext : JsonSerializerContext;
