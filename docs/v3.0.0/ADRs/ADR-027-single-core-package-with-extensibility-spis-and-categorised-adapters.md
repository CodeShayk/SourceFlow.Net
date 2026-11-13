# ADR-027: Single core package with extensibility SPIs, and categorised adapter packages

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-11 |
| **Deciders** | v3 architecture review; product direction during WP-0.2 |

## Context

The first v3 design split the library into 18 packages. Among them were `SourceFlow.Abstractions` (domain contracts), `SourceFlow.Net` (runtime), one package per transport (`AmazonSqs`, `AzureServiceBus`, `GooglePubSub`), one per KMS (`Security.AwsKms`, `.AzureKeyVault`, `.GoogleKms`), `SourceFlow.OpenTelemetry`, `SourceFlow.Workflows.Abstractions`, `SourceFlow.Workflows` and `SourceFlow.Workflows.Wasm`.

During the repository scaffold (WP-0.2), product direction asked for a smaller set:

- **Categorise and consolidate** the projects.
- Make the **core a single project**, with extensibility interfaces for cloud, persistence, security and so on.
- Name the storage adapters `SourceFlow.Storage.*`.
- Name the workflow designer project `SourceFlow.Studio`.
- **Target .NET 10 only.**

The split mainly existed so that domain assemblies would not reference the runtime. That has little value: the runtime's only dependencies are `Microsoft.Extensions.*` abstractions, and an architecture rule can enforce domain purity just as well as a package boundary can (see ARCH-04 below).

## Decision

### 1. One core package: `SourceFlow.Net`

`SourceFlow.Net` contains the domain contracts, the runtime engine and every extensibility SPI. Adapter packages implement the SPIs. It absorbs the former `SourceFlow.Abstractions`, `SourceFlow.Workflows.Abstractions` and `SourceFlow.OpenTelemetry`.

| Namespace | Role | Typical implementers |
|-----------|------|----------------------|
| `SourceFlow` | Domain contracts: `ICommand<TAgg>`, `IEvent`, attributes, `Decision`, `Reaction`, `Error`, ids | Domain code |
| `SourceFlow.Messaging` | Envelope, type registry, serializer contracts | — |
| `SourceFlow.Storage` | **Persistence SPI**: `IEventStore`, `IEventLogReader`, `IStorageSession`, `IOutboxStore`, `IInboxStore`, `ICheckpointStore`, `ILeaseManager`, `IDeadLetterStore`, `IDocumentStore`, plus the in-memory adapter | `SourceFlow.Storage.*` |
| `SourceFlow.Transport` | **Cloud and broker SPI**: `ITransport`, `ITransportReceiver`, `ITransportProvisioner`, `TransportCapabilities`, plus the in-memory transport | `SourceFlow.Cloud.*` |
| `SourceFlow.Security` | **Security SPI**: `IKeyProvider`, `IPayloadProtector`, `PayloadProtectionMiddleware`, personal-data erasure | `SourceFlow.Cloud.*` (KMS) |
| `SourceFlow.Query` | **Query SPI**: SFQL parser, binder and in-memory interpreter, plus `ISqlDialect` and the compiler contract | `SourceFlow.Storage.PostgreSQL`, `.SqlServer` |
| `SourceFlow.Pipeline` | `IMessageMiddleware` (ADR-012) | User code |
| `SourceFlow.Diagnostics` | `ActivitySource`, `Meter`, semantic-convention constants, health checks | — |
| `SourceFlow.Workflows` | Runtime types used by generated workflows: status, compensation entries, operator commands | Generated code |
| `SourceFlow.Hosting` | `SourceFlowBuilder` with `Use*` slots for each SPI, plus options validation | Adapter `Use*` extensions |
| `SourceFlow.Runtime` | The engine (internal) | — |

### 2. The generator stays a separate build project, inside the core package

`src/Core/SourceFlow.Generators` is a Roslyn component, so it must target `netstandard2.0` (RS1041). It loads into the compiler and the IDE, and it must not carry `Microsoft.CodeAnalysis` into applications.

