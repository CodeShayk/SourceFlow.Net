# 07 — Cloud Transports

Design drivers: [ADR-011](../ADRs/ADR-011-cloudevents-envelope-and-w3c-trace-context.md), [ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md) and [ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md). Fixes: GAP-01, GAP-02, GAP-03, GAP-11, GAP-12, GAP-13, AWS-02/03/05, GCP-04/05/07 and RES-05.

## 1. Principle: thin transports, smart core

v2 put retries, idempotency, dead-lettering, encryption and telemetry *inside* each provider's dispatchers and listeners, in two variants (basic and "Enhanced"). That produced divergence and unwired features. In v3 a transport does only what is broker-specific: move bytes and headers, settle messages, and provision resources. Everything else is core middleware (doc 04), written once and identical on every broker.

## 2. Transport SPI

> **As built ([ADR-033](../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)).** The SPI is pull-based: `ITransport` has `SendAsync`, `PublishAsync`, `CreateReceiverAsync`, `CreateDeadLetterReceiverAsync` and `ProvisionAsync`. `ITransportReceiver.ReceiveAsync(max, wait)` feeds the core's receive loop, and `ReceivedMessage` settles with `CompleteAsync`, `AbandonAsync(delay)`, `RenewAsync` or `DeadLetterAsync(reason)`.
>
> `TransportCapabilities` adds `OrderedDelivery`, `NativeDeadLetter`, `SubscriptionFilters`, `MaxBatchSize`, `Deduplication` and `MaxLockDuration`. Errors are typed: `TransportException.IsTransient`, a per-message `TransportSendException`, and `TopologyValidationException.Problems`.
>
> The callback-based sketch below is the original design input.

```csharp
public interface ITransport
{
    string Name { get; }                                   // "aws", "azure-servicebus", "gcp-pubsub", "in-memory"
    TransportCapabilities Capabilities { get; }
    ITransportSender Sender { get; }
    ITopologyProvisioner Provisioner { get; }
    ITransportReceiver CreateReceiver(EndpointTopology endpoint, ReceiverSettings settings);
}

public interface ITransportSender
{
    // Batches are per destination and already ordered per partition key by the outbox relay
    ValueTask<SendResult> SendAsync(Destination destination, ReadOnlyMemory<OutgoingMessage> batch, CancellationToken ct);
}

public interface ITransportReceiver : IAsyncDisposable
{
    ValueTask StartAsync(ReceiveCallback onMessage, CancellationToken ct);
    ValueTask StopAsync(CancellationToken ct);
}

public delegate ValueTask ReceiveCallback(IReceivedMessage message, CancellationToken ct);

public interface IReceivedMessage
{
    MessageEnvelope Envelope { get; }          // already mapped from native headers (binary or structured mode)
    int DeliveryAttempt { get; }
    ValueTask CompleteAsync(CancellationToken ct);
    ValueTask RetryLaterAsync(TimeSpan delay, CancellationToken ct);   // native delay or core fallback
    ValueTask DeadLetterAsync(string reason, CancellationToken ct);    // native DLQ (backstop path only)
    ValueTask RenewAsync(CancellationToken ct);
}

public sealed record TransportCapabilities(
    int MaxMessageBytes, int MaxHeaderCount, bool SupportsOrderedDelivery, bool SupportsNativeDelay,
    TimeSpan MaxNativeDelay, bool SupportsNativeDeadLetter, bool SupportsSubscriptionFilters,
    bool SupportsBatchSend, TimeSpan MaxRetention);
```

