# 01 — v2 Baseline Inventory

This snapshot was taken from `master` @ `086673b` on 2026-11-10. It is the starting point that every later plan maps from.

## 1. Source projects

| v2 project | Target frameworks | C# lines | Contents | v3 fate (plan 02 §4) |
|------------|-------------------|----------|----------|----------------------|
| `src/SourceFlow` | netstandard2.0, 2.1, net8–10 | ~6,200 | Aggregate/Saga/View model, CommandBus, command store adapter (command sourcing), EventQueue, four middleware types, DI scanning, domain telemetry, `Cloud/*` (bus configuration, idempotency, dead letter, circuit breaker, encryption contracts, masker, cloud telemetry, polymorphic JSON converter) | **Rewritten** as `SourceFlow.Net` (single core with extensibility SPIs; bundles `SourceFlow.Generators`) + `SourceFlow.Testing` |
| `src/SourceFlow.Stores.EntityFramework` | net8–10 | ~2,800 | EF Core 9 command, entity and view-model stores; Polly resilience; EF idempotency service and cleanup; table naming; schema helper | **Replaced** by `SourceFlow.Storage.PostgreSQL`, `SourceFlow.Storage.SqlServer` (ADO.NET event store) and `SourceFlow.Storage.EntityFrameworkCore` (read models only) |
| `src/SourceFlow.Cloud.AWS` | netstandard2.1, net8–10 | ~3,300 | SQS/SNS dispatchers and listeners (basic + Enhanced), bootstrapper, KMS encryption, dead-letter monitor, health check, JSON converters | **Rewritten** as `SourceFlow.Cloud.Aws` (transport + KMS key provider) |
| `src/SourceFlow.Cloud.GCP` (2.0.0-beta.1) | net8–10 | ~1,250 | Pub/Sub dispatchers and listeners, bootstrapper, Cloud KMS, health check, emulator support | **Rewritten** as `SourceFlow.Cloud.Gcp` (transport + KMS key provider) |
| `src/SourceFlow.Cloud.Azure` (only on `origin/azure-cloud`, 2.0.0-beta.1) | — | ~21k lines added across src and tests | Service Bus dispatchers and listeners (basic + Enhanced), bootstrapper, Key Vault encryption, dead-letter monitor, health check | **Rewritten** as `SourceFlow.Cloud.Azure` (transport + Key Vault key provider). Its tests and emulator CI are reused as scenario input. |

Solution: `SourceFlow.Net.sln` (classic format) with `src`, `tests`, `misc` and `github` solution folders.

## 2. Test projects

| v2 test project | Framework | Test files | Tests | Helpers / notes |
|-----------------|-----------|------------|-------|-----------------|
| `tests/SourceFlow.Core.Tests` | NUnit 4, Moq | 28 with tests (53 .cs) | **237** | Bank-account E2E domain (`E2E/*`), in-memory stores, dummy messages |
| `tests/SourceFlow.Stores.EntityFramework.Tests` | NUnit 3, Moq, EF SQLite/InMemory | 7 with tests (25 .cs) | **52** | Copy of the bank-account E2E domain with parameterless command constructors |
| `tests/SourceFlow.Cloud.AWS.Tests` | xUnit 2, Moq, FsCheck 2, BenchmarkDotNet, Testcontainers.LocalStack | 55 with tests (81 .cs) | **264 facts + 22 FsCheck properties + 4 benchmarks** | 23 helper files (LocalStack manager, environments, resource manager). Traits: `Unit` (19), `Integration` (33), `RequiresLocalStack` (31), `RequiresAWS` (2) |
| `tests/SourceFlow.Cloud.GCP.Tests` | xUnit 2 | 5 with tests (8 .cs) | **10 facts + 4 skippable facts** | Pub/Sub emulator fixture |
| `tests/SourceFlow.Cloud.Azure.Tests` (on `azure-cloud`) | xUnit | ~28 with tests (62 .cs) | not counted | Azure Service Bus emulator, Azurite, Key Vault helpers, ARM template helper |
| `tests/SourceFlow.Net.EntityFramework.Tests` | — | 0 | 0 | Empty leftover folder (`bin/`, `obj/` only). Delete in WP-0.2. |

**Total on `master`: about 590 executable tests** (237 + 52 + 290 + 14), using **two unit-test frameworks** (NUnit and xUnit), Moq, and FsCheck.

### 2.1 Test-by-area summary (`master`)

