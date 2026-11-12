# 07 — v2 Test Disposition Matrix

Every v2 test file gets exactly one disposition. The retirement work packages (WP-1.R, WP-2.R, WP-3.R) may delete a v2 test project only when **every row for that project is satisfied**: the named v3 tests exist and pass, or the retirement reason is accepted in review.

| Disposition | Meaning |
|-------------|---------|
| **Port** | The scenario carries over almost unchanged; only the API shape differs |
| **Rewrite** | The concept survives, but v3 semantics differ, so the tests are re-derived |
| **Kit** | Replaced by storage or transport conformance kit cases, which run on every adapter |
| **Retire** | The feature is removed or the test only tested a test harness. The reason is given |

## 1. `tests/SourceFlow.Core.Tests` (NUnit, 237 tests, 28 files) → WP-1.R

**Retired 2026-11-11.** Every named v3 test exists and passes, except two:

- **E2E-08:** projection rebuild. The v2 replay concept moves to the blue/green rebuild, which ships with the CLI (WP-4.1) and is tracked there.
- **SCK-36:** covered by SCK-34.

`src/SourceFlow` and this project are deleted. The v2 cloud projects that reference `src/SourceFlow` (`src/SourceFlow.Cloud.AWS`, `src/SourceFlow.Cloud.GCP`) remain only as reference input for M3 and are deleted by WP-3.R.

| v2 file (tests) | Disposition | v3 tests | Notes |
|-----------------|-------------|----------|-------|
| `E2E/E2E.Tests` (1 scenario) | **Port** | E2E-01, E2E-02, E2E-08 | The bank-account domain becomes `samples/Bank` (WP-1.14). Replay becomes a rebuild |
| `Middleware/CommandDispatchMiddlewareTests` (9) | **Port** | UT-MW-01..11, UT-CMD-24 | Nesting, short-circuit, exceptions, double-`next`. Metadata mutation becomes immutable-envelope + `Items` |
| `Middleware/CommandSubscribeMiddlewareTests` (8) | **Port** | UT-MW-01..08 | Stage = Handle/Receive |
| `Middleware/EventDispatchMiddlewareTests` (8) | **Port** | UT-MW-01..10 | Stage = Publish |
| `Middleware/EventSubscribeMiddlewareTests` (14) | **Port** | UT-MW-01..09, UT-SUB-13 | Stage = Project; aggregate and projection variants merge |
| `Impl/CommandBusTests` (21) | **Rewrite** | UT-CMD-01, 02, 24, 26, 31 | Sequencing becomes compare-and-set; "replay doesn't append" becomes "load is a pure fold" (GAP-06) |
| `Impl/CommandPublisherTests` (5) | **Rewrite** | UT-CMD-05, 27 | "Requires entity id" becomes default-id rejection (GAP-20) |
| `Aggregates/EventSubscriberTests` (13) | **Rewrite** | UT-SUB-01..07, UT-PM-01..09, UT-MW | Aggregates no longer subscribe; process managers and handlers do, through durable subscriptions |
| `Projections/EventSubscriberTests` (13) | **Rewrite** | UT-PRJ-01..06, UT-MW | In-memory fan-out becomes checkpointed projections |
| `Sagas/CommandSubscriberTests` (10) | **Rewrite** | GEN-02..03, UT-CMD-01 | Saga matching becomes generated `Decide` dispatch |
| `Sagas/SagaTests` (9) | **Rewrite** | UT-ABS-03, 12, GEN-03, UT-PM-10..13 | `Publish/Raise` guards → `Decision`/`Reaction` guards |
| `Ioc/IocExtensionsTests` (12) | **Rewrite** | UT-HOST-01..08 | Generated modules, no scanning |
| `Messaging/CommandTests` (3), `EventTests` (2), `MetadataTests` (3) | **Rewrite** | UT-MSG-26..30, UT-ABS-01..09 | Records + envelope replace payload wrappers and mutable metadata |
| `Cloud/CloudTelemetryTests` (11) | **Rewrite** | UT-MW-15..20, UT-RX-17..19, UT-DIAG | Trace context in the CloudEvents envelope |
| `Cloud/InMemoryDeadLetterStoreTests` (12) | **Rewrite** | UT-DLQ-01..10, SCK-49..50 | |
| `Cloud/SensitiveDataMaskerTests` (12) | **Rewrite** | UT-RED-01..06, UT-PII | Bespoke masker → M.E.Compliance.Redaction (ADR-013) |
| `Cloud/InMemoryIdempotencyServiceTests` (10) | **Kit** | SCK-34..37, UT-STO-08..09 | TTL key store → transactional inbox (ADR-006) |
| `Aggregates/AggregateTests` (7) | **Retire** | (UT-CMD-23, 27 cover sending) | The `Aggregate<T>` façade is removed |
| `Cloud/CircuitBreakerTests` (16) | **Retire** | (TCK-32..34, UT-OUT-05) | Custom breaker replaced by Polly (ADR-013); Polly is tested upstream |
| `Cloud/PolymorphicJsonConverterTests` (7) | **Retire** | (UT-MSG-10) | No type discriminators or assembly-qualified names on the wire (ADR-005) |
| `Impl/AggregateFactoryTests` (3) | **Retire** | — | Aggregates aren't DI services (GAP-08) |
| `Impl/AggregateSubscriberTests` (4) | **Retire** | (UT-SUB) | Constructor-guard and logging-only tests of a removed component |
| `Impl/EventQueueTests` (14) | **Retire** | (UT-MW, UT-SUB-01) | No in-memory event queue; events are appended and delivered by subscription |
| `Impl/ProjectionSubscriberTests` (4) | **Retire** | — | Logging-only tests |
| `Impl/SagaDispatcherTests` (3) | **Retire** | — | Component removed |
| `Interfaces/IViewModelRepositoryTests` (2) | **Retire** | — | Tested a mock of an interface |

