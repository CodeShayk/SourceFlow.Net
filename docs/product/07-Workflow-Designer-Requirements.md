# 07 — Workflow DSL and Visual Designer Requirements (new for v3)

| | |
|---|---|
| **Origin** | Product request, 2026-11-10. This is a **new** requirement, not reverse-engineered from v2 code. |
| **Status of every requirement below** | **Planned** |
| **Design** | [v3 design doc 15](../v3.0.0/15-Workflow-DSL-and-Studio.md), [ADR-024](../v3.0.0/ADRs/ADR-024-workflow-dsl-compiled-to-process-managers.md), [ADR-025](../v3.0.0/ADRs/ADR-025-studio-react-spa-with-shared-wasm-core.md) |

## 1. Problem

A business process usually spans several services. Order fulfilment, for example, touches Orders, Payments, Inventory and Shipping, each owning its own aggregates.

In v2 the "saga" was only a command handler (product doc 02, DOM-05). In v3 a process manager is plain C# ([ADR-020](../v3.0.0/ADRs/ADR-020-process-managers-with-durable-state-and-timers.md)). Either way, nobody can **see** the end-to-end workflow:

- which events move it forward;
- what happens when a step is rejected or times out;
- how many times a step is retried;
- what gets undone, and in which order, when the process can't complete.

Domain experts can't review it, and engineers can't check it for missing failure paths. Competing products leave this to code, or to commercial tools (doc 13 of the v3 design).

## 2. Users

| Persona | Need |
|---------|------|
| Domain expert / product owner | Read, and ideally sketch, a business workflow across services in business terms |
| Domain developer | Design a saga with all failure, retry and compensation paths; generate correct process-manager code and tests from it |
| Architect / reviewer | See the whole business domain: services, aggregates, who owns which command, which events flow where; review workflow changes in pull requests |
| Operator (later release) | See where live workflow instances are, which are stuck, and resume or abort them |

## 3. Functional requirements

### 3.1 Domain model ingestion (`WFD-1x`)

| ID | Requirement |
|----|-------------|
| WFD-10 | The tool shall load a **business domain workspace**: a set of services that together form one business domain. |
| WFD-11 | For each service it shall read a machine-readable **service manifest**: service name, endpoint, aggregates, the commands each aggregate decides, the events each command can produce, the rejections each command can return, process managers, projections, published integration events and subscriptions. |
| WFD-12 | Manifests shall be produced from the code and its routing configuration, never hand-written, so the diagram can't drift from the code. |
| WFD-13 | The tool shall show a **domain map**: services, their aggregates, command ownership, and event and integration-event flows between services, including choreographed chains with no orchestrator. |

### 3.2 Workflow definition: the DSL (`WFD-2x`)

| ID | Requirement |
|----|-------------|
| WFD-20 | Workflows (orchestrated sagas) shall be defined in a **text DSL** stored in the repository. The DSL is the source of truth; the visual designer edits the DSL. |
| WFD-21 | A workflow shall be expressed as a **state machine driven by events**: states, transitions triggered by events (domain or integration), optional guard conditions, and steps that send commands to a named service's aggregate. |
| WFD-22 | Every step shall declare its **success** transition(s) and its **failure** handling: rejection codes, timeouts and unexpected errors. |
| WFD-23 | A step may declare a **business retry policy**: which failures to retry, maximum attempts, and backoff. This is distinct from the infrastructure retries the runtime already performs. |
| WFD-24 | A step may declare a **compensation** (rollback) action. When the workflow fails, completed steps are compensated **in reverse order**. Compensations have their own retry policy. |
| WFD-25 | A step may be marked as the **pivot** (point of no return). Failures after the pivot use forward recovery (retry or alternative path), never compensation. |
| WFD-26 | A failed compensation shall move the workflow to **manual intervention**, which raises an alert and lets an operator resume or abort the instance. |
| WFD-27 | Every wait for an event shall have a **timeout** (or an explicit, justified "no timeout"). |
| WFD-28 | The tool shall **validate** a workflow against the domain manifests: every referenced command, event and service exists; the named service owns the command; cross-service events are published and subscribed; every state is reachable; terminal states exist; every failure path is handled. |
| WFD-29 | Each workflow shall be **compiled into a SourceFlow process manager** owned by exactly one service (the orchestrator), using the standard runtime: no separate workflow engine. |

