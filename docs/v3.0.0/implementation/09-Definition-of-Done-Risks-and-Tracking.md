# 09 — Definition of Done, Risks and Tracking

## 1. Definition of Done (per work package)

A WP's PR into `v3.0.0` merges only when every item holds:

- [ ] **Scope**: every deliverable listed for the WP in [plan 03](03-Work-Breakdown-Structure.md) exists. Anything deferred has a linked issue and architect approval.
- [ ] **Tests**: every test ID assigned to the WP (plans 05 and 06) is implemented, carries `Id` and `Covers` traits, and passes in `v3-pr`.
- [ ] **v2 parity**: if the WP takes over v2 behaviour, the related [plan 07](07-V2-Test-Disposition-Matrix.md) rows are ticked in the PR description.
- [ ] **Coverage**: the package meets plan 04 §5 thresholds (or is report-only before its milestone exit) and drops no more than 0.5 points.
- [ ] **Design fidelity**: behaviour matches the referenced design sections. Any divergence comes with a design-doc or ADR update **in the same PR**.
- [ ] **Telemetry**: spans, metrics and logs per design doc 08, with single-recording tests for new instruments.
- [ ] **Options**: every new option is validated and honoured (no inert options; GAP-03).
- [ ] **API**: public API approval file updated and reviewed; XML docs complete; analyzers clean; AOT and trim analysis clean for AOT packages.
- [ ] **Performance**: hot-path changes include benchmark evidence (no new allocations; within the time budget).
- [ ] **Failure modes**: each failure path (retry, park, stop, conflict) is listed in the PR and has a test.
- [ ] **Docs**: package README or docs page updated; sample updated if user-facing.

## 2. Milestone exit gates