- It is packed into `SourceFlow.Net` under `analyzers/dotnet/cs` and is never published on its own.
- **It is the only project in v3 that does not target `net10.0`.** Every package ships `lib/net10.0` only.

### 3. Adapter and utility packages, by category

| Category (`src/…`) | Package | Contents |
|--------------------|---------|----------|
| `Core` | `SourceFlow.Net` | §1 (with the generator) |
| `Storage` | `SourceFlow.Storage.PostgreSQL` | Persistence SPI on PostgreSQL, plus the SFQL PostgreSQL compiler |
| `Storage` | `SourceFlow.Storage.SqlServer` | Persistence SPI on SQL Server, plus the SFQL SQL Server compiler |
| `Storage` | `SourceFlow.Storage.EntityFrameworkCore` | EF Core read-model projections bound to the storage session |
| `Cloud` | `SourceFlow.Cloud.Aws` | SNS/SQS transport and provisioner, AWS KMS `IKeyProvider`, health checks |
| `Cloud` | `SourceFlow.Cloud.Azure` | Service Bus transport and provisioner, Key Vault `IKeyProvider`, health checks |
| `Cloud` | `SourceFlow.Cloud.Gcp` | Pub/Sub transport and provisioner, Cloud KMS `IKeyProvider`, health checks |
| `Hosting` | `SourceFlow.AspNetCore` | `MapCommand<T>()` and the query API endpoint |
| `Tooling` | `SourceFlow.Testing` | Specs, test host, conformance-kit bases |
| `Tooling` | `SourceFlow.Tool` | `dotnet sourceflow` CLI |
| `Workflows` | `SourceFlow.Studio` | Workflow DSL semantic core: model, YAML reader, validator, simulator, path enumerator, exporters, manifest model |
| `Workflows` | `SourceFlow.Studio.Wasm` | `browser-wasm` `[JSExport]` facade. A build output embedded in the `@sourceflow/studio` npm package and in the tool, not published to NuGet |

That is **11 NuGet packages** instead of 18.

**One cloud package per cloud.** A team that adopts a cloud almost always uses both its broker and its KMS, so each cloud gets one package.
- Registration stays explicit and independent: `UseAwsTransport()` and `UseAwsKms()` are separate calls.
- Mixing clouds still works. For example, AWS KMS with Azure Service Bus references both cloud packages. The cost is unused SDK assemblies, not changed behaviour.

### 4. Dependency rules (replace design doc 01 §5)

| Rule | Statement |
|------|-----------|
| ARCH-01 | `SourceFlow.Net` references only the BCL, `Microsoft.Extensions.*`, Polly and `Microsoft.Extensions.Resilience`. It has no cloud SDK, database driver or OpenTelemetry SDK. |
| ARCH-02 | `SourceFlow.Net` never references an adapter package, not even through reflection. |
| ARCH-03 | Adapter packages reference `SourceFlow.Net` and their own SDK only. Adapters never reference each other. |
| ARCH-04 | Domain assemblies reference only `SourceFlow.Net`. They use types from the `SourceFlow` and `SourceFlow.Workflows` namespaces only, never `Runtime`, `Storage`, `Transport`, `Security`, `Hosting` or `Diagnostics`. This keeps domain code free of infrastructure without a separate package. |

ARCH-05 to ARCH-08 are unchanged (plan 06 §9).

### 5. OpenTelemetry without an extra package

The core emits through `System.Diagnostics` (an `ActivitySource` and a `Meter`, both named `SourceFlow`). It declares histogram bucket boundaries with `InstrumentAdvice<T>` (.NET 9+), so no SDK views are needed.

Applications subscribe with the OpenTelemetry SDK they already reference:

```csharp
.WithTracing(t => t.AddSource(SourceFlowTelemetry.ActivitySourceName))
.WithMetrics(m => m.AddMeter(SourceFlowTelemetry.MeterName))
```

### 6. Studio and the generator share one DSL implementation

`SourceFlow.Studio` targets `net10.0`. The generator compiles the same source files, linked from `src/Workflows/SourceFlow.Studio/Dsl/**`, into its own `netstandard2.0` assembly, with YamlDotNet bundled.

