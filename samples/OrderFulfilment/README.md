# OrderFulfilment sample

Four services of a commerce domain and the workflow that runs between them: the order-fulfilment workflow of
[design doc 15 §5.1](../../docs/v3.0.0/15-Workflow-DSL-and-Studio.md#51-example-order-fulfilment-with-success-failures-retries-and-rollback).
The [workflows guide](../../docs/v3.0.0/guides/workflows.md) walks through it.

| Service | Project | Owns |
|---------|---------|------|
| orders | [Orders](Orders) | Orders, and the order-fulfilment workflow ([Workflows/order-fulfilment.sfflow.yaml](Orders/Workflows/order-fulfilment.sfflow.yaml)) |
| inventory | [Inventory](Inventory) | Stock reservations, through a warehouse port (`IStockLevels`) |
| payments | [Payments](Payments) | Payments, through a payment gateway port (`IPaymentGateway`) |
| shipping | [Shipping](Shipping) | Shipments, for addresses the carrier can deliver to |

[OrderFulfilment.Host](OrderFulfilment.Host) runs one service per process (`--Service=orders`), on PostgreSQL (a schema per
service) with the AWS, Azure or Google Cloud transport between them. [Orders.Tests](Orders.Tests) holds the specs SourceFlow
Studio generated for the workflow.

## The workflow

An order is placed. The workflow reserves the stock, charges the payment, has a large order (over 5000) reviewed, and
creates the shipment, then marks the order fulfilled.

- **Business retries.** A declined charge is tried again every 10 minutes, three times.
- **Timeouts.** Every wait has one. A service that is down is only seen as time passing, and retried.
- **Compensation.** When the stock is out or the charge fails, what was done is undone in reverse order (refund the payment,
  release the stock), then the order is cancelled.
- **The pivot.** Once the shipment is created there is no going back. A rejected address waits for the customer to correct
  it, and the shipment is created again.
- **Operators.** A compensation that keeps failing, or a review that never comes, stops the instance for an operator, who
  resumes, retries or aborts it with `sourceflow workflow …`.

The sample's workflow is the doc's example with two additions that real services need: the delivery address travels to
shipping (and is captured again when corrected), and the refund names the order, because payments keeps one payment per
order.

## Contracts between the services

Each service's public commands and events are in its own assembly, typed by its aggregate (`ICommand<Reservation>`).
Inventory, payments and shipping each have a contracts module (`InventoryContractsModule`, …) that registers only those
messages, with their id converters. Orders references the three assemblies for their types, and its `OrdersModule`
registers its own domain and the three contract modules, so the workflow can build their commands and read their events.

Orders routes the commands to their owners, with `ReplyOnReject()` for the ones whose rejections the workflow handles, and
subscribes to the topics where the other services publish their events (`inventory.events`, `payments.events`,
`shipping.events`).

## Try it

Open the workspace in SourceFlow Studio:

```bash
dotnet run --project src/Tooling/SourceFlow.Tool -- studio --workspace samples/OrderFulfilment
```

- **Domain map:** the four services and how they are wired.
- **The workflow:** valid against all four manifests, with 437 paths and a minimal cover of 6.
- **Simulation and Paths:** step through the paths, and generate the specs again into `Orders.Tests`.

Run the specs, and the workflow between the four services (the in-memory scenarios need no Docker):

```bash
dotnet test --project samples/OrderFulfilment/Orders.Tests
dotnet test --project tests/EndToEnd/SourceFlow.EndToEnd.Tests -- --filter-class "SourceFlow.EndToEnd.Tests.OrderFulfilmentScenarios"
```

Run the services yourself (PostgreSQL, and credentials for the transport you choose):

```bash
export ConnectionStrings__SourceFlow="Host=localhost;Port=55432;Username=sourceflow;Password=sourceflow;Database=commerce"
port=5101
for service in inventory payments shipping orders; do
  dotnet run --project samples/OrderFulfilment/OrderFulfilment.Host -- --Service=$service --urls=http://localhost:$port \
    --Transport:Kind=aws --Transport:Region=eu-west-1 &
  port=$((port + 1))
done
curl -X POST http://localhost:5104/orders/<a new UUID> -H 'Content-Type: application/json' \
  -d '{ "lines": ["sku-teapot"], "total": 120, "shippingAddress": "1 High Street, Springfield, SP1 2AB" }'
```

The sandbox gateway declines amounts with 51 cents and suspects fraud on 66 cents. The SKUs under
`Inventory:OutOfStock` in `appsettings.json` are out of stock.

## Manifests

Each service's `sourceflow.manifest.json` is exported from the host at design time, and CI checks it is current:

```bash
dotnet run --project src/Tooling/SourceFlow.Tool -- manifest export --project samples/OrderFulfilment/OrderFulfilment.Host \
  --output samples/OrderFulfilment/Orders/sourceflow.manifest.json -- --Service=orders
```

The addresses are plain strings to keep the sample short. Addresses are personal data: a real service marks them
`[PersonalData]`, and the workflow's data field `personal: true`.
