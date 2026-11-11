# 06 — Integration and End-to-End Test Plan

## 1. Targets and fixtures

| Target | Fixture | PR | Nightly |
|--------|---------|----|---------|
| In-memory store / transport | none | ✔ | ✔ |
| PostgreSQL | Testcontainers `postgres:17` (assembly fixture, one database per test class) | ✔ (17) | ✔ (15, 16, 17 + `PartitionEvents` variant) |
| SQL Server | Testcontainers MsSql 2022 | ✔ (from M4) | ✔ (2022, 2025) |
| AWS | LocalStack image `3` (pinned, from v2 CI) | ✔ | real AWS account (`RealCloud`) |
| Google Cloud | Pub/Sub emulator (v2 `.github/gcp-emulator`) | ✔ | real GCP project |
| Azure | Service Bus emulator + SQL Edge (`azure-cloud` `.github/azure-emulator`) | ✔ | real Azure namespace |
| KMS | LocalStack KMS | ✔ | AWS KMS, Azure Key Vault, Google Cloud KMS |

Isolation: every test uses a unique prefix (`t{testId}{random}`) for schemas, queues, topics and subscriptions. The nightly sweeper deletes stale resources (plan 04 §8).

## 2. Storage Conformance Kit (SCK)

`StorageConformanceTests` lives in `SourceFlow.Testing` and runs against in-memory, PostgreSQL, PostgreSQL-partitioned and SQL Server. **A storage adapter is done only when 100 % of mandatory cases pass.**

| ID | Case | Covers | v2 lineage |
|----|------|--------|------------|
| SCK-01 | Append `NoStream` to a new stream → versions 1..n | ADR-008 | → `EfCommandStore.Append_ValidCommand_StoresCommandInDatabase` |
| SCK-02 | Append `Exact(v)` succeeds; stale `Exact` → `ConcurrencyException` | ADR-008 | new |
| SCK-03 | `NoStream` on an existing stream → `ConcurrencyException` | ADR-008 | new |
| SCK-04 | `Any` / `StreamExists` implemented as read + compare-and-set (never sent raw) | doc 05 §3.1 | new |
| SCK-05 | Conflict rolls back **every** queued write (outbox, inbox, documents, checkpoint, process state) | ADR-006 | new |
| SCK-06 | ES-R1: a session that already wrote → append rejected | ES-R1 | new |
| SCK-07 | ES-R2: second stream in a session rejected | ES-R2 | new |
| SCK-08 | Unique `(tenant, stream, version)` backstop maps to `ConcurrencyException` | doc 05 §3.1 | → `CommandRecord` unique index |
| SCK-09 | 1, 3, 100 events in one append; version contiguity | — | → `Append_MultipleCommands_StoresAllCommandsInOrder` |
| SCK-10 | Read stream from a version; up-to-version bound; empty stream → empty | — | → `Load_NonExistentEntity_ReturnsEmptyList` |
| SCK-11 | Streams of different aggregates are isolated | — | → `Append_CommandsForDifferentEntities_StoresSeparately` |
| SCK-12 | Metadata (correlation, causation, actor, trace) round trip | doc 05 §2 | new |
| SCK-13 | Tags persisted and returned | ADR-021 | new |
| SCK-14 | Decision time persisted as event `time` | doc 14 §5.1 | new |
| SCK-15 | Deleted stream rejects appends | doc 05 §9 | → `EfEntityStore.Delete_*` (concept) |
| SCK-16 | Archived stream rejects appends; still readable | doc 05 §9 | new |
| SCK-17 | "Not found" semantics pinned (empty slice; `GetStreamInfo` → null) | GAP-15 | → `Get_NonExistentEntity_ThrowsInvalidOperationException` (semantics changed) |
| SCK-18 | Large payload (1 MB) round trip | — | new |
| SCK-19 | Unicode in data and metadata | — | new |
| SCK-20 | One round trip per commit (adapter command counter) | NFR HP-5 | new |
| SCK-21 | Log read in commit order with an exclusive `after` position | ADR-010 | new |
| SCK-22 | Bucket-range filter correctness | doc 06 §3 | new |
| SCK-23 | Safe horizon excludes in-flight transactions | ADR-010 | new |
| **SCK-24** | **Gap-freedom (property-based, CsCheck)**: 32 writers with random commit delays and one reader checkpointing continuously → the reader sees exactly the set of committed events, each once | ADR-010 | new |
| SCK-25 | Per-stream order in the log equals version order (the §5.3 proof as a test) | ES-R1 | new |
| SCK-26 | Horizon age is reported while a long write transaction is open | doc 05 §5.4 | new |
| SCK-27 | Notifications wake a waiting reader after commit | doc 06 §2 | new |
| SCK-28 | Notification connection loss → fallback to polling, no missed events | — | new |
| SCK-29..33 | Outbox: enqueue atomic with the append; head-of-key claim; fail/backoff; complete needs the lease; partition stability | ADR-006 | new |
| SCK-34..37 | Inbox: record with result; duplicate at commit → `DuplicateMessage`; seen returns the result; batched retention cleanup | ADR-006 | → `EfIdempotencyServiceTests` (9) |
| SCK-38..41 | Checkpoints: save with a valid lease; stale token rejected; expired lease rejected; checkpoint and document writes atomic | doc 05 §7.3 | new |
| SCK-42..45 | Leases: acquire, renew, steal after expiry (token increments), release; database clock only | doc 05 §7.4 | new |
| SCK-46..48 | Process state: load, save with version, concurrent save conflict, completed flag | ADR-020 | new |
| SCK-49..50 | Dead letters: park with full envelope, query, mark replayed, discard | GAP-11 | → `InMemoryDeadLetterStoreTests` |
| SCK-51..55 | Snapshots: save/get, hash invalidation, load = snapshot + tail equals a full fold | doc 05 §8 | new |
| SCK-56..60 | Tenancy: tenant-scoped streams, documents, inbox; RLS on → a cross-tenant read returns nothing even with a forged query | ADR-022 | new |
| SCK-61..64 | EF Core projection: `SaveChanges` and checkpoint in one transaction; fencing failure rolls back the EF changes | doc 05 §10 | → `EfViewModelStoreIntegrationTests` |
| SCK-L01 | Linearisability (CsCheck `SampleParallel`). Parallel `Any` appends, `NoStream` appends and appends with outbox rows must equal the model under some sequential ordering, which checks the compare-and-set and append/outbox atomicity under concurrency. Runs on in-memory, PostgreSQL, and PostgreSQL with RLS. | ADR-006, ADR-008 | new |
| SCK-73..75 | Read-model aliases: first claim once, compare-and-set switch recording the previous target, requested generation kept until switched, concurrent switches with one winner | doc 06 §6, ADR-032 | new |
| SCK-65..72 | Partitioned events variant: append across a partition boundary; horizon read across partitions; detach an old partition; archive exclusion rules | doc 09 §5 | new |
| SCK-M01..M06 | Schema: fresh install; upgrade from every prior version; concurrent migrators serialise; `AutoMigrate` off outside Development; runtime role has DML only; `db script` output equals applied DDL | doc 05 §11 | → `DbContextMigrationHelper` problems (EF-06) |

