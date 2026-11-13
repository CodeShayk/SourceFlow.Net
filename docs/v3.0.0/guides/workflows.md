# Workflows and SourceFlow Studio

A workflow is a long-running process that one service runs across several: it sends commands to their owners, waits for
their events, retries, times out, undoes what it did when a later step fails, and stops for an operator when it can't
go on. You write it in YAML (`*.sfflow.yaml`). The build compiles it into a process manager in the service that owns it.
SourceFlow Studio draws it, checks it as you type, simulates it, and generates its tests.

This guide follows the [OrderFulfilment sample](../../../samples/OrderFulfilment): four services (orders, inventory,
payments and shipping) and the order-fulfilment workflow, which orders runs. The [DSL reference](workflow-dsl-reference.md)
lists every key; design doc 15 has the semantics and the design.

## 1. The domain workspace

Studio and the validator see the whole domain through a workspace file, `sourceflow.domain.yaml`, which lists each
service and its manifest:

```yaml
domain: commerce
description: Order to delivery
services:
  - name: orders
    manifest: Orders/sourceflow.manifest.json
    workflows: Orders/Workflows          # the workflows orders owns
    tests: Orders.Tests                  # where Studio writes their specs
    module: Orders.OrdersModule          # the module the specs register
  - name: inventory
    manifest: Inventory/sourceflow.manifest.json
  - name: payments
    manifest: Payments/sourceflow.manifest.json
  - name: shipping
    manifest: Shipping/sourceflow.manifest.json
```

A manifest says what a service owns and how it is wired: its messages and their fields, each command's events (`emits`)
and rejection codes (`rejects`), its routes, publications and subscriptions. The build reads the code; the design-time
export adds the configuration, without starting anything:

```bash
dotnet sourceflow manifest export --project samples/OrderFulfilment/OrderFulfilment.Host \
  --output samples/OrderFulfilment/Orders/sourceflow.manifest.json -- --Service=orders
```

Commit the manifests, and let CI export them again with `--check`: a manifest that no longer matches its service fails the
build.

The generator finds `emits` and `rejects` in `Decide` methods and command handlers: `Decision.Accept(new X(…))` and
`Decision.Reject(new Error(Codes.Y, …))` with a constant code. When it can't, it warns (SF0019); say it with
`[Emits]`/`[Rejects]`.

## 2. Writing a workflow

The workflow lives with the service that runs it (`Orders/Workflows/order-fulfilment.sfflow.yaml`):

```yaml
sourceflow.workflow: 1
name: order-fulfilment
version: 1
owner: orders

start:
  on: orders.order-placed.v1          # a domain event of orders
  correlate: data.orderId             # the instance id
data:
  orderId: { from: start.data.orderId }
  amount:  { from: start.data.total }
  lines:   { from: start.data.lines }
  address: { from: start.data.shippingAddress }

steps:
  - id: reserve-stock
    send:  { command: inventory.reserve-stock.v1, map: { orderId: $data.orderId, lines: $data.lines } }
    await:
      success: inventory.stock-reserved.v1
      failure:
        - rejected: inventory.out_of_stock
          then: fail
    timeout: { after: PT5M, retry: { max: 2 }, then: fail }
    compensate:
      send:  { command: inventory.release-stock.v1, map: { orderId: $data.orderId } }
      await: { success: inventory.stock-released.v1 }
      retry: { max: 5, backoff: exponential, initial: PT30S, cap: PT10M }
      timeout: { after: PT15M }
  # charge-payment, review-large-order, create-shipment, await-address-fix …

on:
  completed:   { send: { command: orders.mark-fulfilled.v1, map: { orderId: $data.orderId } } }
  compensated: { send: { command: orders.cancel-order.v1,   map: { orderId: $data.orderId, reason: $failure.code } } }
  manual:      { alert: true }
```

What each part does:

- **`start`** creates an instance per start event. `correlate` names the instance id; awaited events that carry the same
  field find the instance by it. Replies to the workflow's own commands find it through the envelope's correlation.