That is about six interfaces, which meets the maintainability target in [doc 01](01-Architecture-Overview.md#2-quality-goals).

## 3. Topology model

`Routing` (doc 04 §3) compiles to a provider-neutral `Topology`:

| Element | Meaning | Owned by |
|---------|---------|----------|
| `Endpoint(name, Ordered, MaxDeliveries, Retention, Dlq)` | A service's input queue. It receives commands sent to the service **and** events from topics the service subscribes to. | The owning service |
| `Topic(name, Ordered)` | A publication channel for integration events | The publishing service |
| `Subscription(topic → endpoint, Filter)` | Fan-out: **one per subscribing service**. Instances of that service compete on its endpoint. | The subscribing service |
| `DeadLetter(endpoint)` | The native backstop DLQ | The owning service |

Physical names come from `INamingConvention`: by default `{environment}-{logical}`, plus provider suffixes (`.fifo`), limited to provider character sets and lengths. They are deterministic, so two services resolve the same topic name.

Ordered endpoints and topics are opt-in per endpoint or topic (`.Ordered()`). The core always serialises per partition key inside an instance (doc 04 RX-R1). Broker ordering adds ordering *across* instances.

> **As built (WP-3.5).** The logical model is `TransportTopology` (ADR-033), built by `SourceFlowRuntime.Topology()` from routing:
> - Ordering is declared with `RoutingBuilder.OrderedEndpoint()` and `OrderedTopic(topic)`. A topic's ordering belongs to its publisher, so `OrderedTopic` on a topic the service does not publish fails at startup.
> - Physical names are not pluggable. Each transport's naming is fixed and deterministic (`AwsNaming`, `GcpNaming`, `AzureNaming`) with a `NamePrefix` option for the environment. A custom convention would have to be applied identically by every service and every IaC module, so instead the transports *describe* their names: `ITopologyDescriber.Describe(topology)` returns the `PhysicalTopology` (`AwsTopology`, `GcpTopology`, `AzureTopology`, and the in-memory transport), computed from the options without a broker call.
> - The `DeadLetter(endpoint)` backstop is drained by the `DeadLetterIngestor`, which runs on receiver instances as its own loop. It drains while messages arrive, then checks every `DeadLetterIngestInterval` (30 s by default). Commits do not wake it, because each check is a broker call. After a broker error it recreates its receiver and backs off, from 1 s doubling up to the interval.

## 4. Provider mappings

| Concern | AWS (SNS + SQS) | Azure Service Bus | Google Pub/Sub | In-memory |
|---------|-----------------|-------------------|----------------|-----------|
| Endpoint | SQS queue (`.fifo` if ordered) | Queue (sessions on if ordered) | Topic `{svc}` + subscription `{svc}` for commands, plus one subscription per subscribed topic (see Subscription row). **One receiver** pulls from all of them. Pub/Sub has no forwarding, so the endpoint is a set of subscriptions. | `Channel<T>` |
| Topic | SNS topic (FIFO topic if ordered) | Topic | Topic | In-proc fan-out |
| Subscription | SNS→SQS subscription with **`RawMessageDelivery=true`** and a filter policy on `ce_type`. A queue policy allows the topic. | Subscription `{svc}` with `ForwardTo = {svc}` queue and a correlation filter on `type` | Subscription `{svc}.{topic}`, filter `attributes.ce_type` | Subscriber list |
| Ordering | FIFO: `MessageGroupId = partitionKey`. **Validated**: a FIFO queue requires a FIFO topic and vice versa | `SessionId = partitionKey`, session receiver | `enable_message_ordering = true` on the subscription, plus publisher `OrderingKey` | Per-key channel |
| Broker dedup (complementary to the inbox) | FIFO: `MessageDeduplicationId = MessageId` (5 min) | `RequiresDuplicateDetection`, `MessageId` | Optional exactly-once delivery on the subscription | — |
| Native delay | `ChangeMessageVisibility` (≤ 12 h) | Re-send with `ScheduledEnqueueTime` and complete the original | `ModifyAckDeadline` (≤ 600 s), then core fallback | Timer |
| Native DLQ (backstop) | Redrive policy to `{queue}-dlq`, `maxReceiveCount = runtime budget + 2` | Built-in `$DeadLetterQueue`, `MaxDeliveryCount = budget + 2` | `dead_letter_policy → {sub}.dlq`, `max_delivery_attempts = budget + 2` | List |
| Lock renewal | `ChangeMessageVisibility` heartbeat | Processor auto-renew | Streaming pull lease management | — |
| CloudEvents binding | Message attributes (`ce_*`, maximum 10). Overflow extensions go in one `sf_ext` JSON attribute | `ApplicationProperties` (`cloudEvents:*`) | Attributes (`ce-*`) | Object |
| Batch send | `SendMessageBatch` / `PublishBatch` (10) | `ServiceBusMessageBatch` | Publisher batching | — |
| Emulator for CI | LocalStack (pinned image) | Service Bus emulator | Pub/Sub emulator | — |

> **As built (WP-3.2, AWS).** `SourceFlow.Cloud.Aws` follows the AWS column, with these details fixed by the implementation:
>
> - **Attributes.** CloudEvents attributes travel as String message attributes named `ce_{attribute}` (`ce_type`, `ce_sftenant`, …), and the overflow is `ce_sfext`.
> - **Transport attributes.** Three attributes count against the 10-attribute budget only when used:
>   - `sf_body`: `base64` or `empty`, for bodies SQS cannot carry as text;
>   - `sf_topic`: the logical topic, on every published event;
>   - `sf_reason`: on an explicit dead letter.
> - **Bodies.** A body travels as UTF-8 text when SQS can carry it unchanged (valid UTF-8 and XML characters only); otherwise it travels as base64. Bodies are therefore never transcoded (TCK-42).
> - **Size limits.** `MaxBodyBytes` is 180 KiB, so a base64 body plus its attributes fits the 256 KiB message and batch limit. Larger encoded messages are refused before sending.
> - **Filters.**
>   - `ExactTypes` and `TypePrefixes` filters become an SNS filter policy on `ce_type`.
>   - Other CESQL filters leave the subscription unfiltered. The receiver evaluates them per topic, using `sf_topic`, and acknowledges non-matching events unseen.
>   - So `SubscriptionFilters` is declared true, and results match the reference evaluator (TCK-26..28).
> - **Endpoint resolution.** An endpoint resolves to `{base}` or `{base}.fifo`, whichever exists, so senders do not need to know the owner's ordering.
> - **Topic lookup.** Topics are found with `ListTopics`, because SNS has no lookup by name. This needs describe permission only.
> - **Dead letters.**
>   - An explicit `DeadLetterAsync` sends a copy carrying `sf_reason` to the redrive target, then deletes the original.
>   - A message redriven by the broker reports `MaxReceiveCountExceeded`.
> - **Broker deduplication.** It is not declared, because standard queues do not deduplicate. FIFO queues still drop a re-sent message id for 5 minutes.
> - **Late settlement.** A settlement after the visibility timeout expired, made with a stale receipt handle, is ignored.
> - **Shutdown.** Disposing a receiver makes its unsettled messages visible again at once.
> - **A failure partway through a receive** (hiding a dead letter, deleting a filtered event) makes every message that receive took and has not settled visible again before the error goes on, and forgets them (WP-C.6, CR-42).

> **As built (WP-3.3, Google Pub/Sub).** `SourceFlow.Cloud.Gcp` follows the Google Pub/Sub column, with these details fixed by the implementation:
>
> - **Ids.** Topics and subscriptions share one id namespace per project, so ids the transport derives use separators that logical names never contain (`~` and `+` in logical names become `-`):
>   - an event topic is `{topic}`;
>   - an endpoint is the topic `{endpoint}+in` with the subscription `{endpoint}`;
>   - a service's subscription to a topic is `{endpoint}~{topic}` (the doc's `{svc}.{topic}`, made unambiguous, since `.` is common in logical names);
>   - the dead-letter topic and its subscription are `{endpoint}+dlq`.
>
>   Ids start with a letter and fit 255 characters (long ones keep a readable head and a hash).
> - **Attributes.** CloudEvents attributes travel as `ce-{attribute}` (the CloudEvents Pub/Sub binding). Pub/Sub limits a value to 1,024 bytes, so longer values (a long subject, the user headers) are split into `ce-{attribute}#0`, `#1`, … and joined on receipt. The body is the message data, byte for byte, up to 9 MiB (10 MB per publish request).
> - **Ordering.** The publisher always sets the ordering key to the partition key. The subscription decides: ordered endpoints enable message ordering on every one of their subscriptions, and Validate checks it (GAP-13). A zero-delay abandon on an ordered subscription uses a one-second ack deadline instead of a nack: the emulator redelivers a nacked ordered message after its successor, while an expiring lease keeps the order everywhere.
> - **Receiving.** A receiver uses synchronous pull with immediate return, taking the endpoint's subscriptions in turn, and polls every `PollInterval` while it waits. It does not use streaming pull with automatic lease extension, because a lease must lapse when a process dies holding a message (TCK-22). The receiver sets each message's ack deadline to its lock duration (at most 10 minutes); abandon sets it to the delay (native delay up to 10 minutes); renew extends it. A late ack is ignored by Pub/Sub, so a lapsed lock is detected by the receiver on renew. A failure partway through a pull round returns every message the round leased and has not settled (deadline set to the release value) before the error goes on, and forgets them (WP-C.6, CR-42).
> - **Dead letters.**
>   - Every endpoint subscription has a dead-letter policy to `{endpoint}+dlq`.
>   - Pub/Sub accepts 5 to 100 deliveries, so the policy holds the clamped value and the label `sf-max-deliveries` holds the topology's limit. The receiver moves a message delivered more often than that to the dead-letter topic itself, with reason `MaxDeliveryAttemptsExceeded`. That also covers projects where the Pub/Sub service agent lacks the publisher grant on the dead-letter topic (the Terraform module grants it, WP-3.5).
>   - A message that is not a SourceFlow CloudEvent is moved at once (`MalformedMessage: …`).
>   - An explicit `DeadLetterAsync` publishes a copy carrying `sf-reason` and acknowledges the original.
> - **Filters.** `ExactTypes` and `TypePrefixes` become a Pub/Sub filter on `attributes.ce-type` when it fits the 256-byte limit. Other CESQL filters leave the subscription unfiltered and are evaluated by the receiver for that subscription, which acknowledges non-matching events unseen. Pub/Sub filters, like ordering, are immutable: drift is reported, never repaired in place.
> - **Subscriptions.** New subscriptions have a 30-second ack deadline (the default lock, so most pulls need no deadline change), retention from `MessageRetention` (at most 7 days), and no expiry. Validate reports a subscription that expires without receivers.
> - **Broker deduplication.** It is not declared: publishing has no deduplication, and the inbox absorbs duplicates.