## 3. Transport Conformance Kit (TCK)

`TransportConformanceTests` lives in `SourceFlow.Testing` and runs against in-memory, AWS (LocalStack, then real), Google Cloud (emulator, then real) and Azure (emulator, then real).

As built ([ADR-033](../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)):
- TCK-18, 24, 25 and 45 start real services on the transport under test, using the kit's generated `TckModule`.
- TCK-32..34 need fault injection (`ITransportFixture.Faults`) and are skipped on real cloud.
- TCK-37..40 use `InjectDefectAsync`.
- TCK-41..43 cover the transport side of payload protection; decryption and `DecryptionFailed` parking come with WP-5.1.
- TCK-00 checks the case list. Cases marked † may be `RealCloudOnly` with a written reason when an emulator diverges (as was already the case for two Azure session tests on `azure-cloud`).

| ID | Case | v2 scenario sources |
|----|------|---------------------|
| TCK-01..04 | Envelope round trip, binary and structured; every CloudEvents attribute and extension; Unicode; `sf_ext` overflow | `SqsMessageAttributesIntegrationTests`, `PropertyBasedTests.MessageAttributePreservation` |
| TCK-05..06 | Maximum-size body accepted; over-size rejected **before** send | `SnsTopicPublishingIntegrationTests.PublishEvent_WithLargeMessage…`, `MessageAttributes_ShouldRespectSizeLimits` |
| TCK-07..09 | Send to an endpoint; batch send with partial failure reported per message; batch limits respected | `SqsBatchOperationsIntegrationTests` (7), `SqsStandardIntegrationTests` |
| TCK-10..11 | Publish to a topic; publish to a missing topic → error under `Validate` | `SnsTopicPublishingIntegrationTests` |
| **TCK-12** | **Fan-out: 2 subscribing services × 2 instances each → every service receives each event exactly once, instances compete** (GAP-13 regression on Google Cloud; GAP-02 on AWS) | `SnsFanOutMessagingIntegrationTests`, GCP `PubSubRoundTripTests` |
| TCK-13..15 | Ordered endpoint: 10,000 messages over 100 keys and 3 consumers keep per-key order; unordered endpoint has no head-of-line blocking; ordering survives redelivery | `SqsFifoIntegrationTests.FifoQueue_ShouldMaintainMessageOrderingWithinMessageGroups`, Azure session tests † |
| TCK-16..18 | Broker deduplication (where supported) plus inbox: a producer retry with the same `MessageId` → one effect | `SqsFifoIntegrationTests.FifoQueue_ShouldHandleContentBasedDeduplication` |
| TCK-19..22 | Settlement: complete removes; `RetryLater(delay)` redelivers after delay ± tolerance; renew extends in-flight time; a crash before complete → redelivery | `SqsStandardIntegrationTests.StandardQueue_ShouldGuaranteeAtLeastOnceDelivery`, `AwsRetryPolicyTests` |
| TCK-23..25 | Native DLQ backstop after max deliveries; the ingestor moves the message to `dead_letters` with headers intact; replay from the CLI | `SqsDeadLetterQueueIntegrationTests`, `AwsDeadLetterQueueProcessingTests` (8) |
| TCK-26..28 | Subscription filters: CESQL `type LIKE …` translated natively (SNS policy, ASB SQL, Pub/Sub filter) or evaluated client-side, with identical results | `SnsMessageFilteringIntegrationTests` (5), Azure `ServiceBusSubscriptionFilteringTests` |
| TCK-29..31 | Correlation and causation preserved across a chain of hops; trace context continues | `SnsCorrelationAndErrorHandlingTests` |
| TCK-32..34 | Resilience: broker unavailable for 60 s → receiver recovers without restart; throttling → backoff; credentials refresh | `AwsServiceThrottlingAndFailureTests` (15), `IamRoleTests.IamCredentials_ShouldRefresh…` |
| TCK-35..40 | Provisioning: `CreateIfMissing` is idempotent; `Validate` detects a missing queue or topic, FIFO mismatch, missing raw delivery, missing DLQ, ordering disabled, filter drift — with a precise diff | `AwsBusBootstrapperTests`, GCP `Bootstrap_Is_Idempotent_On_Repeated_Runs`, Azure `AzureBusBootstrapperTests` |
| TCK-41..43 | Payload protection on: encrypt on send, decrypt on receive; tamper → parked `DecryptionFailed` | `KmsEncryptionIntegrationTests`, `KeyVaultEncryptionTests` |
| TCK-44 | Graceful shutdown: in-flight drained, nothing acknowledged without completion | new |
| TCK-45 | End to end with the core: remote command appended once under injected duplicates | `AwsIntegrationTests` (concept) |

