# 02 — Core Framework Requirements (`SourceFlow.Net`)

Source root: [`src/SourceFlow`](../../src/SourceFlow). The status legend is in the [README](README.md#status-values).

---

## 1. Domain modelling (`DOM`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| DOM-01 | Every write-model entity shall implement `IEntity` with a settable integer `Id`. | Implemented | [IEntity.cs](../../src/SourceFlow/IEntity.cs) |
| DOM-02 | The framework shall provide `Aggregate<TEntity>` as a base for domain façades. It exposes a protected `Send(command)` and a public `ReplayCommands(entityId)`, and rejects null commands. | Implemented | [Aggregate.cs](../../src/SourceFlow/Aggregate/Aggregate.cs) |
| DOM-03 | An aggregate shall be able to react to an event type by implementing `ISubscribes<TEvent>.On(event)`. | Implemented | [ISubscribes.cs](../../src/SourceFlow/Aggregate/ISubscribes.cs), [Aggregate/EventSubscriber.cs](../../src/SourceFlow/Aggregate/EventSubscriber.cs) |
| DOM-04 | Consumers shall be able to resolve aggregates through `IAggregateFactory.Create<TAggregate>()`, backed by the DI container. | Implemented | [AggregateFactory.cs](../../src/SourceFlow/Impl/AggregateFactory.cs) |
| DOM-05 | The framework shall provide `Saga<TEntity>`. A saga declares which commands it handles by implementing `IHandles<TCommand>`; it receives the current entity and the command and returns the updated entity. | Implemented | [Saga.cs](../../src/SourceFlow/Saga/Saga.cs), [IHandles.cs](../../src/SourceFlow/Saga/IHandles.cs) |
| DOM-06 | Before calling a handler, the saga shall create a fresh entity (with the command's entity id) when `command.Entity.IsNew` is true, and otherwise load it from the entity store. | Implemented | `Saga.Handle` |
| DOM-07 | After a handler returns a non-null entity, the saga shall persist it through the entity store. | Implemented | `Saga.Handle` |
| DOM-08 | A saga shall be able to declare "handling command C raises event E" by implementing `IHandlesWithEvent<C,E>`. The framework then builds E (with a parameterless constructor, or one taking the entity), sets its payload to the entity, and raises it. | Partial — failures while building or raising the event are logged and swallowed ([GAP-07](06-Gaps-Risks-and-Open-Questions.md#gap-07)) | `Saga.RaiseEvent` |
| DOM-09 | A saga shall be able to publish follow-up commands (`Publish`) and raise events explicitly (`Raise`). `Raise` shall reject events with no payload. | Implemented | `Saga.Publish`, `Saga.Raise` |
| DOM-10 | The framework shall provide `View<TViewModel>`. A view declares which events it projects by implementing `IProjectOn<TEvent>.On(event)`, which returns the view model to persist (or null to skip). | Implemented | [View.cs](../../src/SourceFlow/Projections/View.cs), [IProjectOn.cs](../../src/SourceFlow/Projections/IProjectOn.cs) |
| DOM-11 | Views shall be able to read and write view models through protected `Find<T>(id)` and `Persist<T>(model)` helpers. | Implemented | `View.Find`, `View.Persist` |
| DOM-12 | Every view model shall implement `IViewModel` with a settable integer `Id`. | Implemented | [IViewModel.cs](../../src/SourceFlow/Projections/IViewModel.cs) |
| DOM-13 | Command and event handler matching shall be type-exact: a saga or view receives a message only if it implements the handler interface for that message's runtime type. | Implemented | `Saga.CanHandle`, `View.CanHandle` |

## 2. Commands, command bus, command log and replay (`CMD`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| CMD-01 | A command shall carry a `Name` (defaults to its type name), a strongly typed `Payload` (`IPayload`), an `EntityRef` (`Id`, `IsNew`) and `Metadata`. | Implemented | [Command.cs](../../src/SourceFlow/Messaging/Commands/Command.cs), [ICommand.cs](../../src/SourceFlow/Messaging/Commands/ICommand.cs) |
| CMD-02 | `Metadata` shall carry a unique `EventId` (GUID), `OccurredOn` (UTC), `SequenceNo`, `IsReplay` and an open `Properties` dictionary. | Implemented | [IMetadata.cs](../../src/SourceFlow/Messaging/IMetadata.cs) |
| CMD-03 | `Command<TPayload>` shall provide constructors for: an existing entity (id), a new entity, and an explicit id plus new-entity flag. | Implemented | `Command` constructors |
| CMD-04 | `ICommandPublisher.Publish` shall reject a null command and a command with no entity reference. | Implemented | [CommandPublisher.cs](../../src/SourceFlow/Messaging/Commands/Impl/CommandPublisher.cs) |
| CMD-05 | For every non-replay command, the command bus shall assign `SequenceNo` = (highest stored sequence number for that entity) + 1, starting at 1. | Partial — reads the entity's full history on every command, and the read and append are not atomic ([GAP-05](06-Gaps-Risks-and-Open-Questions.md#gap-05), [NFR-PERF-03](05-Non-Functional-Requirements.md)) | [CommandBus.cs](../../src/SourceFlow/Messaging/Bus/Impl/CommandBus.cs), [CommandStoreAdapter.cs](../../src/SourceFlow/Impl/CommandStoreAdapter.cs) |
| CMD-06 | The command bus shall dispatch each command to **all** registered `ICommandDispatcher`s at once and wait for all of them. | Implemented | `CommandBus.CoreDispatch` |
| CMD-07 | After a non-replay command has been dispatched successfully, the bus shall append it to the command store. If dispatch throws, the command shall not be appended. | Implemented | `CommandBus.CoreDispatch` |
| CMD-08 | The local `CommandDispatcher` shall pass each command to every `ICommandSubscriber`. The saga subscriber shall call every saga that can handle it, concurrently. | Implemented | [CommandDispatcher.cs](../../src/SourceFlow/Messaging/Commands/Impl/CommandDispatcher.cs), [Saga/CommandSubscriber.cs](../../src/SourceFlow/Saga/CommandSubscriber.cs) |
| CMD-09 | The command store adapter shall turn commands into `CommandData` (assembly-qualified command and payload type names, JSON payload of the concrete payload type, JSON metadata, timestamp) and back. | Implemented | `CommandStoreAdapter.SerializeCommand` / `DeserializeCommand` |
| CMD-10 | `ReplayCommands(entityId)` shall load the entity's commands in ascending sequence order, set `IsReplay = true` on each, and re-dispatch them one after another through the full pipeline (middleware and every dispatcher), without assigning new sequence numbers or appending again. | Partial — cloud dispatchers do not check `IsReplay`, so replayed commands can be re-published ([GAP-06](06-Gaps-Risks-and-Open-Questions.md#gap-06)) | `CommandBus.Replay` |
| CMD-11 | To be replayable, a command type shall be constructible through a **public parameterless constructor**. The framework shall fail loudly, not silently, when a stored command cannot be rebuilt. | Partial — the requirement is unstated, and commands that cannot be rebuilt are **silently skipped** ([GAP-04](06-Gaps-Risks-and-Open-Questions.md#gap-04)) | `CommandStoreAdapter.DeserializeCommands` |
| CMD-12 | A command that creates an entity shall be logged under that entity's real id. Either the caller supplies the id, or the framework assigns it and writes it back to the command before it is appended. | **Not met** — new-entity commands without a caller-assigned id are logged under id 0 ([GAP-20](06-Gaps-Risks-and-Open-Questions.md#gap-20)) | `CommandBus.CoreDispatch`, `Saga.InitialiseEntity` |
| CMD-13 | A command received from another service shall be sequenced and logged by the service that owns the entity, so that the owner can replay it. | **Not met** — cloud listeners call the subscriber directly, bypassing the bus ([GAP-19](06-Gaps-Risks-and-Open-Questions.md#gap-19)) | AWS and GCP command listeners |

## 3. Events and the event pipeline (`EVT`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| EVT-01 | An event shall carry a `Name`, `Metadata`, and the changed entity as its `Payload` (`Event<TEntity>`). | Implemented | [Event.cs](../../src/SourceFlow/Messaging/Events/Event.cs) |
| EVT-02 | `IEventQueue.Enqueue` shall reject null events, run the event dispatch middleware, then dispatch to **all** registered `IEventDispatcher`s concurrently. | Implemented | [EventQueue.cs](../../src/SourceFlow/Messaging/Events/Impl/EventQueue.cs) |
| EVT-03 | The local `EventDispatcher` shall pass each event to every `IEventSubscriber`. Out of the box there are two: the aggregate subscriber (DOM-03) and the projection subscriber (DOM-10). | Implemented | [EventDispatcher.cs](../../src/SourceFlow/Messaging/Events/Impl/EventDispatcher.cs) |
| EVT-04 | The projection subscriber shall call every view that can handle the event concurrently, and persist each non-null view model it gets back. | Implemented | [Projections/EventSubscriber.cs](../../src/SourceFlow/Projections/EventSubscriber.cs) |
| EVT-05 | Events are transient: the framework shall not persist them. Read models are rebuilt by replaying commands (CMD-10). | Implemented (by design) | — |

## 4. Store contracts and adapters (`STO`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| STO-01 | `ICommandStore` shall support `Append(CommandData)` and `Load(entityId)`, returning commands in sequence order. | Implemented | [ICommandStore.cs](../../src/SourceFlow/ICommandStore.cs) |
| STO-02 | `IEntityStore` shall support `Get<T>(id)`, `Persist<T>(entity)` (an upsert) and `Delete<T>(entity)` for any `IEntity`. | Implemented | [IEntityStore.cs](../../src/SourceFlow/IEntityStore.cs) |
| STO-03 | `IViewModelStore` shall support `Get<T>(id)`, `Persist<T>(model)` (an upsert) and `Delete<T>(model)` for any `IViewModel`. | Implemented | [IViewModelStore.cs](../../src/SourceFlow/IViewModelStore.cs) |
| STO-04 | The framework shall wrap each store in an adapter that adds serialisation (for the command store) and optional tracing, so that store implementers deal only with plain data. | Implemented | [Impl/](../../src/SourceFlow/Impl) |
| STO-05 | Consumers and tests shall be able to supply their own adapter implementations. The framework registers the defaults only if none is registered. | Implemented | `TryAddScoped` in [IocExtensions.cs](../../src/SourceFlow/IocExtensions.cs) |
| STO-06 | Store contracts shall define what happens when something is not found (for example, `Get` throws or returns null). | Partial — not specified; the EF store throws `InvalidOperationException` ([GAP-15](06-Gaps-Risks-and-Open-Questions.md#gap-15)) | — |

## 5. Registration and discovery (`REG`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| REG-01 | `services.UseSourceFlow(params Assembly[])` shall register the whole local pipeline in one call. | Implemented | [IocExtensions.cs](../../src/SourceFlow/IocExtensions.cs) |
| REG-02 | When no assemblies are given, the framework shall scan the SourceFlow assembly and the calling assembly. | Implemented | `UseSourceFlow` |
| REG-03 | Scanning shall find and register every public, concrete, non-generic `IAggregate`, `ISaga` and `IView`, each under its marker interface, its concrete type and every other public interface it implements. | Implemented | `AddImplementationAsInterfaces` |
| REG-04 | Scanning shall register the **first** implementation it finds of `ICommandStore`, `IEntityStore` and `IViewModelStore`, and shall not override one that is already registered. | Implemented | `AddFirstImplementationAsInterface` |
| REG-05 | Consumers shall be able to choose the service lifetime for discovered domain types (the default is Singleton). | Partial — Singleton domain types depend on Scoped adapters and publishers, a captive-dependency risk ([GAP-08](06-Gaps-Risks-and-Open-Questions.md#gap-08)) | `UseSourceFlow(services, lifetime, …)` |
| REG-06 | The framework shall break the publisher↔saga/aggregate dependency cycle by injecting `Lazy<ICommandPublisher>`. | Implemented | `UseSourceFlow` |
| REG-07 | Scanning shall tolerate assemblies whose types cannot all be loaded, by registering the types that did load. | Partial — the `ReflectionTypeLoadException` handler calls `GetTypes()` again inside the `catch` block, so the exception escapes and registration fails. It should use `ex.Types` ([GAP-16](06-Gaps-Risks-and-Open-Questions.md#gap-16)) | `GetImplementedTypes` |

## 6. Middleware and extension points (`EXT`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| EXT-01 | The framework shall offer four middleware hooks, each `InvokeAsync(message, next)`: `ICommandDispatchMiddleware` (around the command bus), `ICommandSubscribeMiddleware` (around saga dispatch), `IEventDispatchMiddleware` (around the event queue) and `IEventSubscribeMiddleware` (around each event subscriber). | Implemented | [Messaging/](../../src/SourceFlow/Messaging) |
| EXT-02 | Middleware shall run in registration order (first registered is outermost) and be able to short-circuit by not calling `next`. | Implemented | Pipeline built over `middlewares.Reverse()` in the bus, queue and subscribers |
| EXT-03 | Middleware shall be registered through standard DI (`services.AddSingleton<ICommandDispatchMiddleware, X>()` and so on). | Implemented — no helper methods; covered by tests in `tests/SourceFlow.Core.Tests/Middleware` | — |
| EXT-04 | New transports shall plug in by registering more `ICommandDispatcher` / `IEventDispatcher` implementations, which run alongside the local ones. | Implemented | Used by the AWS and GCP providers |

## 7. Domain observability (`OBS`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| OBS-01 | Domain telemetry shall be **off by default** (`DomainObservabilityOptions.Enabled = false`), so it adds no overhead or behaviour change unless switched on. | Implemented | [DomainTelemetryService.cs](../../src/SourceFlow/Observability/DomainTelemetryService.cs) |
| OBS-02 | `services.AddSourceFlowTelemetry(serviceName, version)` or `AddSourceFlowTelemetry(options => …)` shall switch telemetry on and register the SourceFlow ActivitySource and Meter with OpenTelemetry. | Implemented | [OpenTelemetryExtensions.cs](../../src/SourceFlow/Observability/OpenTelemetryExtensions.cs) |
| OBS-03 | The framework shall emit spans for command bus dispatch and replay, command dispatcher send, event queue enqueue, event dispatch, store adapter operations and serialisation. | Implemented | Span names `sourceflow.commandbus.dispatch`, `sourceflow.commandbus.replay`, `sourceflow.eventqueue.enqueue`, `sourceflow.entitystore.*`, `sourceflow.viewmodelstore.*`, `sourceflow.domain.command.*` |
| OBS-04 | The framework shall emit the metrics `sourceflow.domain.commands.executed`, `…sagas.executed`, `…entities.created`, `…serialization.operations` (counters) and `…operation.duration`, `…serialization.duration` (histograms). | Partial — `sagas.executed` is defined but never recorded; `commands.executed` is recorded twice per command (by the bus and by the store adapter); `entities.created` goes up on every persist, updates included ([GAP-14](06-Gaps-Risks-and-Open-Questions.md#gap-14)) | `DomainTelemetryService` |
| OBS-05 | The framework shall provide exporter helpers for console and OTLP, resource attributes, and batch-processing tuning. | Implemented | `AddSourceFlowConsoleExporter`, `AddSourceFlowOtlpExporter`, `AddSourceFlowResourceAttributes`, `ConfigureSourceFlowBatchProcessing` |
| OBS-06 | All pipeline components shall write structured `Action=…` log entries through `ILogger`. | Implemented | Throughout |

## 8. Performance helpers (`PERF`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| PERF-01 | JSON serialisation of commands shall use pooled buffers to cut allocations. | Implemented | [ByteArrayPool.cs](../../src/SourceFlow/Performance/ByteArrayPool.cs) |
| PERF-02 | Fan-out to subscribers shall use pooled task buffers. | Implemented | [TaskBufferPool.cs](../../src/SourceFlow/Performance/TaskBufferPool.cs) |

---

## Acceptance scenarios (core)

These come from the end-to-end tests in [`tests/SourceFlow.Core.Tests/E2E`](../../tests/SourceFlow.Core.Tests/E2E) (the bank-account sample).

1. **Create.** When `AccountAggregate.CreateAccount(id, "Holder", 100)` is called, `AccountSaga` creates a `BankAccount` with balance 100, persists it, and raises `AccountCreated`. `AccountView` then writes an `AccountViewModel`, and the command store holds one command with `SequenceNo = 1`.
2. **Mutate.** When a deposit of 50 follows, the entity balance becomes 150, `AccountUpdated` is raised, the view model is updated, and the command is stored with `SequenceNo = 2`.
3. **Validate.** When a saga handler throws (for example, a deposit of 0 or below), the command is **not** appended and no event is raised.
4. **Replay.** When `ReplayCommands(id)` is called on an entity with N stored commands, the sagas and views reach the same final state, and the command store still holds exactly N commands.
5. **Middleware.** With middleware registered in the order M1, M2, a published command passes M1 → M2 → core. A middleware that does not call `next` stops dispatch and append.
