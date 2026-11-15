# 04 — Cloud Messaging Requirements

This document covers the cloud abstractions in the core package ([`src/SourceFlow/Cloud`](../../src/SourceFlow/Cloud)) and the two providers, [`SourceFlow.Cloud.AWS`](../../src/SourceFlow.Cloud.AWS) and [`SourceFlow.Cloud.GCP`](../../src/SourceFlow.Cloud.GCP). The status legend is in the [README](README.md#status-values).

---

## 1. Concept

A service talks to other bounded contexts through four kinds of route, declared once at startup:

| Direction | Builder call | Meaning | AWS resource | GCP resource |
|-----------|--------------|---------|--------------|--------------|
| Outbound command | `.Send.Command<T>(q => q.Queue("orders.fifo"))` | Commands of type T go to this queue | SQS queue | Pub/Sub topic |
| Outbound event | `.Raise.Event<T>(t => t.Topic("order-events"))` | Events of type T go to this topic | SNS topic | Pub/Sub topic |
| Inbound commands | `.Listen.To.CommandQueue("orders.fifo")` | This service handles commands arriving here | SQS queue (polled) | Pull subscription `orders.fifo-sub` |
| Inbound events | `.Subscribe.To.Topic("payment-events")` | This service handles events from this topic | SNS→SQS subscription on the **first** listen queue | Pull subscription `payment-events-sub` |

Cloud dispatchers run **alongside** the local dispatchers (EXT-04). A routed command is sent to the cloud **and** handled by any matching local saga. This "hybrid" behaviour is intended and documented in [Architecture 04](../legacy-version/Architecture/04-Current-Dispatching-Patterns.md#hybrid-processing-pattern). A message type with no route is skipped by the cloud dispatcher.

---

## 2. Bus configuration and bootstrapping (`BUS`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| BUS-01 | One provider-neutral fluent builder (`BusConfigurationBuilder`) shall declare all four kinds of route, and every provider shall consume it. | Implemented | [BusConfiguration.cs](../../src/SourceFlow/Cloud/Configuration/BusConfiguration.cs) |
| BUS-02 | Routes shall take **short names only**. The builder shall reject empty names, URLs (`http(s)://…`) and ARNs (`arn:…`) with an error that shows the expected form. | Implemented | `CommandEndpointBuilder.Queue`, `EventEndpointBuilder.Topic`, `ListenToConfigurationBuilder`, `SubscribeToConfigurationBuilder` |
| BUS-03 | Routing lookups (`ShouldRoute`, `GetQueueName`, `GetTopicName`, listening queues, subscribed topics) shall throw a clear error if called before bootstrapping. | Implemented | `BusConfiguration.EnsureResolved` |
| BUS-04 | Asking for the queue or topic of an unrouted type shall throw an error that shows the builder call needed to fix it. | Implemented | same |
| BUS-05 | Each provider shall register a bootstrapper as a hosted service, **before** its listeners, that resolves every short name to a full resource id and creates any missing resource (get-or-create, safe to repeat). | Implemented | [AwsBusBootstrapper.cs](../../src/SourceFlow.Cloud.AWS/Infrastructure/AwsBusBootstrapper.cs), [GcpBusBootstrapper.cs](../../src/SourceFlow.Cloud.GCP/Infrastructure/GcpBusBootstrapper.cs) |
| BUS-06 | Bootstrapping shall fail fast on configuration that cannot work: on AWS, subscribing to topics with no listen queue; on GCP, a missing `ProjectId`. | Implemented | bootstrappers |
| BUS-07 | Teams that manage infrastructure as code shall be able to switch off automatic resource creation (validate only). | Planned / not implemented ([GAP-12](06-Gaps-Risks-and-Open-Questions.md#gap-12)) | — |

## 3. Idempotency (`IDM`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| IDM-01 | The core shall define `IIdempotencyService` with `HasProcessed`, `MarkAsProcessed(key, ttl)`, `Remove` and `GetStatistics`. | Implemented | [IIdempotencyService.cs](../../src/SourceFlow/Cloud/Configuration/IIdempotencyService.cs) |
| IDM-02 | An in-memory implementation with TTL expiry and a cleanup service that runs every minute shall be the default when nothing else is configured. | Implemented | [InMemoryIdempotencyService.cs](../../src/SourceFlow/Cloud/Configuration/InMemoryIdempotencyService.cs) |
| IDM-03 | `IdempotencyConfigurationBuilder` shall offer `UseInMemory()`, `UseEFIdempotency(cs, interval)`, `UseCustom<T>()` and `UseCustom(factory)`, and each provider's registration call shall accept it. | Implemented | [IdempotencyConfigurationBuilder.cs](../../src/SourceFlow/Cloud/Configuration/IdempotencyConfigurationBuilder.cs) |
| IDM-04 | Every inbound listener shall check the idempotency service **before** handling a message, mark the message processed (24 h TTL) **after** handling it successfully, and acknowledge and drop duplicates. | Partial — only the AWS `*Enhanced` listeners do this, and they are not registered by default. The default AWS listeners and the GCP listeners never consult the idempotency service, even though it is registered ([GAP-01](06-Gaps-Risks-and-Open-Questions.md#gap-01)) | [AwsSqsCommandListenerEnhanced.cs](../../src/SourceFlow.Cloud.AWS/Messaging/Commands/AwsSqsCommandListenerEnhanced.cs) |
| IDM-05 | The idempotency key shall identify the **logical message**, not one transport delivery, so that a producer retry is also caught. | Partial — the key is `"{type}:{transport MessageId}"`. `Metadata.EventId` (a GUID set when the message is created) would identify the logical message ([GAP-09](06-Gaps-Risks-and-Open-Questions.md#gap-09)) | same |

## 4. Dead-letter handling (`DLQ`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| DLQ-01 | The core shall define a provider-neutral `DeadLetterRecord` (message id, body, type, reason, error, source, provider, delivery count, exception details, metadata, replay state), plus `IDeadLetterStore` with save, get, query (filter and page), count, mark-replayed, delete and purge-older-than. | Implemented | [DeadLetter/](../../src/SourceFlow/Cloud/DeadLetter) |
| DLQ-02 | An in-memory `IDeadLetterStore` shall be provided. | Implemented — but not registered by any provider ([GAP-01](06-Gaps-Risks-and-Open-Questions.md#gap-01)) | [InMemoryDeadLetterStore.cs](../../src/SourceFlow/Cloud/DeadLetter/InMemoryDeadLetterStore.cs) |
| DLQ-03 | Listeners shall write a dead-letter record when a message is missing its type attribute, names a type that cannot be resolved, fails to deserialise, or fails processing after more than 3 deliveries. | Opt-in (AWS `*Enhanced` listeners only) | `AwsSqsCommandListenerEnhanced.CreateDeadLetterRecord` |
| DLQ-04 | A background monitor shall poll the configured dead-letter queues (every 60 s by default), record their messages, raise alerts above a threshold (default 10), and support filtered replay back to the source queue. | Opt-in (AWS only; not registered) | [AwsDeadLetterMonitor.cs](../../src/SourceFlow.Cloud.AWS/Monitoring/AwsDeadLetterMonitor.cs) |
| DLQ-05 | `IDeadLetterProcessor` (process, replay with a filter, statistics) shall have at least one implementation per provider. | Not implemented — the interface has no implementation ([GAP-11](06-Gaps-Risks-and-Open-Questions.md#gap-11)) | [IDeadLetterProcessor.cs](../../src/SourceFlow/Cloud/DeadLetter/IDeadLetterProcessor.cs) |
| DLQ-06 | Bootstrapping shall be able to set up native dead-letter routing (SQS redrive policy, Pub/Sub dead-letter topic). | Not implemented ([GAP-11](06-Gaps-Risks-and-Open-Questions.md#gap-11)) | — |
| DLQ-07 | Messages that can never succeed ("poison" messages: unknown type, bad JSON) shall not be redelivered forever. | Implemented — the default listeners delete or acknowledge them after logging. Without the opt-in AWS listeners, no dead-letter record is kept | listeners |

## 5. Resilience (`RES`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| RES-01 | The core shall provide a circuit breaker (Closed → Open → HalfOpen) with failure threshold (default 5), open duration (1 min), success threshold to close (2), operation timeout (30 s), handled and ignored exception lists, state-change events, manual `Trip` and `Reset`, and statistics. | Implemented | [CircuitBreaker.cs](../../src/SourceFlow/Cloud/Resilience/CircuitBreaker.cs), [CircuitBreakerOptions.cs](../../src/SourceFlow/Cloud/Resilience/CircuitBreakerOptions.cs) |
| RES-02 | Outbound cloud dispatchers shall run through the circuit breaker and throw `CircuitBreakerOpenException` while it is open. | Opt-in (AWS `*Enhanced` dispatchers only) | [AwsSqsCommandDispatcherEnhanced.cs](../../src/SourceFlow.Cloud.AWS/Messaging/Commands/AwsSqsCommandDispatcherEnhanced.cs) |
| RES-03 | Listener poll loops shall survive transport errors, backing off exponentially (2ⁿ s, capped at 60 s), and reset after a successful receive. | Implemented (AWS and GCP) | listeners |
| RES-04 | Messages that fail during processing shall **not** be acknowledged, so the transport redelivers them (SQS after the visibility timeout, Pub/Sub after the ack deadline). | Implemented | listeners |
| RES-05 | The SDK retry settings in provider options (`MaxRetries`, `RetryDelay`) shall be applied to the cloud clients. | Not implemented — the options are not applied ([GAP-03](06-Gaps-Risks-and-Open-Questions.md#gap-03)) | `AwsOptions`, `GcpOptions` |

## 6. Message security (`SEC`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| SEC-01 | The core shall define `IMessageEncryption` (`EncryptAsync`, `DecryptAsync`, `AlgorithmName`, `KeyIdentifier`), `EncryptionOptions`, and `MessageDecryptionException`. | Implemented | [Security/](../../src/SourceFlow/Cloud/Security) |
| SEC-02 | Each provider shall implement **envelope encryption**: a data key from the provider's KMS, AES-256-GCM over the message body, and a JSON envelope (wrapped key, nonce, tag, ciphertext). Data keys shall be cached (300 s by default; 0 turns caching off). | Implemented (AWS KMS, GCP Cloud KMS) | [AwsKmsMessageEncryption.cs](../../src/SourceFlow.Cloud.AWS/Security/AwsKmsMessageEncryption.cs), [GcpKmsMessageEncryption.cs](../../src/SourceFlow.Cloud.GCP/Security/GcpKmsMessageEncryption.cs) |
| SEC-03 | When an `IMessageEncryption` is registered, dispatchers shall encrypt outbound bodies and listeners shall decrypt inbound bodies. | Opt-in (AWS `*Enhanced` only); not wired on GCP ([GAP-01](06-Gaps-Risks-and-Open-Questions.md#gap-01)) | — |
| SEC-04 | Properties marked `[SensitiveData(type)]` shall be masked in logs, by type: credit card, email, phone, SSN, personal name, IP address, password, API key, or custom. Masking shall be lazy, so values are formatted only when the log line is written. | Implemented (utility); used by AWS `*Enhanced` only | [SensitiveDataMasker.cs](../../src/SourceFlow/Cloud/Security/SensitiveDataMasker.cs) |
| SEC-05 | Credentials shall come from the provider SDK's standard credential chain. Credential fields in configuration shall be marked obsolete. | Implemented (AWS: `AccessKeyId`, `SecretAccessKey`, `SessionToken` are `[Obsolete]`; GCP: Application Default Credentials) | [AwsOptions.cs](../../src/SourceFlow.Cloud.AWS/Configuration/AwsOptions.cs) |
| SEC-06 | Wire messages shall not let a remote sender instantiate arbitrary types. | Partial — listeners resolve types from the assembly-qualified name in the message attributes, with no allow-list. Only types routed in the bus configuration should be accepted ([GAP-10](06-Gaps-Risks-and-Open-Questions.md#gap-10)) | listeners |

## 7. Cloud telemetry and health (`CTL`)

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| CTL-01 | The core shall provide a `SourceFlow.Cloud` ActivitySource and semantic-convention constants (`messaging.*`, `sourceflow.*`, `cloud.*`). | Implemented | [CloudActivitySource.cs](../../src/SourceFlow/Cloud/Observability/CloudActivitySource.cs) |
| CTL-02 | The core shall provide `CloudMetrics`: counters for commands dispatched, processed, succeeded and failed, events published and received, and duplicates detected; histograms for dispatch, processing and publish duration and message size; gauges for queue depth, DLQ depth and active processors. | Implemented (utility); recorded by AWS `*Enhanced` only | [CloudMetrics.cs](../../src/SourceFlow/Cloud/Observability/CloudMetrics.cs) |
| CTL-03 | Trace context (`traceparent`) shall travel with messages so that one trace covers the sending and receiving services. | Partial — the AWS enhanced components read and write `traceparent`; the default AWS components and the GCP components do not | `AwsSqsCommandListenerEnhanced.ExtractTraceParent` |
| CTL-04 | Each provider shall register an `IHealthCheck` that checks the messaging endpoint can be reached. | Implemented (AWS: SQS and SNS; GCP: Pub/Sub) | [AwsHealthCheck.cs](../../src/SourceFlow.Cloud.AWS/Infrastructure/AwsHealthCheck.cs), [GcpHealthCheck.cs](../../src/SourceFlow.Cloud.GCP/Infrastructure/GcpHealthCheck.cs) |

---

## 8. AWS provider (`AWS`) — `SourceFlow.Cloud.AWS` 2.0.0

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| AWS-01 | `services.UseSourceFlowAws(configureOptions, configureBus, configureIdempotency?)` shall register options, SQS and SNS clients, bus configuration, idempotency, dispatchers, bootstrapper, listeners and health check. | Implemented | [IocExtensions.cs](../../src/SourceFlow.Cloud.AWS/IocExtensions.cs) |
| AWS-02 | `AwsOptions.Region` shall select the region for every AWS client. | **Not implemented** — the clients come from `AddAWSService<T>()` (SDK default config), and the factories that would apply `Region` and `MaxRetries` are never called ([GAP-03](06-Gaps-Risks-and-Open-Questions.md#gap-03)) | [SqsClientFactory.cs](../../src/SourceFlow.Cloud.AWS/Infrastructure/SqsClientFactory.cs) |
| AWS-03 | `EnableCommandRouting` and `EnableEventRouting` shall switch outbound routing on or off. | Not implemented — the options exist but nothing reads them ([GAP-03](06-Gaps-Risks-and-Open-Questions.md#gap-03)) | `AwsOptions` |
| AWS-04 | The bootstrapper shall get or create every SQS queue. A name ending in `.fifo` shall create a FIFO queue with content-based deduplication. | Implemented | `AwsBusBootstrapper.GetOrCreateQueueAsync` |
| AWS-05 | The bootstrapper shall create the SNS topics and subscribe each subscribed topic to the **first** listen queue. | Partial — subscriptions are created without `RawMessageDelivery`, although the docs say it is set; events and commands then share one queue ([GAP-02](06-Gaps-Risks-and-Open-Questions.md#gap-02)) | `AwsBusBootstrapper` |
| AWS-06 | The command dispatcher shall send camelCase JSON with the attributes `CommandType` (assembly-qualified name), `EntityId` and `SequenceNo`, using `MessageGroupId = EntityId` so that FIFO queues keep per-entity order. | Implemented | [AwsSqsCommandDispatcher.cs](../../src/SourceFlow.Cloud.AWS/Messaging/Commands/AwsSqsCommandDispatcher.cs) |
| AWS-07 | The event dispatcher shall publish camelCase JSON to SNS with the attributes `EventType` and `EventName`, and the subject set to the event name. | Implemented | [AwsSnsEventDispatcher.cs](../../src/SourceFlow.Cloud.AWS/Messaging/Events/AwsSnsEventDispatcher.cs) |
| AWS-08 | Listeners shall long-poll each queue in its own loop (wait 20 s, up to 10 messages, 300 s visibility timeout, all configurable), handle each message in a new DI scope, and delete it after it succeeds. | Implemented | [AwsSqsCommandListener.cs](../../src/SourceFlow.Cloud.AWS/Messaging/Commands/AwsSqsCommandListener.cs), [AwsSnsEventListener.cs](../../src/SourceFlow.Cloud.AWS/Messaging/Events/AwsSnsEventListener.cs) |
| AWS-09 | The event listener shall unwrap the SNS notification envelope before deserialising the event, and deliver the event to every `IEventSubscriber`. | Implemented | `AwsSnsEventListener.ProcessMessage` |
| AWS-10 | The provider shall offer a production pipeline (idempotency, dead-letter records, KMS decryption, circuit breaker, metrics, trace propagation, masked logging) **selectable through the registration API**. | Opt-in — the `*Enhanced` classes exist but `UseSourceFlowAws` has no switch for them ([GAP-01](06-Gaps-Risks-and-Open-Questions.md#gap-01)) | `Messaging/*/*Enhanced.cs` |
| AWS-11 | The provider shall be tested against LocalStack (unit and integration categories) in CI. | Implemented | [Master-Build.yml](../../.github/workflows/Master-Build.yml), `tests/SourceFlow.Cloud.AWS.Tests` |

## 9. GCP provider (`GCP`) — `SourceFlow.Cloud.GCP` 2.0.0-beta.1

| ID | Requirement | Status | Source |
|----|-------------|--------|--------|
| GCP-01 | `services.UseSourceFlowGcp(configureOptions, configureBus, configureIdempotency?)` shall register Pub/Sub publisher and subscriber clients, bus configuration, idempotency, dispatchers, bootstrapper, listeners (each can be switched off with `EnableCommandListener` / `EnableEventListener`) and health check. | Implemented | [IocExtensions.cs](../../src/SourceFlow.Cloud.GCP/IocExtensions.cs) |
| GCP-02 | Clients shall pick up the Pub/Sub emulator automatically when `PUBSUB_EMULATOR_HOST` is set. | Implemented | [PubSubClientFactory.cs](../../src/SourceFlow.Cloud.GCP/Infrastructure/PubSubClientFactory.cs) |
| GCP-03 | The bootstrapper shall make sure that a topic exists for every command queue name and event topic name, and that a pull subscription named `{name}{SubscriptionSuffix}` (default `-sub`) exists for every listen queue and subscribed topic, using the configured ack deadline (default 60 s). | Implemented | [GcpBusBootstrapper.cs](../../src/SourceFlow.Cloud.GCP/Infrastructure/GcpBusBootstrapper.cs) |
| GCP-04 | Each service subscribing to a topic shall get its own copy of every event (fan-out). | Partial — the subscription name comes only from the topic name and suffix, so two services with the default suffix **share** one subscription and compete for events ([GAP-13](06-Gaps-Risks-and-Open-Questions.md#gap-13)) | `GcpBusBootstrapper.SubscriptionId` |
| GCP-05 | Commands shall be published with `OrderingKey = EntityId` and delivered in order per entity. | Partial — the ordering key is set, but subscriptions are not created with message ordering enabled, so order is not guaranteed ([GAP-13](06-Gaps-Risks-and-Open-Questions.md#gap-13)) | [PubSubCommandDispatcher.cs](../../src/SourceFlow.Cloud.GCP/Messaging/Commands/PubSubCommandDispatcher.cs) |
| GCP-06 | Listeners shall pull up to `MaxMessagesPerPull` (default 10), wait `EmptyPullDelay` (1 s) when nothing arrives, acknowledge on success or poison message, and leave failures unacknowledged so they are redelivered. | Implemented | [PubSubCommandListener.cs](../../src/SourceFlow.Cloud.GCP/Messaging/Commands/PubSubCommandListener.cs), [PubSubEventListener.cs](../../src/SourceFlow.Cloud.GCP/Messaging/Events/PubSubEventListener.cs) |
| GCP-07 | The provider shall reach feature parity with AWS on idempotency, dead letters, encryption wiring, circuit breaker and trace propagation before it leaves beta. | Not met ([GAP-01](06-Gaps-Risks-and-Open-Questions.md#gap-01)) | — |
| GCP-08 | The provider shall be tested against the Pub/Sub emulator in CI. | Implemented | [GCP-Build.yml](../../.github/workflows/GCP-Build.yml) |

## 10. Provider parity matrix

| Capability | AWS (default wiring) | AWS (`*Enhanced`, manual wiring) | GCP |
|------------|:---:|:---:|:---:|
| Route, send and publish | ✔ | ✔ | ✔ |
| Auto-provision resources | ✔ | ✔ | ✔ |
| Per-entity ordering | ✔ (FIFO queues) | ✔ | ✘ (ordering not enabled on subscriptions) |
| Idempotent receive | ✘ | ✔ | ✘ |
| Dead-letter records | ✘ | ✔ | ✘ |
| Envelope encryption | ✘ | ✔ | ✘ (class exists, not wired) |
| Circuit breaker on send | ✘ | ✔ | ✘ |
| Cloud metrics and trace propagation | ✘ | ✔ | ✘ |
| Health check | ✔ | ✔ | ✔ |
| Emulator / local CI | LocalStack | LocalStack | Pub/Sub emulator |
| Azure Service Bus | — | — | — (Planned as a separate provider) |

---

## Acceptance scenarios (cloud)

1. **Config validation.** `.Send.Command<X>(q => q.Queue("https://…"))` throws `ArgumentException` at startup and names the expected short form.
2. **Bootstrap.** On an empty LocalStack, or the Pub/Sub emulator, startup creates every declared queue, topic and subscription. Starting again creates nothing new and succeeds.
3. **Command round-trip.** Service A routes `CreateOrder` to `orders.fifo`. Service B listens there. Publishing in A leads to B's `OrderSaga` handling the command exactly once, and the message is deleted or acknowledged.
4. **Event fan-out.** Services B and C both subscribe to `order-events`. An event raised in A reaches B's views **and** C's views. (This fails today on GCP with the default suffix — GAP-13.)
5. **Poison message.** A message with an unknown `CommandType` is logged and removed, and is not redelivered.
6. **Transient failure.** A saga that throws once leaves the message unacknowledged. It is redelivered after the visibility timeout or ack deadline, and handled once.
7. **Duplicate.** With idempotency wired, the same transport message delivered twice is handled once, and `sourceflow.duplicates.detected` goes up.