**Tally**: Port 5 · Rewrite 13 · Kit 1 · Retire 9. All 237 scenarios are accounted for.

## 2. `tests/SourceFlow.Stores.EntityFramework.Tests` (NUnit, 52 tests, 7 files) → WP-2.R

**Retired 2026-11-11.** Every named v3 test exists and passes. SCK cases run on in-memory, PostgreSQL and PostgreSQL with RLS, and UT-EF-01..10 run on PostgreSQL. E2E-08 follows with the rebuild tooling (WP-4.1). `src/SourceFlow.Stores.EntityFramework` and this project are deleted.

| v2 file (tests) | Disposition | v3 tests | Notes |
|-----------------|-------------|----------|-------|
| `E2E/E2E.Tests` (1 scenario) | **Port** | E2E-01, 02, 08 on PostgreSQL (and SQL Server from M4) | |
| `Stores/EfCommandStoreIntegrationTests` (7) | **Kit** | SCK-01, 09, 10, 11 | The command log becomes the event log |
| `Unit/EfIdempotencyServiceTests` (9) | **Kit** | SCK-34..37 | Concurrency and expiry semantics are kept in the inbox |
| `Stores/EfViewModelStoreIntegrationTests` (11) | **Rewrite** | UT-PRJ-01..06, UT-EF-01..10, SCK-61..64 | View models become versioned documents or EF read models |
| `Configutaion/ConnectionStringConfigurationTests` (5) | **Rewrite** | UT-HOST-01..08, SCK-M01..06 | `UsePostgreSql`/`UseSqlServer`; separate read-model databases (async only) |
| `Unit/SourceFlowEfOptionsTests` (7) | **Rewrite** | UT-RT-15..20 | Options validation with `ValidateOnStart` |
| `Stores/EfEntityStoreIntegrationTests` (11) | **Retire** | (SCK-15, 17) | No entity store: state is a fold over events. Not-found semantics pinned in SCK-17 |

