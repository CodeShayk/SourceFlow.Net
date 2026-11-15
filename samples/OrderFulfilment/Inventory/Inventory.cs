using System.Text.Json.Serialization;
using SourceFlow;

namespace Inventory;

/// <summary>An order, as Inventory knows it: the reservation's id.</summary>
[StronglyTypedId]
public readonly partial record struct OrderId(Guid Value);

/// <summary>The rejection codes of the Inventory service.</summary>
public static class InventoryErrors
{
    /// <summary>A line can't be reserved: not enough stock.</summary>
    public const string OutOfStock = "inventory.out_of_stock";

    /// <summary>The order has no lines.</summary>
    public const string NothingToReserve = "inventory.nothing_to_reserve";
}

// ================================================================ contract: what other services send and receive

/// <summary>Reserves one unit of stock per line (a line is a SKU) for an order.</summary>
[MessageType("inventory.reserve-stock", 1)]
public sealed record ReserveStock(OrderId OrderId, IReadOnlyList<string> Lines) : ICommand<Reservation>;

/// <summary>Gives back the stock reserved for an order (compensation).</summary>
[MessageType("inventory.release-stock", 1)]
public sealed record ReleaseStock(OrderId OrderId) : ICommand<Reservation>;

/// <summary>Stock was reserved for an order.</summary>
[MessageType("inventory.stock-reserved", 1)]
public sealed record StockReserved(OrderId OrderId, IReadOnlyList<string> Lines) : IEvent;

/// <summary>The stock reserved for an order was released, or there was nothing to release.</summary>
[MessageType("inventory.stock-released", 1)]
public sealed record StockReleased(OrderId OrderId, IReadOnlyList<string> Lines) : IEvent;

// ================================================================ the service

/// <summary>What the warehouse has on hand. The sample's default reads a list of SKUs that are out of stock from configuration.</summary>
public interface IStockLevels
{
    /// <summary>True when one more unit of <paramref name="sku"/> can be reserved.</summary>
    ValueTask<bool> IsAvailableAsync(string sku, CancellationToken cancellationToken);
}

/// <summary>Stock levels where everything is available except the given SKUs.</summary>
public sealed class ConfiguredStockLevels(IEnumerable<string> outOfStock) : IStockLevels
{
    private readonly HashSet<string> _outOfStock = new(outOfStock, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ValueTask<bool> IsAvailableAsync(string sku, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(!_outOfStock.Contains(sku));
    }
}

/// <summary>The stock reserved for one order.</summary>
[Aggregate("reservation")]
public sealed partial record Reservation : IAggregate<Reservation, OrderId>
{
    /// <summary>The state before the first event.</summary>
    public static Reservation Initial { get; } = new();

    /// <summary>The reserved lines; empty when nothing is (or is no longer) reserved.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>True once the reservation was released.</summary>
    public bool Released { get; init; }

    /// <summary>Releases the reservation. Releasing twice, or releasing nothing, still answers: the compensation must end.</summary>
    public Decision Decide(ReleaseStock c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Decision.Accept(new StockReleased(c.OrderId, Released ? [] : Lines));
    }

    private Reservation When(StockReserved e)
    {
        return this with { Lines = e.Lines, Released = false };
    }

    private Reservation When(StockReleased e)
    {
        return this with { Lines = [], Released = true };
    }
}

/// <summary>Reserves stock: asks the warehouse about every line, then decides.</summary>
public sealed class ReserveStockHandler(IStockLevels stock) : ICommandHandler<ReserveStock, Reservation>
{
    /// <inheritdoc />
    public async ValueTask<Decision> HandleAsync(ReserveStock command, Reservation state, DecisionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(state);
        if (state.Lines.Count > 0)
        {
            return Decision.NoOp;   // already reserved: the event was published when it was
        }

        if (command.Lines.Count == 0)
        {
            return Decision.Reject(new Error(InventoryErrors.NothingToReserve, "The order has no lines."));
        }

        foreach (string sku in command.Lines)
        {
            if (!await stock.IsAvailableAsync(sku, cancellationToken).ConfigureAwait(false))
            {
                return Decision.Reject(new Error(InventoryErrors.OutOfStock, $"'{sku}' is out of stock."));
            }
        }

        return Decision.Accept(new StockReserved(command.OrderId, command.Lines));
    }
}

/// <summary>The Inventory service's topic.</summary>
public static class InventoryTopics
{
    /// <summary>Where Inventory publishes <see cref="StockReserved"/> and <see cref="StockReleased"/>.</summary>
    public const string Events = "inventory.events";
}

/// <summary>The Inventory module: the reservation aggregate and its handler.</summary>
[SourceFlowModule(typeof(InventoryJsonContext))]
public sealed partial class InventoryModule;

/// <summary>
/// The Inventory contract for other services: its commands and events, without the aggregate that decides them. A sender
/// registers this module and routes the commands to the <c>inventory</c> endpoint.
/// </summary>
public sealed class InventoryContractsModule : ISourceFlowModule
{
    /// <inheritdoc />
    public static string ModuleName => nameof(InventoryContractsModule);

    /// <inheritdoc />
    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddMessage<ReserveStock>("inventory.reserve-stock", 1);
        builder.AddMessage<ReleaseStock>("inventory.release-stock", 1);
        builder.AddMessage<StockReserved>("inventory.stock-reserved", 1);
        builder.AddMessage<StockReleased>("inventory.stock-released", 1);
        builder.AddJsonTypeInfoResolver(InventoryJsonContext.Default);
        builder.AddJsonConverter(new OrderId.SourceFlowJsonConverter());
    }
}

/// <summary>JSON metadata for the Inventory service (DC-1).</summary>
[JsonSerializable(typeof(ReserveStock))]
[JsonSerializable(typeof(ReleaseStock))]
[JsonSerializable(typeof(StockReserved))]
[JsonSerializable(typeof(StockReleased))]
[JsonSerializable(typeof(Reservation))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class InventoryJsonContext : JsonSerializerContext;

/// <summary>How the Inventory service is set up: what a host, the design-time export and the tests share.</summary>
public static class InventoryService
{
    /// <summary>The service name and endpoint.</summary>
    public const string Name = "inventory";

    /// <summary>
    /// Adds the Inventory service: its module, and the publication of its events to <see cref="InventoryTopics.Events"/>.
    /// Stock levels come from an <see cref="IStockLevels"/> in the container (default: everything in stock).
    /// </summary>
    public static SourceFlow.Hosting.SourceFlowBuilder AddInventoryService(this SourceFlow.Hosting.SourceFlowBuilder sf)
    {
        ArgumentNullException.ThrowIfNull(sf);
        sf.ServiceName = Name;
        sf.AddModule<InventoryModule>();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<IStockLevels>(sf.Services, new ConfiguredStockLevels([]));
        sf.Routing(r =>
        {
            r.Publish<StockReserved>().AsIs().To(InventoryTopics.Events);
            r.Publish<StockReleased>().AsIs().To(InventoryTopics.Events);
        });
        return sf;
    }
}