- **`data`** is the instance's state. Values come from the start event, or from `capture` on a success
  (`capture: { paymentId: event.data.paymentId }`); a later capture can replace a value, as `await-address-fix` replaces
  the address.
- **A step** sends a command (or nothing, to only wait) and waits for one success event. Steps run in order; `next`
  changes that after a success, and `{ goto: step }` after a failure. `next: complete` ends the workflow.
- **Failures** are rejections of the step's command (`rejected: code`) or failure events (`on: type`). A failure can be
  retried (`retry`) before its `then` applies: `fail` undoes what was done, `manual` stops for an operator, `complete`
  ends the workflow, `{ goto }` continues elsewhere. An outcome the step doesn't declare stops the instance for an
  operator.
- **Timeouts.** Every wait has one (WF-V06), because an outage never reaches the workflow as a failure: the runtime retries
  and parks messages, and the workflow only sees time pass. A timeout can be retried; each attempt sends the command
  again with a new id.
- **Compensations** undo a step after a later failure, in reverse order. A compensation that keeps failing stops the
  instance for an operator, with the rest of the stack kept.
- **`pivot: true`** marks the point of no return: after it, failures can't compensate (WF-V07), so they recover forward.
  In the sample, a rejected address waits for the customer to correct it, then creates the shipment again.
- **Guards** (`when`) are CESQL over `$data.*` and `event.data.*`. A step whose guard doesn't hold is skipped:
  `review-large-order` runs only when `$data.amount > 5000`.
- **`$hook`** computes a map value in C#: a `private static partial object? Name(IReadOnlyDictionary<string, object?> data)`
  on the generated workflow record, which you implement (SF0021 when it is missing). Hooks are pure, like `Decide`.

Studio and the build check the workflow against the workspace's manifests: types exist, each command has one owner,
awaited events come from the command and reach the owner, rejections are declared and replied to, every state is
reachable and one path completes, retries are bounded, maps fit the commands' fields, and more (WF-V01 to WF-V16, doc 15
§5.4). In the build, each problem is SF0020 at the line and column in the YAML file.

## 3. Wiring the services

The owner must be able to send the commands, receive the events and receive the rejections:

```csharp
public static SourceFlowBuilder AddOrdersService(this SourceFlowBuilder sf)
{
    sf.ServiceName = "orders";
    sf.AddModule<OrdersModule>();
    sf.Routing(r =>
    {
        r.Command<ReserveStock>().To("inventory").ReplyOnReject();   // the workflow handles its rejection
        r.Command<ReleaseStock>().To("inventory");
        r.Command<Charge>().To("payments").ReplyOnReject();
        r.Command<Refund>().To("payments");
        r.Command<CreateShipment>().To("shipping").ReplyOnReject();
        r.Subscribe("inventory.events");                            // the events the workflow awaits
        r.Subscribe("payments.events");
        r.Subscribe("shipping.events");
    });
    return sf;
}
```

The other services publish the awaited events (`r.Publish<StockReserved>().AsIs().To("inventory.events")`). Rejections
come back as `CommandRejected<T>`, which the owner's JSON context lists.

**Register the other services' contracts in the owner's module.** The workflow builds and reads other services' messages
in the owner's process, so the owner's module must register them, with their id converters. The sample's `OrdersModule`
registers the generated domain module and the three contract modules. It is also the module the workspace names for the
specs, so a spec runs the workflow exactly as the service does.

## 4. Testing

`WorkflowSpec` runs a workflow in memory, outcome by outcome:

```csharp
var spec = WorkflowSpec.For<OrderFulfilmentWorkflowV1, OrdersModule>();
spec.Start("orders.order-placed.v1", """{"orderId":"…","lines":["sku-1"],"total":120,"shippingAddress":"…"}""")
    .ThenSends("inventory.reserve-stock.v1");
spec.Reject("inventory.out_of_stock").ThenSends("orders.cancel-order.v1");
spec.ThenStatus(WorkflowStatus.Compensated);
```