**Tally**: Port 1 · Kit 2 · Rewrite 3 · Retire 1.

## 3. `tests/SourceFlow.Cloud.GCP.Tests` (xUnit, 14 tests, 5 files) → WP-3.R

| v2 file (tests) | Disposition | v3 tests |
|-----------------|-------------|----------|
| `Unit/GcpIocExtensionsTests` (5) | **Rewrite** | UT-GCP-01..05, UT-HOST |
| `Unit/GcpOptionsTests` (1) | **Rewrite** | UT-GCP-06..08 |
| `Unit/PubSubCommandDispatcherTests` (2) | **Rewrite** | UT-GCP-09..12, UT-CMD-14 |
| `Unit/PubSubEventDispatcherTests` (2) | **Rewrite** | UT-GCP-13..15 |
| `Integration/PubSubRoundTripTests` (4) | **Kit** | TCK-07, 10, 12, 35 (idempotent bootstrap) |

> **Satisfied (WP-3.R).** Every row has its v3 tests, and they pass:
> - UT-GCP-01..25 and the UT-HOST suite.
> - UT-CMD-14, added for this check: an event without a publication route is not enqueued.
> - TCK-07, 10, 12 and 35 on the Pub/Sub emulator.
>
> `src/SourceFlow.Cloud.GCP` and `tests/SourceFlow.Cloud.GCP.Tests` are deleted. The v2 `GCP-Build` workflow and `.github/gcp-emulator` stay for `master`, as plan 08 §1 says.

## 4. `tests/SourceFlow.Cloud.AWS.Tests` (xUnit, 264 facts + 22 properties + 4 benchmarks) → WP-3.R

### 4.1 Unit (19 files)

| v2 file (tests) | Disposition | v3 tests | Notes |
|-----------------|-------------|----------|-------|
| `AwsBusBootstrapperTests` (9) | **Rewrite** | UT-AWS (provisioner plan), TCK-35..40 | `…UsesFirstQueueForSubscriptions` is **inverted**: UT-AWS-05 asserts one receiver per endpoint with raw delivery (GAP-02) |
| `BusConfigurationTests` (13) | **Rewrite** | UT-RT-01..14, UT-TOP-01..05 | |
| `AwsSqsCommandDispatcherTests` (4), `…Enhanced` (8) | **Rewrite** | UT-AWS (sender), UT-CMD-23, UT-RED | Circuit breaker → Polly; masker → redaction; encryption → TCK-41 |
| `AwsSnsEventDispatcherTests` (4), `…Enhanced` (6) | **Rewrite** | UT-AWS (publish), UT-CMD-12, 14 | |
| `AwsSqsCommandListenerTests` (5), `…Enhanced` (5) | **Rewrite** | UT-RX-01..07 | Behaviour moves to the core receive pipeline |
| `AwsSnsEventListenerTests` (4), `…Enhanced` (4) | **Rewrite** | UT-RX-01, 02, 04, 13 | |
| `AwsDeadLetterQueuePropertyTests` (9) | **Rewrite** | UT-DLQ-01..10, TCK-23..25 | |
| `AwsHealthCheckTests` (4) | **Rewrite** | UT-HC, UT-AWS | |
| `AwsKmsMessageEncryptionTests` (8) | **Rewrite** | UT-SEC (AwsKms) | WP-5.1 |
| `IocExtensionsTests` (2) | **Rewrite** | UT-AWS-01..04, UT-HOST | |
| `PropertyBasedTests` (6 properties) | **Rewrite** | UT-MSG-08 (CsCheck), TCK-01..04, UT-RT-01..04 | `AwsCiCdIntegrationReliability` retired (harness) |
| `AwsResiliencePatternPropertyTests` (1) | **Kit** | TCK-32..34 | |
| `AwsJsonConverterTests` (8) | **Retire** | (UT-MSG-08, 09) | Payload and metadata converters existed only for assembly-qualified-name typing |
| `AwsPerformanceMeasurementPropertyTests` (1) | **Retire** | — | Measured the test harness |
| `LocalStackEquivalencePropertyTest` (1) | **Retire** | — | Harness self-test |

