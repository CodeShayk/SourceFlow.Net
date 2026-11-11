# 07 — Cloud Transports

Design drivers: [ADR-011](ADRs/ADR-011-cloudevents-envelope-and-w3c-trace-context.md), [ADR-014](ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md) and [ADR-017](ADRs/ADR-017-provisioning-modes-and-topology-as-data.md). Fixes: GAP-01, GAP-02, GAP-03, GAP-11, GAP-12, GAP-13, AWS-02/03/05, GCP-04/05/07 and RES-05.

## 1. Principle: thin transports, smart core

v2 put retries, idempotency, dead-lettering, encryption and telemetry *inside* each provider's dispatchers and listeners, in two variants (basic and "Enhanced"). That produced divergence and unwired features. In v3 a transport does only what is broker-specific: move bytes and headers, settle messages, and provision resources. Everything else is core middleware (doc 04), written once and identical on every broker.

## 2. Transport SPI

> **As built ([ADR-033](ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)).** The SPI is pull-based: `ITransport` has `SendAsync`, `PublishAsync`, `CreateReceiverAsync`, `CreateDeadLetterReceiverAsync` and `ProvisionAsync`. `ITransportReceiver.ReceiveAsync(max, wait)` feeds the core's receive loop, and `ReceivedMessage` settles with `CompleteAsync`, `AbandonAsync(delay)`, `RenewAsync` or `DeadLetterAsync(reason)`.
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

## 5. Provisioning ([ADR-017](ADRs/ADR-017-provisioning-modes-and-topology-as-data.md))

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

The TCK is an abstract test suite in `SourceFlow.Testing`. Each transport test project subclasses it and supplies a fixture: an emulator, or a real cloud account in nightly runs. **GA requires every mandatory scenario to pass for every transport** ([ADR-014](ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md), Q5).

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
