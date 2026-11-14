# SourceFlow Studio

SourceFlow Studio is the workflow track of SourceFlow.Net v3. You describe a long-running, cross-service process (an orchestrated saga) in a small YAML file (`*.sfflow.yaml`). The build compiles it into an ordinary process manager in the service that owns it. Studio checks it against what every service of the business domain really owns and publishes, simulates it, enumerates every path it can take, generates tests for those paths, and draws it as a statechart, a swimlane sequence, Mermaid or BPMN. It is not a workflow engine: at run time a workflow is a generated process manager with the same storage, outbox, inbox, timers and telemetry as a hand-written one ([ADR-020](../ADRs/ADR-020-process-managers-with-durable-state-and-timers.md), [ADR-024](../ADRs/ADR-024-workflow-dsl-compiled-to-process-managers.md)). Services that don't use workflows take no dependency on any of it.

This document covers the tooling. The [workflows guide](../guides/workflows.md) is the tutorial, the [DSL reference](../guides/workflow-dsl-reference.md) lists every key of the file format, and [Architecture/09 Process managers and workflows](Architecture/09-Process-Managers-and-Workflows.md) explains how workflows run.

## Contents

- [What is in the box](#what-is-in-the-box)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Service manifests](#service-manifests)
- [The domain workspace](#the-domain-workspace-sourceflowdomainyaml)
- [Workflow files](#workflow-files)
- [Build-time compilation](#build-time-compilation)
- [The SourceFlow.Studio library](#the-sourceflowstudio-library)
- [The WebAssembly core and the TypeScript client](#the-webassembly-core-and-the-typescript-client)
- [The Studio app](#the-studio-app)
- [Static site](#static-site)
- [The workflow-diff GitHub Action](#the-workflow-diff-github-action)
- [Operational notes](#operational-notes)
- [Troubleshooting](#troubleshooting)
- [Links](#links)

## What is in the box

| Part | Where | What it does |
|------|-------|--------------|
| Workflow DSL `sourceflow.workflow: 1` | [`schemas/workflow-1.json`](../../../schemas/workflow-1.json) | The file format. The JSON Schema is normative for structure; semantic rules are the validator's |
| Service manifest `sourceflow.manifest.json` (`manifestVersion: 1`) | Generated per service | What a service owns and how it is wired: messages and their fields, each command's `emits` and `rejects`, routes, publications, subscriptions, workflows |
| Domain workspace `sourceflow.domain.yaml` | Your repository | The services of one business domain and their manifests |
| Workflow compiler | `SourceFlow.Generators`, packed inside `SourceFlow.Net` | Validates `*.sfflow.yaml` at build time (SF0020, SF0021) and generates the process manager |
| Runtime types | `SourceFlow.Net`, namespace `SourceFlow.Workflows` | `WorkflowStatus`, `WorkflowInstanceId`, `WorkflowTimer`, `WorkflowControl`, the `workflow-instances` read model |
| `SourceFlow.Studio` | [`src/Workflows/SourceFlow.Studio`](../../../src/Workflows/SourceFlow.Studio) | The .NET library: workspace loader, reader, validator (WF-S01..S06, WF-V01..V16), simulator, path enumerator, spec generator, Mermaid/BPMN exporters, statechart and swimlane models |
| `SourceFlow.Studio.Wasm` | [`src/Workflows/SourceFlow.Studio.Wasm`](../../../src/Workflows/SourceFlow.Studio.Wasm) | The same DSL sources compiled to `browser-wasm` behind a JSON API (`[JSExport]`) |
| `@sourceflow/core-client` | [`studio/core-client`](../../../studio/core-client) | Typed TypeScript client for the WebAssembly core (Web Worker in the browser, in-process in Node) |
| `@sourceflow/studio` | [`studio/app`](../../../studio/app) | The React designer and the `sourceflow-studio build` static-site command |
| `dotnet sourceflow studio` | `SourceFlow.Tool` | The local host that serves the designer and a loopback-only API over the workspace |
| `workflow-diff` action | [`studio/action`](../../../studio/action) | Before/after statecharts and a summary comment on pull requests that change `*.sfflow.yaml` |
| `WorkflowSpec` | `SourceFlow.Testing` | Runs a generated workflow in memory, outcome by outcome; the API generated specs use |

One semantics: parsing, validation and simulation are written once, in C#, under `src/Workflows/SourceFlow.Studio/Dsl`. The Roslyn generator compiles those sources into itself, the browser runs them as WebAssembly, and the generated process manager embeds the simulator as its engine. TypeScript only renders, edits and calls the core ([ADR-025](../ADRs/ADR-025-studio-react-spa-with-shared-wasm-core.md), [ADR-027 §6](../ADRs/ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md)).

## Installation

Everything .NET targets `net10.0` only.

| You want to | Add |
|-------------|-----|
| Own and run workflows in a service | `dotnet add package SourceFlow.Net` (the generator and the runtime types are in it; nothing else is needed) |
| Test generated workflows | `dotnet add package SourceFlow.Testing` (`WorkflowSpec`) |
| Validate, simulate or export workflows from your own .NET code | `dotnet add package SourceFlow.Studio` |
| Export manifests, run Studio, operate instances | The `dotnet sourceflow` command-line tool ([SourceFlow.Tool](SourceFlow.Tool-README.md)) |

Inside this repository, run the tool from source: `dotnet run --project src/Tooling/SourceFlow.Tool -- <command>`. The examples below write `dotnet sourceflow <command>`.

**The designer front end.** The tool embeds the built Studio app and WebAssembly core when they were built before it. To build them (Node 22.18 or later and the .NET 10 SDK; no `wasm-tools` workload is needed):

```bash
cd studio
npm ci
npm run build:wasm     # publishes SourceFlow.Studio.Wasm to artifacts/studio-wasm
npm run build          # builds studio/app/dist
```

`studio/` is an npm workspace with three packages: `@sourceflow/core-client`, `@sourceflow/studio` and `@sourceflow/workflow-diff`. Publishing `@sourceflow/studio` to npm and `sourceflow/workflow-diff` to the GitHub Marketplace is a release step; until then use them from the repository as shown below.

## Quick start

This walk-through uses the [TravelBooking sample](../../../samples/TravelBooking/README.md): two services, bookings and hotels, and the `trip-booking` workflow, owned by bookings. To look before you build, `docker compose up -d --build` at the repository root serves SourceFlow Studio for this workspace, read-only, on http://localhost:5080.

**1. Export each service's manifest.** Each service has its own host. The host's configuration requires a connection string, so the export passes a placeholder after `--` (the host is built, never started):

```bash
dotnet sourceflow manifest export --project samples/TravelBooking/Bookings.Api \
  --output samples/TravelBooking/Bookings/sourceflow.manifest.json -- "--ConnectionStrings:SourceFlow=Host=localhost;Port=1"
dotnet sourceflow manifest export --project samples/TravelBooking/Hotels.Api \
  --output samples/TravelBooking/Hotels/sourceflow.manifest.json -- "--ConnectionStrings:SourceFlow=Host=localhost;Port=1"
```

**2. Describe the domain** in `samples/TravelBooking/sourceflow.domain.yaml`:

```yaml
domain: travel
description: A guest books a hotel room
services:
  - name: bookings   # orchestrator of trip-booking
    manifest: Bookings/sourceflow.manifest.json
    workflows: Bookings/Workflows
    tests: TravelBooking.Tests
    module: Bookings.BookingsModule
  - name: hotels
    manifest: Hotels/sourceflow.manifest.json
```

**3. Write the workflow** in the owning service, `Bookings/Workflows/trip-booking.sfflow.yaml` (the [guide](../guides/workflows.md#2-writing-a-workflow) explains each part):

```yaml
sourceflow.workflow: 1
name: trip-booking
version: 1
owner: bookings

start:
  on: bookings.booking-requested.v1
  correlate: data.bookingId
data:
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
          then: fail
        - rejected: hotels.unknown_hotel
          then: fail
    timeout: { after: PT1M, retry: { max: 2 }, then: fail }
    compensate:
      send:  { command: hotels.release-room.v1, map: { hotelId: $data.hotelId, bookingId: $data.bookingId } }
      await: { success: hotels.room-released.v1 }
      retry: { max: 5, backoff: exponential, initial: PT10S, cap: PT5M }
      timeout: { after: PT10M }
  # await-guest, confirm-reservation (the pivot) …
```

**4. Build the service.** `SourceFlow.Net` adds every `*.sfflow.yaml` in the project to the compilation. Problems are reported as `SF0020` at the line and column of the YAML file; a clean build produces `Bookings.TripBookingWorkflowV1`, registered by the service's module.

**5. Open the workspace in Studio:**

```bash
dotnet sourceflow studio --workspace samples/TravelBooking --open
# SourceFlow Studio: http://127.0.0.1:5180/. Press Ctrl+C to stop.
```

The domain map shows the two services. Open the workflow: it is valid against both manifests, and the Paths tab lists 27 paths with a minimal cover of 6.

**6. Generate and run the specs.** In the Paths tab, keep the minimal cover selected, **Preview specs**, then **Write specs**. Studio writes `TravelBooking.Tests/Workflows/TripBookingV1PathTests.cs`:

```bash
dotnet test --project samples/TravelBooking/TravelBooking.Tests
```

## Service manifests

A manifest describes one service as data. Studio, the validator and the generator read manifests; they never load a service's assemblies.

### What a manifest contains

| Key | Content | Source |
|-----|---------|--------|
| `manifestVersion` | Always `1` | |
| `service`, `endpoint` | The service name and its endpoint | Configuration |
| `sourceflowVersion` | The SourceFlow.Net version the service was built with | Build |
| `messages[]` | Every `[MessageType]`: `type`, `version`, `kind` (`command`, `event`, `integration-event`, `timeout`), `clrType`, the target `aggregate` of a command, and `fields` (JSON name, schema-like `type`, `nullable`, `personalData`) | Build |
| `aggregates[]` | Name, CLR type, `commands[]` with `type`, `version`, `emits` and `rejects`, and the `events` the aggregate's `When` handles | Build (static analysis) |
| `processManagers[]` | Hand-written process managers: `name`, `handles`, `sends`, `schedules` | Build |
| `projections[]` | Name, version and handled events | Build |
| `publishes[]` | `{ domainEvent, as, topic }` | Configuration |
| `subscribes[]` | `{ topic, only?, where? }` | Configuration |
| `commandRoutes[]` | `{ command, to, replyOnReject }` | Configuration |
| `workflows[]` | `{ name, version, file, fingerprint }` of the DSL workflows the service owns (`fingerprint` is `sha256:` and 16 hex digits) | Build |
| `schemas` | A reference to the service's schema catalog, for example `catalog://hotels` | |

Message references use the CloudEvents form `{name}.v{version}`, for example `hotels.room-reserved.v1`. Command entries keep `type` and `version` apart. An excerpt of the TravelBooking sample's hotels manifest ([`samples/TravelBooking/Hotels/sourceflow.manifest.json`](../../../samples/TravelBooking/Hotels/sourceflow.manifest.json)):

```json
{
  "manifestVersion": 1,
  "service": "hotels",
  "endpoint": "hotels",
  "sourceflowVersion": "3.0.0",
  "aggregates": [
    {
      "name": "hotel",
      "clrType": "Hotels.Hotel",
      "commands": [
        {
          "type": "hotels.reserve-room",
          "version": 1,
          "emits": [ "hotels.room-reserved.v1" ],
          "rejects": [ "hotels.sold_out", "hotels.unknown_hotel" ]
        }
      ],
      "events": [ "hotels.hotel-registered.v1", "hotels.reservation-confirmed.v1", "hotels.room-released.v1", "hotels.room-reserved.v1" ]
    }
  ],
  "publishes": [
    { "domainEvent": "hotels.room-reserved.v1", "as": "hotels.room-reserved.v1", "topic": "hotels.events" }
  ],
  "schemas": "catalog://hotels"
}
```

A generated workflow appears under `workflows`, not under `processManagers`.

### Build time: the static manifest

The generator puts the code-derived part in every assembly that has SourceFlow types, as `[assembly: SourceFlowManifest("<json>")]`. It finds `emits` and `rejects` by reading each `Decide` method (and each command handler's `HandleAsync`):

- events created inline in `Decision.Accept(…)`, including params, collection expressions, conditionals and switch arms;
- rejection codes from `new Error("code", …)`, static `Error` fields and properties initialised that way (also in another file), `with` expressions, `Error.InvalidCommand` and the `Error` built-ins.

When it can't tell, it reports **SF0019** (warning, category `SourceFlow.Manifest`): an event built in a helper, a computed code, or a decision returned by a helper. Declare the outcomes with `[Emits]` and `[Rejects]` on the method (both in namespace `SourceFlow`, repeatable). The declarations are added to the manifest and silence the warning:

```csharp
[Emits(typeof(Happened))]
[Rejects("opaque.declared")]
public Decision Decide(E c, DecisionContext ctx) => Helper(c.ItemId);

private static Decision Helper(ItemId id) => Decision.Accept(new Happened(id));
```

### Design time: `dotnet sourceflow manifest export`

Routes, publications, subscriptions and `replyOnReject` live in configuration, so the export runs your application's `AddSourceFlow(…)` configuration without starting anything:

1. It builds the project (unless `--no-build`).
2. It runs `dotnet run --no-build --no-launch-profile` with `SOURCEFLOW_DESIGN_TIME=1` and `SOURCEFLOW_MANIFEST_OUT=<temporary file>`.
3. In design-time mode `AddSourceFlow` doesn't register the SourceFlow host. On the generic host's `HostBuilt` event it writes the merged manifest and ends the process. No hosted service, provisioning, storage or transport call runs, and code after `Build()` doesn't run either.
4. The manifest is written as indented, sorted JSON with LF line endings, so repeated runs are byte-identical.

The runtime model decides which messages, aggregates, process managers and projections are listed; the static manifests supply `emits`, `rejects` and fields.

| Option | Default | Meaning |
|--------|---------|---------|
| `--project <path>` | none | The service project (a `.csproj`, or a folder with exactly one) |
| `--output <file>` | `<project folder>/artifacts/sourceflow.manifest.json` with `--project`; standard output inside a service | Where to write |
| `--configuration`, `-c` | `Debug` | Build configuration |
| `--no-build` | off | Don't build first |
| `--check` | off | Compare with the file at `--output` instead of writing; exit 1 on drift, naming the first differing line. CRLF in the committed file is ignored |
| `-- <app args>` | none | Passed to the application, for example a placeholder connection string (`-- "--ConnectionStrings:SourceFlow=Host=localhost;Port=1"`) |

Requirements: the application must build a generic host (`Host.CreateApplicationBuilder`, `WebApplication.CreateBuilder` or `HostBuilder`) that calls `AddSourceFlow`; the design-time run times out after 5 minutes. Inside a service that hosts the CLI (`SourceFlowCli.TryRunAsync`), `dotnet run -- sourceflow manifest export` exports from the service's own provider (`SourceFlowManifestExporter.Export(IServiceProvider)`).

### Drift check in CI

Manifests are build artifacts that you commit, so Studio and other services can read them. Regenerate them in CI with `--check`. This repository's `v3-pr.yml` does it for both TravelBooking services (and for the services of the OrderFulfilment test fixture):

```yaml
- name: TravelBooking manifest drift check
  shell: bash
  run: |
    set -e
    for service in Bookings Hotels; do
      dotnet src/Tooling/SourceFlow.Tool/bin/Release/net10.0/SourceFlow.Tool.dll manifest export \
        --project "samples/TravelBooking/$service.Api" -c Release --no-build --check \
        --output "samples/TravelBooking/$service/sourceflow.manifest.json" \
        -- "--ConnectionStrings:SourceFlow=Host=localhost;Port=1"
    done
```

The unreachable connection string is never used: the host is built but not started. Keep committed manifests outside an ignored `artifacts/` folder (the sample commits them next to each service's project).

## The domain workspace (`sourceflow.domain.yaml`)

The workspace lists the services of one business domain. It only reads other services' manifests; a workflow is always stored in, and compiled by, the service that owns it ([ADR-007](../ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md)). Put the file at the root of a mono-repo, or in a dedicated domain repository.

| Key | Required | Meaning |
|-----|----------|---------|
| `domain` | yes | The domain name |
| `description` | no | One line shown in Studio |
| `services` | yes, non-empty | The services, in display order |
| `services[].name` | yes | The service name; must equal the manifest's `service`. Unique |
| `services[].manifest` | yes | Path (relative to the workspace file) or `http(s)` URL of its `sourceflow.manifest.json` |
| `services[].workflows` | no | The folder holding the workflows this service owns. `*.sfflow.yaml` files under it are listed recursively, sorted. Studio writes only inside these folders |
| `services[].project` | no | The service's project (a `.csproj` or its folder), so Studio can re-export its manifest |
| `services[].tests` | no | The service's test project folder, where Studio writes generated specs |
| `services[].module` | no | Full name of the service's module type (for example `Orders.OrdersModule`), registered by the generated specs |

Loading rules (`DomainWorkspace.LoadAsync`, shared by the tool and the static-site builder):

- The path can be the file or a folder that holds `sourceflow.domain.yaml`.
- Unknown keys, a missing `domain`, an empty or duplicated service list, a missing or invalid manifest, and a manifest for another service are errors; key errors give the line and column.
- Remote manifests (URLs) are refused unless the caller supplies a fetcher (`WorkspaceLoadOptions.FetchRemote`); Studio works offline.

## Workflow files

A workflow is a `*.sfflow.yaml` file in the owning service's project. The [DSL reference](../guides/workflow-dsl-reference.md) lists every key, generated from the schema; the [guide](../guides/workflows.md) explains how to use them; design doc 15 [§5.3](../Design/15-Workflow-DSL-and-Studio.md#53-execution-semantics) gives the execution semantics.

- YAML 1.2 core schema; anchors and aliases are rejected (WF-S01). Quoted values are always strings.
- Durations are ISO 8601 without years and months: `PT30S`, `PT5M`, `P1D`, `P1DT12H`.
- Guards (`when`) use a CESQL subset implemented once in the core: `OR`, `AND`, `NOT`, comparisons, `LIKE`, `[NOT] IN`, `EXISTS`, arithmetic and literals, with three-valued logic.
- `{ $hook: Name }` in a `map` calls a C# method you write (see [Hooks](#hooks-and-sf0021)).

**The layout sidecar.** When you move states in the statechart, Studio saves their positions next to the workflow in `<name>.sfflow.layout.json` (`{ "version": 1, "nodes": { "<id>": { "x": …, "y": … } } }`). It holds moved nodes only, in whole pixels, pruned of steps that no longer exist. It is optional, and the generator ignores it. Commit it if you want the layout shared.

## Build-time compilation

### How files reach the generator

`SourceFlow.Net`'s `build/SourceFlow.Net.props` adds `**/*.sfflow.yaml` (outside `bin` and `obj`) as `AdditionalFiles` and makes three MSBuild properties visible to the generator:

| Property | Use |
|----------|-----|
| `SourceFlowServiceName` | The compiling service. When set, a workflow whose `owner` differs is an error (WF-V02), and the generated domain query is named after it. The TravelBooking sample sets `<SourceFlowServiceName>bookings</SourceFlowServiceName>` in `Bookings.csproj` |
| `RootNamespace` | Fallback namespace for generated types when the module has none |
| `ProjectDir` | Makes the manifest's `workflows[].file` relative to the project |

### Validation at compile time (SF0020)

At build time only some manifests are known: the compilation's own static manifest and those of referenced SourceFlow assemblies. The generator validates in *partial mode*: rules about a command whose owner is unknown are skipped, but unknown message types are still reported, with a hint to reference the assembly that declares them. Every WF diagnostic becomes **SF0020** at the YAML line and column, with the rule id first in the message; warnings (WF-V09, WF-V14) stay warnings:

```text
Workflows/trip-booking.sfflow.yaml(35,5): error SF0020: WF-V06: Step 'await-guest' waits without a timeout; add 'timeout: { after: PT…, then: … }', or 'timeout: { none: true, reason: … }'.
```

The generator also reports SF0020 when a referenced message has no CLR type in the compilation (`WF-V01: … has no CLR type in this compilation; reference the contract package that declares it.`), and when the project has no `[SourceFlowModule]` to register the workflow.

Validate against the whole domain in Studio, or with the library, to get the cross-service rules the build can't check.

### What is generated

For `name: trip-booking`, `version: 1`, in a project whose module is in namespace `Bookings`:

| Generated | Shape |
|-----------|-------|
| The process manager | `[ProcessManager("trip-booking@1")] public sealed partial record TripBookingWorkflowV1 : IProcessManagerRuntime<TripBookingWorkflowV1, WorkflowInstanceId>, IWorkflowState<TripBookingWorkflowV1>`, with `ProcessName`, `Initial`, `State` (the engine state as JSON) and `Status` |
| Correlation | The start message and awaited events that carry the instance key (the last segment of `start.correlate`) correlate by that member; `WorkflowTimer` and `WorkflowControl` by their instance; replies through the envelope correlation |
| The engine | The simulator's sources, injected once per assembly as internal types in `SourceFlow.Workflows.Engine`. Services without workflows get none of it |
| Module registration | A `RegisterWorkflows` part on the module: the shared `WorkflowTimer`/`WorkflowControl` messages, each workflow's state metadata, `CommandRejected<T>` for every command with `rejected:` failures, and the process manager |
| A domain query | `WorkflowInstancesQuery(string Workflow)`, registered as `<service>.workflow-instances` version 1 |
| Manifest entry | `workflows[]` with `name`, `version`, `file` and `fingerprint` |

The owner must be able to build the other services' commands and read their events, so its module must register their message types and id converters, and its JSON context must list `CommandRejected<T>` for each command whose rejections the workflow handles. The TravelBooking sample's `BookingsModule` registers the service's generated module plus the Hotels contract module, and its JSON context lists `CommandRejected<ReserveRoom>` and `CommandRejected<ConfirmReservation>` ([`samples/TravelBooking/Bookings/Bookings.cs`](../../../samples/TravelBooking/Bookings/Bookings.cs)).

### Hooks and SF0021

A `{ $hook: Name }` map value is computed by a static partial method you implement on the generated record. Add `IWorkflowHooks` so the purity analyzers (SF0009) treat it like `React`:

```csharp
namespace Orders;

public sealed partial record ApproveOrderWorkflowV1 : SourceFlow.Workflows.IWorkflowHooks
{
    private static partial object? Note(IReadOnlyDictionary<string, object?> data) => "auto";
}
```

`data` is the instance data. A hook referenced in the YAML without an implementation is **SF0021** (error) at the line of the `$hook`, with the exact signature to add.

### Diagnostics

| Id | Severity | Category | Meaning |
|----|----------|----------|---------|
| SF0019 | Warning | `SourceFlow.Manifest` | A `Decide` (or handler) outcome can't be determined statically; declare it with `[Emits]`/`[Rejects]` |
| SF0020 | Error (warning for WF-V09, WF-V14) | `SourceFlow.Workflows` | A workflow DSL diagnostic (WF-S\*, WF-V\*), at the YAML position |
| SF0021 | Error | `SourceFlow.Workflows` | A `$hook` has no implementation |

## The SourceFlow.Studio library

`SourceFlow.Studio` is the semantic core as a .NET library. The CLI and the Studio host use it, and you can use it in your own tools, for example to validate every workflow of a domain in a CI step.

| Namespace / type | Purpose |
|------------------|---------|
| `SourceFlow.Studio.Workspace.DomainWorkspace` | `LoadAsync(path, options?, ct)`: the workspace, its `Services` (`WorkspaceService`: `Name`, `Manifest`, `WorkflowFiles`, `ProjectLocation`, `TestsLocation`, `ModuleType`) and `Manifests`. Throws `WorkspaceException` |
| `SourceFlow.Studio.Dsl.Manifests.ServiceManifest` | `Parse(json)` (throws `FormatException`); `MessageRef.Of(name, version)` |
| `SourceFlow.Studio.Dsl.Reading.WorkflowReader` | `Read(text)` → `ReadResult` (`Workflow`, structural `Diagnostics`) |
| `SourceFlow.Studio.Dsl.Validation.WorkflowEnvironment` | What a workflow is validated against: manifests, sibling workflows (WF-V15), the compiling service, and partial mode |
| `SourceFlow.Studio.Dsl.WorkflowAnalysis` | `Analyze(text, environment)` → `WorkflowAnalysisResult` (`Workflow`, `Diagnostics` sorted by position, `HasErrors`) |
| `SourceFlow.Studio.Dsl.Validation.WorkflowFingerprint` | `Compute(workflow)`: the structural fingerprint used by WF-V15 |
| `SourceFlow.Studio.Dsl.Simulation.WorkflowSimulator` | `Start(workflow, startData, now, options?)` and `Step(state, input)` → `SimulationResult` (`State`, effects, transitions) |
| `SourceFlow.Studio.Dsl.Simulation.PathEnumerator` | `Enumerate(workflow, options?)` → `PathSet` (`Paths`, `Edges`, `Uncovered`, `Truncated`, `MinimalCover()`); `Replay(workflow, path)` |
| `SourceFlow.Studio.Dsl.Specs.SpecGenerator` | `Generate(workflow, environment, options?)` → C# `WorkflowSpec` tests |
| `SourceFlow.Studio.Dsl.Export.MermaidExporter`, `BpmnExporter` | `Export(workflow)` → Mermaid `stateDiagram-v2`, BPMN 2.0 XML |
| `SourceFlow.Studio.Dsl.Export.WorkflowGraph`, `WorkflowSequence` | The statechart (kinded nodes and edges) and the swimlane sequence of one path |
| `SourceFlow.Studio.Dsl.Api.StudioApi` | The whole core as a JSON-in, JSON-out API (what the WebAssembly build exports) |

### Example: validate, enumerate, export, generate specs

Adapted from the sample's tests (`tests/Workflows/SourceFlow.Studio.Tests/TravelBookingSampleTests.cs`):

```csharp
using SourceFlow.Studio.Dsl;
using SourceFlow.Studio.Dsl.Export;
using SourceFlow.Studio.Dsl.Simulation;
using SourceFlow.Studio.Dsl.Specs;
using SourceFlow.Studio.Dsl.Validation;
using SourceFlow.Studio.Workspace;

DomainWorkspace workspace = await DomainWorkspace.LoadAsync("samples/TravelBooking");
var environment = new WorkflowEnvironment(workspace.Manifests);

foreach (WorkspaceService service in workspace.Services)
{
    foreach (string file in service.WorkflowFiles)   // relative to workspace.Root, '/' separators
    {
        string text = await File.ReadAllTextAsync(Path.Combine(workspace.Root, file));
        WorkflowAnalysisResult analysis = WorkflowAnalysis.Analyze(text, environment);
        foreach (var d in analysis.Diagnostics)
        {
            Console.WriteLine($"{file}({d.Location.Line},{d.Location.Column}): {d.Severity} {d.Id}: {d.Message}");
        }

        if (analysis.Workflow is not { } workflow || analysis.HasErrors)
        {
            continue;
        }

        PathSet paths = PathEnumerator.Enumerate(workflow);
        Console.WriteLine($"{workflow.ProcessName}: {paths.Paths.Count} paths, minimal cover {paths.MinimalCover().Count}");

        string mermaid = MermaidExporter.Export(workflow);
        string bpmn = BpmnExporter.Export(workflow);
        string specs = SpecGenerator.Generate(workflow, environment, new SpecOptions
        {
            Namespace = "TravelBooking.Tests.Workflows",
            ModuleType = "Bookings.BookingsModule",
            WorkflowType = "Bookings.TripBookingWorkflowV1",
        });
    }
}
```

### Diagnostic ids

Every diagnostic has a stable id, a severity, a message and a 1-based line and column (`WorkflowRules` lists them). WF-V09 and WF-V14 are warnings; all others are errors.

| Id | Meaning |
|----|---------|
| WF-S01 | Not valid YAML, not a mapping, or uses anchors or aliases |
| WF-S02 | A key the DSL doesn't define |
| WF-S03 | A required key is missing or a value has the wrong shape |
| WF-S04 | Unsupported `sourceflow.workflow` version (with an upgrade hint) |
| WF-S05 | A duration that isn't ISO 8601 |
| WF-S06 | A name, step id, message reference or path in the wrong format |
| WF-V01 | A referenced message type exists in no manifest |
| WF-V02 | A command has no single owner, isn't routed to it, or the workflow's `owner` isn't the compiling service or isn't in the workspace |
| WF-V03 | An awaited event can't arrive: it isn't emitted by (or published from) the sent command, or doesn't reach the owner (owner's domain event, or a publication on a topic the owner subscribes to, allowed by its `only` filter) |
| WF-V04 | An awaited message can't be correlated; await-only steps must carry the instance key |
| WF-V05 | An unreachable step, or no path reaches `Completed` |
| WF-V06 | A wait without a timeout (or `timeout: none` without a `reason`) |
| WF-V07 | `then: fail` or `compensate` at or after the pivot |
| WF-V08 | A `rejected:` code the command can't produce, or a remote route without `replyOnReject` |
| WF-V09 | A step before the pivot changes another service but has no compensation (or `compensate: none` without a `reason`) |
| WF-V10 | A retry outside `1 ≤ max ≤ 20`, exponential backoff without a positive `initial`, or `cap` below `initial` |
| WF-V11 | A `next` or `goto` target doesn't exist |
| WF-V12 | A mapped field doesn't exist or has the wrong type, or a required field isn't mapped |
| WF-V13 | A guard doesn't parse or bind against data and event fields |
| WF-V14 | A `[PersonalData]` field copied into data without `personal: true` |
| WF-V15 | Duplicate `name@version` for the owner, or a published version changed structurally without a new `version` |
| WF-V16 | A compensation that is itself compensated, pivots, has `next` or branches on failures |

Design doc 15 [§5.4](../Design/15-Workflow-DSL-and-Studio.md#54-validation-rules-wfd-28) has the full rule text; [`tests/workflow-golden/cases`](../../../tests/workflow-golden/cases) has a failing case for every rule, with its `expected.diagnostics.json`.

### Simulator

The simulator is the reference implementation of the execution semantics, and the generated process manager runs it (a differential test checks they agree over 10,000 random outcome sequences per sample workflow). It is pure and deterministic: time only moves when a timer fires.

| Input (`SimulationInput`) | Meaning |
|---------------------------|---------|
| `Succeed(data?)` | The current step's success event |
| `Reject(code, message?)` | The current command is rejected with `code` |
| `FailWith(eventType, data?)` | A declared failure event |
| `FireTimer()` / `Timer(kind, step, attempt)` | The pending timer (or a specific one; `kind` is `timeout` or `retry`) |
| `Receive(type, data?)` | Any message by type |
| `RejectedFor(command, code, message?)` | A rejection of a specific command |
| `Resume(fromStep?)`, `RetryStep()`, `Abort(reason, compensate)` | Operator commands |

`SimulationState` holds the status, step, phase (`Awaiting`, `Retrying`), attempt, retries per outcome, the compensation stack, data, the failure, the one pending timer, the clock and the last attempt per step. `SimulationResult` lists the effects (`send` with the command's deterministic message id, `schedule`, `cancel`, `status`, `capture`, `skip`, `alert`, `ignored`) and the transitions (`success`, `failure`, `retry`, `timeout`, `compensate`, `manual`, `complete`). `SimulationOptions.Guard` forces guard decisions; `SimulationOptions.Hook` supplies hook values (otherwise a placeholder).

### Path enumerator

The enumerator re-runs the simulator per path prefix and branches on every outcome, on guarded steps (taken or skipped), and on retry loops at 0, 1 and the maximum. `start.when` and success guards are assumed to hold. Each path ends in a terminal status or `ManualIntervention`. Coverage marks every statechart edge; `MinimalCover()` is a greedy set covering every edge and every end status.

| `PathOptions` | Default | Meaning |
|---------------|---------|---------|
| `MaxStepEntries` | 2 | How often a step may be entered on one path |
| `MaxPaths` | 2000 | Stop after this many paths (`Truncated` is then true) |
| `MaxMoves` | 200 | Longest path |

### Spec generator

`SpecGenerator.Generate` writes one xUnit test per path, against `WorkflowSpec.For<TWorkflow, TModule>()` from `SourceFlow.Testing`. A small solver over guard literals picks start data that makes each guarded step take the path's decision. The file starts with `// <auto-generated>`, the workflow's `name@version` and fingerprint.

| `SpecOptions` | Default | Meaning |
|---------------|---------|---------|
| `Namespace` | `Workflows.Tests` | Namespace of the test class |
| `ModuleType` | `Module` | Full name of the module the spec registers |
| `WorkflowType` | the generated class name | Full name of the generated workflow record |
| `AllPaths` | `false` | Every path instead of the minimal cover |
| `PathIds` | none | Only these paths (`P01`, …) |

A generated test reads like this (`samples/TravelBooking/TravelBooking.Tests/Workflows/TripBookingV1PathTests.cs`):

```csharp
/// <summary>P09 hold-room: success → await-guest: timeout → hold-room: compensated ⇒ Compensated</summary>
[Fact]
public void P09_Compensated()
{
    var spec = WorkflowSpec.For<Bookings.TripBookingWorkflowV1, Bookings.BookingsModule>();
    spec.Start("bookings.booking-requested.v1", """{"bookingId":"bdcbea6c-2dc6-58e2-8f01-846c12116012","hotelId":"71905a65-5f08-5214-94c5-0c0cdfe13aac","guest":"sample-guest","nights":1}""")
        .ThenSends("hotels.reserve-room.v1");
    spec.Succeed("hotels.room-reserved.v1", """{"hotelId":"71905a65-5f08-5214-94c5-0c0cdfe13aac","bookingId":"bdcbea6c-2dc6-58e2-8f01-846c12116012","nights":1}""")
        .ThenSends();
    spec.FireTimer()
        .ThenSends("hotels.release-room.v1");
    spec.Succeed("hotels.room-released.v1", """{"hotelId":"71905a65-5f08-5214-94c5-0c0cdfe13aac","bookingId":"bdcbea6c-2dc6-58e2-8f01-846c12116012"}""")
        .ThenSends("bookings.decline-booking.v1");
    spec.ThenStatus(WorkflowStatus.Compensated);
}
```

`WorkflowSpec<TWorkflow>` also has `Fail(type, json)`, `Reject(code, message?)`, `Control(action, step?, reason?, compensate)`, `State`, `Sent`, `Now` and `Status()`. See [SourceFlow.Testing](SourceFlow.Testing-README.md).

### Exporters

| Export | Content |
|--------|---------|
| Mermaid | `stateDiagram-v2` with a title `name@version (owner)`, a symbol and text label per edge kind, compensations as a nested state numbered in the order they run |
| BPMN 2.0 | The semantic model only (no diagram interchange): a message start event, send and receive tasks, timer and message boundary events, guard gateways with CESQL `conditionExpression`, compensation boundary events with `isForCompensation` tasks, retries as documentation. Checked for well-formedness, unique ids and resolved references; not validated against the OMG XSDs |

The Mermaid export of the golden `02-minimal` case:

```text
---
title: approve-order@1 (orders)
---
stateDiagram-v2
    state "Compensating" as Compensating
    state "Manual intervention" as ManualIntervention
    state "approve" as approve
    approve : send orders.approve-order.v1
    approve : await orders.order-approved.v1
    [*] --> approve : orders.order-placed.v1
    approve --> Completed : ✓ orders.order-approved.v1
    approve --> ManualIntervention : ⏱ PT1H
    Compensating --> Compensated : nothing to undo
    Completed --> [*]
    Compensated --> [*]
    ManualIntervention --> [*] : ✋ operator resumes or aborts
```

## The WebAssembly core and the TypeScript client

### `SourceFlow.Studio.Wasm`

The project compiles the `Dsl/**` sources directly for `browser-wasm` (so the bundle carries neither `SourceFlow.Net` nor the workspace loader), trims on publish and needs no `wasm-tools` workload. The published bundle is about 1.77 MB brotli. It isn't a NuGet package; it ships inside the tool, the Studio app and the action. Build it with `npm run build:wasm` in `studio/` (which runs `dotnet publish src/Workflows/SourceFlow.Studio.Wasm -c Release -o artifacts/studio-wasm`).

It exports each `StudioApi` method unchanged (`[JSExport]`). Every call takes one JSON request string and returns `{"ok": true, "result": …}` or `{"ok": false, "error": "…", "diagnostics"?: […]}`; it never throws. Calls are pure, so results can be cached. The simulator state crosses as an opaque string.

| Export | Request | Result |
|--------|---------|--------|
| `Version` | `{}` | `{api, dsl}` (`api` is 1, `dsl` is `sourceflow.workflow/1`) |
| `Parse` | `{yaml}` | `{workflow, diagnostics}` (structural only) |
| `Validate` | `{yaml, manifests: [json…]}` | `{valid, fingerprint, diagnostics}` |
| `SimulateStep` | `{yaml, state?, input, guards?, now?}` | The new state (opaque `state` string, status, step, phase, attempt, timer, data), effects and transitions. `input.kind`: `start`, `succeed`, `fail`, `reject`, `rejected`, `receive`, `timer`, `resume`, `retry-step`, `abort` |
| `EnumeratePaths` | `{yaml, maxStepEntries?, maxPaths?, maxMoves?}` | `{paths, edges, uncovered, truncated, minimalCover}`; each move carries its `SimulateStep` input |
| `ToMermaid`, `ToBpmn` | `{yaml}` | `{text}` |
| `GenerateSpecs` | `{yaml, manifests, namespace?, moduleType?, workflowType?, allPaths?, pathIds?}` | `{text}` |
| `Graph` | `{yaml}` | `{nodes, edges}`: node kinds `start`, `step`, `pivot`, `compensation`, `compensating`, `terminal`, `manual`; edge kinds `start`, `success`, `failure`, `retry`, `timeout`, `compensation`, `manual`, `skip`, each with a symbol label and a description |
| `Sequence` | `{yaml, manifests, path: "P03", …}` | `{path, lanes, messages}`: one lane per service plus `external` for messages no manifest accounts for |

### `@sourceflow/core-client`

The typed client over that API. `src/generated/exports.d.ts` is generated from the `[JSExport]` signatures by a .NET test that fails on drift.

| Entry point | Use |
|-------------|-----|
| `createBrowserClient(frameworkUrl, cacheSize?)` (`@sourceflow/core-client`) | Runs the core in a module Web Worker; the worker and runtime load on the first call |
| `createInProcessClient(frameworkUrl, cacheSize?)` (`@sourceflow/core-client/node`) | Runs the core on the calling thread in Node (tools, scripts, the action) |
| `StudioCoreClient` | `version`, `parse`, `validate`, `simulateStep`, `enumeratePaths`, `toMermaid`, `toBpmn`, `generateSpecs`, `graph`, `sequence`, `dispose` |

Results are cached (LRU, 64 entries by default; `0` disables). Every method takes `{ signal?: AbortSignal }`: because WebAssembly can't be interrupted, cancelling restarts the worker and re-sends the other calls in flight. Failures reject with `StudioCoreError` (with `diagnostics` when the workflow doesn't read).

```ts
import { createBrowserClient } from '@sourceflow/core-client';

const core = createBrowserClient(new URL('/_framework/', location.href).href);
const result = await core.validate({ yaml, manifests: manifestJsonStrings });
for (const d of result.diagnostics) {
  console.log(`${d.line}:${d.column} ${d.severity} ${d.id} ${d.message}`);
}
```

## The Studio app

### Running it: `dotnet sourceflow studio`

```bash
dotnet sourceflow studio --workspace samples/TravelBooking
dotnet sourceflow studio --workspace samples/TravelBooking --read-only --port 0 --open
```

| Option | Default | Meaning |
|--------|---------|---------|
| `--workspace <path>` | `sourceflow.domain.yaml` | The workspace file, or the folder that holds it |
| `--port <n>` | `5180` | Loopback port; `0` picks a free one |
| `--read-only` | off | Refuse every write: workflow and layout files, manifest exports and spec writes (spec previews still work) |
| `--open` | off | Open the page in the default browser |
| `--assets <dir>` | the copy embedded in the tool | Serve the app from this folder (`studio/app/dist`) |
| `--framework <dir>` | the copy embedded in the tool | Serve the WebAssembly core from this `_framework` folder (`artifacts/studio-wasm/wwwroot/_framework`) |

The host loads the workspace at start and fails fast on a bad one. It is a Kestrel server bound to `127.0.0.1` only, and it:

- refuses requests whose `Host` header isn't `localhost`, `127.0.0.1` or `[::1]` (DNS rebinding), with 403;
- refuses writes whose `Origin` is another site (403) and writes that aren't JSON (415);
- sends `Content-Security-Policy` (`'self'`, plus `'wasm-unsafe-eval'` for the core), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`;
- reads only `*.sfflow.yaml` and `*.sfflow.layout.json` inside the workspace folder, never through a symbolic link, and writes only inside a service's `workflows` folder, atomically (temporary file, then a move).

There is no authentication: it is a local tool. Nothing is sent anywhere else.

| Endpoint | Purpose |
|----------|---------|
| `GET /api/workspace` | The services with their manifest JSON, workflow files, `project`, `tests`, `module`, and whether the host is read-only |
| `GET /api/files?path=` | Read a workflow or layout file |
| `PUT /api/files?path=` with `{"content": "…"}` | Write it |
| `POST /api/services/{service}/manifest/export` | Run the design-time export for the service's `project` and overwrite its manifest |
| `POST /api/services/{service}/specs` with `{workflow, paths?, allPaths?, dryRun?}` | Generate specs into the service's `tests` folder (`dryRun` returns the text without writing) |

The host also serves the app (with a fallback to `index.html` for client routes) and the WebAssembly core, as Brotli when the browser accepts it.

**Developing the app.** `npm run dev` in `studio/app` starts Vite on port 5173 and proxies `/api` and `/_framework` to a running `dotnet sourceflow studio` on 5180.

### The shell

A header (domain name, a **Read-only** badge, the Light / Dark / System theme switch, the keyboard map), the workspace explorer on the left, the routed view in the middle, and the diagnostics panel at the bottom. The core validates the open workflow in a Web Worker on every change (the median round trip from a key press to the panel is 8 ms); the panel lists errors first and names the severity in words.

Routes: `/` is the domain overview, `/services/{name}` a service (its aggregates and commands, and **Export the manifest again** when it has a `project`), `/workflows/{path}` a workflow.

### Views

**Domain map** (the overview). Services as containers with their aggregates inside, built from the manifests. Command edges (`→`) go from a sender (command routes and process-manager sends) to the owning aggregate; publication edges (`⇢`) go to topics and subscription edges (`⇠`) come from them, with their `only` filters. Hovering or selecting a node (click, or Enter on a focused node) highlights its downstream choreography chain and dims the rest.

**Workflow view.** Two tab groups side by side. On the left: **Statechart**, **Swimlane** and **Paths**. On the right: **Properties**, **YAML** and **Simulate**. A toolbar has **Save**, **Undo**, **Redo** and an **Export** group (SVG, PNG, Mermaid, BPMN).

**Statechart.** Computed by the core (`Graph`), so it shows what the generator runs; laid out by ELK in its own worker. Every transition kind has its own line style, symbol and name; colour is only a redundant cue:

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

The pivot is a diamond-marked box (◆), manual intervention a dashed octagon, the ends double-bordered pills, and compensations sit in a dotted *Compensating* group, numbered in undo order. A legend explains the encodings. States with diagnostics carry a "✕ n problems" badge.

**Swimlane.** One lane per service for one path (the path picker starts on the minimal cover): commands go to their owner, events come from the service that emits or publishes them, and rejections, timers, compensations, status changes and alerts are shown in order. Computed by the core (`Sequence`).

### Editing

The YAML text is the model. Visual edits become minimal text edits that keep comments, ordering and formatting:

- **Properties.** Select a state (click, or Enter on a focused state) to edit its step: send and map, await with failures, retries, timeout, compensation, pivot, guard and `next`. Renaming a step renames the `next` and `goto` targets that point at it.
- **Palette.** Lists the commands of every service, grouped by service, with what each emits, rejects and carries. At a step it offers only what is valid there: success events the command emits, failures it can produce, fields not yet mapped. Adding a step inserts it after the selection with a unique id taken from the command.
- **YAML.** Monaco with completion and hover from `schemas/workflow-1.json`; the markers are the core's diagnostics. It loads the first time the tab opens. Selecting a diagnostic opens this tab at its line and selects its step.
- **Undo and redo** share one history of up to 200 states for visual edits and typing (typing within 600 ms joins one entry).
- **Save** writes the workflow and, when you moved states, its layout sidecar. A file that doesn't parse is edited as text only; visual edits are refused until the YAML tab fixes it.

### Simulation

The **Simulate** tab runs one instance on the core's simulator. Edit the start event's data as JSON (it starts with the correlation field); each guarded step can evaluate its guard, or be forced to run or be skipped. The simulated clock starts at 2026-01-01T00:00:00Z, so runs repeat exactly.

- **Choices** follow the state: a waiting step offers its success event, each declared failure and its timeout; a retry offers the retry timer; a compensation can succeed, be rejected or time out; manual intervention offers resume, retry the step, and abort with or without compensating.
- **Step** applies one choice, **Run to the end** follows a plan (success by default; retries fire and compensations succeed on their own; it stops at manual intervention or after 200 moves), **Back** undoes the last move.
- It shows the instance (status, step, phase, attempt, time, failure), the pending timer, the outbox with each command's deterministic message id, the compensation stack, the data, and every move. The statechart outlines the state the instance is at.
- Editing the workflow marks the simulation out of date; restart it.

### Paths and specs

The **Paths** tab lists the enumerated paths with their end status and whether they are in the minimal cover, the edges each covers, and the edges no path covers. Filter by end or by the cover; select the cover, the shown paths or single paths (the selection starts as the minimal cover). **Simulate** on a path replays it in the Simulate tab.

**Preview specs** and **Write specs** call the host, which generates with the same `SpecGenerator` from the saved file into `{tests}/Workflows/{Name}V{version}PathTests.cs`, namespace `{test project}.Workflows`. Both are disabled while there are unsaved changes. The host refuses:

- a service without `tests` or `module` in the workspace (400);
- a `tests` folder outside the workspace, through a link (403), or without a `.csproj` (400);
- a workflow with errors (400);
- an existing target file that doesn't start with `// <auto-generated>` (409): Studio only overwrites files it generated;
- writing in read-only mode (403); a preview still works.

### Exports

From the workflow toolbar, made in the browser and saved as downloads:

| Button | File | Content |
|--------|------|---------|
| SVG | `<name>.svg` | The statechart as the canvas shows it (ELK plus saved positions), in the current theme, with a `<title>` on every state and transition |
| PNG | `<name>.png` | The SVG drawn at twice its size |
| Mermaid | `<name>.mmd` | The core's `ToMermaid` |
| BPMN | `<name>.bpmn` | The core's `ToBpmn` |

The SVG renderer (`studio/app/src/graph/svg.ts`) is a pure function shared by the export, the static site and the `workflow-diff` action: the same graph and positions give the same bytes.

### Keyboard map

| Keys | Action |
|------|--------|
| `Shift+?` | Show the keyboard map |
| `Alt+1` | Go to the workspace explorer |
| `Alt+2` | Go to the open workflow |
| `Alt+3` | Go to the diagnostics |
| `F8` / `Shift+F8` | Next / previous diagnostic |
| `Alt+T` | Switch between light and dark |
| `Alt+H` | Go to the domain overview |
| `Ctrl+S` | Save the workflow (and its layout) |
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo (inside the YAML editor or a text field, the field's own undo and redo) |

Plain-key shortcuts never fire while you type in a text field. On macOS, `Alt` shortcuts fall back to the physical key.

### Accessibility

- States and transitions are focusable and named by kind, step and details (and "simulated here" for the simulated state); edges carry a sentence for screen readers.
- Everything is keyboard operable, including adding a step, giving it a failure and connecting it to another step.
- The theme follows the system's light or dark preference unless you choose one, and nothing animates under `prefers-reduced-motion`.
- Text meets 4.5:1 contrast and the outlines of states, edges and controls meet 3:1 in both themes; axe reports no serious violations. The tabs follow the WAI-ARIA tabs pattern.

### Read-only mode

With `--read-only` (or on a static site), the header shows **Read-only**, editing controls are disabled, and the host answers every write with 403 "The Studio host runs with --read-only.". Viewing, simulation, paths, spec previews and exports still work.

## Static site

A read-only snapshot of a workspace for a docs portal or GitHub Pages, with simulation, paths and exports working in the browser:

```bash
npx @sourceflow/studio build --workspace . --out site
# in this repository, after building the app and the core:
node studio/app/bin/studio.mjs build --workspace samples/TravelBooking --out site
```

The TravelBooking demo's Studio image ([`samples/TravelBooking/Studio.Dockerfile`](../../../samples/TravelBooking/Studio.Dockerfile)) is this site for the sample's workspace, served by nginx; `docker compose up -d --build` at the repository root serves it on http://localhost:5080.

| Option | Default | Meaning |
|--------|---------|---------|
| `--workspace <path>` | `.` | `sourceflow.domain.yaml`, or the folder holding it |
| `--out <dir>` | `site` | Where to write |
| `--assets <dir>` | the package's `dist` | The built Studio app |
| `--framework <dir>` | the package's `framework`, then `artifacts/studio-wasm/wwwroot/_framework` | The WebAssembly core |
| `--help` | | Usage |

The workspace is read with the same rules as the .NET loader (unknown keys are errors with a line and column; remote manifests are refused). The site contains the app, the core files `dotnet.js` needs (about 6 MB, without `.br`/`.gz` copies), `data/workspace.json` (the services and manifests), `data/files.json` (the workflows and layouts), `.nojekyll` (GitHub Pages would otherwise drop `_framework`) and a `.sourceflow-site` marker. The command only ever replaces a folder it wrote; a non-empty folder without the marker is refused.

The site's `index.html` carries `<meta name="sourceflow-mode" content="static">`: the app reads the snapshot, keeps routes in the URL hash (a static host has no fallback to `index.html`) and loads the core from its own folder, so the site works from any sub-path. Generating specs needs the local host.

## The workflow-diff GitHub Action

`studio/action` (`@sourceflow/workflow-diff`) runs on a pull request that changes `*.sfflow.yaml` files. It lists the changed files between base and head with git, reads both versions, has the core (in Node, through `@sourceflow/core-client/node`) build each statechart, and:

- writes before and after SVGs to `out/` (`<path with folders joined by __>.before.svg` and `.after.svg`), with added (＋), removed (−) and changed (△) states and transitions marked by a word as well as a coloured outline;
- posts one comment (created once, updated on every push) with the changes in words, Mermaid diagrams that GitHub renders (changed states marked with `classDef`), and a link to the run's artifact.

A workflow that can't be read is reported with the core's message. A comment- or format-only edit is reported as "no change to the statechart". A comment over GitHub's 65,536 characters drops the diagrams. The SVGs reach the pull request as an artifact, not inline images.

| Input | Default | Meaning |
|-------|---------|---------|
| `token` | `${{ github.token }}` | Token to comment with (needs `pull-requests: write`) |
| `base` | the pull request's base | Base commit |
| `head` | the pull request's head | Head commit |
| `out` | `workflow-diff` | Folder for the SVGs |
| `theme` | `light` | `light` or `dark` |
| `comment` | `"true"` | `false` only writes the SVGs (for example for forks, whose token is read-only) |
| `artifact` | `workflow-diff` | The artifact name the comment links to |

| Output | Meaning |
|--------|---------|
| `changed` | How many workflow files changed |
| `out` | The folder with the SVGs |

The action runs on Node 24 (`runs.using: node24`, `main: dist/main.js`). Build it with `npm run build:wasm` then `npm run build:action` in `studio/`. This repository runs it from source (`.github/workflows/workflow-diff.yml`):

```yaml
on:
  pull_request:
    paths: [ "**/*.sfflow.yaml" ]

permissions:
  contents: read
  pull-requests: write

jobs:
  diff:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0   # the base commit's files too
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - uses: actions/setup-node@v4
        with:
          node-version: "22.18"
      - name: Build the action and its WebAssembly core
        working-directory: studio
        run: |
          npm ci
          npm run build:wasm
          npm run build:action
      - name: Diff the workflows
        uses: ./studio/action
        with:
          out: workflow-diff
          comment: ${{ github.event.pull_request.head.repo.full_name == github.repository }}
      - uses: actions/upload-artifact@v4
        with:
          name: workflow-diff
          path: workflow-diff
          if-no-files-found: ignore
```

Once published as `sourceflow/workflow-diff`, the build step is replaced by `uses: sourceflow/workflow-diff@v1`. See [GitHub Actions setup](GitHub-Actions-Setup.md) for the repository's other workflows.

## Operational notes

- **Commit** manifests, workflows and layout sidecars; check manifests for drift in CI. Workflows live with their owner; the workspace may point at other repositories' manifests by relative path.
- **Regenerate specs** after changing a workflow. Studio overwrites only files it generated.
- **Version** a workflow when you change its structure after publishing (WF-V15 compares the fingerprint recorded in the owner's manifest). Deploy both versions; see [Architecture/09](Architecture/09-Process-Managers-and-Workflows.md#5-versioning-running-instances).
- **Operate instances** with `dotnet sourceflow workflow list|show|resume|retry-step|abort` ([guide §5](../guides/workflows.md#5-running-and-operating)). Alert on `sourceflow.workflow.manual_intervention` (rule `SourceFlowWorkflowManualIntervention` in `deploy/observability/sourceflow-alerts.yaml`); see [Observability and performance](Observability-and-Performance.md).
- **Personal data.** Instance data is JSON in the process state. `personal: true` fields are not yet crypto-shredded in workflow state; WF-V14 still warns about unmarked personal data. See the [Security and compliance guide](Security-and-Compliance-Guide.md).
- **CI for the front end.** `.github/workflows/studio-pr.yml` builds the core, runs Vitest (including the golden suite against the published WebAssembly bundle), Playwright with axe, the performance budgets and a static-build smoke test.
- **Compatibility.** `sourceflow.workflow: 1` and `manifestVersion: 1` are compatibility contracts with SourceFlow 3.x. A file with another DSL version fails with WF-S04.

## Troubleshooting

| Symptom | Cause and fix |
|---------|---------------|
| `SF0020: WF-V01: x.y.v1 has no CLR type in this compilation; reference the contract package that declares it.` | The owner doesn't reference the assembly declaring another service's message. Add the project or package reference, and register its contracts module in the owner's module |
| `SF0020: The workflow needs a [SourceFlowModule] in this project to be registered.` | The project owning the workflow has no module. Add `[SourceFlowModule] public sealed partial class …Module;` |
| `SF0020: WF-V02 …` about the owner | `owner:` differs from `SourceFlowServiceName`, or the service isn't in the workspace |
| `SF0020: WF-V08 … add .ReplyOnReject() to its route` | The workflow handles a rejection of a remote command whose route doesn't ask for replies. Add `.ReplyOnReject()` to `r.Command<T>().To(…)`, re-export the manifest, and list `CommandRejected<T>` in the owner's JSON context |
| `SF0021: The workflow hook 'X' has no implementation` | Add `private static partial object? X(IReadOnlyDictionary<string, object?> data)` to the generated partial record (in the module's namespace) |
| SF0019 on a `Decide` method | Declare the outcomes with `[Emits(typeof(…))]` and `[Rejects("code")]` |
| `… exited without writing a manifest. It must build a generic host …` | The project doesn't build a generic host that calls `AddSourceFlow`, or exits before `Build()` |
| `Manifest drift: … differs from the regenerated manifest at line n.` | Run `dotnet sourceflow manifest export` with the same arguments and commit the result |
| `Service 'x': manifest '…' not found` when loading the workspace | Export the manifest, or fix the path (relative to the workspace file) |
| `… the manifest '…' is remote; remote manifests are off unless a fetcher is configured.` | Check out the other repository next to this one and use a relative path |
| `The Studio host could not listen on port 5180 …` | Pass `--port 0` for a free port, or another port |
| The browser shows "The Studio app isn't in this build" | The tool was built before `studio/app/dist`. Run `npm run build` in `studio/` and pass `--assets studio/app/dist` (and `--framework artifacts/studio-wasm/wwwroot/_framework` for the core) |
| 403 "The Studio host answers loopback names only" | You opened the host through another host name or a proxy. Use `http://127.0.0.1:<port>/` or `http://localhost:<port>/` |
| 403 when saving or exporting | The host runs with `--read-only`, or the file is outside a service's `workflows` folder |
| Write specs is disabled or fails | Save first; add `tests` and `module` to the service in `sourceflow.domain.yaml`; a 409 means the target file wasn't generated by Studio (move or delete it) |
| Visual edits refused: "The YAML doesn't parse (line n)" | Fix the YAML in the YAML tab first |
| `WorkflowVersionRemovedException` at startup | A removed workflow version still has active instances. Keep the version until `dotnet sourceflow workflow list --name <name> --version <v>` says it can be removed, or abort the instances |
| `No built Studio app in … ` / `No WebAssembly core in …` from the static build | Run `npm run build` and `npm run build:wasm` in `studio/`, or pass `--assets` / `--framework` |

## Links

- [Workflows and SourceFlow Studio guide](../guides/workflows.md) and [Workflow DSL reference](../guides/workflow-dsl-reference.md)
- [Architecture/09 Process managers and workflows](Architecture/09-Process-Managers-and-Workflows.md)
- [Design doc 15: Workflow DSL and SourceFlow Studio](../Design/15-Workflow-DSL-and-Studio.md)
- [ADR-020 Process managers](../ADRs/ADR-020-process-managers-with-durable-state-and-timers.md), [ADR-024 Workflow DSL](../ADRs/ADR-024-workflow-dsl-compiled-to-process-managers.md), [ADR-025 Studio](../ADRs/ADR-025-studio-react-spa-with-shared-wasm-core.md), [ADR-027 §6](../ADRs/ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md)
- [SourceFlow.Net](SourceFlow.Net-README.md), [SourceFlow.Testing](SourceFlow.Testing-README.md), [SourceFlow.Tool](SourceFlow.Tool-README.md)
- Sample: [`samples/TravelBooking`](../../../samples/TravelBooking/README.md); golden suite: [`tests/workflow-golden`](../../../tests/workflow-golden)
- Schema: [`schemas/workflow-1.json`](../../../schemas/workflow-1.json); action: [`studio/action/action.yml`](../../../studio/action/action.yml)