### 4.2 Integration (31 files)

| v2 file (tests) | Disposition | v3 tests |
|-----------------|-------------|----------|
| `SqsStandardIntegrationTests` (6) | **Kit** | TCK-07, 19..22; PERF-07 |
| `SqsFifoIntegrationTests` (6) | **Kit** | TCK-13..18 |
| `SqsBatchOperationsIntegrationTests` (7) | **Kit** | TCK-07..09 |
| `SqsMessageAttributesIntegrationTests` (6) | **Kit** | TCK-01..06 |
| `SqsMessageProcessingPropertyTests` (1) | **Kit** | TCK-19..22 |
| `SqsDeadLetterQueueIntegrationTests` (2), `…PropertyTests` (1) | **Kit** | TCK-23..25 |
| `SnsTopicPublishingIntegrationTests` (7) | **Kit** | TCK-05, 06, 10, 11, 41 |
| `SnsFanOutMessagingIntegrationTests` (4) | **Kit** | TCK-12 |
| `SnsMessageFilteringIntegrationTests` (5), `SnsMessageFilteringAndErrorHandlingPropertyTests` (2) | **Kit** | TCK-26..28 |
| `SnsCorrelationAndErrorHandlingTests` (5) | **Kit** | TCK-23, 29..31 |
| `SnsEventPublishingPropertyTests` (2) | **Kit** | TCK-10, UT-MSG-08 |
| `AwsDeadLetterQueueProcessingTests` (8) | **Kit** | TCK-23..25, UT-DLQ |
| `AwsServiceThrottlingAndFailureTests` (15) | **Kit** | TCK-32..34, CH-07 |
| `AwsRetryPolicyTests` (13) | **Rewrite** | UT-AWS-12 (SDK retry options honoured, GAP-03), TCK-32, UT-OUT-05 |
| `AwsHealthCheckIntegrationTests` (22), `AwsHealthCheckPropertyTests` (1) | **Rewrite** | UT-HC, UT-SEC (KMS health), TCK-35 |
| `KmsEncryptionIntegrationTests`, `KmsEncryptionRoundTripPropertyTests`, `KmsKeyRotationIntegrationTests`, `KmsKeyRotationPropertyTests`, `KmsSecurityAndPerformanceTests`, `KmsSecurityAndPerformancePropertyTests` | **Rewrite** | UT-SEC-01..20, TCK-41..43, nightly real KMS; masking parts → UT-RED |
| `AwsCircuitBreakerTests` (12) | **Retire** | (TCK-32..34) — Polly (ADR-013) |
| `AwsIntegrationTests` (1) | **Retire** | (UT-AWS-12) — trivial options check |
| `EnhancedAwsTestEnvironmentTests` (11), `EnhancedLocalStackManagerTests` (12), `LocalStackIntegrationTests` (5), `LocalStackPreservationPropertyTests` (9), `LocalStackCITimeoutExplorationTests` (3) | **Retire** | Harness self-tests, replaced by Testcontainers fixtures and the emulator smoke job (WP-0.3) |

### 4.3 Security, performance, helpers

| v2 file(s) | Disposition | v3 tests | Notes |
|------------|-------------|----------|-------|
| `Security/IamRoleTests` (6), `IamSecurityPropertyTests` (5) | **Retire** | TCK-34 (credential refresh), TCK-36 (`Validate` with a describe-only role, real AWS nightly), UT-TOP (policy export) | Evaluating IAM policy is AWS's job, not the framework's. LocalStack IAM enforcement is partial |
| `Performance/SqsPerformanceBenchmarks`, `SnsPerformanceBenchmarks`, `AwsScalabilityBenchmarks` | **Rewrite** | PERF-07, PERF-09 in `benchmarks/` | |
| `TestHelpers/*` (23 files) | **Retire** | — | Superseded by `SourceFlow.Testing` fixtures, the TCK fixture contract and the resource sweeper. Resource-naming and cleanup ideas are reused in the TCK fixture |

