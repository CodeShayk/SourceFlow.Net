# SourceFlow.Net v3.0.0 — Implementation Plan

| | |
|---|---|
| **Status** | Plan, ready for execution. No code has been written yet. |
| **Where code will live** | Branch **`v3.0.0`** cut from `master`, rewriting the repository's own `src/` and `tests/` ([plan 02](02-Branching-Versioning-and-Repository-Layout.md)) |
| **Baseline** | v2.0.0 on `master` (commit `086673b`), plus the Azure provider on `origin/azure-cloud` |
| **Design** | [docs/v3.0.0](../README.md): 14 design documents and 23 ADRs. Product sign-off received 2026-11-10. |

## Purpose

These plans turn the v3 design into executable work, grounded in what exists in v2:

- every v2 project and test file has a stated fate (port, rewrite, replace or retire);
- every v3 component has a work package with files to create, tests to write, acceptance criteria and dependencies;
- every design rule, gap fix and product requirement traces to at least one test.

Engineers (or coding agents) pick up a work package, create a `v3/<wp-id>-<slug>` branch from `v3.0.0`, and open a PR back into `v3.0.0` that satisfies that package's Definition of Done.

## Documents

| # | Plan | Answers |
|---|------|---------|
| 01 | [v2 Baseline Inventory](01-V2-Baseline-Inventory.md) | What exists today: projects, size, ~590 tests, frameworks, CI, branches |
| 02 | [Branching, Versioning and Repository Layout](02-Branching-Versioning-and-Repository-Layout.md) | How the `v3.0.0` branch, GitVersion, the solution and the projects are set up, and how v2 code is retired |
| 03 | [Work Breakdown Structure](03-Work-Breakdown-Structure.md) | 54 work packages across M0–M6: deliverables, tasks, tests, acceptance criteria, dependencies, estimates, critical path |
| 04 | [Test Strategy and Coverage](04-Test-Strategy-and-Coverage.md) | Frameworks, test project layout, naming, coverage targets and how they are enforced |
| 05 | [Unit Test Plan](05-Unit-Test-Plan.md) | Unit and generator test catalogue per component, with v2 lineage |
| 06 | [Integration and E2E Test Plan](06-Integration-and-E2E-Test-Plan.md) | Storage conformance kit, Transport Conformance Kit, end-to-end scenarios, chaos, migration, query, performance |
| 07 | [v2 Test Disposition Matrix](07-V2-Test-Disposition-Matrix.md) | All 95 v2 test files on `master` (plus the `azure-cloud` suites) → port, rewrite, replace or retire, with the v3 test that takes over |
| 08 | [CI/CD Pipelines and Quality Gates](08-CI-CD-and-Quality-Gates.md) | Workflows for the `v3.0.0` branch, emulators, nightly real-cloud runs, gates, release |
| 09 | [Definition of Done, Risks and Tracking](09-Definition-of-Done-Risks-and-Tracking.md) | PR and milestone gates, implementation risks, tracking board, cutover to `master` |
| 10 | [Track S: Workflow DSL and SourceFlow Studio](10-Studio-Workflow-Track-Plan.md) | **New requirement** (product doc 07): 13 work packages (32 ew) for the saga DSL, code generation, React designer, tests and CI. Separate Studio 1.0 release train |

## Conventions

| Prefix | Meaning | Defined in |
|--------|---------|------------|
| `WP-m.n` | Work package *n* of milestone *m* (`WP-1.6`). `WP-m.R` is a retirement package. | 03 |
| `WP-S.n` | Track S work package (workflow DSL and Studio) | 10 |
| `UT-WF`, `UT-MAN`, `WF-GOLD`, `WF-DIFF`, `WF-E2E`, `STU-*`, `WASM-*` | Track S tests | 10 §3 |
| `UT-<area>-nn` | Unit test case | 05 |
| `GEN-nn` | Generator or analyzer test | 05 |
| `SCK-nn` | Storage conformance kit case | 06 |
| `TCK-nn` | Transport conformance kit case | 06 |
| `E2E-nn`, `CH-nn`, `MIG-nn`, `QRY-nn`, `PERF-nn`, `ARCH-nn` | End-to-end, chaos, migration, query, performance and architecture tests | 06, 04 |
| `DC-n` | Design correction found while planning; already applied to the design docs | 03 §1 |

Sizes are in **engineer-weeks (ew)** of focused work. They are estimates for planning, not commitments. Totals and the critical path are in [plan 03 §10](03-Work-Breakdown-Structure.md#10-schedule-critical-path-and-staffing).
