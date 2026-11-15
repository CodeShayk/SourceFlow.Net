# Using SourceFlow Studio

SourceFlow Studio is where you read, check, try out and test a workflow before it runs. It shows how the services of a
domain are wired, draws each workflow as a statechart, validates it against what every service really owns as you type,
simulates instances step by step, lists every path a workflow can take, and writes the tests for them.

This guide is a hands-on tour, done on the two samples in this repository:

| Sample | Services | Workflow | What it is good for |
|--------|----------|----------|---------------------|
| [OrderFulfilment](../../../../samples/OrderFulfilment/README.md) | orders, inventory, payments, shipping | `order-fulfilment`, owned by orders | Most of the tour: a guarded step, business retries, two compensations, a pivot with forward recovery |
| [TravelBooking](../../../../samples/TravelBooking/README.md) | bookings, hotels | `trip-booking`, owned by bookings | A smaller second workspace to repeat the exercises on |

Other documents cover the rest. The [workflows guide](workflows.md) teaches you to write a workflow, and the
[DSL reference](workflow-dsl-reference.md) lists every key. [SourceFlow Studio](../SourceFlow.Studio-README.md) is the
complete reference for the tooling: manifests, the workspace file, the library, the host and its API, static sites and
the `workflow-diff` action.

## Contents

