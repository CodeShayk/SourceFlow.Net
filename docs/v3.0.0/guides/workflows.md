# Workflows and SourceFlow Studio

A workflow is a long-running process that one service runs across several: it sends commands to their owners, waits for
their events, retries, times out, undoes what it did when a later step fails, and stops for an operator when it can't
go on. You write it in YAML (`*.sfflow.yaml`). The build compiles it into a process manager in the service that owns it.
SourceFlow Studio draws it, checks it as you type, simulates it, and generates its tests.

This guide follows the [TravelBooking sample](../../../samples/TravelBooking/README.md): two services (bookings and
hotels) and the trip-booking workflow, which bookings runs. It holds a room at the hotel, waits for the guest to confirm,
then confirms the reservation. `docker compose up -d --build` at the repository root runs it, with a read-only Studio for
its workspace on http://localhost:5080. The [DSL reference](workflow-dsl-reference.md) lists every key; design doc 15 has
the semantics and the design.

## 1. The domain workspace

Studio and the validator see the whole domain through a workspace file, `sourceflow.domain.yaml`, which lists each
service and its manifest ([`samples/TravelBooking/sourceflow.domain.yaml`](../../../samples/TravelBooking/sourceflow.domain.yaml)):

```yaml
domain: travel
description: A guest books a hotel room
services:
  - name: bookings
    manifest: Bookings/sourceflow.manifest.json
    workflows: Bookings/Workflows        # the workflows bookings owns
    tests: TravelBooking.Tests           # where Studio writes their specs
    module: Bookings.BookingsModule      # the module the specs register
  - name: hotels
    manifest: Hotels/sourceflow.manifest.json
```

A manifest says what a service owns and how it is wired: its messages and their fields, each command's events (`emits`)
and rejection codes (`rejects`), its routes, publications and subscriptions. The build reads the code; the design-time
export adds the configuration, without starting anything. The host's configuration requires a connection string, so the
export passes a placeholder that is never used:

```bash
dotnet sourceflow manifest export --project samples/TravelBooking/Bookings.Api \
  --output samples/TravelBooking/Bookings/sourceflow.manifest.json -- "--ConnectionStrings:SourceFlow=Host=localhost;Port=1"
```

Commit the manifests, and let CI export them again with `--check`: a manifest that no longer matches its service fails the
build.

The generator finds `emits` and `rejects` in `Decide` methods and command handlers: `Decision.Accept(new X(…))` and
`Decision.Reject(new Error(Codes.Y, …))` with a constant code. When it can't, it warns (SF0019); say it with
`[Emits]`/`[Rejects]`.

## 2. Writing a workflow

The workflow lives with the service that runs it
([`Bookings/Workflows/trip-booking.sfflow.yaml`](../../../samples/TravelBooking/Bookings/Workflows/trip-booking.sfflow.yaml)):