> **As built (WP-3.4, Azure Service Bus).** `SourceFlow.Cloud.Azure` follows the Azure Service Bus column, with these details fixed by the implementation:
>
> - **Names.** An endpoint is the queue `{endpoint}`, a topic is the topic `{topic}`, and a service's subscription on a topic is named after its endpoint (at most 50 characters; long names keep a readable head and a hash). Queues and topics share one name space in a namespace, so Validate names a queue that occupies a topic's name.
> - **Wire.** CloudEvents attributes travel as application properties `cloudEvents:{attribute}` (the AMQP binding), and the body byte for byte up to 192 KiB (256 KiB messages on the standard tier). The broker message id is the envelope id, the session id is the partition key (the message id without one), and the subject is the CloudEvents type. Published events carry `sf-topic`, so the receiver can apply a filter that rules cannot express.
> - **Rules.** `ExactTypes` become one correlation rule per type on `cloudEvents:type`; `TypePrefixes` become SQL `LIKE` rules. Other CESQL filters get a match-all rule and are evaluated by the receiver, which completes non-matching events unseen.
> - **Ordering.** Ordered endpoints have sessions; the publisher always sets the session id. A session receiver works on up to `MaxConcurrentSessions` sessions, accepting the next free one in the background, and takes messages from a session only while none of its messages is unsettled. A key's next message therefore waits for its head on every instance (TCK-15): Service Bus itself would hand out the next message of a session while the head is in flight.
> - **Locks.** The queue's `LockDuration` (an option, 30 seconds by default) bounds how long a dead process holds a message. A receiver that asks for a longer lock keeps the broker lock renewed until its own lock passes; renew renews the broker lock. A late settlement after the lock was lost is ignored.
> - **Settlement failures (WP-C.6, CR-09).** When completing, abandoning or dead-lettering fails, the delivery is forgotten and releases its hold on its session before the error goes on. Its session receiver is closed, so the session lock ends and the broker hands the session (with the message) out again; a message without a session is abandoned, best effort. A failure partway through a receive does the same for what that receive accepted. Session locks are renewed only for sessions with deliveries in flight, so an idle session lapses to another instance even when the receive loop has stopped calling.
> - **Delayed retries.** On a queue without sessions, a delay re-sends a scheduled copy and completes the original. The copy has a broker id of its own (so duplicate detection keeps it) and carries the deliveries so far in `sf-deliveries`, so `DeliveryCount` keeps counting. On a session, the message keeps its place instead: it holds its session (lock renewed) until the delay passes, then it is abandoned. The longest native delay is 1 hour.
> - **Dead letters.** The built-in `$DeadLetterQueue`, with `MaxDeliveryCount` = the topology's limit; explicit dead letters keep their reason. A message that is not a SourceFlow CloudEvent is dead-lettered (`MalformedMessage`); on the dead-letter queue such a message is deferred, so the ingestor moves on and it stays for inspection.
> - **Duplicate detection** is opt-in (`DuplicateDetectionWindow`), and `Deduplication` is declared only then. Within the window, the broker also drops a dead-letter replay, which keeps its message id.
> - **Connections.** `FullyQualifiedNamespace` with a `TokenCredential` (`DefaultAzureCredential` by default); a connection string is for Development and the emulator, and outside Development the host logs a warning. The emulator serves management on a separate endpoint, given as `AdministrationConnectionString`. The receiver of an endpoint learns whether it has sessions from provisioning or from the queue's description, so receivers need the Manage right unless the process provisions.