This is one implementation compiled twice, so there is still only one definition of the semantics (WFD-NFR-2). It avoids a second multi-targeted package. The `Dsl` folder is restricted to C# features that compile on `netstandard2.0`, with polyfills for `init`, records and the nullable attributes. Implementation risk IR-14 still applies.

### 7. Repository and test layout

Source projects live under `src/<Category>/<Package>/`. Test projects are one per category, and traits select subsets in CI.

| Test project | Covers |
|--------------|--------|
| `tests/Core/SourceFlow.Core.Tests` | Contracts, runtime, in-memory SCK and TCK, security middleware, diagnostics |
| `tests/Core/SourceFlow.Generators.Tests` | Generators and analyzers, including the workflow generator |
| `tests/Storage/SourceFlow.Storage.Tests` | SCK on PostgreSQL, SQL Server and EF Core; SFQL compilers (`Provider` trait) |
| `tests/Cloud/SourceFlow.Cloud.Tests` | Transport unit tests, TCK and KMS round trips (`Cloud` trait) |
| `tests/Hosting/SourceFlow.Hosting.Tests` | ASP.NET Core endpoints |
| `tests/Tooling/SourceFlow.Tooling.Tests` | Testing toolkit, CLI and v2 migration |
| `tests/Workflows/SourceFlow.Studio.Tests` | DSL, manifests, golden and differential suites, WASM facade |
| `tests/EndToEnd/SourceFlow.EndToEnd.Tests` | Bank E2E, workflow E2E and chaos (`Category` trait) |
| `tests/Architecture/SourceFlow.Architecture.Tests` | ARCH-01 to ARCH-08 |

`InternalsVisibleTo` is granted to both `<Project>.Tests` and `SourceFlow.<Category>.Tests`.

### 8. Package identities

| Package | Relation to v2 |
|---------|----------------|
| `SourceFlow.Net` | Keeps the v2 ID. |
| `SourceFlow.Cloud.Aws` | Continues `SourceFlow.Cloud.AWS`. NuGet IDs are case-insensitive, so these are the same package. |
| `SourceFlow.Cloud.Gcp` | Continues `SourceFlow.Cloud.GCP`. |
| `SourceFlow.Cloud.Azure` | Was never published from `master`. |
| `SourceFlow.Storage.EntityFrameworkCore` and `SourceFlow.Storage.PostgreSQL` | Replace `SourceFlow.Stores.EntityFramework`. Its final 2.x release note points to them (WP-6.3). |

## Alternatives

| Alternative | Why not |
|-------------|---------|
| Keep `SourceFlow.Abstractions` separate | Domain assemblies would avoid only `Microsoft.Extensions.*` abstractions, and ARCH-04 already enforces domain purity. It adds a package to every install and to every versioning decision. |
| Merge the generator into `SourceFlow.Net` by multi-targeting one project | The `net10.0` and `netstandard2.0` builds would be two different products sharing one file, with `#if` on every source file and custom packing rules. |
| Separate KMS and transport packages per cloud | Doubles the cloud package count for a rare mixed-cloud case that still works with one package per cloud. |
| One test project per package (the earlier plan 04 §3) | 20+ test projects slow restore and build. Category projects with traits give the same CI fan-out. |

## Consequences

- (+) One reference gives a user the contracts, the runtime, the in-memory adapters and the generator. There are fewer packages to version, document and release.
- (+) The SPI namespaces state the extension points clearly: a storage, cloud, security or query provider implements one namespace's interfaces and registers through one `Use*` slot.
- (+) Every runtime package targets .NET 10 only.
- (−) Domain assemblies reference the runtime assembly. ARCH-04 (namespace rule) mitigates this.
- (−) A cross-cloud KMS user pulls an unused transport SDK.
- (−) Category test projects are larger, so CI filters by trait (plan 08).
- Supersedes the package-map parts of [doc 01 §5–6](../Design/01-Architecture-Overview.md#5-logical-architecture), the packaging note in [ADR-004](ADR-004-source-generation-instead-of-reflection.md) and the package list in [ADR-024](ADR-024-workflow-dsl-compiled-to-process-managers.md) and [ADR-025](ADR-025-studio-react-spa-with-shared-wasm-core.md).
