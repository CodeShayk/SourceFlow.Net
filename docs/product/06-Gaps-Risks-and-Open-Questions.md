# 06 — Gaps, Risks and Open Questions

These findings came out of reading the code against the requirements in docs 02–05 and against the project's own documentation. Each one gives the **evidence** (where to look), the **impact**, and a **suggested change**. Items marked *verify* are strongly suggested by the code but have not been reproduced against a live environment.

**Priority:** **P1** breaks correctness or a stated guarantee, or blocks production use. **P2** is a significant functional, operational or security shortfall. **P3** affects quality, accuracy or documentation.

## Summary

| ID | Title | Area | Priority |
|----|-------|------|----------|
| [GAP-01](#gap-01) | Production messaging pipeline is built but not wired | Cloud | P1 |
| [GAP-02](#gap-02) | AWS events and commands share one queue without raw delivery | AWS | P1 *(verify)* |
| [GAP-03](#gap-03) | Provider options that are silently ignored (incl. AWS Region) | Cloud | P1 |
| [GAP-04](#gap-04) | Command rebuild contract is implicit and failures are silent | Core | P1 |
| [GAP-05](#gap-05) | Sequence assignment is O(n), racy, and not atomic with side effects | Core / EF | P1 |
| [GAP-06](#gap-06) | Replay causes external side effects | Core / Cloud | P1 |
| [GAP-19](#gap-19) | Cloud-received commands are not logged by the owning service | Core / Cloud | P1 |
| [GAP-20](#gap-20) | New-entity commands are logged under id 0 unless the caller assigns an id | Core | P1 |
| [GAP-13](#gap-13) | GCP: shared subscriptions across services, no ordering, no hardening | GCP | P1 (for GA) |
| [GAP-07](#gap-07) | Saga event-raising failures are swallowed | Core | P2 |
| [GAP-08](#gap-08) | Singleton domain types capture Scoped dependencies | Core | P2 |
| [GAP-09](#gap-09) | Idempotency key is per delivery, not per logical message | Cloud | P2 |
| [GAP-10](#gap-10) | No allow-list for inbound message types | Cloud / Security | P2 |
| [GAP-11](#gap-11) | Dead-letter story is incomplete | Cloud | P2 |
| [GAP-12](#gap-12) | Auto-provisioning cannot be switched off | Cloud / Security | P2 |
| [GAP-14](#gap-14) | Metric and log inaccuracies | Observability | P3 |
| [GAP-15](#gap-15) | Store "not found" semantics are unspecified | Core | P3 |
| [GAP-16](#gap-16) | Assembly-scan fallback rethrows | Core | P3 |
| [GAP-17](#gap-17) | EF runtime type registration is process-global | EF | P3 |
| [GAP-18](#gap-18) | Documentation and package-metadata drift | Docs | P3 |

---

## P1 findings

### GAP-01
**Production messaging pipeline is built but not wired.** *(IDM-04, DLQ-02..04, RES-02, SEC-03, CTL-02/03, AWS-10, GCP-07)*

- **Evidence.**
  - `UseSourceFlowAws` registers the basic `AwsSqsCommandDispatcher`, `AwsSnsEventDispatcher`, `AwsSqsCommandListener` and `AwsSnsEventListener` ([IocExtensions.cs](../../src/SourceFlow.Cloud.AWS/IocExtensions.cs)).
  - The `*Enhanced` versions, which add idempotency, dead-letter records, decryption, circuit breaker, metrics, trace propagation and masked logging, are never registered.
  - Nothing registers what the enhanced versions depend on: `IDeadLetterStore`, `ICircuitBreaker`, `CloudTelemetry`, `CloudMetrics`, `SensitiveDataMasker`, `IMessageEncryption`. `AwsDeadLetterMonitor` isn't registered either.
  - `IIdempotencyService` **is** registered by both providers, but no default listener uses it.
  - The GCP provider has no enhanced pipeline at all, and `GcpKmsMessageEncryption` is never wired.
- **Impact.** Users who follow the README and configure idempotency (for example `UseEFIdempotency`) believe they are protected against duplicates, but they are not. The AWS changelog lists these as features of the package.
- **Suggested change.**
  1. Merge the basic and enhanced classes into one implementation whose features switch on according to which services are registered (or through options).
  2. Have `UseSourceFlowAws` and `UseSourceFlowGcp` register safe defaults for every dependency: in-memory dead-letter store, a circuit breaker, telemetry and the masker.
  3. Add opt-in calls such as `.WithKmsEncryption(...)` and `.WithDeadLetterMonitor(...)`.
  4. Bring GCP to parity before GA.

### GAP-02
**AWS events and commands share one queue without raw delivery.** *(AWS-05)* — *verify*

- **Evidence.**
  - `AwsBusBootstrapper` subscribes every subscribed SNS topic to the **first** listen queue, and that same queue URL is used for both command listening and event listening.
  - `SubscribeRequest` does not set `RawMessageDelivery`, although [SourceFlow.Cloud.AWS-README.md](../legacy-version/SourceFlow.Cloud.AWS-README.md) and [07-AWS-Cloud-Architecture.md](../legacy-version/Architecture/07-AWS-Cloud-Architecture.md) both show it as `true`.
  - So `AwsSqsCommandListener` and `AwsSnsEventListener` poll the same queue. When the command listener gets an SNS envelope, it finds no `CommandType` attribute, logs an error and returns **without deleting** the message. When the event listener gets a command, it finds no `EventType` and does the same.
- **Impact.**
  - Messages bounce between the two consumers. Each wrong pickup delays the message by the visibility timeout (300 s by default) and adds to its receive count. With a redrive policy, valid messages can end up dead-lettered.
  - Error logs fill with false alarms.
  - Separately, AWS only lets FIFO topics deliver to FIFO queues. The documented example (`orders.fifo` as the listen queue, `order-events` as the topic) may fail against real AWS even though LocalStack accepts it.
- **Suggested change.** Use a dedicated event queue per service (for example `{service}-events`), or teach a single listener to tell the two kinds of message apart and route them. Set `RawMessageDelivery` deliberately. Validate FIFO/standard compatibility when bootstrapping.

### GAP-03
**Provider options that are silently ignored.** *(RES-05, AWS-02, AWS-03)*

- **Evidence.**
  - AWS clients are registered with `services.AddAWSService<IAmazonSQS>()` and `<IAmazonSimpleNotificationService>()`, which read the SDK's default configuration (the `AWS` config section and environment).
  - `SqsClientFactory` and `SnsClientFactory`, which would apply `AwsOptions.Region` and `MaxRetries`, are never called.
  - `AwsOptions.EnableCommandRouting`, `EnableEventRouting` and `RetryDelay` are never read.
  - `GcpOptions.EnableCommandRouting`, `EnableEventRouting`, `MaxRetries` and `RetryDelay` are never read.
- **Impact.** `options.Region = RegionEndpoint.EUWest1`, the first line of every documented example, has no effect. The service can create and use resources in an unexpected region or account with no warning.
- **Suggested change.** Build the clients from `AwsOptions`, or call `services.AddDefaultAWSOptions(...)` from them. Either honour or delete every unused option. Add a test asserting that configured options reach the client config.

### GAP-04
**Command rebuild contract is implicit and failures are silent.** *(CMD-11, NFR-CMP-06)*

- **Evidence.**
  - `CommandStoreAdapter.DeserializeCommand` rebuilds commands with `Activator.CreateInstance(commandType)`, which needs a **public parameterless constructor**, and resolves types with `Type.GetType(assemblyQualifiedName)`.
  - `DeserializeCommands` wraps each one in `try { … } catch { continue; }`.
  - The core sample `CreateAccount` has no parameterless constructor. The EF test copies add one.
- **Impact.**
  - Replay silently leaves out commands that cannot be rebuilt, for example after a rename or namespace move, or because of a missing constructor. The rebuilt state is then wrong, with no error.
  - `GetNextSequenceNo` uses the same `Load`, so skipped commands also produce **wrong sequence numbers**. That causes unique-key violations on EF, or duplicate sequence numbers on stores without the constraint.
- **Suggested change.**
  - Document the constructor requirement and check it at registration time (scan command types).
  - Log at error level and surface failures, for example with a `ReplayResult` or a strict mode that throws.
  - Store a stable logical type name (such as `[CommandName("orders.create.v1")]`) with a type registry, not the assembly-qualified name.
  - Add an upcaster hook for payload versioning.

### GAP-05
**Sequence assignment is O(n), racy, and not atomic with side effects.** *(CMD-05, EF-21, NFR-REL-02/03, NFR-PERF-03)*

- **Evidence.**
  - [CommandBus.CoreDispatch](../../src/SourceFlow/Messaging/Bus/Impl/CommandBus.cs) does three things in order: (1) `GetNextSequenceNo` (load and deserialise the full history, then max + 1); (2) dispatch to the sagas, which persist the entity and raise events; (3) `Append`.
  - The EF unique index on `(EntityId, SequenceNo)` catches a concurrent duplicate only at step 3, after step 2's side effects.
  - `DatabaseResiliencePolicy` retries every `DbUpdateException`, including that constraint violation, and the failed `CommandRecord` stays tracked between attempts.
- **Impact.** Two concurrent commands for one entity can both change it. One then fails to log, so the history no longer reproduces the state. Latency also grows with the length of each entity's history.
- **Suggested change.**
  - Reserve the sequence number first: optimistic concurrency on an entity version column, or append before handling, with the handler running inside the same transaction.
  - Get `MAX(SequenceNo)` through a store method (add `ICommandStore.GetLastSequenceNo`).
  - Put the entity persist and the command append in one unit of work.
  - Publish events after commit (outbox).
  - Exclude unique-constraint violations from retries.

### GAP-06
**Replay causes external side effects.** *(CMD-10, NFR-REL-05)*

- **Evidence.**
  - Replay sends commands through **every** `ICommandDispatcher`, and neither `AwsSqsCommandDispatcher` nor `PubSubCommandDispatcher` checks `Metadata.IsReplay`.
  - Sagas raise events during replay. `IsReplay` lives on the command, not the event, so cloud event dispatchers publish them again.
  - Any `Publish` a saga makes during replay creates a **new**, non-replay command, which gets a new sequence number and is appended.
- **Impact.** Rebuilding one entity can re-send commands and events to other services and grow the log.
- **Suggested change.** Carry an ambient replay context (an AsyncLocal, or a flag copied onto raised events and published commands). Cloud dispatchers should skip it. Saga `Publish` and `Raise` should be suppressed or redirected during replay. Add a test asserting that replay produces no outbound messages and no new log rows.

### GAP-19
**Cloud-received commands are not logged by the owning service.** *(G2, G3, CMD-07)*

- **Evidence.** `AwsSqsCommandListener` and `PubSubCommandListener` call `ICommandSubscriber.Subscribe` directly, not `ICommandBus.Publish`. The receiving service therefore assigns no sequence number, appends nothing to its command store, and runs no `ICommandDispatchMiddleware`. The **sender** appends the command to *its* store, with a sequence number computed from *its* store.
- **Impact.** In a distributed deployment, the service that owns the entity cannot replay it, and sequence numbers depend on whoever sent the command. Goal G2 (rebuild any entity) fails once commands cross a service boundary.
- **Suggested change.** Decide where the command log lives (see Q2). If it lives in the owner, route inbound commands through a receive-side bus path that assigns the sequence, appends and runs the middleware, but does not re-dispatch to the cloud. Have the sender skip appending commands it routes away.

### GAP-20
**New-entity commands are logged under id 0 unless the caller assigns an id.** *(DOM-06, CMD-05)*

- **Evidence.**
  - `Command(bool newEntity, payload)` sets `Entity.Id = 0`.
  - The bus computes the sequence number and appends using `command.Entity.Id`.
  - The saga creates the entity with that id and persists it. The EF store inserts it, so a database-generated key is possible.
  - Nothing writes the real id back to the command. The sample works only because `AccountAggregate.CreateAccount` sets `command.Entity.Id = accountId` explicitly.
- **Impact.** Every unassigned "create" command goes into the log under entity 0, sharing one sequence. Replaying the real entity misses its creation command.
- **Suggested change.** Require ids assigned by the caller or the framework for new entities (validate `IsNew && Id == 0` in `CommandPublisher`), or write the persisted id back to the command before appending. Consider supporting `Guid` ids (see Q4).

### GAP-13
**GCP: shared subscriptions across services, no ordering, no hardening.** *(GCP-04, GCP-05, GCP-07)*

- **Evidence.**
  - Subscription ids are `{topic}{SubscriptionSuffix}`, default `-sub`, so two services subscribing to `order-events` both use `order-events-sub` and **compete** for events instead of each receiving a copy.
  - `PubSubCommandDispatcher` sets `OrderingKey`, but `CreateSubscriptionAsync` doesn't enable message ordering, and the publisher isn't configured for ordering.
  - There is no idempotency check, no dead-letter policy and no encryption wiring (GAP-01).
- **Impact.** Event fan-out between bounded contexts, the main reason to subscribe to a topic, silently breaks with default settings. Per-entity order isn't guaranteed.
- **Suggested change.** Name subscriptions after the service (add `GcpOptions.ServiceName`, or make the suffix required). Create subscriptions with `EnableMessageOrdering = true` and turn on publisher ordering. Configure a dead-letter topic. Wire idempotency, encryption and trace propagation.

---

## P2 findings

### GAP-07
**Saga event-raising failures are swallowed.** *(DOM-08, NFR-REL-06)*
- **Evidence.** In `Saga.RaiseEvent`, the whole build-and-raise step sits inside `try { … } catch (Exception ex) { logger.LogError(…) }`. That includes view-projection and event-dispatch failures, because `Raise` awaits the event queue.
- **Impact.** The entity is persisted and the command is appended, but views and other services never see the event. They drift out of sync, with nothing to trigger a retry.
- **Suggested change.** Let exceptions from building the event bubble up (they are programming errors). Make dispatch failure behaviour a deliberate policy: fail the command, or hand off to an outbox or retry.

### GAP-08
**Singleton domain types capture Scoped dependencies.** *(REG-05)*
- **Evidence.** `UseSourceFlow` registers aggregates, sagas and views as **Singleton** by default. They take `Lazy<ICommandPublisher>` and `IEntityStoreAdapter` / `IViewModelStoreAdapter`, which are registered **Scoped**. The event subscribers are Singletons that hold `IEnumerable<IAggregate>` and `IEnumerable<IView>`.
- **Impact.** With scope validation on (the ASP.NET Core default in Development), resolution throws. With it off, singletons keep the first scope's adapters and their EF `DbContext`, which is not thread-safe and lives for the whole process.
- **Suggested change.** Default domain types to Scoped, or resolve the scoped collaborators per call through `IServiceScopeFactory`. Add a test with `ValidateScopes = true`.

### GAP-09
**Idempotency key is per delivery, not per logical message.** *(IDM-05)*
- **Evidence.** The key is `$"{type}:{message.MessageId}"`. A producer that retries a send gets a new transport `MessageId`.
- **Suggested change.** Key on `Metadata.EventId`, which is set once when the command or event is created and already travels in the body. Keep the transport id as a fallback.

### GAP-10
**No allow-list for inbound message types.** *(SEC-06, NFR-SEC-04)*
- **Evidence.** Listeners call `Type.GetType(attribute)` on the `CommandType` / `EventType` attribute and deserialise into whatever type that resolves to, so long as it is an `ICommand` or `IEvent`.
- **Impact.** System.Text.Json limits gadget-style attacks. Even so, anyone who can write to a queue can make the service create, and run sagas for, any command type it has loaded, including commands that are meant to stay internal.
- **Suggested change.** Accept only the types declared in the bus configuration, or registered as receivable, and dead-letter everything else.

### GAP-11
**Dead-letter story is incomplete.** *(DLQ-05, DLQ-06, NFR-OPS-06)*
- **Evidence.**
  - `IDeadLetterProcessor` has no implementation.
  - The only `IDeadLetterStore` is in-memory and isn't registered.
  - Bootstrappers don't configure an SQS redrive policy or a Pub/Sub dead-letter topic, and the "> 3 receives" threshold in the AWS enhanced listener is hard-coded.
- **Suggested change.** Add native DLQ configuration to the bus builder (for example `.Queue("orders.fifo").WithDeadLetter(maxReceives: 5)`). Provide an EF-backed `IDeadLetterStore` and a provider `IDeadLetterProcessor`. Make the thresholds options.

### GAP-12
**Auto-provisioning cannot be switched off.** *(BUS-07)*
- **Evidence.** The bootstrappers always call create/subscribe APIs (`CreateQueue`, `CreateTopic`, `Subscribe`, `CreateSubscription`).
- **Impact.** Runtime credentials need create permissions, which breaks least privilege. Configuration managed in Terraform or CloudFormation (redrive, encryption, retention) can drift from what the code creates.
- **Suggested change.** Add a `ProvisioningMode` option: `CreateIfMissing` (the default), `ValidateOnly` (fail if missing) or `None`.

---

## P3 findings

### GAP-14
**Metric and log inaccuracies.** *(OBS-04, NFR-OPS-05)*
- `sourceflow.domain.sagas.executed` is never recorded.
- `commands.executed` is recorded by both `CommandBus` and `CommandStoreAdapter.Append`, so it counts twice.
- `entities.created` goes up on every persist, updates included, and is recorded inside a tag-setting callback.
- `Projections.EventSubscriber` logs "No Sagas Found" with `Action=Command_Dispatcher`.
- `EventDispatcher` logs `subscribers.GetType()` (the collection) rather than the individual subscriber.
- The spelling `Event_Disptcher_Aggregate` is wrong.

### GAP-15
**Store "not found" semantics are unspecified.** *(STO-06)*
The interfaces don't say whether `Get` returns null or throws. The EF stores throw `InvalidOperationException`, while custom stores might return null and cause a `NullReferenceException` inside a saga handler. Pick one behaviour (for example `TryGet`, or a dedicated `EntityNotFoundException`) and document it.

### GAP-16
**Assembly-scan fallback rethrows.** *(REG-07)*
In `IocExtensions.GetImplementedTypes`, the `catch (ReflectionTypeLoadException)` block calls `a.GetTypes()` again, which throws again and escapes. Use `ex.Types.Where(t => t != null)`.

### GAP-17
**EF runtime type registration is process-global.** *(EF-05)*
`EntityDbContext` and `ViewModelDbContext` keep registered types and assemblies in `static HashSet`s. Two hosts in one process (common in integration tests) share them and can interfere. Move the registrations into options held by the DI container.

### GAP-18
**Documentation and package-metadata drift.** *(NFR-DOC-03, NFR-RE-03/04, NFR-CMP-02)*
- RawMessageDelivery is documented but not set (GAP-02). `options.Region` is documented but not applied (GAP-03).
- README badges claim .NET Standard targets for the EF package, which its csproj does not have.
- The core csproj description leaves out .NET 8. The EF description says SQL Server only.
- The v2.0.0 changelog says "Release Date: TBC", but the README gives 15 Mar 2026. The changelog doesn't mention the GCP provider, which now exists (beta).
- The README sells "Event Sourcing Foundation — Event-first design with full audit trail", but the product is command-sourced and events aren't stored (see Q1).

---

## Open questions for product decisions

| # | Question | Why it matters |
|---|----------|----------------|
| Q1 | **Command sourcing or event sourcing?** Should the product say clearly that it is *command-sourced*, or should it move toward storing events (with snapshots and upcasters)? | Positioning, the companion book's narrative, audit guarantees, and whether events need a schema policy. |
| Q2 | **Where does the authoritative command log live** when commands cross services: with the sender or with the owning service? | Decides the fix for GAP-19 and how replay works in a distributed system. |
| Q3 | **Should hybrid dispatch be the default?** Today a routed command is also handled by any matching local saga. Many users expect "routed means remote only". | A per-route option (`.Queue(...).RemoteOnly()`) would prevent double handling without relying on idempotency. |
| Q4 | **What id strategy?** `IEntity.Id` and `IViewModel.Id` are `int`. Should the framework support `Guid` or string ids and generate ids for new entities? | Distributed id allocation, GAP-20, and cross-service references. |
| Q5 | **GA bar for GCP.** Is parity with AWS (GAP-01, GAP-13) required before 2.0.0 GA, or will it ship as beta? | Release planning and changelog accuracy. |
| Q6 | **Azure provider scope and date.** The README lists it as v2.0.0 (TBC). Is it still in scope for 2.x? | Roadmap communication. |
| Q7 | **Consistency model.** Should the framework offer transactional guarantees (unit of work plus outbox), or document eventual consistency and leave it to the user? | Drives the fixes for GAP-05 and GAP-07, and the store contract changes they need. |