### 4.1 How each v2 provider defect is fixed

| v2 defect | v3 resolution |
|-----------|---------------|
| AWS: events and commands share the first queue without raw delivery, and two listeners compete (GAP-02) | One endpoint, one receiver, raw delivery always set, messages told apart by `ce_type` (RX-R5) |
| AWS: a FIFO queue subscribed to a standard topic | Topology validation rejects the combination at startup |
| AWS: `Region` and `MaxRetries` ignored (GAP-03) | `AwsTransportOptions` builds the SDK client config (§6). Unknown or unused options can't exist. |
| GCP: services compete on the `{topic}-sub` subscription (GAP-13) | The subscription id includes the subscribing service: `{svc}.{topic}` |
| GCP: ordering key set but ordering not enabled (GAP-13) | Ordered topology sets `enable_message_ordering` and publisher ordering; validation checks it |
| No native DLQ configuration (GAP-11) | Every endpoint gets a DLQ backstop, and the ingestor drains it into `sf_dead_letters` |
| Production features opt-in or missing per provider (GAP-01) | Features live in core middleware, so every transport has them |

## 5. Provisioning ([ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md))

| Mode | Behaviour | Required permissions |
|------|-----------|----------------------|
| `CreateIfMissing` (Development default) | Creates missing resources, then validates configuration | Create, describe and subscribe permissions |
| `Validate` (Production default) | Describes resources and checks they exist, ordering compatibility, raw delivery, DLQ attached, subscription filters, encryption, and retention ≥ inbox retention. Startup fails with a full diff report on mismatch. | Describe only |
| `None` | Skips the checks (for emergencies) | — |