v2 suites **not** carried into the TCK: the LocalStack harness self-tests, the CI timeout exploration and IAM policy-syntax tests. Their reasons are in [plan 07](07-V2-Test-Disposition-Matrix.md).

## 4. End-to-end scenarios (`SourceFlow.EndToEnd.Tests`, Bank sample)

| ID | Scenario | Store × transport | Covers | v2 lineage |
|----|----------|-------------------|--------|------------|
| E2E-01 | Account lifecycle: open → deposit → withdraw → close; summary projection (inline) and branch totals (async) correct | mem × mem; PostgreSQL × mem | ADR-002, 016 | → Core + EF `EndToEnd_AccountLifecycle_WorksAsExpected` |
| E2E-02 | Business rejections (insufficient funds, closed account) append nothing and return errors | same | ADR-018 | same |
| E2E-03 | Idempotency key: the same deposit twice → one event, the second returns `Duplicate` with the original version | same | ADR-006 | new |
| **E2E-04** | **50 parallel deposits on one account → all accepted after re-decides; balance exact; conflicts metric > 0** | same | ADR-008, GAP-05 | new |
| E2E-05 | `If-Match`-style `ExpectedVersion` mismatch → `Conflict`, nothing appended | same | doc 03 §5.1 | new |
| E2E-06 | Read-your-writes: a query with the token from the command sees the async projection updated | same | doc 14 §6 | new |
| E2E-07 | Temporal read: balance as of version 2 and as of month end | same | doc 03 §5.4 | new |
| E2E-08 | Rebuild: bump the projection version → the new generation catches up → the alias switches; no side effects (handlers not re-run) | same | ADR-016, GAP-06 | → `CommandBus.Replay_*` (replaced concept) |
| E2E-09 | Crash after commit, before projection → the projection catches up after restart (durability) | PostgreSQL × mem | ADR-016 | new |
| E2E-10 | Poison event in a projection (Stop) → that partition stalls, others continue; skip via CLI recovers | same | doc 06 §7 | new |
| **E2E-11** | **4-partition async projection over 1,000 accounts → each event applied exactly once** | mem, PostgreSQL | ADR-016 | new |
| E2E-12 | Two nodes share subscriptions; kill one → the other takes over within the lease TTL; no double application | PostgreSQL | doc 06 §4 | new |
| E2E-13 | Money transfer happy path: request → withdraw → deposit → complete | mem, PostgreSQL | ADR-020 | new |
| **E2E-14** | **Transfer timeout (`FakeTimeProvider` +5 min) → compensation sent; process completed** | same | ADR-020 | new |
| E2E-15 | Transfer with an insufficient-funds rejection → `CommandRejected<Withdraw>` reply → failed transfer | same | ADR-018, 020 | new |
| E2E-16 | Middleware: a tenant middleware + validation middleware in the Handle and Receive stages | mem | ADR-012 | → middleware suites |
| E2E-17..20 | Cross-service: Accounts publishes `AccountOpenedV1` → Notifications projects recipients (one subscription per service) | PostgreSQL × {mem, AWS, Google Cloud, Azure} | RT-R4, GAP-13 | → `SnsFanOut…`, GCP round trip |
| E2E-21..24 | Remote command: Accounts routes `RequestStatement` → **Notifications owns, appends to its own stream, and can replay it**; duplicate delivery → one effect | PostgreSQL × {mem, AWS, Google Cloud, Azure} | ADR-007, GAP-19 | → `AwsSqsCommandListener*` |
| E2E-25..28 | CLI against PostgreSQL: `dlq replay`, `projection rebuild`, `topology validate`, `query --explain` | PostgreSQL × mem | doc 04 §9.3, doc 14 | new |
| E2E-29..31 | Erasure: erase a customer → events read `[erased]`; the summary is scrubbed; a SFQL filter on a PII field is rejected | PostgreSQL × mem | doc 10 §4 | new |