> **Not yet satisfied (WP-3.R).** Some rows name v3 tests that belong to later work packages:
> - UT-SEC: KMS payload protection, WP-5.1.
> - PERF-07, PERF-09 and CH-07: load and chaos, WP-4.7.
>
> UT-HC exists since WP-4.3. `src/SourceFlow.Cloud.AWS` and `tests/SourceFlow.Cloud.AWS.Tests` therefore stay until the others exist. They are not in the solution and do not build against v3.

**Tally (AWS)**: Unit: Rewrite 15 · Kit 1 · Retire 3. Integration: Kit 15 · Rewrite 9 · Retire 7. Plus Security Retire 2 · Performance Rewrite 3 · Helpers Retire 23.

## 5. `origin/azure-cloud` `tests/SourceFlow.Cloud.Azure.Tests` → WP-3.4 input; branch archived at WP-3.R

| v2 file group | Disposition | v3 tests |
|---------------|-------------|----------|
| `Unit/AzureServiceBusCommandDispatcherTests`, `…EventDispatcherTests`, `AzureBusBootstrapperTests`, `AzureIocExtensionsTests` | **Rewrite** | UT-ASB-01..25 |
| `Unit/DependencyVerificationTests` | **Rewrite** | ARCH-02, ARCH-03 |
| `Integration/ServiceBusCommandDispatching(Property)Tests`, `ServiceBusEventPublishingTests` | **Kit** | TCK-07..11 |
| `Integration/ServiceBusEventSessionHandlingTests` | **Kit** | TCK-13..15 († where the emulator diverges) |
| `Integration/ServiceBusSubscriptionFiltering(Property)Tests` | **Kit** | TCK-26..28 |
| `Integration/ServiceBusHealthCheckTests`, `KeyVaultHealthCheckTests` | **Rewrite** | UT-HC, UT-SEC |
| `Integration/KeyVaultEncryption(Property)Tests` | **Rewrite** | UT-SEC (AzureKeyVault), TCK-41..43 |
| `Integration/ManagedIdentityAuthenticationTests` | **Rewrite** | UT-ASB (credential selection) + nightly real Azure |
| `Integration/AzureConcurrentProcessing(Property)Tests`, `AzureAutoScaling(Property)Tests` | **Rewrite** | E2E-12, PERF-09 |
| `Integration/AzurePerformanceBenchmarkTests`, `AzurePerformanceMeasurementPropertyTests` | **Rewrite** | PERF-07, PERF-09 |
| `Integration/AzureMonitorIntegrationTests`, `AzureTelemetryCollectionPropertyTests` | **Rewrite** | UT-DIAG, UT-OTEL |
| `Integration/AzureCircuitBreakerTests` | **Retire** | Polly (ADR-013) |
| `Integration/AzureTestResourceManagementPropertyTests`, `AzuriteEmulatorEquivalencePropertyTests`, `TestHelpers/*` | **Retire** | Harness self-tests. The emulator configs are reused in `.github/emulators` |

## 6. Overall

| Source | Files with tests | Port | Rewrite | Kit | Retire |
|--------|------------------|------|---------|-----|--------|
| Core | 28 | 5 | 13 | 1 | 9 |
| EF | 7 | 1 | 3 | 2 | 1 |
| GCP | 5 | 0 | 4 | 1 | 0 |
| AWS (unit + integration + security + perf) | 55 | 0 | 27 | 16 | 12 |
| **Total on `master`** | **95** | **6** | **47** | **20** | **22** |

No v2 behaviour that v3 keeps is left without a v3 test. Each retired behaviour has a stated reason, and where it is relevant, a v3 test proving the replacement (Polly, inbox, redaction, removed façades).