`dotnet sourceflow topology export --format json` writes the topology for IaC. Example:

```json
{
  "service": "accounts",
  "endpoints": [{ "name": "prod-accounts", "ordered": true, "maxDeliveries": 12, "retention": "P14D", "dlq": "prod-accounts-dlq" }],
  "topics": [{ "name": "prod-accounts-events", "ordered": true }],
  "subscriptions": [{ "topic": "prod-payments-events", "endpoint": "prod-accounts", "filter": { "ce_type": ["payments.payment-settled.v1"] } }]
}
```

Reference Terraform and Bicep modules that consume this JSON are published in the repository (`deploy/`). They are maintained with the conformance kit's topology fixtures.

> **As built (WP-3.5).**
> - **Shared mode behaviour.** Every transport's provisioner derives from `TopologyProvisioner`. It first checks the topology in every mode, `None` included: names present and logical, delivery limit ≥ 1, ordered topics published, filters only on subscribed topics, and filters that parse. Then `None` stops, `CreateIfMissing` creates what is missing, and both `CreateIfMissing` and `Validate` report every difference at once. Existing resources are never changed.
> - **Default mode.** When `SourceFlowBuilder.Provisioning` is left unset, it is `CreateIfMissing` in Development, with the in-memory transport, or without a host environment, and `Validate` in every other environment.
> - **Export.** The library export is `TopologyExport.ToJson(runtime.TopologyDocument())`, and `dotnet sourceflow topology export [--format json|text] [--output file]` writes it. The format is versioned (`schemaVersion: 1`) and deterministic: sorted, with no timestamps. Its JSON Schema is `deploy/topology/topology.schema.json`. Besides the logical topology and the CloudEvents types per topic and endpoint, it lists `resources`: each physical resource with its `kind`, `name`, `logical` owner, `role` and provider `properties` in the provider's units. A resource is marked `owned: false` when it belongs to another stack: subscribed topics belong to their publisher, and remote endpoints to their service.
> - **Modules.** `deploy/topology/terraform/{aws,gcp,azure}` and `deploy/topology/bicep/servicebus.bicep` read the export and compute no names. The Google Cloud module also grants the Pub/Sub service agent the dead-letter permissions, and the AWS module outputs a least-privilege runtime IAM policy.
> - **Azure `$Default` rule.** ARM creates a match-everything rule called `$Default` with every Azure subscription. The export names each subscription's first rule `$Default`, so the modules replace that rule rather than add one beside it.
> - **Tests.** The modules are tested against the fixtures (plan 06 §3.1, IAC-01..04). The Terraform modules applied to LocalStack and the Pub/Sub emulator pass `Validate`. The Azure modules have no emulator for ARM, so they are checked with mock providers and `bicep build`.