### 3.3 Visual designer (`WFD-3x`)

| ID | Requirement |
|----|-------------|
| WFD-30 | A **React** web application shall render workflows as **state-machine diagrams**, with success, failure, retry and compensation paths visually distinct. |
| WFD-31 | It shall render a **swimlane (sequence) view** per service, showing commands and events over time for a chosen path. |
| WFD-32 | It shall support **visual editing**: add, remove or connect states and steps from a palette of commands and events taken from the manifests; edit properties; with lossless round trip to the DSL text, which is shown side by side. |
| WFD-33 | It shall **simulate** a workflow: choose the outcome of each step (success, specific rejection, timeout) and see the resulting path, the commands sent, the retries and the compensation sequence. |
| WFD-34 | It shall **generate test specifications** (process-manager Given/When/Then specs) from simulated paths, including every failure and compensation path. |
| WFD-35 | It shall **export** diagrams (SVG, PNG, Mermaid) and offer a BPMN 2.0 export for stakeholders who use BPMN tools. |
| WFD-36 | It shall run **locally** against the repository (`dotnet sourceflow studio`) and also as a **read-only static build**, for example in documentation or as a diagram diff on pull requests. |
| WFD-37 | *(Later release)* An **observe mode** shall overlay live instance counts per state, stuck instances and recent failures, using the service's own query API (respecting the query boundary in product Q2). |

## 4. Non-functional requirements (`WFD-NFR`)

| ID | Requirement |
|----|-------------|
| WFD-NFR-1 | **Separate utility.** It is not part of the runtime packages and adds no runtime dependency to services. Generated process managers depend only on SourceFlow v3 packages. |
| WFD-NFR-2 | **One semantics.** Validation and simulation in the designer give exactly the same results as the build-time validator and code generator. |
| WFD-NFR-3 | **Performance.** Opening a workspace of 20 services and 50 workflows takes under 3 s. A 200-state workflow lays out and renders in under 1 s. |
| WFD-NFR-4 | **Accessibility.** WCAG 2.1 AA, including keyboard-only editing and a non-colour encoding of path types. |
| WFD-NFR-5 | **Security.** It works offline. The local server binds to localhost only by default. No telemetry is sent anywhere. Git is the only storage. |
| WFD-NFR-6 | **Versioned DSL.** The DSL has a schema version. Breaking changes need a new version and a migration command. |

## 5. Acceptance scenarios

1. **Visualise.** Given the Orders, Payments, Inventory and Shipping services, opening the workspace shows the domain map with correct command ownership and integration-event flows.
2. **Design.** A developer draws "order fulfilment":
   - reserve stock → charge payment (retry three times on `payment.declined`, every 10 minutes);
   - **pivot**: create shipment;
   - complete.
   Validation reports an error until every step has a timeout, and until "reserve stock" has a compensation.
3. **Rollback.** In simulation, when payment fails after three retries, the designer shows the compensation path: release stock, then cancel the order, ending in `Compensated`.
4. **Generate.** Building the Orders service produces a process manager from the DSL. The generated specs cover all five paths: success, retry-then-success, compensated, timeout and manual intervention. All pass.
5. **Run.** The end-to-end test runs the generated workflow across the four services on the in-memory and cloud transports, with the same outcomes as the simulation.

## 6. Open questions

| # | Question | Proposed answer |
|---|----------|-----------------|
| WQ1 | Ship with 3.0.0 GA or on its own release train? | **Own release train** ("SourceFlow Studio 1.0"). It isn't release-blocking for 3.0.0, but is targeted to be ready at GA (v3 design doc 12) |
| WQ2 | BPMN import as well as export? | Export only. BPMN semantics don't map cleanly to aggregate-owned commands |
| WQ3 | Observe mode in Studio 1.0? | No. It comes in 1.1, after the query API and dashboards have settled |