| Area | Main v2 test files | What they prove today |
|------|--------------------|-----------------------|
| Command bus and sequencing | `Impl/CommandBusTests` (21), `Impl/CommandPublisherTests` (5) | Sequence numbers, append-after-dispatch, replay doesn't append, middleware order |
| Events | `Impl/EventQueueTests` (14), `Aggregates/EventSubscriberTests` (13), `Projections/EventSubscriberTests` (13) | Fan-out to aggregates and views, middleware order, null guards |
| Sagas | `Sagas/SagaTests` (9), `Sagas/CommandSubscriberTests` (10), `Impl/SagaDispatcherTests` (3) | Handler matching, publish/raise guards |
| Middleware | four `Middleware/*Tests` (39 total) | Nesting order, short-circuit, exception flow, metadata mutation |
| DI | `Ioc/IocExtensionsTests` (12), `Impl/AggregateFactoryTests` (3) | Registration completeness |
| Messaging primitives | `Messaging/*Tests` (8) | Constructors, metadata |
| Cloud core | `Cloud/*Tests` (68): circuit breaker, telemetry, dead-letter store, idempotency, polymorphic JSON, masker | Utility behaviour |
| End to end | `E2E/E2E.Tests` (Core and EF, 1 scenario each) | Account lifecycle across saga → events → views (+ replay) |
| EF stores | `Ef*StoreIntegrationTests` (29), `EfIdempotencyServiceTests` (9), options and configuration (12) | CRUD, ordering, not-found semantics, connection-string resolution |
| AWS unit | 19 files (~100) | Dispatcher and listener behaviour with mocked SDK, bootstrapper, bus configuration, KMS, JSON converters |
| AWS integration (LocalStack) | 29 files (~160) | SQS standard/FIFO/batch/attributes/DLQ, SNS publish/fan-out/filtering/correlation, KMS, health, retry/throttling, circuit breaker, IAM, LocalStack harness |
| GCP | 5 files (14) | Registration, dispatch attributes, emulator round-trip, idempotent bootstrap |

## 3. Continuous integration (`.github/workflows`)

| Workflow | Trigger | Does |
|----------|---------|------|
| `Master-Build.yml` | push `master` | Build; `Category=Unit`; LocalStack 3 service; `Integration&RequiresLocalStack` |
| `GCP-Build.yml` | push `gcp_cloud`; PR to `gcp_cloud`, `master` | GCP unit tests; Pub/Sub emulator via `.github/gcp-emulator` compose; pack |
| `Release-CI.yml` | push `release/**`; tag `release-packages` | Build, test with LocalStack, version by GitVersion, publish to GitHub Packages on the tag (NuGet.org disabled) |
| `Master-CodeQL.yml`, `Release-CodeQL.yml` | push and schedule | CodeQL |
| `Azure-Build.yml` (on `azure-cloud`) | that branch | Service Bus emulator via `.github/azure-emulator` compose |

LocalStack is pinned to image `3` (commit `223c37a`: "avoid license errors on latest"). Plan 08 keeps it pinned and records the risk.

## 4. Versioning and refs

- `GitVersion.yml`: `next-version: 2.0.0`, `tag-prefix: '[vV]'`, branch configs for master, release, pre-release, develop, pull-request and feature. **No configuration matches a branch named `v3.0.0`** (plan 02 §2 fixes this).
- Tags: `v1.0.0`, `v1.0.0-alpha`, `v2.0.0`, `release-packages`.
- Remote branches: `azure-cloud`, `cloud-support`, `gcp_cloud`, `release/v2.0.0-aws`, `v1.0.0`, `v1.0.0-alpha.1`.

## 5. Facts that shape the plan

1. **Nothing in v2 survives as code.** v3 is a rewrite (ADR-002, ADR-003). v2 is used as **scenario source**: its tests describe behaviour that must keep working, change, or be dropped deliberately ([plan 07](07-V2-Test-Disposition-Matrix.md)).
2. **The bank-account domain** (Core and EF E2E folders) is the shared example. It becomes the v3 sample and E2E domain (WP-1.14).
3. **LocalStack, Pub/Sub emulator and Service Bus emulator CI** already exist on three branches. They are merged into one emulator matrix (plan 08).
4. **The Azure provider exists only on `azure-cloud`.** Its scenarios feed the Azure TCK fixture (WP-3.4). The branch is archived at M3 exit.
5. **Two test frameworks plus Moq and FsCheck** are consolidated into one stack (plan 04 §2).