## 5. Chaos (`SourceFlow.EndToEnd.Tests`, `Category=Chaos`, nightly)

| ID | Fault | Expected |
|----|-------|----------|
| CH-01..06 | Kill the process at each `IFaultInjector` point (`session.beforeCommit`, `afterCommitBeforeAck`, `relay.afterPublishBeforeDelete`, `subscription.afterApplyBeforeCommit`, `lease.beforeRenew`, `receiver.beforeComplete`) | No loss; duplicates absorbed; projections converge |
| CH-07 | Broker outage for 10 min under load | Commands keep succeeding; `outbox.age` grows then drains in order |
| CH-08 | PostgreSQL primary restart | Fail fast; resume from checkpoints; no duplicate effects |
| CH-09 | SIGSTOP a node longer than the lease TTL | Leases move; the resumed node's commits fail on fencing |
| CH-10 | A long write transaction held for 2 min | Subscriptions pause; the `horizon_age` alert fires; nothing is skipped after release |

## 6. Migration (`SourceFlow.Tooling.Tests`, MIG)

A fixture creates a **v2 database** with the v2 `CommandRecord` DDL and entity tables, seeded by replaying the v2 E2E account scenario rows as SQL (no v2 code reference).

| ID | Case |
|----|------|
| MIG-01..02 | `plan` counts entities and commands; reports commands lost to GAP-04 (no parameterless ctor) and GAP-20 (id 0) |
| MIG-03 | `scaffold` generates an `IV2Import` stub per entity type |
| MIG-04..06 | `import` is idempotent (`NoStream`), resumable after interruption, one session per stream |
| MIG-07 | `archive` copies `CommandRecord` to `sourceflow_v2_archive.commands` |
| MIG-08..09 | `verify` compares rebuilt v3 state with v2 entity rows; a mismatch report fails the exit code |
| MIG-10..12 | v2 wire bridge: a v2 AWS message with a mapped `CommandType` is accepted with a deterministic id; an unmapped one is parked; the bridge is off by default |

## 7. Query (QRY)

