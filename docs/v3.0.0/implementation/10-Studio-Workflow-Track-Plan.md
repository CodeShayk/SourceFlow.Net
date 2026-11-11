# 10 — Track S: Workflow DSL and SourceFlow Studio

| | |
|---|---|
| **Requirement** | [Product doc 07](../../product/07-Workflow-Designer-Requirements.md) (WFD-10…37, WFD-NFR-1…6) |
| **Design** | [Design doc 15](../15-Workflow-DSL-and-Studio.md), [ADR-024](../ADRs/ADR-024-workflow-dsl-compiled-to-process-managers.md), [ADR-025](../ADRs/ADR-025-studio-react-spa-with-shared-wasm-core.md) |
| **Release** | **Studio 1.0**, a separate release train. Targeted for 3.0.0 GA; **not** release-blocking for 3.0.0 ([design doc 12 §5.0](../12-Migration-Delivery-and-Traceability.md#50-sourceflow-studio-release-train-product-doc-07-wq1)) |
| **Branch** | Same integration branch `v3.0.0`; work branches `v3/wp-s.<n>-<slug>` |
| **Lane** | **F**: 1 .NET/Roslyn engineer + 1 React/TypeScript engineer, with lane B (generators) reviewing |

## 1. Repository additions (extends [plan 02 §3](02-Branching-Versioning-and-Repository-Layout.md#3-target-repository-layout-on-v300))

```text
src/
├─ Core/SourceFlow.Net/                 # + namespace SourceFlow.Workflows: runtime types for generated workflows (WP-S.5)
├─ Core/SourceFlow.Generators/          # + WorkflowGenerator, manifest emission (WP-S.1, S.5); compiles Studio's Dsl/** sources
├─ Workflows/SourceFlow.Studio/         # WP-S.3, S.4 DSL semantic core (net10.0)
├─ Workflows/SourceFlow.Studio.Wasm/    # WP-S.7 browser-wasm [JSExport] facade
└─ Tooling/SourceFlow.Tool/             # + manifest export, workflow …, studio (WP-S.2, S.6, S.8)
studio/                                 # WP-S.8…S.12 React 19 + TypeScript SPA (pnpm workspace)
├─ app/                                 #   the SPA
├─ core-client/                         #   typed TS facade over the WASM worker
├─ cli/                                 #   `@sourceflow/studio build` static site builder
└─ actions/workflow-diff/               #   GitHub Action (Node 24 LTS)
schemas/
└─ workflow-1.json                      # normative JSON Schema of the DSL (WP-S.3)
samples/OrderFulfilment/                # WP-S.13 orders, payments, inventory, shipping + sourceflow.domain.yaml
tests/
├─ Workflows/SourceFlow.Studio.Tests/   # UT-WF, UT-MAN, WF-GOLD, WF-DIFF
├─ EndToEnd/SourceFlow.EndToEnd.Tests/  # + WF-E2E (Category=Workflow, parameterised over transports)
└─ workflow-golden/                     # WF-GOLD fixtures shared by the .NET and WASM test runs
```

## 2. Work packages (32 ew)

### WP-S.1 Static manifest emission · 2 ew · depends: WP-1.2, WP-1.12
- **Design**: doc 15 §3.
- **Deliverables**: generator emits `[assembly: SourceFlowManifest(json)]` (aggregates, commands, handled events, process managers, projections); static analysis of `Decide` for `emits`/`rejects`; `[Emits]`/`[Rejects]` attributes in `SourceFlow.Net` (namespace `SourceFlow`); analyzer **SF0019**.
- **Tests**: UT-MAN-01..08, GEN-51..55.
- **Acceptance**: the Bank and OrderFulfilment samples produce exact `emits`/`rejects` with no SF0019 warnings left.

### WP-S.2 Design-time manifest export + workspace · 1.5 ew · depends: WP-S.1, WP-1.10, WP-4.1
- **Deliverables**: `SOURCEFLOW_DESIGN_TIME` mode in hosting (configuration runs; hosted services, provisioning and I/O off); `dotnet sourceflow manifest export --project` merging routes, publications, subscriptions and `replyOnReject`; `sourceflow.domain.yaml` model + loader; a CI **manifest drift check**.
- **Tests**: UT-MAN-09..15, STU-E2E-01 precondition.
- **Acceptance**: export makes no network or database calls (asserted under a fault-injecting store and transport). Repeated runs are byte-identical.

### WP-S.3 DSL core: model, reader, schema, validator · 3.5 ew · depends: WP-S.1, WP-1.13 (CESQL evaluator)
- **Deliverables**: `SourceFlow.Studio` model, a YamlDotNet reader with source positions, `schemas/workflow-1.json`, validator rules **WF-V01…V16** with stable diagnostic ids and positions, a CESQL guard binder (shared evaluator).
- **Tests**: UT-WF-01..40, WF-GOLD-01..30.
- **Acceptance**: every WF-V rule has a positive and a negative golden case. The schema and the validator agree on all golden files.

### WP-S.4 Simulator, path enumerator, spec generator, exporters · 3 ew · depends: WP-S.3
- **Deliverables**: a deterministic **simulator** implementing the doc 15 §5.3 transition table (state, outbox, timers, compensation stack); a **path enumerator** (retry loops at 0, 1, max; bounded); a **spec generator** (`ProcessManagerSpec` C# per path); **Mermaid** `stateDiagram-v2` and **BPMN 2.0** exporters.
- **Tests**: UT-WF-41..60.
- **Acceptance**: order fulfilment enumerates exactly the expected path set (golden). Generated specs compile.

### WP-S.5 Workflow code generation + runtime abstractions · 4 ew · depends: WP-S.3, WP-1.9
- **Deliverables**: `WorkflowGenerator` (AdditionalFiles `**/*.sfflow.yaml` via package build props); a generated `[ProcessManager("name@v")]` record, timeout message and operator commands (`ResumeWorkflow`, `AbortWorkflow`, `RetryWorkflowStep`); `$hook` partial declarations; diagnostics **SF0020** (WF-V rules at YAML positions) and **SF0021**; the workflow runtime types (namespace `SourceFlow.Workflows` in `SourceFlow.Net`); deterministic re-send ids `UUIDv5(instance, step, attempt)`; side-by-side versions.
- **Packaging**: the generator compiles the `src/Workflows/SourceFlow.Studio/Dsl/**` sources (linked, so one implementation) into its `netstandard2.0` assembly. That assembly and YamlDotNet are bundled in `analyzers/dotnet/cs` of `SourceFlow.Net`, with a dependency-path test. `SourceFlow.Studio` itself stays `net10.0` only ([ADR-027 §6](../ADRs/ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md#6-studio-and-the-generator-share-one-dsl-implementation)).
- **Tests**: GEN-56..64, **WF-DIFF-01..05**.
- **Acceptance**: the differential property test (simulator ≡ generated process manager) holds over 10,000 random outcome sequences per sample workflow.

### WP-S.6 Runtime support: metrics, CLI, version guard · 1.5 ew · depends: WP-S.5, WP-4.1
- **Deliverables**: the `sourceflow.workflow.*` instruments (doc 15 §7) + generated alert rule; `dotnet sourceflow workflow list|show|resume|abort|retry-step`; a startup guard blocking removal of a version with active instances; the generated domain query `<svc>.workflow-instances.v1` (used by observe mode in Studio 1.1).
- **Tests**: UT-WF-61..70, WF-E2E-09..10.

### WP-S.7 WASM core + TypeScript facade · 1.5 ew · depends: WP-S.4
- **Deliverables**: `SourceFlow.Studio.Wasm` (`[JSExport]`: `parse`, `validate`, `simulateStep`, `enumeratePaths`, `toMermaid`, `toBpmn`, `generateSpecs`); a Web Worker host; `studio/core-client` typed API (generated `.d.ts` from the export signatures); lazy loading with caching; size budget **≤ 6 MB brotli**.
- **Tests**: WASM-01..08 (Vitest in Node against the built bundle; the same WF-GOLD fixtures as .NET → identical results).

### WP-S.8 Studio shell + local host · 2.5 ew · depends: WP-S.7, WP-S.2
- **Deliverables**: the React app shell (routing, workspace explorer, diagnostics panel, theming, keyboard map); `dotnet sourceflow studio` (Kestrel, embedded SPA assets, loopback-only bind, Host-header guard, workspace REST: manifests, workflows, layout files, run manifest export); a `--read-only` mode.
- **Tests**: STU-UT-01..10, STU-CT-01..05, UT-STU-HOST-01..10 (.NET), STU-E2E-01..03.

### WP-S.9 Views: domain map, statechart, swimlane · 3.5 ew · depends: WP-S.8
- **Deliverables**: React Flow custom nodes and edges with the doc 15 §6.1 encodings (line style + icon + label for success, failure, retry, timeout, compensation, pivot, manual); an ELK layout adapter in a worker; the domain-map aggregation from manifests; a swimlane renderer per selected path.
- **Tests**: STU-UT-11..25, STU-CT-06..18, STU-E2E-04..06, STU-A11Y-01..03, STU-PERF-01..02.

### WP-S.10 Editing and round trip · 3 ew · depends: WP-S.9
- **Deliverables**: a palette filtered by validity at the selection; a properties panel per step (send/map, await, failures, retry, timeout, compensate, pivot, guard); Monaco + monaco-yaml side by side, with diagnostics from the core; a **CST-preserving YAML writer** (minimal edits; comments kept); the `*.sfflow.layout.json` sidecar; undo/redo.
- **Tests**: STU-UT-26..40, STU-CT-19..28, STU-E2E-07..10 (including **byte-equality round trip** on all golden files).

### WP-S.11 Simulation, path explorer, spec generation UI · 2 ew · depends: WP-S.10, WP-S.4
- **Deliverables**: a simulation panel (choose outcome per step, step/run, state, outbox, timers, compensation stack view); a path explorer with coverage; a "Generate specs" action writing to the owner's test project through the local host.
- **Tests**: STU-CT-29..30, STU-E2E-11..13.

### WP-S.12 Exports, static build, PR diagram action · 2 ew · depends: WP-S.9
- **Deliverables**: SVG/PNG export; Mermaid/BPMN download; `npx @sourceflow/studio build` (read-only site); the `workflow-diff` GitHub Action (before/after SVG comment on PRs touching `*.sfflow.yaml`).
- **Tests**: STU-E2E-14..15, an Action integration test against a fixture repository.

### WP-S.13 OrderFulfilment sample, docs, hardening · 2 ew · depends: all above, WP-3.6
- **Deliverables**: `samples/OrderFulfilment` (four services, contract packages, `sourceflow.domain.yaml`, the doc 15 §5.1 workflow, generated specs); user guide (DSL reference generated from the schema); accessibility and performance fixes.
- **Tests**: **WF-E2E-01..12** across transports; STU-A11Y-04..05; STU-PERF-03.

**Schedule.** 32 ew over lane F (2 engineers ≈ 4 ew per sprint at 85 % focus, about 8 sprints). It starts at sprint 6 (after the v3 M1 exit) and is due at sprint 13, ahead of the 3.0.0 GA target ([plan 03 §10](03-Work-Breakdown-Structure.md#10-schedule-critical-path-and-staffing)).

| Studio milestone | WPs | Sprint (target) |
|------------------|-----|-----------------|
| S1 Manifests + DSL core | S.1–S.4 | 6–8 |
| S2 Code generation + runtime support | S.5–S.6 | 8–10 |
| S3 Studio designer | S.7–S.11 | 9–12 |
| S4 Exports, PR diagrams, hardening → **Studio 1.0 RC** | S.12–S.13 | 12–13 |

## 3. Test catalogue (track S)

### 3.1 .NET

| ID | Area | Cases |
|----|------|-------|
| UT-MAN-01..08 | Static manifest | Aggregates, commands, handled events; `emits` from `Decision.Accept(new X(...))`; `rejects` from `Decision.Reject(Errors.Y)` (constant code); helper-built events → SF0019; `[Emits]`/`[Rejects]` override; process managers' `sends`/`schedules`; projections |
| UT-MAN-09..15 | Design-time export | Routing, publications, subscriptions, `replyOnReject` merged; no I/O in design-time mode; deterministic ordering; workspace loader (relative paths, missing manifest error); drift detection |
| UT-WF-01..12 | Reader / schema | Every DSL element; ISO-8601 durations; source positions; unknown keys rejected; schema version mismatch → migration hint |
| UT-WF-13..40 | Validator | **WF-V01…V16**, one positive + one negative per rule, plus combined cases (pivot + compensation, nested goto loops, guard binding errors) |
| UT-WF-41..52 | Simulator | Each row of the doc 15 §5.3 transition table: start, success + capture, rejection with retry, retry exhausted → fail → **reverse-order compensation**, compensation retry, compensation exhausted → manual, `then: manual`, timeout current vs stale attempt, goto after pivot, guard-skipped step, resume and abort semantics |
| UT-WF-53..56 | Path enumerator | Bounded loops; path count for golden workflows; coverage marks |
| UT-WF-57..60 | Exporters + spec generator | Mermaid snapshot; BPMN snapshot (valid against BPMN 2.0 XSD); generated `ProcessManagerSpec` compiles and passes |
| UT-WF-61..70 | Runtime support | Metric single recording per transition kind; CLI resume/abort send operator commands; version-removal guard; the workflow-instances domain query binds (SF0018) |
| GEN-51..55 | Generator: manifest + SF0019 | Attribute emitted; JSON shape snapshot; incremental caching unaffected |
| GEN-56..64 | Generator: workflows | Snapshot of the generated process manager for order fulfilment; SF0020 at the YAML line and column for each WF-V rule class; SF0021 missing hook; AdditionalFiles discovery via build props; side-by-side `@1`/`@2`; bundling (the generator's linked DSL code + YamlDotNet load from the analyzer folder) |
| **WF-DIFF-01..05** | Differential / property (CsCheck) | Random outcome sequences: the simulator trace ≡ the generated process manager run in `SourceFlowTestHost` (state sequence, outbox messages, timers, terminal status), for each sample workflow; shrinks to a minimal counterexample |
| WF-GOLD-01..30 | Golden suite | `(workflow.yaml, manifests) → diagnostics.json, paths.json, mermaid.mmd`, run by both .NET and WASM test runners |
| UT-STU-HOST-01..10 | Local host | Loopback bind; non-loopback Host header refused; writes limited to workflow and layout paths; read-only mode; manifest export trigger |

### 3.2 End to end (`SourceFlow.EndToEnd.Tests`, `Category=Workflow`, OrderFulfilment sample)

| ID | Path | Transports |
|----|------|------------|
| WF-E2E-01 | Happy path → `Completed`, `mark-fulfilled` sent | in-memory, AWS, Azure, Google Cloud |
| WF-E2E-02 | `payments.declined` ×2 then captured → completed (business retry with fixed backoff under `FakeTimeProvider`) | same |
| WF-E2E-03 | Declined ×4 → fail → **refund not needed, release stock, cancel order** (reverse order) → `Compensated` | same |
| WF-E2E-04 | Out of stock at the first step → `Compensated` with an empty stack | in-memory, PostgreSQL |
| WF-E2E-05 | Payment timeout ×3 → fail → compensation | same |
| WF-E2E-06 | Release-stock compensation keeps failing → `ManualIntervention` + alert metric | same |
| WF-E2E-07 | Operator `ResumeWorkflow` from manual → compensation completes | same |
| WF-E2E-08 | Large order → review step (guard) → approved → shipped | same |
| WF-E2E-09 | After the pivot: `address_invalid` → `await-address-fix` → corrected → shipped (forward recovery, no compensation) | same |
| WF-E2E-10 | Version 2 deployed while v1 instances are in flight → v1 completes on v1, new orders on v2; v1 removal blocked while active | PostgreSQL |
| WF-E2E-11 | Duplicate delivery of every awaited event → no double transitions (inbox + deterministic resend ids) | AWS, Azure, Google Cloud emulators |
| WF-E2E-12 | Crash at each fault-injection point mid-workflow → resumes correctly (chaos, nightly) | PostgreSQL |

### 3.3 Studio (TypeScript)

| ID | Kind | Cases |
|----|------|-------|
| STU-UT-01..40 | Vitest unit | Workspace store; DSL ↔ graph mapping; edge-kind encoding; ELK adapter; palette filtering; CST writer minimal-diff rules; undo/redo; path coverage marks |
| STU-CT-01..30 | React Testing Library | Explorer, diagnostics panel, canvas nodes and edges (accessible names), properties panel forms, Monaco integration, simulation panel, path explorer |
| WASM-01..08 | Vitest (Node) | Built WASM API: parse, validate, simulate, enumerate, export, spec generation; results equal the WF-GOLD expectations; worker cancellation; size budget check |
| STU-E2E-01..15 | Playwright (Chromium, Firefox, WebKit) | Open workspace (OrderFulfilment); domain map shows ownership; open workflow; introduce an error → diagnostic at YAML and canvas; fix by visual edit; **round-trip byte equality**; add a retry and a compensation visually; simulate the compensated path; generate specs; export SVG/Mermaid/BPMN; static build renders read-only; local host refuses writes in read-only mode |
| STU-A11Y-01..05 | axe-core + keyboard scripts | No serious violations; keyboard-only: create a step, connect, set a failure; screen-reader names for edge kinds; contrast; reduced motion |
| STU-PERF-01..03 | Playwright traces | 200-state workflow lays out and renders < 1 s; workspace of 20 services / 50 workflows opens < 3 s; validation round trip < 100 ms |

### 3.4 Coverage targets (adds to plan 04 §5)

Extends [plan 04 §5](04-Test-Strategy-and-Coverage.md#5-coverage-targets-and-enforcement).

| Package | Line | Branch |
|---------|------|--------|
| `SourceFlow.Studio` | 90 % | 85 % |
| `SourceFlow.Net`, namespace `SourceFlow.Workflows` | 95 % | 90 % |
| `SourceFlow.Studio.Wasm` (facade) | 90 % | 80 % |
| Workflow parts of `SourceFlow.Generators` / `SourceFlow.Tool` | 90 % / 80 % | 85 % / 70 % |
| `studio/app` + `studio/core-client` (Vitest, v8 coverage) | 85 % statements | 80 % branches |

Gate rules are those of plan 04 §5, applied to the Vitest coverage summary as well.

## 4. CI additions (extends plan 08)

Extends [plan 08](08-CI-CD-and-Quality-Gates.md).

| Workflow / job | Trigger | Steps |
|----------------|---------|-------|
| `studio-pr.yml` | PRs into `v3.0.0` touching `studio/**`, `src/Workflows/**`, `schemas/**`, `samples/OrderFulfilment/**` | pnpm install (frozen lockfile) → ESLint + Prettier check → `tsc --noEmit` → install the .NET `wasm-tools` workload → build the WASM core → Vitest (+ coverage) → WASM-01..08 → Playwright (3 browsers) + axe → STU-PERF budgets → static-build smoke → coverage gate |
| `v3-pr.yml` (existing) | as before | + `SourceFlow.Studio.Tests`, WF-GOLD, WF-DIFF (bounded), WF-E2E (in-memory + emulators), manifest drift check |
| `v3-nightly.yml` (existing) | schedule | + WF-DIFF long run; WF-E2E on real AWS, Azure and Google Cloud; WF-E2E-12 chaos |
| `workflow-diff` Action | PRs touching `**/*.sfflow.yaml` | Render before/after SVGs, comment on the PR |
| Release | Studio tags `studio-v1.*` | npm publish `@sourceflow/studio` (provenance); NuGet for `SourceFlow.Studio`; the Tool embeds the matching SPA build |

Renovate covers npm dependencies (grouped weekly). `npm audit` / `pnpm audit` blocks on high-severity issues. Licences are checked (permissive only).

## 5. Definition of Done additions and gates

In addition to [plan 09 §1](09-Definition-of-Done-Risks-and-Tracking.md#1-definition-of-done-per-work-package):
- [ ] Any DSL change updates `schemas/workflow-1.json`, the golden suite, the generated DSL reference, and **both** the .NET and WASM test runs.
- [ ] Any change to transition semantics updates the simulator **and** the generator in the same PR, and WF-DIFF passes.
- [ ] UI changes include component tests, a Playwright scenario when user-visible, and pass axe with no serious violations.

**Studio 1.0 release gate:**
- S1–S4 exit criteria met ([design doc 12 §5.0](../12-Migration-Delivery-and-Traceability.md#50-sourceflow-studio-release-train-product-doc-07-wq1));
- WF-E2E-01..11 green on all three cloud transports;
- performance and accessibility budgets met;
- the DSL `sourceflow.workflow/1` and `manifestVersion: 1` are frozen (public contracts).

## 6. Risks (adds to plan 09 §6)

Extends [plan 09 §6](09-Definition-of-Done-Risks-and-Tracking.md#6-implementation-risk-register).

| ID | Risk | Likelihood | Impact | Mitigation |
|----|------|------------|--------|------------|
| IR-13 | Static analysis of `Decide` misses emitted events or rejection codes, making the diagram wrong | Medium | Medium | SF0019 plus `[Emits]`/`[Rejects]`; WF-V03/V08 fail loudly on mismatch; the drift check |
| IR-14 | Bundling `SourceFlow.Studio` + YamlDotNet inside the analyzer package causes load or version conflicts | Medium | High | Bundle into the analyzer folder with private assets; a GEN-64 load test in a clean sample; if it fails, fallback to an MSBuild task that converts YAML to a generated C# input |
| IR-15 | WASM bundle size or startup hurts UX | Medium | Low | Trimming, lazy load in a worker, caching, the 6 MB budget in CI; the TS UI stays usable for viewing while the core loads |
| IR-16 | Simulator and generator semantics drift | Low | High | Shared core plus the WF-DIFF differential property tests on every PR |
| IR-17 | React/TypeScript expertise is thin in a .NET team | Medium | Medium | A dedicated React engineer in lane F; a component library (Radix); code review by a frontend reviewer |
| IR-18 | The lossless YAML round trip is hard (comments, anchors) | Medium | Medium | Restrict the DSL subset (no anchors or aliases; schema-enforced); CST writer golden tests; byte-equality E2E |