## 6. Options are honoured, always

Every transport option maps to an SDK setting, and options validation fails startup when a value can't be honoured. This removes the GAP-03 failure class.

| `AwsTransportOptions` | Maps to |
|-----------------------|---------|
| `Region`, `Profile`, `ServiceUrl` (LocalStack) | `AWSOptions` passed to `CreateServiceClient<IAmazonSQS/SNS>()` |
| `Credentials` | **Not an option.** The SDK credential chain is used (IAM role, IRSA, SSO). |
| `MaxSdkRetries`, `Timeout` | `ClientConfig.MaxErrorRetry`, `Timeout` |
| `ReceiveWaitTime`, `VisibilityTimeout`, `PrefetchCount` | The receiver's `ReceiveMessageRequest` |

Azure (`FullyQualifiedNamespace` + `TokenCredential`, i.e. managed identity) and GCP (`ProjectId` + Application Default Credentials, with the emulator detected through `PUBSUB_EMULATOR_HOST`) follow the same rule. Connection strings are accepted only in Development (an analyzer warning, plus a runtime warning in other environments).

## 7. Transport Conformance Kit (TCK)

The TCK is an abstract test suite in `SourceFlow.Testing`. Each transport test project subclasses it and supplies a fixture: an emulator, or a real cloud account in nightly runs. **GA requires every mandatory scenario to pass for every transport** ([ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md), Q5).