```yaml
sourceflow.workflow: 1
name: trip-booking
version: 1
owner: bookings

start:
  on: bookings.booking-requested.v1   # a domain event of bookings
  correlate: data.bookingId           # the instance id; hotels' events carry it too
data:                                 # the guest's name is not kept
  bookingId: { from: start.data.bookingId }
  hotelId:   { from: start.data.hotelId }
  nights:    { from: start.data.nights }

steps:
  - id: hold-room
    send:  { command: hotels.reserve-room.v1, map: { hotelId: $data.hotelId, bookingId: $data.bookingId, nights: $data.nights } }
    await:
      success: hotels.room-reserved.v1
      failure:
        - rejected: hotels.sold_out
          then: fail                  # nothing to undo yet: the booking is declined
        - rejected: hotels.unknown_hotel
          then: fail
    timeout: { after: PT1M, retry: { max: 2 }, then: fail }
    compensate:                       # undo the hold when a later step fails
      send:  { command: hotels.release-room.v1, map: { hotelId: $data.hotelId, bookingId: $data.bookingId } }
      await: { success: hotels.room-released.v1 }
      retry: { max: 5, backoff: exponential, initial: PT10S, cap: PT5M }
      timeout: { after: PT10M }

  - id: await-guest
    await:                            # no command: wait for the guest
      success: bookings.guest-confirmed.v1
      failure:
        - on: bookings.guest-cancelled.v1
          then: fail                  # release the hold, decline the booking
    timeout: { after: PT15M, then: fail }

  - id: confirm-reservation
    pivot: true                       # point of no return: the guest was told the booking stands
    send:  { command: hotels.confirm-reservation.v1, map: { hotelId: $data.hotelId, bookingId: $data.bookingId } }
    await:
      success: hotels.reservation-confirmed.v1
      failure:
        - rejected: hotels.hold_lost
          then: manual                # hotel staff released the hold: an operator rebooks the guest
    timeout: { after: PT1M, retry: { max: 3, backoff: exponential, initial: PT10S }, then: manual }

on:
  completed:   { send: { command: bookings.confirm-booking.v1, map: { bookingId: $data.bookingId } } }
  compensated: { send: { command: bookings.decline-booking.v1, map: { bookingId: $data.bookingId, reason: $failure.code } } }
  manual:      { alert: true }
```

What each part does:

