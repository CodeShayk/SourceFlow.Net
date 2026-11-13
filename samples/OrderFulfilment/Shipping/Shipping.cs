using System.Text.Json.Serialization;
using SourceFlow;
using SourceFlow.Hosting;

namespace Shipping;

/// <summary>An order, as Shipping knows it: the shipment's id.</summary>
[StronglyTypedId]
public readonly partial record struct OrderId(Guid Value);

/// <summary>The rejection codes of the Shipping service.</summary>
public static class ShippingErrors
{
    /// <summary>The carrier can't deliver to the address. The customer must correct it.</summary>
    public const string AddressInvalid = "shipping.address_invalid";
}

// ================================================================ contract: what other services send and receive

/// <summary>Hands an order's lines to the carrier for delivery to an address.</summary>
[MessageType("shipping.create-shipment", 1)]
public sealed record CreateShipment(OrderId OrderId, IReadOnlyList<string> Lines, string Address) : ICommand<Shipment>;

/// <summary>The carrier accepted the shipment.</summary>
[MessageType("shipping.shipment-created", 1)]
public sealed record ShipmentCreated(OrderId OrderId, string TrackingNumber) : IEvent;

// ================================================================ the service

/// <summary>The carrier's address rule.</summary>
public static class Addresses
{
    /// <summary>
    /// True when the carrier can deliver to <paramref name="address"/>: at least a street, a town and a postcode, separated
    /// by commas, with a digit somewhere (the house number or the postcode).
    /// </summary>
    public static bool IsDeliverable(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        string[] parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && address.Any(char.IsAsciiDigit);
    }
}

/// <summary>An order's shipment.</summary>
[Aggregate("shipment")]
public sealed partial record Shipment : IAggregate<Shipment, OrderId>
{
    /// <summary>The state before the first event.</summary>
    public static Shipment Initial { get; } = new();

    /// <summary>The carrier's tracking number, once the shipment exists.</summary>
    public string? TrackingNumber { get; init; }

    /// <summary>Creates the shipment once; an undeliverable address is rejected, so the order can correct it.</summary>
    public Decision Decide(CreateShipment c, DecisionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (TrackingNumber is not null)
        {
            return Decision.NoOp;
        }

        return Addresses.IsDeliverable(c.Address)
            ? Decision.Accept(new ShipmentCreated(c.OrderId, "TRK-" + c.OrderId.Value.ToString("N")[..12].ToUpperInvariant()))
            : Decision.Reject(new Error(ShippingErrors.AddressInvalid, "The carrier can't deliver to this address."));
    }

    private Shipment When(ShipmentCreated e)
    {
        return this with { TrackingNumber = e.TrackingNumber };
    }
}

/// <summary>The Shipping service's topic.</summary>
public static class ShippingTopics
{
    /// <summary>Where Shipping publishes <see cref="ShipmentCreated"/>.</summary>
    public const string Events = "shipping.events";
}

/// <summary>The Shipping module: the shipment aggregate.</summary>
[SourceFlowModule(typeof(ShippingJsonContext))]
public sealed partial class ShippingModule;

/// <summary>
/// The Shipping contract for other services: its command and event, without the aggregate that decides them. A sender
/// registers this module and routes the command to the <c>shipping</c> endpoint.
/// </summary>
public sealed class ShippingContractsModule : ISourceFlowModule
{
    /// <inheritdoc />
    public static string ModuleName => nameof(ShippingContractsModule);

    /// <inheritdoc />
    public static void Register(ISourceFlowModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddMessage<CreateShipment>("shipping.create-shipment", 1);
        builder.AddMessage<ShipmentCreated>("shipping.shipment-created", 1);
        builder.AddJsonTypeInfoResolver(ShippingJsonContext.Default);
        builder.AddJsonConverter(new OrderId.SourceFlowJsonConverter());
    }
}

/// <summary>How the Shipping service is set up: what a host, the design-time export and the tests share.</summary>
public static class ShippingService
{
    /// <summary>The service name and endpoint.</summary>
    public const string Name = "shipping";

    /// <summary>Adds the Shipping service: its module, and the publication of its events to <see cref="ShippingTopics.Events"/>.</summary>
    public static SourceFlowBuilder AddShippingService(this SourceFlowBuilder sf)
    {
        ArgumentNullException.ThrowIfNull(sf);
        sf.ServiceName = Name;
        sf.AddModule<ShippingModule>();
        sf.Routing(r => r.Publish<ShipmentCreated>().AsIs().To(ShippingTopics.Events));
        return sf;
    }
}

/// <summary>JSON metadata for the Shipping service (DC-1).</summary>
[JsonSerializable(typeof(CreateShipment))]
[JsonSerializable(typeof(ShipmentCreated))]
[JsonSerializable(typeof(Shipment))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ShippingJsonContext : JsonSerializerContext;
