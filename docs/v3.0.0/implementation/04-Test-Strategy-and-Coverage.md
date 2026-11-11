# 04 — Test Strategy and Coverage

## 1. Principles

1. **Test behaviour and contracts, not implementation.** Storage and transport adapters are proven by shared **conformance kits**. Each adapter re-runs the same suite, so every provider is held to the same contract (ADR-014; design doc 11).
2. **Unit tests for pure logic, E2E for the guarantees.** Atomicity, exactly-once effect, ordering and gap-freedom are proven end to end, with fault injection and property-based concurrency.
3. **Determinism.** Time comes from `FakeTimeProvider`, ids are seeded, and waits use `WaitForIdleAsync`. **No `Task.Delay`/`Thread.Sleep` in tests.** An analyzer bans them in test projects (§8).
4. **Every rule has a test.** Each design rule (ES-R1, RX-R1…, RT-R1…, Q-B1…), gap fix (GAP-01…20) and product requirement is linked to at least one test through a `Covers` trait (§7).
5. **v2 scenarios are not lost.** Every v2 test file has a disposition ([plan 07](07-V2-Test-Disposition-Matrix.md)).

## 2. Test stack (one stack, replacing v2's mix)

| Purpose | v2 used | v3 uses | Why |
|---------|---------|---------|-----|
| Framework | NUnit 3/4 (Core, EF) **and** xUnit 2 (AWS, GCP) | **xUnit v3** | One framework. Assembly fixtures for shared containers, `TestContext` cancellation, explicit tests, .NET 10 support |
| Assertions | NUnit/xUnit asserts | **Shouldly** | Readable, permissively licensed (FluentAssertions 8 moved to a commercial licence) |
| Test doubles | Moq | **Hand-written fakes first**; **NSubstitute** only at SDK seams (`IAmazonSQS`, `ServiceBusSender`, `PublisherServiceApiClient`) | In-memory adapters are real implementations and better fakes. Avoids Moq's past SponsorLink controversy |
| Property-based | FsCheck 2 | **CsCheck** | C#-first, good shrinking, and `SampleParallel` **linearisability checking** for concurrent append and outbox |
| Snapshots | — | **Verify** (+ Verify.SourceGenerators) | Generated code, public API and golden SQL |
| Analyzer tests | — | **Microsoft.CodeAnalysis.CSharp.Testing** | Diagnostics and code fixes |
| Containers | Testcontainers.LocalStack | **Testcontainers** (PostgreSql, MsSql, LocalStack); Pub/Sub and Service Bus emulators through Testcontainers modules where available, otherwise the existing compose files (`.github/emulators`) | Reuses what already works in v2 CI |
| Time | `DateTime.UtcNow` | **Microsoft.Extensions.TimeProvider.Testing** (`FakeTimeProvider`) | Deterministic timers and leases |
| Coverage | coverlet.collector | **coverlet.collector + ReportGenerator** | Merged per-package reports and gate |
| Mutation | — | **Stryker.NET** | Measures test strength (weekly) |
| Benchmarks | BenchmarkDotNet (AWS only) | **BenchmarkDotNet** (`benchmarks/`), **NBomber** for load | Doc 09 §7 |
| Faults | — | **Toxiproxy** (network), `IFaultInjector` (in-process) | Chaos suite |
| Fuzzing | — | **SharpFuzz** | Parsers (doc 10 §7) |
| Front end (Studio) | — | **Vitest**, React Testing Library, **Playwright**, **axe-core** | Track S (plan 10) |
| Architecture | — | **NetArchTest.Rules** | Dependency rules (doc 01 §5) |
| Public API | — | **PublicApiGenerator** | Breaking-change detection |

## 3. Test project layout (`tests/`)