- [1. Open a sample in Studio](#1-open-a-sample-in-studio)
- [2. Find your way around](#2-find-your-way-around)
- [3. The domain map: how the services are wired](#3-the-domain-map-how-the-services-are-wired)
- [4. Read a workflow: the statechart](#4-read-a-workflow-the-statechart)
- [5. Follow one path: the swimlane](#5-follow-one-path-the-swimlane)
- [6. Simulate an instance](#6-simulate-an-instance)
- [7. Paths and coverage](#7-paths-and-coverage)
- [8. Generate the specs](#8-generate-the-specs)
- [9. Change a workflow](#9-change-a-workflow)
- [10. Export diagrams and share the workspace](#10-export-diagrams-and-share-the-workspace)
- [11. From the simulator to the running services](#11-from-the-simulator-to-the-running-services)
- [Troubleshooting](#troubleshooting)

## 1. Open a sample in Studio

There are two ways to open a workspace, and they differ in what you can do.

**Read-only, from the sample's compose file.** Each sample's `docker compose` stack includes a read-only Studio site for
its workspace. You need only Docker:

```bash
docker compose -f samples/OrderFulfilment/docker-compose.yml up -d    # Studio on http://localhost:5090
docker compose up -d                                                  # TravelBooking: Studio on http://localhost:5080
```

**Locally, with the `studio` command.** The local host serves the same app over your working copy. You can edit, save
and write specs. From the repository root (you need the .NET 10 SDK):

```bash
dotnet run --project src/Tooling/SourceFlow.Tool -- studio --workspace samples/OrderFulfilment --open
# SourceFlow Studio: http://127.0.0.1:5180/. Press Ctrl+C to stop.
```

With the installed tool, the same command is `dotnet sourceflow studio --workspace samples/OrderFulfilment --open`. Add
`--read-only` to look without being able to change anything, and `--port 0` if port 5180 is taken. The host listens on
`127.0.0.1` only and sends nothing anywhere else.

| You can | Compose site (5090, 5080) | `studio --read-only` | `studio` |
|---------|:-------------------------:|:--------------------:|:--------:|
| See the domain map, statechart and swimlane | ✓ | ✓ | ✓ |
| Simulate, list paths, export diagrams | ✓ | ✓ | ✓ |
| Preview the generated specs | | ✓ | ✓ |
| Edit and save workflows, write specs | | | ✓ |

**What Studio reads.** A workspace is a folder with a `sourceflow.domain.yaml` that lists the domain's services. The
OrderFulfilment one ([`samples/OrderFulfilment/sourceflow.domain.yaml`](../../../../samples/OrderFulfilment/sourceflow.domain.yaml)):

```yaml
domain: commerce
description: Order to delivery
services:
  - name: orders     # orchestrator of order-fulfilment
    manifest: Orders/sourceflow.manifest.json
    workflows: Orders/Workflows          # the workflows orders owns; Studio writes only in here
    tests: Orders.Tests                  # where Studio writes their specs
    module: Orders.OrdersModule          # the module the specs register
  - name: inventory
    manifest: Inventory/sourceflow.manifest.json
  - name: payments
    manifest: Payments/sourceflow.manifest.json
  - name: shipping
    manifest: Shipping/sourceflow.manifest.json
```

Each manifest describes one service as data: its messages and their fields, each command's events and rejection codes,
its routes, publications and subscriptions. Studio never loads a service's code, only these files. They are exported
from the services and checked for drift in CI. The sample's [README](../../../../samples/OrderFulfilment/README.md#how-it-is-put-together)
shows the export command.

## 2. Find your way around

Studio has four areas:

- **The header** shows the domain name (`commerce`) and a **Read-only** badge when you can't edit. It also has the
  Light / Dark / System theme switch and **Keyboard map**.
- **The workspace explorer**, on the left, lists the services (▣) and, under each owner, its workflows (⇢). In the
  OrderFulfilment sample, `order-fulfilment` sits under `orders`.
- **The main view** shows the domain overview, a service or a workflow.
- **The diagnostics panel**, at the bottom, lists the open workflow's problems, errors first. It reads "No problems." for
  both samples.

The keys you will use most:

| Keys | Action |
|------|--------|
| `Shift+?` | Show every shortcut |
| `Alt+1`, `Alt+2`, `Alt+3` | Go to the explorer, the open workflow, the diagnostics |
| `F8` / `Shift+F8` | Next / previous diagnostic |
| `Alt+H` | Back to the domain overview |
| `Ctrl+S`, `Ctrl+Z`, `Ctrl+Y` | Save, undo, redo |

Everything works from the keyboard. States and transitions take focus, and Enter selects them.

## 3. The domain map: how the services are wired

The overview (`/`, or `Alt+H`) opens on the **domain map**. Studio builds it from the four manifests:

- Each **service** is a container with its aggregates inside.
- **Command edges** (`→`) run from the sender to the aggregate that owns the command. Orders sends
  `inventory.reserve-stock`, `payments.charge` and `shipping.create-shipment` to the other three services. Those are the
  routes that orders' configuration declares.
- **Publication edges** (`⇢`) run from a service to a topic, and **subscription edges** (`⇠`) run from a topic to a
  subscriber, labelled with any `only` filter. The other services publish their events, and orders subscribes to their
  topics. That is how the workflow's awaited events reach it.

**Try it.** Hover over `orders`, or select it with a click or Enter. Studio highlights the chain downstream of it and dims
the rest. Then select `payments` to see how much less that one service touches.

Under the map, a table lists each service with its number of commands and the workflows it owns. Select a service, in the
table or in the explorer, to open its page. The page shows its manifest, its workflows folder, and its aggregates with
the commands each one handles. Here you can check, for example, that `payments` handles `payments.charge` and
`payments.refund`.

The service page has a button, **Export the manifest again**. It works only when the workspace gives the service a
`project`. Neither sample does, because their hosts need arguments that the button doesn't pass (a placeholder
connection string, and for OrderFulfilment the `Service` setting). Export their manifests from the command line, as the
sample's README shows, and Studio picks them up the next time it loads.

## 4. Read a workflow: the statechart

Select **order-fulfilment** in the explorer. The header shows "Owned by **orders**" and the file path. The workflow view
has two groups of tabs. On the left are **Statechart**, **Swimlane** and **Paths**. On the right are **Properties**,
**YAML** and **Simulate**. A toolbar holds **Save**, **Undo**, **Redo** and the exports.

Studio's core computes the statechart from the YAML, so it shows exactly what the generated process manager runs. Read
it from the top:

| On the canvas | In the YAML |
|---------------|-------------|
| `▶ orders.order-placed.v1` into `reserve-stock` | `start.on`: one instance per order placed |
| `reserve-stock` → `charge-payment` on `✓ inventory.stock-reserved.v1` | The step's success event |
| A dotted loop `↻ 2× on timeout` on `reserve-stock`, then `⏱ PT5M` to *Compensating* | `timeout: { after: PT5M, retry: { max: 2 }, then: fail }` |
| `✕ inventory.out_of_stock` from `reserve-stock` to *Compensating* | `rejected: inventory.out_of_stock`, `then: fail` |
| `↻ 3× every PT10M on payments.declined` on `charge-payment` | The business retry of a declined card |
| `⤼ unless $data.amount > 5000` from `review-large-order` to `create-shipment` | The guard: small orders skip the review |
| `⏱ P1D` from `review-large-order` to `✋ Manual intervention` | Nobody reviewed the order within a day |
| `create-shipment` drawn with ◆ | `pivot: true`: the point of no return |
| `✕ shipping.address_invalid` from `create-shipment` to `await-address-fix`, and `✓ orders.address-corrected.v1` back | Forward recovery: `goto` the fix, then `next: create-shipment` |
| A dotted *Compensating* group: `⟲ 1 charge-payment`, then `⟲ 2 reserve-stock`, then *Compensated* | The compensations, numbered in the order they run (the most recent first): refund, then release the stock |
| `✋ a compensation failed` from *Compensating* to *Manual intervention* | A compensation that keeps failing stops for an operator |

Each kind of transition has its own line style, symbol and word, so the chart reads without colour:

| Kind | Line | Symbol |
|------|------|--------|
| Start | solid | ▶ |
| Success | solid | ✓ |
| Failure | dashed | ✕ |
| Retry | dotted loop | ↻ |
| Timeout | dash-dot | ⏱ |
| Compensation | dotted | ⟲ |
| Manual intervention | long dash-dot | ✋ |
| Skipped by a guard | long dash | ⤼ |

The legend on the canvas repeats these. Note what the chart doesn't have: no failure after `create-shipment` leads to
*Compensating*. Once the shipment is created, the workflow can only go forward or stop for an operator.

**Try it.** Select `charge-payment` (click, or Enter on the focused state). The **Properties** tab shows the step in
form view: the command and its map, the success event, both failures with their `Then` and retries, the timeout, the
compensation (`payments.refund.v1`, undone when `payments.refunded.v1` arrives) and the flow. Open the **YAML** tab to
see the same step as text. You can drag states to tidy the layout; that is covered in [§9](#9-change-a-workflow).

## 5. Follow one path: the swimlane

The **Swimlane** tab shows one path through the workflow as messages between the services. The first lane is the
workflow itself (`order-fulfilment@1 (orders)`), followed by one lane per service that the path involves. The **Path**
picker lists every path. Paths in the minimal cover say so, and the tab opens on the first of them.

**Try it.** Pick **P127**, a suspected fraud. It reads, top to bottom:

```text
▶ orders.order-placed.v1                     orders    → workflow
→ inventory.reserve-stock.v1                 workflow  → inventory
✓ inventory.stock-reserved.v1                inventory → workflow
→ payments.charge.v1                         workflow  → payments
✕ rejected payments.fraud_suspected          payments  → workflow
  Compensating
⟲ inventory.release-stock.v1                 workflow  → inventory
✓ inventory.stock-released.v1                inventory → workflow
  Compensated
→ orders.cancel-order.v1                     workflow  → orders
```

Shipping has no lane because this path never reaches it. There is also no refund: the charge was rejected, so
`charge-payment` never completed and there is nothing of it to undo. Only the stock reservation is released. Then pick
**P317**, the longest path in the cover, to watch retries ("attempt 2") and compensation timeouts in sequence.

## 6. Simulate an instance

The **Simulate** tab runs one instance on the core's simulator, the same engine the generated process manager embeds.
Nothing is sent anywhere. The clock starts at 2026-01-01T00:00:00Z and moves only when a timer fires, so a run repeats
exactly.

The tab has three parts:

- **Start** holds the start event's data as JSON, a guard picker for each guarded step (**Evaluate the guard**,
  **Run the step**, **Skip the step**), and **Start** / **Restart**, **Back** and **Reset**.
- **Plan** sets the outcome each step's wait ends with, for **Run to the end**.
- **The instance** shows the status, step, phase, attempt, time and failure. It also shows **Next outcome** with
  **Step**, and the **Compensation stack**, **Timers**, **Outbox** (each command with its deterministic message id),
  **Data** and **Moves**. The statechart outlines the state the instance is in.

### An order delivered

The start data begins as `{ "orderId": "instance-1" }`, the correlation field only. Replace it with a real order:

```json
{ "orderId": "instance-1", "total": 120, "lines": ["kettle"], "shippingAddress": "1 High Street, Leeds, LS1 1AA" }
```

Select **Start**. The status is `Running` at `reserve-stock`, the outbox holds `→ inventory.reserve-stock.v1`, and the
timers show the `PT5M` timeout. Then **Step** with the offered outcome each time:

| Next outcome | Then |
|--------------|------|
| `✓ inventory.stock-reserved.v1` | `charge-payment`; the stack holds `⟲ undo reserve-stock` |
| `✓ payments.payment-captured.v1` | `review-large-order` is skipped (120 isn't over 5000); the outbox adds `shipping.create-shipment.v1` |
| `✓ shipping.shipment-created.v1` | `Completed`; the outbox adds `orders.mark-fulfilled.v1` |

The Data part shows what the instance keeps: `orderId`, `amount`, `lines` and `address` from the start event, and
`paymentId`, which `charge-payment` captures from the payment event. It stays `null` here because the simulated event
carries no data.

If you start with only `{ "orderId": "instance-1" }`, `amount` is null. The guard `$data.amount > 5000` is then unknown
and doesn't hold, so the review is skipped. Set the step's guard picker to **Run the step** to force it.

### A declined card, charged again

**Restart** with the same data, step `✓ inventory.stock-reserved.v1`, then choose `✕ Rejected payments.declined`. The
instance stays at `charge-payment`, now in phase `Retrying`, attempt 2. The timer reads "retry for charge-payment,
attempt 2, due" ten minutes later. Choose `↻ Fire the retry timer`: the time moves to 00:10, and the outbox gets a second
`payments.charge.v1` with a new message id. Each attempt is a new command. Step a success and the order goes on.

### A large order nobody reviews

**Restart** with `"total": 6000`. Step the reservation and the payment, and the instance waits at
`review-large-order`. Its timer is due a day later. Choose `⏱ Timeout after P1D`: the status becomes
`ManualIntervention`, and the next outcomes are the operator's commands:

| Choice | What it does |
|--------|--------------|
| `▶ Resume` | Runs the current step again |
| `↻ Retry the step` | Sends the current step again, as a new attempt |
| `⟲ Abort and compensate` | Undoes the completed steps, then ends `Aborted` |
| `■ Abort` | Ends `Aborted` without undoing anything |

Choose `⟲ Abort and compensate`. The status is `Compensating` and the outbox adds `payments.refund.v1`, because the most
recent step is undone first. Step `⟲ Compensated` and `inventory.release-stock.v1` follows. Step it again, and the
instance ends `Aborted`, with the stack empty. These are the commands `dotnet sourceflow workflow resume|retry-step|abort`
sends to a real instance (see [§11](#11-from-the-simulator-to-the-running-services)).

A compensation can fail too. Instead of `⟲ Compensated`, choose `✕ The compensation is rejected` or `⏱ The compensation
times out` to watch it retry, and then stop for an operator with the rest of the stack kept.

### A wrong address after the point of no return

**Restart** with `"total": 120` and step through to `create-shipment`. Choose `✕ Rejected shipping.address_invalid`. The
instance moves to `await-address-fix` and doesn't compensate. Its timer is due two days later. Step
`✓ orders.address-corrected.v1`, and `create-shipment` sends the command again (attempt 2) with the corrected address.

### Plan and run

You don't have to step every move. In **Plan**, set **Outcome of charge-payment** to `✕ Rejected
payments.fraud_suspected`, **Restart**, and select **Run to the end**. Studio follows the plan: other steps succeed,
retries fire and compensations succeed by themselves. The run ends `Compensated`. It stops early at manual intervention,
or after 200 moves. **Back** undoes the last move.

If you edit the workflow while a simulation is open, Studio says the simulation is out of date. Restart it.

## 7. Paths and coverage

The **Paths** tab lists every distinct path the workflow can take. Studio branches on every outcome, on each guard
(taken or skipped), and on each retry loop at 0, 1 and the maximum. For `order-fulfilment` the summary reads:

```text
437 paths · 25 of 25 edges covered · a minimal cover of 6
```

Of the 437 paths, 120 end `Completed`, 79 `Compensated` and 238 `ManualIntervention`. The **minimal cover** is the
smallest set of paths that together cross every edge of the statechart and reach every end status. Those six are the
paths worth testing:

| Path | End | What it exercises |
|------|-----|-------------------|
| P04 | ✓ Completed | A reviewed large order, a rejected address corrected, a shipment timeout retried, then shipped |
| P23 | ✋ ManualIntervention | A large order nobody reviews within a day |
| P30 | ✋ ManualIntervention | A small order (review skipped) whose address is never corrected |
| P127 | ⟲ Compensated | Payment suspected as fraud; the stock is released |
| P218 | ⟲ Compensated | Out of stock; nothing to undo |
| P317 | ⟲ Compensated | Timeouts, a declined card retried, the review rejecting the order, and both compensations timing out once before they succeed |

**Try it.**

- Use **Show** to filter the list: **All paths**, **The minimal cover**, **Paths that complete**, **Paths that compensate**
  or **Paths that need an operator**.
- Select **Simulate** on P317. Studio replays it in the Simulate tab, move by move, and the Path line names it.
- Clear P317's checkbox. The Specs part now reads "5 paths selected, covering … of 25 edges", and **Edges the selection
  misses** lists the edges only P317 crossed. Select it again, or use **Select the minimal cover**.

The TravelBooking workflow is smaller: 27 paths over 15 edges, with a minimal cover of 6 (P02, P07, P09, P12, P13 and
P16).

## 8. Generate the specs

Each selected path becomes one xUnit test that runs the generated workflow in memory, with `WorkflowSpec` from
SourceFlow.Testing. The Specs part at the bottom of the Paths tab has **Preview specs** and **Write specs**.

To generate specs, you need:

- the local host (the compose site has none);
- a `tests` folder and a `module` for the owner in `sourceflow.domain.yaml` (both samples have them);
- the workflow saved (specs are generated from the saved file);
- for **Write specs**, a host started without `--read-only`.

**Try it.** Keep the minimal cover selected and select **Preview specs**. Studio says "6 tests for
Orders.Tests/Workflows/OrderFulfilmentV1PathTests.cs" and shows the file. It is identical to the committed
[`OrderFulfilmentV1PathTests.cs`](../../../../samples/OrderFulfilment/Orders.Tests/Workflows/OrderFulfilmentV1PathTests.cs),
which Studio generated. Each test names its path:

```csharp
/// <summary>P218 reserve-stock: rejected inventory.out_of_stock ⇒ Compensated</summary>
[Fact]
public void P218_Compensated()
{
    var spec = WorkflowSpec.For<Orders.OrderFulfilmentWorkflowV1, Orders.OrdersModule>();
    spec.Start("orders.order-placed.v1", """{"orderId":"bdcbea6c-…","lines":[],"total":100,"shippingAddress":"sample-shippingAddress"}""")
        .ThenSends("inventory.reserve-stock.v1");
    spec.Reject("inventory.out_of_stock")
        .ThenSends("orders.cancel-order.v1");
    spec.ThenStatus(WorkflowStatus.Compensated);
}
```

Studio chose the start data. `total` is 5001 in the paths where the review runs, and 100 where it's skipped. **Write
specs** writes the file. Then run:

```bash
dotnet test --project samples/OrderFulfilment/Orders.Tests
```

Studio overwrites only files that start with `// <auto-generated>`. It refuses to replace a test file you wrote yourself.
Regenerate the specs whenever you change the workflow.

## 9. Change a workflow

Editing needs the local host without `--read-only`. Studio writes to the sample's files, so start from a clean working
copy, and when you have finished, run `git restore samples/OrderFulfilment`.

The YAML is the model. A visual edit (in Properties or the palette) becomes a minimal text edit, so comments, ordering
and formatting stay as they were. Typing in the YAML tab edits the same text. **Undo** and **Redo** cover both. Studio
validates every change as you make it, against all four manifests.

### First: a published workflow needs a new version

Make any structural change, for example the first exercise below. The diagnostics panel then shows:

```text
✕ Error WF-V15 line 6, column 10: order-fulfilment@1 changed since it was published; in-flight instances run the
published structure, so give the change 'version: 2' (doc 15 §5.6).
```

The orders manifest records the fingerprint of `order-fulfilment@1`. Running instances keep the structure they started
with, so a changed structure needs a new version. In the **YAML** tab, change `version: 1` to `version: 2`. The error
goes away, and the exercises below show only their own diagnostics. (In a real service, you deploy both versions; see the
[workflows guide §5](workflows.md#5-running-and-operating).)

### Break a rule and read the diagnostics

Select `create-shipment`. Under **Await**, set **Then (rejection shipping.address_invalid)** from "go to
await-address-fix" to **fail (compensate)**. Two errors appear:

```text
✕ Error WF-V07 …: Step 'create-shipment' is at or after the pivot, so a failure can't 'fail' (no compensation after the
point of no return); use manual, complete or goto.
✕ Error WF-V05 …: Step 'await-address-fix' is never reached.
```

The states concerned carry a "✕ n problems" badge. Select a diagnostic, or press `F8`. Studio opens the YAML tab at its
line and selects its step. Press `Ctrl+Z` and both errors go away.

Now select `reserve-stock`, clear **Timeout after**, and press Enter:

```text
✕ Error WF-V06 …: Step 'reserve-stock' waits without a timeout; add 'timeout: { after: PT…, then: … }', or
'timeout: { none: true, reason: … }'.
```

Every wait needs a timeout. An outage never reaches a workflow as a failure; the workflow only sees time pass. Undo again.

The build applies the same checks. In the build, each problem is `SF0020` at its line and column in the YAML file. The
[reference](../SourceFlow.Studio-README.md#diagnostic-ids) lists every rule.

### Add a step with the palette

Select `charge-payment`. Below the properties, **Add a step** says "After charge-payment." The palette offers the commands
of all four services, grouped by service. For a command, **Waits for** offers only the events that command emits.
Choose:

- **Command**: (waits only: sends nothing)
- **Waits for**: `orders.order-approved.v1`

Then select **Add step**. A step named `wait` appears after `charge-payment` on the canvas and in the YAML, and the
diagnostics flag it at once: `Step 'wait' waits without a timeout`. In its Properties:

1. Set **Timeout after** to `P1D` and press Enter. **Then (timeout)** starts as `fail (compensate)`, and the error goes
   away.
2. Change **Id** to `manager-approval` and press Enter. A rename also renames any `next` and `goto` that pointed at the
   step.
3. Open **Paths**. The summary now reads 507 paths over 27 edges, with a minimal cover of 7: every order now waits for
   an approval, which can also time out.

Use **Remove step manager-approval** when you're done.

### Arrange and save

Drag states to rearrange the statechart. **Save** (`Ctrl+S`) writes the workflow. If you moved states, it also writes
their positions to `order-fulfilment.sfflow.layout.json` beside it. The generator ignores that file; commit it if you
want the layout shared. The toolbar's status reads "Unsaved changes", "Saved." or "No changes". A YAML file that doesn't
parse can be edited as text only, and Studio refuses visual edits until the YAML tab fixes it.

When you have finished, `git restore samples/OrderFulfilment` puts the sample back.

## 10. Export diagrams and share the workspace

**Exports.** The workflow toolbar downloads the open workflow, made in your browser:

| Button | File | Use it for |
|--------|------|------------|
| SVG | `order-fulfilment.svg` | Docs and slides: the statechart as the canvas shows it, in the current theme |
| PNG | `order-fulfilment.png` | The same at twice the size |
| Mermaid | `order-fulfilment.mmd` | A `stateDiagram-v2` that GitHub and many wikis render |
| BPMN | `order-fulfilment.bpmn` | BPMN 2.0 tools (the semantic model, without diagram layout) |

The Mermaid export starts like this:

```text
---
title: order-fulfilment@1 (orders)
---
stateDiagram-v2
    state "Compensating" as Compensating
    state "Manual intervention" as ManualIntervention
    state "reserve-stock" as reserve_stock
    reserve_stock : send inventory.reserve-stock.v1
    reserve_stock : await inventory.stock-reserved.v1
```

**A read-only site for your team.** The compose sites are static snapshots of a workspace, and you can build one for a
docs portal or GitHub Pages. In this repository, after building the front end (`npm run build:wasm` and `npm run build`
in `web/`):

```bash
node web/app/bin/studio.mjs build --workspace samples/OrderFulfilment --out site
```

The sample's [`Studio.Dockerfile`](../../../../samples/OrderFulfilment/Studio.Dockerfile) does exactly this and serves
the folder with nginx. Simulation, paths and exports work on the site. Spec generation needs the local host.

**Diagrams on pull requests.** The `workflow-diff` action comments on pull requests that change a `*.sfflow.yaml`. The
comment describes the changes in words and shows before and after diagrams with the changes marked. See the
[reference](../SourceFlow.Studio-README.md#the-workflow-diff-github-action).

## 11. From the simulator to the running services

The paths you simulated are the ones the running sample takes. With the OrderFulfilment stack up
(`docker compose -f samples/OrderFulfilment/docker-compose.yml up -d`), place orders and compare:

| In Studio | In the running sample |
|-----------|-----------------------|
| A path that completes with the review skipped | An order of `"lines": ["kettle"]`, `"total": 120` |
| P218, out of stock | The line `discontinued-lamp` |
| P127, fraud suspected | A total of `30.66` (the sandbox gateway's fraud value) |
| `charge-payment` retried after `payments.declined` | A total of `30.51`: charged again three times, ten minutes apart |
| The review, approved | A total over 5000, then `POST /orders/{id}/approve` |
| `await-address-fix`, then shipped | The address `"somewhere"`, then `POST /orders/{id}/address` |
| P23 or P30, then the operator's choices | A large order nobody reviews for a day, then `sourceflow workflow abort … --compensate` |

For example:

```bash
ORDER=0193b000-0000-7000-8000-000000000002
curl -X POST http://localhost:8090/orders/$ORDER -H 'Content-Type: application/json' \
  -d '{ "lines": ["kettle"], "total": 30.66, "shippingAddress": "1 High Street, Leeds, LS1 1AA" }'

curl -X POST http://localhost:8090/query -H 'Content-Type: application/json' \
  -d '{ "query": "from readmodel \"workflow-instances\" | project instance, status, step, failureCode" }'
# … ["0193b000-0000-7000-8000-000000000002","Compensated",null,"payments.fraud_suspected"] …
```

The Aspire dashboard (http://localhost:18890) shows the same sequence as the P127 swimlane, as one trace across the
services. The operator's commands, inside the orders container:

```bash
COMPOSE="docker compose -f samples/OrderFulfilment/docker-compose.yml"
$COMPOSE exec orders dotnet OrderFulfilment.Host.dll sourceflow workflow list --name order-fulfilment --active
$COMPOSE exec orders dotnet OrderFulfilment.Host.dll sourceflow workflow show order-fulfilment@1 $ORDER
```

## Troubleshooting

| Symptom | What to do |
|---------|------------|
| The browser shows "The Studio app isn't in this build" | The tool was built before the front end. In `web/`, run `npm ci`, `npm run build:wasm` and `npm run build`, then start Studio with `--assets web/app/dist --framework artifacts/studio-wasm/wwwroot/_framework` |
| `The Studio host could not listen on port 5180` | Another Studio is running; pass `--port 0` or another port |
| 403 "The Studio host answers loopback names only" | Open `http://127.0.0.1:<port>/` or `http://localhost:<port>/`, not another host name |
| Save, Write specs or Export the manifest again is disabled | The host runs with `--read-only` (the header says so), or you are on a compose site; for the export, the service has no `project` in the workspace file |
| Preview and Write specs are disabled | Save first; the owner needs `tests` and `module` in `sourceflow.domain.yaml` |
| Write specs fails with 409 | The target file wasn't generated by Studio; move or delete it |
| WF-V15 after any change | The workflow is published; give the change a new `version` ([§9](#first-a-published-workflow-needs-a-new-version)) |
| "The workflow changed since this simulation started" | Restart the simulation |
| A guarded step is always skipped in the simulator | Its guard reads data the start event didn't give; add the field (for example `"total": 6000`) or set the guard picker to **Run the step** |
| A manifest change doesn't show | Studio loads the manifests when it starts; restart it (or reload the compose site after rebuilding its image) |

The [reference's troubleshooting](../SourceFlow.Studio-README.md#troubleshooting) covers build and manifest errors.