- **`start`** creates an instance per start event. `correlate` names the instance id; awaited events that carry the same
  field find the instance by it (here `bookingId`, which the hotels' events carry). Replies to the workflow's own commands
  find it through the envelope's correlation.
- **`data`** is the instance's state. Values come from the start event, or from `capture` on a success
  (`capture: { roomNumber: event.data.roomNumber }`, if the hotel's event carried a room number); a later capture can
  replace a value. Keep personal data out of it:
  the sample leaves the guest's name with the booking.
- **A step** sends a command, or nothing to only wait (`await-guest` waits for the guest's confirmation), and waits for one
  success event. Steps run in order; `next` changes that after a success, and `{ goto: step }` after a failure.
  `next: complete` ends the workflow.
- **Failures** are rejections of the step's command (`rejected: code`, such as `hotels.sold_out`) or failure events
  (`on: type`, such as `bookings.guest-cancelled.v1`). A failure can be retried (`retry`) before its `then` applies:
  `fail` undoes what was done, `manual` stops for an operator, `complete` ends the workflow, `{ goto }` continues
  elsewhere. An outcome the step doesn't declare stops the instance for an operator.
- **Timeouts.** Every wait has one (WF-V06), because an outage never reaches the workflow as a failure: the runtime retries
  and parks messages, and the workflow only sees time pass. A timeout can be retried; each attempt sends the command
  again with a new id. When the Hotels service is down, `hold-room` sends the hold again twice, a minute apart, before it
  gives up; a guest who doesn't confirm within 15 minutes lets the hold lapse.
- **Compensations** undo a step after a later failure, in reverse order. When the guest cancels or the hold lapses,
  `hold-room`'s compensation releases the room, and `on.compensated` declines the booking. A compensation that keeps
  failing stops the instance for an operator, with the rest of the stack kept.
- **`pivot: true`** marks the point of no return: after it, failures can't compensate (WF-V07), so they recover forward.
  In the sample, `confirm-reservation` is the pivot: once the guest was told the booking stands, a hold that hotel staff
  released (`hotels.hold_lost`) can't be undone automatically, so the instance stops for an operator.
- **Guards** (`when`) are CESQL over `$data.*` and `event.data.*`. A step whose guard doesn't hold is skipped, for
  example a step with `when: $data.nights > 14`. The trip-booking workflow has none.
- **`$hook`** computes a map value in C#: a `private static partial object? Name(IReadOnlyDictionary<string, object?> data)`
  on the generated workflow record, which you implement (SF0021 when it is missing). Hooks are pure, like `Decide`.

Studio and the build check the workflow against the workspace's manifests: types exist, each command has one owner,
awaited events come from the command and reach the owner, rejections are declared and replied to, every state is
reachable and one path completes, retries are bounded, maps fit the commands' fields, and more (WF-V01 to WF-V16, doc 15
§5.4). In the build, each problem is SF0020 at the line and column in the YAML file.

## 3. Wiring the services

The owner must be able to send the commands, receive the events and receive the rejections. The Bookings service does it
in `AddBookingsService` ([`Bookings/Bookings.cs`](../../../samples/TravelBooking/Bookings/Bookings.cs)), which its host,
the design-time export and the tests share:

```csharp
public static SourceFlowBuilder AddBookingsService(this SourceFlowBuilder sf)
{
    sf.ServiceName = "bookings";
    sf.AddModule<BookingsModule>();
    sf.Projections(p => p.Add<BookingSummaryProjection>().Inline());
    sf.Routing(r =>
    {
        r.Command<ReserveRoom>().To("hotels").ReplyOnReject();        // the workflow handles its rejection
        r.Command<ReleaseRoom>().To("hotels");
        r.Command<ConfirmReservation>().To("hotels").ReplyOnReject();
        r.Subscribe("hotels.events");                                 // the events the workflow awaits
    });
    return sf;
}
```

The Hotels service publishes the awaited events (`r.Publish<RoomReserved>().AsIs().To("hotels.events")`, in
`AddHotelsService`). Rejections come back as `CommandRejected<T>`, which the owner's JSON context lists
(`CommandRejected<ReserveRoom>`, `CommandRejected<ConfirmReservation>`).

**Register the other services' contracts in the owner's module.** The workflow builds and reads other services' messages
in the owner's process, so the owner's module must register them, with their id converters. The Hotels service ships a
contracts module (`HotelsContractsModule`) that registers only its public commands and events, without the aggregate
that decides them. The Bookings service's module registers its own generated module and that contract:

```csharp
public sealed class BookingsModule : ISourceFlowModule
{
    public static string ModuleName => nameof(BookingsModule);

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        BookingsDomainModule.Register(builder);      // the booking aggregate, its read model and the generated workflow
        HotelsContractsModule.Register(builder);     // the Hotels commands and events the workflow sends and awaits
    }
}
```

It is also the module the workspace names for the specs (`module: Bookings.BookingsModule`), so a spec runs the workflow
exactly as the service does.

## 4. Testing

`WorkflowSpec` runs a workflow in memory, outcome by outcome:

```csharp
var spec = WorkflowSpec.For<TripBookingWorkflowV1, BookingsModule>();
spec.Start("bookings.booking-requested.v1", """{"bookingId":"…","hotelId":"…","guest":"Ada","nights":2}""")
    .ThenSends("hotels.reserve-room.v1");
spec.Reject("hotels.sold_out").ThenSends("bookings.decline-booking.v1");
spec.ThenStatus(WorkflowStatus.Compensated);
```

Studio writes these for you: the Paths view enumerates every path (retry loops at 0, 1 and the maximum) and selects a
minimal set that covers every transition and every end status. **Generate specs** writes one test per path to
`{tests}/Workflows/{Name}V{version}PathTests.cs`. The trip-booking workflow has 27 paths, ending `Completed`,
`Compensated` or `ManualIntervention`, and a minimal cover of 6
([`TravelBooking.Tests/Workflows/TripBookingV1PathTests.cs`](../../../samples/TravelBooking/TravelBooking.Tests/Workflows/TripBookingV1PathTests.cs)).
Regenerate them after changing the workflow; Studio only overwrites files it generated. The aggregates have hand-written
specs beside them ([`DomainTests.cs`](../../../samples/TravelBooking/TravelBooking.Tests/DomainTests.cs)):

```bash
dotnet test --project samples/TravelBooking/TravelBooking.Tests
```

The repository's end-to-end tests run the sample's two services as separate hosts (TB-E2E-01 to 06): the booking
confirmed on PostgreSQL over the in-memory broker and the AWS, Azure and Google Cloud transports, and the other outcomes
(sold out, the guest too late, the guest cancelling, the hold lost after the pivot and an operator's abort, an outage of
the Hotels service ridden out by retries) on the in-memory store and PostgreSQL:

```bash
dotnet test --project tests/EndToEnd/SourceFlow.EndToEnd.Tests -- --filter-class "SourceFlow.EndToEnd.Tests.TravelBookingScenarios"
```

## 5. Running and operating

The generated process manager is registered by the owner's module. It records each transition on
`sourceflow.workflow.transitions` and opens a `workflow {name} {step}` span. An instance that stops for an operator counts on
`sourceflow.workflow.manual_intervention`, and the `SourceFlowWorkflowManualIntervention` alert pages on it. Instances are
in the `workflow-instances` read model and the generated `<service>.workflow-instances` domain query
(`bookings.workflow-instances` in the sample):

```bash
curl -X POST http://localhost:8080/query -H 'Content-Type: application/json' \
  -d '{ "query": "from readmodel \"workflow-instances\" | project instance, status, step" }'
```

The CLI works on a service's instances. Run it in the service (`dotnet run -- sourceflow workflow …`, or in the demo's
container as below), or with the global tool:

```bash
docker compose exec bookings sh -c 'dotnet Bookings.Api.dll sourceflow workflow list --name trip-booking --active'
docker compose exec bookings sh -c 'dotnet Bookings.Api.dll sourceflow workflow show trip-booking@1 <booking id>'

sourceflow workflow resume trip-booking@1 <booking id> [--step confirm-reservation] [--note "…"]
sourceflow workflow retry-step trip-booking@1 <booking id>
sourceflow workflow abort trip-booking@1 <booking id> --reason "…" [--compensate]
```

`resume` continues a compensation that stopped, or runs the current (or given) step again. `retry-step` sends the current
step or compensation again as a new attempt. `abort` ends the instance, compensating first with `--compensate`. In the
demo, release a booking's hold as hotel staff (`POST http://localhost:8081/hotels/{hotel}/holds/{booking}/release`) and
then confirm as the guest: the instance stops in `ManualIntervention`, and the operator aborts it once the guest is
rebooked (`--reason 'rebooked by phone'`).

**Versions.** A structural change to a published workflow needs a new `version` (WF-V15). Deploy both versions: new
instances start on the latest, and running instances finish on the version they started on, even when the versions live in
different modules or assemblies. A service refuses to start without a version that still has active instances
(`WorkflowVersionRemovedException`); `sourceflow workflow list --name trip-booking --version 1` says when it can go.

## 6. SourceFlow Studio

```bash
dotnet sourceflow studio --workspace samples/TravelBooking       # http://127.0.0.1:5180 (loopback only)
dotnet sourceflow studio --workspace samples/TravelBooking --read-only
```

In this repository, run the tool from source: `dotnet run --project src/Tooling/SourceFlow.Tool -- studio --workspace
samples/TravelBooking`. The `docker compose` demo serves the same workspace read-only on http://localhost:5080.

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
workspace as a static site that works from any folder, for example GitHub Pages. The demo's Studio image is such a site
([`samples/TravelBooking/Studio.Dockerfile`](../../../samples/TravelBooking/Studio.Dockerfile)).

**Diagrams on pull requests.** The `workflow-diff` action (`studio/action`) comments on pull requests that change
`*.sfflow.yaml` files: the changes in words, before and after diagrams with what changed marked, and SVGs as an artifact.

**Keyboard and accessibility.** `Shift+?` shows the keyboard map; `Alt+1`, `Alt+2` and `Alt+3` go to the explorer, the
editor and the diagnostics, `F8` and `Shift+F8` move through the diagnostics, and `Alt+T` switches the theme. Every state and transition has a name for screen
readers. Everything you can do with the mouse you can do with the keyboard, including adding a step and its failures.
Studio follows the system's light or dark theme and reduced-motion setting. Text meets 4.5:1 contrast, and the outlines
of states, edges and controls meet 3:1.
