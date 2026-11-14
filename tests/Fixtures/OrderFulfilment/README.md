# OrderFulfilment test fixture

Four services of a commerce domain (orders, inventory, payments, shipping) and the order-fulfilment workflow between
them: the example of
[design doc 15 §5.1](../../../docs/v3.0.0/Design/15-Workflow-DSL-and-Studio.md#51-example-order-fulfilment-with-success-failures-retries-and-rollback).
It is test data, not an example to follow. The user-facing example is the
[TravelBooking sample](../../../samples/TravelBooking/README.md), which the
[workflows guide](../../../docs/v3.0.0/guides/workflows.md) walks through.

| Project | What it is |
|---------|------------|
| [Orders](Orders) | Orders, and the workflow ([Workflows/order-fulfilment.sfflow.yaml](Orders/Workflows/order-fulfilment.sfflow.yaml)) |
| [Inventory](Inventory), [Payments](Payments), [Shipping](Shipping) | The other three services, each with a contracts module |
| [OrderFulfilment.Host](OrderFulfilment.Host) | One host that runs one service per process (`--Service=orders`) |
| [Orders.Tests](Orders.Tests) | The specs SourceFlow Studio generated for the workflow (437 paths, a minimal cover of 6) |
| [sourceflow.domain.yaml](sourceflow.domain.yaml) | The domain workspace; each service's `sourceflow.manifest.json` is next to its project |

## What uses it

- The end-to-end workflow scenarios WF-E2E-01..12
  ([`OrderFulfilmentScenarios.cs`](../../EndToEnd/SourceFlow.EndToEnd.Tests/OrderFulfilmentScenarios.cs)).
- The Studio tests UT-MAN-16 and UT-WF-71..73
  ([`OrderFulfilmentSampleTests.cs`](../../Workflows/SourceFlow.Studio.Tests/OrderFulfilmentSampleTests.cs)).
- The Playwright tests STU-E2E-01 and STU-A11Y-04 (`studio/app/e2e`), which open this workspace read-only.
- The manifest drift check in `v3-pr.yml`, which exports each service's manifest with `--check`.

## Open it in Studio

```bash
dotnet run --project src/Tooling/SourceFlow.Tool -- studio --workspace tests/Fixtures/OrderFulfilment
```

Export a manifest again after changing a service:

```bash
dotnet run --project src/Tooling/SourceFlow.Tool -- manifest export --project tests/Fixtures/OrderFulfilment/OrderFulfilment.Host \
  --output tests/Fixtures/OrderFulfilment/Orders/sourceflow.manifest.json \
  -- --Service=orders "--ConnectionStrings:SourceFlow=Host=localhost;Port=1"
```
