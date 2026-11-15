# 05 — Non-Functional Requirements

The status legend is in the [README](README.md#status-values). Where a requirement is about a guarantee, the **Current behaviour** column says what the code actually provides today, because the difference matters to anyone designing on top of it.

---

## 1. Compatibility and platform (`NFR-CMP`)

| ID | Requirement | Status / current behaviour |
|----|-------------|----------------------------|
| NFR-CMP-01 | The core package shall target .NET Standard 2.0 and 2.1 and .NET 8, 9 and 10, so that .NET Framework 4.6.2+ and modern .NET can both use it. | Implemented ([SourceFlow.csproj](../../src/SourceFlow/SourceFlow.csproj)). The README badge still lists .NET Framework 4.6.2 for v1 |
| NFR-CMP-02 | The EF package shall target .NET 8, 9 and 10. | Implemented. The README badges also claim .NET Standard 2.0/2.1 for this package, which the csproj does not target ([GAP-18](06-Gaps-Risks-and-Open-Questions.md#gap-18)) |
| NFR-CMP-03 | The AWS package shall target .NET Standard 2.1 and .NET 8, 9 and 10; the GCP package shall target .NET 8, 9 and 10. | Implemented |
| NFR-CMP-04 | Packages shall integrate with `Microsoft.Extensions.*` (DependencyInjection, Logging, Hosting, Options, HealthChecks) and add no other container or logging framework. | Implemented |
| NFR-CMP-05 | Breaking API or namespace changes shall only ship in a major version, with a migration guide in the changelog. | Implemented for v2.0.0 (Cloud.Core → `SourceFlow.Cloud.*`; see the [changelog](../legacy-version/Versions/v2.0.0/CHANGELOG.md)) |
| NFR-CMP-06 | Stored commands shall stay readable after commands or payloads are renamed, moved or given a new assembly version. | **Not met.** Types are stored and resolved by assembly-qualified name, and commands that fail to load are skipped silently ([GAP-04](06-Gaps-Risks-and-Open-Questions.md#gap-04)) |

## 2. Delivery and consistency guarantees (`NFR-REL`)

| ID | Requirement | Current behaviour |
|----|-------------|-------------------|
| NFR-REL-01 | **Per-entity ordering.** Commands for one entity shall be handled and logged in the order they were sent. | In-process: yes, provided callers do not publish concurrently for the same entity. AWS: yes with FIFO queues (`MessageGroupId = EntityId`). GCP: **not guaranteed** (GAP-13) |
| NFR-REL-02 | **Per-entity sequence integrity.** Concurrent writers to one entity shall never both apply a change. One shall win and the other shall be rejected **before** side effects. | **Partial.** The EF unique index rejects the second append, but only **after** its saga has already changed and persisted the entity and raised events ([GAP-05](06-Gaps-Risks-and-Open-Questions.md#gap-05)) |
| NFR-REL-03 | **Atomicity.** Persisting the entity and appending the command shall commit or fail together. Events shall be published only after the commit (outbox pattern or equivalent). | **Not met.** The order is: saga persists the entity → event raised → bus appends the command, each step separate. A failure partway leaves them inconsistent (GAP-05) |
| NFR-REL-04 | **Cloud delivery.** Inbound messages shall be processed at least once, and handlers shall see each logical message effectively once. | At-least-once: yes (ack/delete only after success). Effectively-once: only with the opt-in AWS enhanced listeners (GAP-01), and only per transport message id (GAP-09) |
| NFR-REL-05 | **Replay safety.** Replay shall have no external side effects: no cloud publishes and no new commands logged. | **Not met.** Cloud dispatchers do not check `IsReplay`, and commands published from sagas during replay are new, non-replay commands ([GAP-06](06-Gaps-Risks-and-Open-Questions.md#gap-06)) |
| NFR-REL-06 | **Failure visibility.** No failure on the main path shall be swallowed silently. | **Partial.** Saga event-raising errors are logged and swallowed (GAP-07). Command deserialisation errors are swallowed without a log (GAP-04). The AWS event listener carries on when one subscriber throws synchronously |
| NFR-REL-07 | **Graceful shutdown.** Listeners shall stop polling when the host is cancelled, and messages in flight shall go back to the transport rather than being lost. | Implemented. Cancellation ends the loops, and messages not yet acknowledged are redelivered |

## 3. Security (`NFR-SEC`)

| ID | Requirement | Status / current behaviour |
|----|-------------|----------------------------|
| NFR-SEC-01 | No secrets in configuration. Cloud credentials shall come from the SDK credential chain or workload identity. | Implemented (SEC-05) |
| NFR-SEC-02 | Message bodies in transit and at rest in the broker shall be encryptable with customer-managed KMS keys using authenticated encryption (AES-256-GCM). | Implemented as components; wired only through the opt-in AWS enhanced pipeline (SEC-03) |
| NFR-SEC-03 | PII and secrets marked `[SensitiveData]` shall never appear unmasked in logs written by the framework. | Partial. Masking exists, but the default dispatchers and listeners do not use it. They log type names and ids, not payloads |
| NFR-SEC-04 | Deserialising inbound messages shall be restricted to the message types declared in the bus configuration. | **Not met** ([GAP-10](06-Gaps-Risks-and-Open-Questions.md#gap-10)) |
| NFR-SEC-05 | SQL text and parameter values shall be left out of telemetry by default. | Implemented (`IncludeSqlInTraces = false`) |
| NFR-SEC-06 | The repository shall run static security analysis on master and release branches. | Implemented (CodeQL workflows), with a [SECURITY.md](../../SECURITY.md) disclosure policy |

## 4. Performance and scalability (`NFR-PERF`)

| ID | Requirement | Status / current behaviour |
|----|-------------|----------------------------|
| NFR-PERF-01 | Fan-out to sagas, views and dispatchers shall run concurrently. | Implemented (`Task.WhenAll`, `TaskBufferPool`) |
| NFR-PERF-02 | Serialisation on the hot path shall reuse pooled buffers. | Implemented (`ByteArrayPool`) |
| NFR-PERF-03 | Assigning the next sequence number shall cost O(1) with respect to the entity's history length. | **Not met.** Each command loads and deserialises the entity's whole history to find the highest sequence number (`CommandStoreAdapter.GetNextSequenceNo`). Cost grows linearly with history length ([GAP-05](06-Gaps-Risks-and-Open-Questions.md#gap-05)) |
| NFR-PERF-04 | Reflection used for dispatch shall be cached per type. | Implemented in the cloud listeners (`_typeCache`, `_methodInfoCache`). Saga event raising and replay use reflection without a cache |
| NFR-PERF-05 | Telemetry shall cost close to nothing when switched off. | Implemented (domain telemetry is off by default; the store adapters skip tracing when no telemetry service is present) |
| NFR-PERF-06 | Cloud listeners shall scale out horizontally (competing consumers) with no coordination beyond the broker. | Implemented for commands. For events on AWS, all instances of a service share one queue, which is correct. Shared idempotency needs the EF backend (IDM-03) |
| NFR-PERF-07 | Message size shall be checked against the target broker's limit (for example, SNS 256 KB, Pub/Sub 10 MB) before sending, with a clear error. | Not enforced. `EncryptionOptions.MaxMessageSize` (256 KB) is defined but never checked |

## 5. Observability and operability (`NFR-OPS`)

| ID | Requirement | Status / current behaviour |
|----|-------------|----------------------------|
| NFR-OPS-01 | Every pipeline stage shall be traceable with OpenTelemetry and exportable over OTLP. | Implemented (OBS-02..05) |
| NFR-OPS-02 | One distributed trace shall cover sender and receiver across a cloud hop. | Partial (CTL-03) |
| NFR-OPS-03 | Each cloud provider shall expose a standard ASP.NET Core health check. | Implemented (CTL-04) |
| NFR-OPS-04 | Startup shall fail fast, with a clear message, on invalid routing or provider configuration. | Implemented (BUS-02, BUS-04, BUS-06). Exception: provider options that are silently ignored (GAP-03) |
| NFR-OPS-05 | Logs shall be structured (message templates with named properties) and use a consistent `Action=` vocabulary. | Implemented. Some messages are copied from elsewhere and misleading, for example the projection subscriber logging "No Sagas Found" ([GAP-14](06-Gaps-Risks-and-Open-Questions.md#gap-14)) |
| NFR-OPS-06 | Operators shall be able to inspect and replay dead-lettered messages. | Partial — only through the opt-in AWS monitor; no `IDeadLetterProcessor` implementation (GAP-11) |

## 6. Testability (`NFR-TST`)

| ID | Requirement | Status / current behaviour |
|----|-------------|----------------------------|
| NFR-TST-01 | Every framework seam shall be an interface that can be replaced through DI, including the store adapters. | Implemented (STO-05) |
| NFR-TST-02 | The repository shall ship an end-to-end reference scenario that runs in memory, and the same scenario on EF. | Implemented (bank-account E2E in Core.Tests and Stores.EntityFramework.Tests) |
| NFR-TST-03 | Cloud providers shall have unit tests with no external dependencies, plus integration tests against local emulators (LocalStack, the Pub/Sub emulator), split by `Category` trait. | Implemented. AWS: about 80 test files covering unit, integration, performance and security. GCP: about 8 files (beta) |
| NFR-TST-04 | CI shall run unit tests on every push to master and integration tests against the emulators. | Implemented ([Master-Build.yml](../../.github/workflows/Master-Build.yml), [GCP-Build.yml](../../.github/workflows/GCP-Build.yml)) |
| NFR-TST-05 | Duplicated code shall be tracked. | Configured ([.jscpd.json](../../.jscpd.json)). The basic and `*Enhanced` AWS classes are near-copies of each other |

## 7. Packaging, versioning and release (`NFR-RE`)

| ID | Requirement | Status / current behaviour |
|----|-------------|----------------------------|
| NFR-RE-01 | Versions shall follow SemVer and be computed by GitVersion from the branch: master is stable; `release/*` gets a `beta` tag; `develop` gets `beta`; `pre-release/*` gets `alpha`; PRs get `PullRequest.N`. | Implemented ([GitVersion.yml](../../GitVersion.yml)) |
| NFR-RE-02 | Packages shall be published only from an explicit `release-packages` tag. Branch builds produce pre-release versions only. | Implemented (Release-CI). Publishing to NuGet.org is currently switched off; packages go to GitHub Packages |
| NFR-RE-03 | Each package's csproj shall carry an accurate `Description`, used as the NuGet listing. | Partial — the core description says ".NET 9.0 and .NET 10.0" and leaves out .NET 8. The EF description says "SQL Server support", although any provider works (EF-13) |
| NFR-RE-04 | Each release shall have a changelog under `docs/Versions/<version>/`. | Implemented for v1.0.0 and v2.0.0. The v2.0.0 changelog still says "Release Date: TBC", while the README says 15 Mar 2026 |

## 8. Documentation (`NFR-DOC`)

| ID | Requirement | Status / current behaviour |
|----|-------------|----------------------------|
| NFR-DOC-01 | Each package shall have a README covering install, registration, configuration options and an end-to-end example. | Implemented (`docs/*-README.md`) |
| NFR-DOC-02 | Architecture docs shall describe the command flow, event flow, dispatching patterns, persistence and cloud architecture. | Implemented ([docs/Architecture](../legacy-version/Architecture)) |
| NFR-DOC-03 | Documentation shall match the code. | Partial — see [GAP-02](06-Gaps-Risks-and-Open-Questions.md#gap-02) (RawMessageDelivery), [GAP-03](06-Gaps-Risks-and-Open-Questions.md#gap-03) (Region), [GAP-18](06-Gaps-Risks-and-Open-Questions.md#gap-18) (target frameworks) |