| Milestone | Exit criteria (all required) |
|-----------|------------------------------|
| **M0** | Branch protected; GitVersion produces `3.0.0-alpha.N`; `v3-pr` and nightly run green on the empty scaffold; quality harness gates fail when they should (demonstrated by a deliberately bad PR) |
| **M1** | WP-1.1…1.14 done. The Bank sample runs end to end in memory (E2E-01..16 green). The in-memory store passes the full SCK, and the in-memory transport passes the full TCK. GEN-01..50 and UT areas for M1 green. Core coverage ≥ 90/85. **Public API frozen** for Abstractions and SourceFlow.Net. WP-1.R done (v2 core deleted) |
| **M2** | PostgreSQL passes the full SCK on 15/16/17 (5 green nights). SCK-24 gap-freedom long run green. QRY in-memory ↔ PostgreSQL equivalence green. PERF-03..08 baselines recorded; doc 09 component targets met or waived. WP-2.R done |
| **M3** | **AWS, Google Cloud and Azure** pass the full TCK on emulators (PR) and real cloud (5 green nights). E2E-17..24 green on all three. Provisioning `Validate` works with describe-only roles. WP-3.R done; `azure-cloud` archived |
| **M4** | Query API (WP-4.2) green on QRY-53..59 with zero DAST high/medium findings. SQL Server passes the full SCK on 2022/2025. CLI and AspNetCore complete. Dashboards and alerts generated with no drift. Chaos CH-01..10 green. The tier-M load test meets the doc 08 §6 SLOs |
| **M5** | Payload protection + 3 KMS providers green on real KMS. The erasure E2E (E2E-29..31) is green. Migration MIG-01..12 green on the seeded v2 database. Fuzzing ran 2 h per target with no crashes. Security review signed off |
| **Studio 1.0** (separate train) | [Plan 10 §5](10-Studio-Workflow-Track-Plan.md#5-definition-of-done-additions-and-gates) release gate. Not a prerequisite for 3.0.0 GA |
| **M6 / GA** | Mutation ≥ 70 % (core). Zero quarantined tests. Zero open Sev-1/Sev-2. RC used in production by ≥ 2 adopters for 4 weeks. Doc 09 §7.1 targets met or formally waived. Cutover runbook (§7) completed |

## 3. Tracking

- **Board**: a GitHub Project "SourceFlow v3.0.0" with one issue per WP (created in WP-0.5 from plan 03). Fields: `WP`, `Milestone`, `Lane` (A–E), `Size (ew)`, `Status` (Todo, In progress, In review, Done), `Owner`, `Tests delivered` (count of assigned IDs passing), `v2 rows satisfied`.
- **Labels**: `v3`, `wp:<id>`, `milestone:M<n>`, `lane:<A-E>`, `design-change`, `perf-baseline`, `quarantine`, `forward-port`.
- **Issue template "Work package"**: deliverables checklist, test-ID checklist, v2 disposition rows, DoD checklist (§1).
- **Weekly report** (generated in CI from the board and the traceability CSV): burn-up in ew, test IDs passing against planned, coverage per package, open risks, nightly pass rate.
- **Traceability report**: ARCH-08 output published as a nightly artifact. It lists every design rule, ADR, GAP and quality scenario with the tests that cover it.

## 4. v2 hotfixes during v3 development

- Fixes continue on `master` (and on `v2.x` after cutover).
- For each v2 fix, the author adds a row to `docs/v3.0.0/implementation/forward-port-log.md` (created in WP-0.5). The row records the v2 PR; whether the defect **can occur in v3's design** (usually not, because the code is rewritten); and if it can, the v3 test that guards it and the WP that owns it.
- Nothing is cherry-picked across. v3 fixes are re-implemented against v3 code.

## 5. Scope control

- **Fixed scope for 3.0.0** (product sign-off 2026-11-10): event sourcing, owner-scoped querying, AWS + Azure + Google Cloud GA, PostgreSQL + SQL Server, everything in M1–M5.
- **The only scope valve**: the ad-hoc HTTP query endpoint (`MapSourceFlowQuery`) can move to 3.1. Named domain queries (`MapDomainQueries`) can't.
- **Change control**: anything that alters a decision needs an ADR (new, or superseding). Anything that changes a work package's deliverables needs an update to plan 03 in the same PR, plus architect approval.
- **Schedule pressure** moves the date, using the 15 % buffer first (plan 03 §10). It doesn't drop transports or guarantees.

## 6. Implementation risk register

| ID | Risk | Likelihood | Impact | Mitigation | Owner |
|----|------|------------|--------|------------|-------|
| IR-01 | Generator complexity (incrementality, diagnostics, code fixes) delays M1 | Medium | High | Start WP-1.2 in sprint 2 with a Roslyn-experienced owner; snapshot tests from day one; minimal emitter surface; hand-written "generated" fixtures let runtime work proceed in parallel | Lane B |
| IR-02 | `xid8` horizon behaves differently behind PgBouncer or on read replicas | Medium | High | SCK-24 run through PgBouncer (transaction mode) in nightly; LISTEN uses a direct connection; replicas are used only for the query side (QRY-45) | Lane C |
| IR-03 | Emulator divergence (Azure sessions, LocalStack features) hides bugs or blocks PRs | High | Medium | `RealCloudOnly` cases with written reasons; nightly real cloud is release-blocking; divergence list kept in the TCK fixture | Lane D/E |
| IR-04 | LocalStack image licensing changes again | Medium | Medium | Keep the pinned image; fallback plan: a LocalStack auth token secret, or run the AWS TCK against real AWS on PRs that touch `AmazonSqs` | Lane E |
| IR-05 | Real-cloud nightly cost and flakiness | Medium | Medium | Sandbox accounts with budgets; the sweeper; retries only for infrastructure provisioning, never inside assertions; flaky TCK cases quarantined at most 5 days | Lane E |
| IR-06 | Three parallel transports overload M3 staffing | Medium | High | The SPI and TCK land first (WP-3.1); two people on transports plus Lane E; scope fixed, so the buffer is used | PM |
| IR-07 | `ubuntu`-only containers: Windows runners can't run Linux containers | High | Low | Integration jobs run on Linux only; Windows runs build + unit (documented) | Lane E |
| IR-08 | AOT constraints conflict with EF Core and the CLI | Medium | Low | Those packages are explicitly not AOT-marked (plan 02 §6); ARCH-06 checks only AOT packages | Lane A |
| IR-09 | Property-based tests are flaky under CI load | Medium | Medium | Deterministic seeds recorded on failure; bounded iterations on PR; long runs nightly only | Lane C |
| IR-10 | The `azure-cloud` author's context is lost | Medium | Medium | A knowledge-transfer session before WP-3.4; their emulator configs and excluded-test reasons are carried over | PM |
| IR-11 | v2 users keep needing fixes that pull people off v3 | Medium | Medium | A rotating v2 duty (one person per sprint, max 20 %); forward-port log | PM |
| IR-12 | Branch name `v3.0.0` clashes with the release tag (DC-4) | High if ignored | Medium | Plan 02 §2 rules; the cutover runbook deletes the branch before tagging | Lane E |
| IR-19 | The SQL syntax and join support grow the attack surface, or pressure builds to add more SQL (unions, CTEs) before it's secured | Medium | High | Allow-list grammar, I-3 no-literal property test, injection corpus and DAST as PR/nightly gates; new constructs only via `sfql/1.1` with an ADR and their own security tests | Lane B |
| IR-13…18 | Track S risks (manifest accuracy, analyzer bundling, WASM size, semantic drift, React skills, YAML round trip) | — | — | [Plan 10 §6](10-Studio-Workflow-Track-Plan.md#6-risks-adds-to-plan-09-6) | Lane F |

## 7. GA cutover runbook (WP-6.3)

1. Freeze `v3.0.0` (block merges except release fixes). Confirm the M6 gate (§2).
2. On `master`: create `v2.x` from the current `master` head, and protect it.
3. Update `GitVersion.yml` on `v3.0.0` for the post-merge `master` state (remove the `v3` and `v3-work` entries, keep `next-version: 3.0.0`). Merge `v3.0.0` → `master` with a merge commit (preserves history), resolving `.github/workflows` so that `master` runs the v3 workflows and `v2.x` keeps the v2 ones.
4. Delete branch `v3.0.0` (push `archive/v3.0.0` first if wanted). **Only then** create tag `v3.0.0` on `master` (plan 02 §2, DC-4).
5. `v3-release.yml` publishes 3.0.0 to GitHub Packages and NuGet.org (after approval).
6. Publish the final 2.x packages' deprecation notes pointing to the v3 replacements (plan 02 §5). Post the migration guide and release notes.
7. Re-point the docs default to v3. Update the README roadmap table (v3.0.0 released; Azure and GCP GA).
8. Start the 12-month v2 support window on `v2.x` (design doc 12 §1).