| ID | Case | Where |
|----|------|-------|
| QRY-01..20 | Every doc 14 §10 example (A–F), plus domain queries, against the in-memory interpreter with expected rows | `SourceFlow.Core.Tests` |
| QRY-21..35 | Golden SQL (approved files) for PostgreSQL and SQL Server per example; parameterisation (no literals in SQL text) | `SourceFlow.Storage.Tests` (`Provider=PostgreSQL\|SqlServer`) |
| QRY-36..40 | **Cross-provider equivalence**: the same query over the same seeded data → identical rows (in-memory, PostgreSQL, SQL Server) | storage test projects |
| QRY-41..43 | Tenancy: implicit tenant predicate; RLS backstop; `tenants = 'all'` needs a permission | PostgreSQL |
| QRY-44..45 | Consistency: `horizon` pages are monotonic; `at_least(token)` waits on a replica checkpoint | PostgreSQL (primary + replica container) |
| QRY-46..49 | **Joins**: inner and left joins of read models, events ↔ read model (`aggregatekey`), lookup sugar; golden SQL per provider | in-memory, PostgreSQL, SQL Server |
| QRY-50..52 | **Grouping and ordering**: `GROUP BY`/`HAVING`, `DISTINCT`, `count(distinct)`, `NULLS FIRST`/`LAST`, `IN`/`EXISTS` subqueries — cross-provider equivalence (identical rows) | in-memory, PostgreSQL, SQL Server |
| QRY-53..56 | **Per-service query API end to end** (Bank sample on PostgreSQL): SQL and pipeline syntax over HTTP, `/validate` doesn't execute (statement log empty), `/explain` shows placeholders only, read-your-writes token across the API | PostgreSQL + `WebApplicationFactory` |
| QRY-57..59 | **Injection corpus** over HTTP (doc 14 §12): OWASP and sqlmap payloads, stacked queries, comments, Unicode bidi and homoglyphs, NUL, JSON bombs, XSS in parameters and stored data → each rejected (SFQ01xx/02xx) or executed with values as parameters; **zero database errors**; no unescaped `<script` in any response; database role audit shows no non-`SELECT` statement | PostgreSQL, SQL Server |
| QRY-60 | **No-literal property** (CsCheck): random valid ASTs with random literals → the generated SQL text contains no literal substring on any provider | in-memory compiler harness, PostgreSQL, SQL Server |

## 8. Performance (PERF)

| ID | Benchmark | Target (doc 09 §7.1) |
|----|-----------|----------------------|
| PERF-01 | In-process command (in-memory, 1 event) | p50 ≤ 20 µs, ≤ 2 KB/op |
| PERF-02 | Middleware overhead with 3 middleware | ≤ 1 µs, 0 B steady state |
| PERF-03 | PostgreSQL commit, 1/3/10 events + outbox | p99 ≤ 10 ms; 1 round trip |
| PERF-04 | Load with 0/100/1,000 events, with and without a snapshot | linear without, flat with |
| PERF-05 | Subscription document-projection rebuild | ≥ 20,000 events/s per partition |
| PERF-06 | Global log read | ≥ 50,000 events/s |
| PERF-07 | Outbox relay commit → broker acknowledgement (LocalStack, emulators) | p95 ≤ 250 ms |
| PERF-08 | Event → async projection latency (LISTEN/NOTIFY) | p95 ≤ 100 ms |
| PERF-09 | Commands/s per 4-vCPU instance (NBomber, distinct streams) | ≥ 5,000/s |
| PERF-10 | Startup to ready (JIT / AOT) | ≤ 150 ms / ≤ 50 ms |

## 9. Architecture (ARCH, `SourceFlow.Architecture.Tests`)

| ID | Rule |
|----|------|
| ARCH-01 | `SourceFlow.Net` references only the BCL, `Microsoft.Extensions.*`, Polly and `Microsoft.Extensions.Resilience` (no cloud SDK, database driver or OpenTelemetry SDK) |
| ARCH-02 | Core never references an adapter package (no reflection loading either; checked by scanning for `Type.GetType`/`Assembly.Load*` in core) |
| ARCH-03 | Adapter packages (`SourceFlow.Storage.*`, `SourceFlow.Cloud.*`, `SourceFlow.AspNetCore`) reference `SourceFlow.Net` and their own SDK only, never each other |
| ARCH-04 | Sample domain projects reference only `SourceFlow.Net` and use only the `SourceFlow` and `SourceFlow.Workflows` namespaces (no `Runtime`, `Storage`, `Transport`, `Security`, `Hosting` or `Diagnostics` types) |
| ARCH-05 | No `Activator.CreateInstance`, `MakeGenericMethod` or `Type.GetType(string)` in runtime packages (HP-1) |
| ARCH-06 | Trim and AOT analysis warnings = 0 for AOT-marked packages (`dotnet publish -p:PublishAot=true` of a sample) |
| ARCH-07 | Public types in adapters are `sealed` unless designed for extension |
| ARCH-08 | **Traceability**: every ID in `tests/traceability.csv` has at least one `[Trait("Covers", …)]` test |