Studio writes these for you: the Paths view enumerates every path (retry loops at 0, 1 and the maximum) and selects a
minimal set that covers every transition and every end status. **Generate specs** writes one test per path to
`{tests}/Workflows/{Name}V{version}PathTests.cs`. The order-fulfilment workflow has 437 paths and a minimal cover of 6
(`samples/OrderFulfilment/Orders.Tests`). Regenerate them after changing the workflow; Studio only overwrites files it
generated.

The repository's end-to-end tests run the sample between its four services on the in-memory store, PostgreSQL, and the
AWS, Azure and Google Cloud transports (WF-E2E-01 to 12).

## 5. Running and operating

The generated process manager is registered by the owner's module. It records each transition on
`sourceflow.workflow.transitions` and opens a `workflow {name} {step}` span. An instance that stops for an operator counts on
`sourceflow.workflow.manual_intervention`, and the `SourceFlowWorkflowManualIntervention` alert pages on it. Instances are
in the `workflow-instances` read model and the generated `<service>.workflow-instances` domain query.

The CLI works on a service's instances (run it in the service, `dotnet run -- sourceflow workflow …`, or with the global
tool):

```bash
sourceflow workflow list --name order-fulfilment --active
sourceflow workflow show order-fulfilment@1 <order id>
sourceflow workflow resume order-fulfilment@1 <order id> [--step create-shipment] [--note "…"]
sourceflow workflow retry-step order-fulfilment@1 <order id>
sourceflow workflow abort order-fulfilment@1 <order id> --reason "…" [--compensate]
```

`resume` continues a compensation that stopped, or runs the current (or given) step again. `retry-step` sends the current
step or compensation again as a new attempt. `abort` ends the instance, compensating first with `--compensate`.

**Versions.** A structural change to a published workflow needs a new `version` (WF-V15). Deploy both versions: new
instances start on the latest, and running instances finish on the version they started on, even when the versions live in
different modules or assemblies. A service refuses to start without a version that still has active instances
(`WorkflowVersionRemovedException`); `sourceflow workflow list --name order-fulfilment --version 1` says when it can go.

## 6. SourceFlow Studio

```bash
dotnet sourceflow studio --workspace samples/OrderFulfilment       # http://127.0.0.1:5180 (loopback only)
dotnet sourceflow studio --workspace samples/OrderFulfilment --read-only
```

- **Domain map:** the services, what they own, and who sends or publishes to whom. Select a service to follow its chain.
- **Statechart:** the workflow, with each transition kind drawn with its own line, symbol and word (success, failure,
  retry, timeout, compensation, manual) and the pivot as a diamond. Colours only repeat what the shapes and words say.
- **Swimlane:** one path as messages between the services.
- **Editing.** Edit visually (the palette adds a step that sends a command and waits for an event it emits; the properties
  panel edits a step) or in the YAML editor beside it, with completion and hover from the schema. Visual edits change only
  the lines they must, so comments and formatting stay. Diagnostics show in the panel, on the canvas and in the editor.
- **Simulation:** step an instance through outcomes and timers, and watch its state, outbox, timers and compensation stack.
- **Paths and specs:** see the paths and their coverage, select some, and generate their specs.
- **Exports:** the statechart as SVG or PNG, and the workflow as Mermaid or BPMN 2.0.

**A read-only site.** `npx @sourceflow/studio build --workspace . --out site` writes the Studio and a snapshot of the
workspace as a static site that works from any folder, for example GitHub Pages.

**Diagrams on pull requests.** The `workflow-diff` action (`studio/action`) comments on pull requests that change
`*.sfflow.yaml` files: the changes in words, before and after diagrams with what changed marked, and SVGs as an artifact.

**Keyboard and accessibility.** `Shift+?` shows the keyboard map; `Alt+1`, `Alt+2` and `Alt+3` go to the explorer, the
editor and the diagnostics, `F8` and `Shift+F8` move through the diagnostics, and `Alt+T` switches the theme. Every state and transition has a name for screen
readers. Everything you can do with the mouse you can do with the keyboard, including adding a step and its failures.
Studio follows the system's light or dark theme and reduced-motion setting. Text meets 4.5:1 contrast, and the outlines
of states, edges and controls meet 3:1.