| Group | Scenarios (abridged) |
|-------|----------------------|
| Envelope | Binary and structured round-trips; every CloudEvents attribute and extension preserved; header overflow → `sf_ext`; Unicode; maximum-size body accepted; over-size body rejected before send |
| Send and publish | Batch send; partial batch failure reported per message; publish to topic with fan-out to 2 services × 2 instances (each service once, competing within a service) |
| Ordering | Ordered endpoint: 10,000 messages over 100 keys across 3 consumers → per-key order holds; unordered endpoint: no deadlock |
| Settlement | Complete removes the message; abandon/RetryLater redelivers after the delay (±tolerance); renew extends in-flight time; a crash before complete → redelivery |
| Dead-letter | Exceeding max deliveries → message in native DLQ; the ingestor moves it to `sf_dead_letters` with headers intact |
| Filters | A subscription filter delivers only the listed types. **CESQL filters** (the same syntax as SFQL `where`, doc 14) are translated to the native filter where expressible (SNS filter policy, ASB SQL filter, Pub/Sub filter). Otherwise they are evaluated client-side before deserialisation, with identical results on every transport. |
| Provisioning | CreateIfMissing is idempotent; Validate detects missing or misconfigured resources (FIFO mismatch, missing raw delivery, missing DLQ, ordering off) with a precise report |
| Resilience | Broker outage for 60 s → the receiver recovers without a restart; throttling → backoff; credential expiry → refresh |
| Shutdown | Stop drains in-flight messages within the timeout; nothing is acknowledged that wasn't completed |
| End-to-end (with the core) | Command to a remote owner → appended exactly once under duplicate injection; integration event → projection in another service; process manager timeout delivered |

## 8. Migration bridge from v2 wire formats (optional)

`sf.UseAmazonSqs(aws => aws.AcceptV2Messages(map => map.Type("MyApp.Commands.CreateAccount, MyApp", "bank.account.open", 1)))` adds a receive-side translator. It reads v2's `CommandType`/`EventType` assembly-qualified-name attributes **through an explicit allow-list mapping**, converts the v2 JSON to the v3 record, and assigns a deterministic `MessageId` (UUIDv5 of the v2 `MessageId`). This lets v2 and v3 services coexist during migration. It is planned for removal in 4.0.

**As built (WP-5.4).**

- **Option.** It is an option of the AWS transport: `sf.UseAwsTransport(o => o.AcceptV2Messages(map => map.Type(v2Type, type, version, convert?)))`. It is off by default.
- **Where it runs.** `AwsReceiver` tries it when a message is not a SourceFlow CloudEvent.
- **What it reads.**
  - Commands: `CommandType`, `EntityId` and `SequenceNo` attributes on the SQS message.
  - Events: an `EventType` attribute, raw on SQS or inside an SNS notification.
  - The v2 message id is the SQS message id; for an SNS notification, it is the SNS `MessageId`.
- **Matching.** Types match without assembly version, culture or public key token.
- **Body.** The converter (`Func<V2Message, JsonNode?>`) builds the v3 JSON; without one, the v2 `payload` object is used.
- **Envelope.**
  - `MessageId` is UUIDv5 of the v2 message id, and the source is `sourceflow-v2`.
  - Headers: `sourceflow-v2-type` and `sourceflow-v2-message-id`.
- **Unmapped types.** An unmapped v2 type keeps its normalized name and raw body, and is marked `sourceflow-v2-unmapped`. The receive pipeline parks it as `UnknownMessageType`.
- **Left on the queue.** A mapped message whose body isn't JSON, like any undecodable message, is left for the redrive policy.
- **Core part.** The allow-list and translation (`V2MessageMap`) live in the core's `SourceFlow.Migration`; only the attribute reading (`AwsV2Bridge`) is AWS-specific.
- **Tests.** UT-AWS-31..33 (mocked SQS) and MIG-10..12 on LocalStack.