| Project | Kind | Tests | Needs | Runs |
|---------|------|-------|-------|------|
| `SourceFlow.Abstractions.Tests` | Unit | UT-ABS | — | PR |
| `SourceFlow.Generators.Tests` | Unit (generator/analyzer) | GEN | — | PR |
| `SourceFlow.Net.Tests` | Unit + in-memory conformance | UT-MSG/STO/RT/CMD/MW/DIAG/OUT/RX/DLQ/SUB/PRJ/PM/HOST/CAT/QL/SNAP/TOP/HC/RED, SCK (in-memory), TCK (in-memory) | — | PR |
| `SourceFlow.Testing.Tests` | Unit | UT-TST | — | PR |
| `SourceFlow.PostgreSql.Tests` | Integration | SCK (PostgreSQL), SCK-M, QRY (PostgreSQL), PERF smoke | Docker | PR (PostgreSQL 17), nightly (15/16/17 + partitioned variant) |
| `SourceFlow.SqlServer.Tests` | Integration | SCK (SQL Server), QRY (SQL Server) | Docker | PR (2022), nightly (2022/2025) |
| `SourceFlow.EntityFrameworkCore.Tests` | Integration | UT-EF, SCK-61..64 | Docker | PR |
| `SourceFlow.AmazonSqs.Tests` | Unit + TCK | UT-AWS, TCK | LocalStack / AWS | PR (unit + LocalStack), nightly (real AWS) |
| `SourceFlow.GooglePubSub.Tests` | Unit + TCK | UT-GCP, TCK | Pub/Sub emulator / GCP | PR + nightly |
| `SourceFlow.AzureServiceBus.Tests` | Unit + TCK | UT-ASB, TCK | Service Bus emulator / Azure | PR + nightly |
| `SourceFlow.Security.Tests` | Unit + integration | UT-SEC, UT-PII, KMS round trips | LocalStack KMS / real KMS | PR + nightly |
| `SourceFlow.AspNetCore.Tests` | Unit (`WebApplicationFactory`) | UT-ASP | — | PR |
| `SourceFlow.OpenTelemetry.Tests` | Unit | UT-OTEL | — | PR |
| `SourceFlow.Tool.Tests` | Unit + integration | UT-CLI, MIG | Docker for MIG | PR |
| `SourceFlow.E2E.Tests` | End to end | E2E (parameterised over transports and stores) | Docker + emulators | PR (in-memory + PostgreSQL + emulators), nightly (real cloud) |
| `SourceFlow.Chaos.Tests` | Chaos | CH | Docker, Toxiproxy | Nightly, milestone exits |
| `SourceFlow.Architecture.Tests` | Rules | ARCH | — | PR |
| `SourceFlow.Workflows.Tests`, `SourceFlow.Workflows.E2E.Tests`, `studio/**` (Vitest, Playwright) | Track S (plan 10 §3) | UT-WF, UT-MAN, WF-GOLD, WF-DIFF, WF-E2E, STU-*, WASM-* | Docker (E2E), browsers | PR + nightly |
| `benchmarks/SourceFlow.Benchmarks` | Performance | PERF | Docker (component) | PR (micro), nightly (component) |

The conformance kits themselves (`StorageConformanceTests`, `TransportConformanceTests`) ship in **`src/SourceFlow.Testing`**, so third-party adapter authors can run them too.

## 4. Conventions

- **Names**: `Method_Condition_Expected` (the v2 style, kept for continuity), for example `SendAsync_DuplicateIdempotencyKey_ReturnsOriginalResult`.
- **Traits**: `[Trait("Id", "UT-CMD-07")]`, `[Trait("Covers", "GAP-05;ADR-008")]`, `[Trait("Category", "Unit|Integration|Conformance|E2E|Chaos|Performance")]`, `[Trait("Requires", "Docker|LocalStack|PubSubEmulator|ServiceBusEmulator|Aws|Azure|Gcp")]`.
- **Arrange data** comes from the Bank sample (WP-1.14). There are no anonymous dummy messages except in generator tests.
- **One assertion focus per test.** Use `Should.SatisfyAllConditions` for related checks.
- **Containers** are shared per assembly (xUnit v3 assembly fixtures). Each test gets an isolated schema or database name or queue prefix, so tests can run in parallel.

## 5. Coverage targets and enforcement

Coverage is measured per package on the **merged** report of unit + integration + conformance runs in the PR pipeline.

