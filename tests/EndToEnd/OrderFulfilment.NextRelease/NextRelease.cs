using System.Text.Json.Serialization;
using SourceFlow;

namespace OrderFulfilment.NextRelease;

/// <summary>The next release's module: order-fulfilment version 2 (generated from Workflows/order-fulfilment-v2.sfflow.yaml).</summary>
[SourceFlowModule(typeof(NextReleaseJsonContext))]
public sealed partial class NextReleaseModule;

/// <summary>JSON metadata for the module (DC-1): the rejection replies version 2 handles.</summary>
[JsonSerializable(typeof(CommandRejected<Inventory.ReserveStock>))]
[JsonSerializable(typeof(CommandRejected<Payments.Charge>))]
[JsonSerializable(typeof(CommandRejected<Shipping.CreateShipment>))]
[JsonSerializable(typeof(CommandRejected<Inventory.ReleaseStock>))]
[JsonSerializable(typeof(CommandRejected<Payments.Refund>))]
[JsonSerializable(typeof(Inventory.OrderId), TypeInfoPropertyName = "InventoryOrderId")]
[JsonSerializable(typeof(Payments.OrderId), TypeInfoPropertyName = "PaymentsOrderId")]
[JsonSerializable(typeof(Shipping.OrderId), TypeInfoPropertyName = "ShippingOrderId")]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class NextReleaseJsonContext : JsonSerializerContext;