| Package | Line | Branch | Notes |
|---------|------|--------|-------|
| `SourceFlow.Abstractions` | 95 % | 90 % | Small, critical contracts |
| `SourceFlow.Generators` | 90 % | 85 % | Every emitter path and diagnostic |
| `SourceFlow.Net` | **90 %** | **85 %** | Runtime core |
| `SourceFlow.Testing` | 85 % | 75 % | |
| `SourceFlow.PostgreSql`, `SourceFlow.SqlServer` | 90 % | 80 % | Mostly through the SCK |
| `SourceFlow.EntityFrameworkCore` | 85 % | 75 % | |
| `SourceFlow.AmazonSqs`, `.AzureServiceBus`, `.GooglePubSub` | 85 % | 75 % | Unit + emulator TCK. Real-cloud-only branches are listed in exclusions with a reason |
| `SourceFlow.Security.*` | 90 % | 80 % | |
| `SourceFlow.AspNetCore`, `SourceFlow.OpenTelemetry` | 90 % | 80 % | |
| `SourceFlow.Tool` | 80 % | 70 % | Command handlers covered; console formatting partially |
| Track S packages and `studio/` | see [plan 10 §3.4](10-Studio-Workflow-Track-Plan.md#34-coverage-targets-adds-to-plan-04-5) | | Includes TypeScript coverage via Vitest |

**Gate rules** (`tools/coverage-gate`, run in `v3-pr`):
1. Fail if any package is below its target, once that package's milestone has exited. Before exit, report only.
2. Fail if any package drops **more than 0.5 points** against the `v3.0.0` base, even above target.
3. Exclusions apply only via `[ExcludeFromCodeCoverage(Justification = "…")]`, or `coverlet.runsettings` for generated (`*.g.cs`) and sample code. Each exclusion is reviewed in PR.

**Mutation** (Stryker.NET, weekly): `SourceFlow.Net` ≥ 70 % by WP-6.4. Surviving mutants in `CommandProcessor`, `InMemoryStore`, `SubscriptionEngine` and `OutboxRelay` are triaged within the sprint.

## 6. Public API approval

Each package's test project has `PublicApi_IsApproved` (Verify snapshot of `PublicApiGenerator` output). An API change needs the approved file updated in the same PR, plus a reviewer from the architecture group. The API freezes at **M1 exit** for `Abstractions` and the `SourceFlow.Net` surface (design doc 11 §1). After the freeze, changes need an ADR or a design-doc update.

## 7. Requirement and rule traceability

- `tests/traceability.csv` lists every ID that must be covered: design rules (ES-R1, ES-R2, RX-R1…R6, RT-R1…R6, Q-R1…R6, Q-B1…B6, DM-R1…R6, G-1…G-12, query-API injection rules I-1…I-8 and S-1…S-6), ADR-001…026, GAP-01…20, product Q1–Q7 decisions, and doc 01 §2 quality scenarios.
- **ARCH-08** reflects over every test assembly, collects the `Covers` traits, and **fails if any ID in the CSV has no test**. Uncovered IDs therefore block the build.

## 8. Flakiness and hygiene

- An analyzer (banned API list) in test projects forbids `Task.Delay`, `Thread.Sleep`, `DateTime.Now/UtcNow` and `Guid.NewGuid` in test code. Use `FakeTimeProvider`, `WaitForIdleAsync` and seeded ids.
- Quarantine: `[Trait("Quarantine", "<issue>")]` excludes a test from gating for at most **5 working days**. CI fails if a quarantine is older than that. **Zero quarantined tests at GA** (WP-6.4).
- Every integration test cleans up its namespaced resources. Nightly real-cloud runs have a sweeper job that deletes resources older than 24 hours with the `sourceflow-test` prefix.

## 9. Expected test volume at GA (estimate)

| Category | Approximate count |
|----------|-------------------|
| Unit (all packages) | 700 |
| Generator and analyzer | 50 |
| Storage conformance | 72 cases × 4 targets (in-memory, PostgreSQL, PostgreSQL-partitioned, SQL Server) |
| Transport conformance | 45 cases × 4 transports (in-memory, AWS, Google Cloud, Azure) |
| End to end | 31 scenarios (some parameterised × 4 transports) |
| Query | 45+ (in-memory + golden SQL + cross-provider) |
| Chaos / migration / performance | 10 / 12 / 10 |

That is about **1,300 executed test cases** per full nightly run, compared with about 590 in v2.
