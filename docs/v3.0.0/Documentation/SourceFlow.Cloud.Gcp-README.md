# SourceFlow.Cloud.Gcp

`SourceFlow.Cloud.Gcp` connects SourceFlow.Net services through Google Cloud Pub/Sub, and provides Cloud KMS as the key provider for payload protection and crypto-shredding. Pub/Sub has no queues and no forwarding, so a service's endpoint is a set of subscriptions that one receiver pulls from: a command subscription on the service's own command topic, and one subscription per topic the service subscribes to, named after the service so that services fan out and instances compete. The transport only moves bytes and headers, settles messages and provisions resources. Retries, the inbox, parking, encryption and telemetry are core behaviour and work the same on every broker (see [Architecture: Cloud Transports](Architecture/07-Cloud-Transports.md)).

## Contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [How SourceFlow maps to Pub/Sub](#how-sourceflow-maps-to-pubsub)
- [Configuration](#configuration)
- [Credentials and endpoints](#credentials-and-endpoints)
- [Resource ids](#resource-ids)
- [Ordering](#ordering)
- [Provisioning](#provisioning)
- [IAM permissions](#iam-permissions)
- [Receiving, leases, retries and dead letters](#receiving-leases-retries-and-dead-letters)
- [Message size and wire format](#message-size-and-wire-format)
- [Subscription filters](#subscription-filters)
- [Cloud KMS key provider](#cloud-kms-key-provider)
- [Health checks](#health-checks)
- [Local development with the Pub/Sub emulator](#local-development-with-the-pubsub-emulator)
- [Troubleshooting](#troubleshooting)
- [Reference](#reference)

## Installation

```bash
dotnet add package SourceFlow.Cloud.Gcp
```

The package targets `net10.0` only. It depends on the core package `SourceFlow.Net`, `Google.Cloud.PubSub.V1` and `Google.Cloud.Kms.V1`.

You also need a storage adapter, for example [SourceFlow.Storage.PostgreSQL](SourceFlow.Storage.PostgreSQL-README.md), because the outbox and inbox that make delivery effectively-once live in the store.

## Quick start

```csharp
using SourceFlow.Cloud.Gcp;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "orders";
    sf.AddModule<OrdersModule>();
    sf.UsePostgreSql(builder.Configuration.GetConnectionString("SourceFlow")!);

    sf.UseGcpTransport(o =>
    {
        o.ProjectId = "acme-prod";      // required
        o.NamePrefix = "prod-";
    });

    sf.Routing(r =>
    {
        r.Command<ReserveStock>().To("inventory").ReplyOnReject();
        r.Publish<OrderPlaced>().AsIs().To("orders.events");
        r.Subscribe("inventory.events");
    });
});

builder.Services.AddHealthChecks()
    .AddSourceFlow()          // sourceflow.ready, .store, .transport, ...
    .AddSourceFlowGcp();      // sourceflow-gcp: the endpoint subscription exists and Pub/Sub answers

WebApplication app = builder.Build();
app.Run();
```

`UseGcpTransport` validates the options at once (a missing or malformed project id, values out of range). When the host starts it provisions or validates the service's topics and subscriptions ([Provisioning](#provisioning)), opens one receiver over all of the endpoint's subscriptions, starts the outbox relay, and starts the dead-letter ingestor.

## How SourceFlow maps to Pub/Sub

| SourceFlow concept | Pub/Sub resources | Notes |
|--------------------|-------------------|-------|
| Endpoint (a service's input) | Topic `{prefix}{endpoint}+in` and subscription `{prefix}{endpoint}` | Commands sent to the service are published to its `+in` topic. |
| Subscription to a topic | Subscription `{prefix}{endpoint}~{topic}` on the topic | One per subscribing service; instances of a service compete on it. Optional filter on `attributes.ce-type`. |
| Receiving | One receiver pulls from the command subscription and every topic subscription, in turn | Pub/Sub has no forwarding, so the endpoint is the set of subscriptions. |
| Dead-letter backstop | Topic and subscription `{prefix}{endpoint}+dlq` | Every endpoint subscription has a dead-letter policy to it. |
| Topic | Topic `{prefix}{topic}` | Owned by the publishing service. |
| Remote command | Publish to the owner's `{endpoint}+in` topic | |

The capabilities the transport declares to the core (`GcpTransport.GcpCapabilities`):

| Capability | Value |
|------------|-------|
| `SystemName` (`messaging.system`) | `gcp_pubsub` |
| `MaxBodyBytes` | 9 MiB (`GcpWire.MaxBodyBytes`; 10 MB per publish request) |
| `MaxAttributes` | 100 |
| `MaxRetention` | 7 days |
| `NativeDelay` / `MaxDelay` | yes / 10 minutes (the ack deadline limit) |
| `OrderedDelivery` | yes (ordering keys, message ordering on the subscription) |
| `NativeDeadLetter` | yes (dead-letter policy, enforced by the receiver as well) |
| `SubscriptionFilters` | yes (Pub/Sub filters, with a receiver-side fallback) |
| `MaxBatchSize` | 100 |
| `Deduplication` | not declared: publishing has none; the inbox deduplicates |
| `MaxLockDuration` | 10 minutes |

## Configuration

`UseGcpTransport(Action<GcpTransportOptions>)` registers `GcpTransport` as a singleton and as the service's `ITransport`.

### `GcpTransportOptions`

| Option | Type | Default | Maps to / rules |
|--------|------|---------|-----------------|
| `ProjectId` | `string` | `""` (required) | The project that owns the topics and subscriptions. 6 to 30 lowercase letters, digits and hyphens, starting with a letter (legacy `domain:` prefixes are accepted). |
| `EmulatorDetection` | `EmulatorDetection` | `EmulatorOrProduction` | `ClientBuilderBase.EmulatorDetection`: the emulator in `PUBSUB_EMULATOR_HOST` when it is set, production otherwise. Use `ProductionOnly` to ignore the variable. |
| `Endpoint` | `string?` | `null` (the global endpoint) | `ClientBuilderBase.Endpoint`, for example the regional `europe-west1-pubsub.googleapis.com:443`, which keeps ordered messages in one region. |
| `Credential` | `GoogleCredential?` | `null` (Application Default Credentials) | `ClientBuilderBase.GoogleCredential`. |
| `MaxRpcRetries` | `int` | `3` | Retries with exponential backoff (100 ms doubling to 10 s) on `Unavailable`, `ResourceExhausted`, `Aborted`, `Internal` and `DeadlineExceeded`; 0 to 10. |
| `Timeout` | `TimeSpan` | 30 seconds | The deadline of one call including its retries (`CallSettings.Expiration`); greater than 0 and at most 10 minutes. |
| `PollInterval` | `TimeSpan` | 100 ms | How long a receiver waits between empty pulls while it waits for the first message; 10 ms to 10 seconds. |
| `NamePrefix` | `string` | `""` | Prepended to every topic and subscription id. Letters, digits, `-`, `_` and `.`; at most 100 characters. |
| `MessageRetention` | `TimeSpan` | 7 days | `message_retention_duration` of new endpoint and topic subscriptions; 10 minutes to 7 days. |

| Method | Purpose |
|--------|---------|
| `Validate()` | Called by `UseGcpTransport`; throws `ArgumentException`. |
| `ToPublisherBuilder()`, `ToSubscriberBuilder()` | The client builders the options describe. |
| `ToCallSettings()` | The call settings (deadline and retries) used on every call. |

### Runtime options that shape the transport

These are core options (`sf.Configure(o => …)`, see [SourceFlow.Net](SourceFlow.Net-README.md)).

| `SourceFlowOptions` | Default | Effect on Pub/Sub |
|---------------------|---------|-------------------|
| `ReceiveLockDuration` | 30 seconds | Each message's ack deadline. At most 10 minutes; a longer value fails when the receiver is created. Renewed at half this interval while a handler runs. |
| `PrefetchPerWorker` | 16 | Messages asked for per receive (a pull takes at most 1,000). |
| `PollInterval` | 1 second | How long one receive waits for the first message; the receiver pulls every `GcpTransportOptions.PollInterval` within it. |
| `ImmediateRetries` | 3 | In-process retries before a delayed retry. |
| `DelayedRetries` | 10 s, 1 min, 10 min, 1 h | Native delays through the ack deadline. Pub/Sub's limit is 10 minutes, so longer delays are capped at 10 minutes. |
| `IngestBrokerDeadLetters` | `true` | Runs the dead-letter ingestor on receiver instances. |
| `DeadLetterIngestInterval` | 30 seconds | How often the ingestor pulls the `+dlq` subscription when it was empty. |

The delivery limit is `1 + DelayedRetries.Count + 2`: 7 with the default schedule.

### Using your own clients

`GcpTransport` has a constructor over existing `PublisherServiceApiClient` and `SubscriberServiceApiClient` instances, for example with a custom call invoker. Only the project, naming, call, receive and retention options apply then.

```csharp
GcpTransportOptions options = new() { ProjectId = "acme-prod", NamePrefix = "prod-" };
builder.Services.AddSingleton(new GcpTransport(publisher, subscriber, options));
builder.Services.AddSourceFlow(sf =>
{
    // ...
    sf.UseTransport(sp => sp.GetRequiredService<GcpTransport>());
});
```

## Credentials and endpoints

- Credentials default to Application Default Credentials: workload identity on GKE, the metadata server on Compute Engine and Cloud Run, or `GOOGLE_APPLICATION_CREDENTIALS`. Set `Credential` to use another `GoogleCredential`.
- `PUBSUB_EMULATOR_HOST` switches the clients to the emulator unless `EmulatorDetection` says otherwise.
- `Unauthenticated` (a token that expired mid-call) is mapped to a transient error; the credential refreshes on the next call. `ResourceExhausted` is mapped to `TransportThrottledException`, with the server's `RetryInfo` delay when it sends one (1 second otherwise).

## Resource ids

Topics and subscriptions share one id namespace per project. The ids the transport derives (`GcpNaming`) use separators that logical names never contain, so they cannot collide:

| Resource | Id |
|----------|----|
| Event topic | `{prefix}{topic}` |
| Endpoint command topic | `{prefix}{endpoint}+in` |
| Endpoint command subscription | `{prefix}{endpoint}` |
| Service subscription to a topic | `{prefix}{endpoint}~{topic}` |
| Dead-letter topic and its subscription | `{prefix}{endpoint}+dlq` |

- Characters other than letters, digits, `-`, `_` and `.` become `-`, which includes any `~` or `+` in a logical name.
- Ids start with a letter and do not start with `goog`; otherwise they get `sf-` in front.
- Ids fit Pub/Sub's 255-character limit; long ones keep a readable head and an 8-character hash.

## Ordering

```csharp
sf.Routing(r =>
{
    r.OrderedEndpoint();   // message ordering on every subscription of the endpoint
});
```

- The publisher always sets the ordering key to the envelope's partition key (the stream id by default).
- Pub/Sub decides ordering on the subscription. An ordered endpoint enables message ordering on its command subscription and on each topic subscription, and `Validate` checks it.
- Pub/Sub topics have no ordering setting, so `OrderedTopic(...)` has no physical effect on Pub/Sub; order for a subscriber comes from its own ordered endpoint.
- Message ordering and filters are immutable on a subscription: a change is reported as drift, never repaired in place. Recreate the subscription.
- Ordering keys are scoped to a region. Use a regional `Endpoint` to keep ordered publishing in one region.
- A zero-delay abandon on an ordered subscription sets a one-second ack deadline instead of a nack, which keeps the order on the emulator as well as on Pub/Sub.

## Provisioning

Provisioning follows [ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md). The host calls `ProvisionAsync(topology, mode)` once at startup.

| `ProvisioningMode` | What happens on Pub/Sub |
|--------------------|-------------------------|
| `CreateIfMissing` | Creates what is missing: the dead-letter topic and subscription (7-day retention), the command topic and subscription, published topics, subscribed topics that do not exist, and the topic subscriptions with their filters. Endpoint subscriptions get a 30-second ack deadline, `MessageRetention`, no expiry, message ordering when ordered, a dead-letter policy, and the `sf-max-deliveries` label. Resources created concurrently by another instance are accepted. Existing ones are never changed. Then validates. |
| `Validate` | Reads the resources and fails startup with a `TopologyValidationException` listing every difference at once. |
| `None` | Only checks the topology itself; no Pub/Sub call. |

When `SourceFlowBuilder.Provisioning` is `null`, the mode is `CreateIfMissing` in Development (or without a host environment) and `Validate` elsewhere.

What `Validate` checks:

| Problem kind | Detected when |
|--------------|---------------|
| `MissingEndpoint` | The command topic or the command subscription does not exist. |
| `MissingDeadLetter` | The dead-letter topic or subscription does not exist; a subscription has no dead-letter policy, points at another topic, or has a different delivery limit. |
| `MissingTopic` | A published or subscribed topic does not exist. |
| `MissingSubscription` | The endpoint's subscription to a subscribed topic does not exist. |
| `OrderingMismatch` | Message ordering on a subscription differs from the endpoint's ordering. |
| `FilterDrift` | A topic subscription's filter differs from the routing filter. |
| `Misconfigured` | A subscription is attached to another topic, the command subscription filters messages, or a subscription expires when it has no receivers (and its messages with it). |

### Infrastructure as code

```bash
dotnet run -- sourceflow topology export --output orders.topology.json
dotnet run -- sourceflow topology validate
```

`GcpTopology.Describe(options, topology)` (or the CLI export) lists every topic and subscription with `topic`, `ackDeadlineSeconds`, `enableMessageOrdering`, `messageRetentionDuration` (for example `604800s`), `filter`, `labels`, and for endpoint subscriptions `deadLetterTopic` and `maxDeliveryAttempts`. The Terraform module [`deploy/topology/terraform/gcp`](../../../deploy/topology/README.md) consumes it. It can also grant the Pub/Sub service agent the dead-letter permissions (`grant_service_agent`) and the service's identity its runtime roles (`runtime_member`).

## IAM permissions

### Runtime with `Validate` (production)

What the Terraform module grants `runtime_member`:

| Role | On |
|------|----|
| `roles/pubsub.subscriber` and `roles/pubsub.viewer` | The service's subscriptions (command, topic subscriptions, dead-letter) |
| `roles/pubsub.publisher` | Its published topics, the `+in` topics of remote endpoints it sends to, and its own `+dlq` topic (explicit dead letters) |
| `roles/pubsub.viewer` | Every topic of the topology |

### Pub/Sub service agent

Pub/Sub moves a message to a dead-letter topic as the project's Pub/Sub service agent, which needs publisher on the dead-letter topic and subscriber on each subscription with a dead-letter policy. Without these grants the receiver still enforces the delivery limit itself (see below), so nothing is lost; with them Pub/Sub enforces it as well.

### Provisioning with `CreateIfMissing` (development)

The identity also needs to create topics and subscriptions, for example `roles/pubsub.editor` on the project.

### KMS

`GoogleKmsKeyProvider` calls `Encrypt` and `Decrypt` on the crypto key, so the identity needs `roles/cloudkms.cryptoKeyEncrypterDecrypter` on it.

## Receiving, leases, retries and dead letters

A `GcpReceiver` uses synchronous pull with immediate return and takes the endpoint's subscriptions in turn, so none starves. It deliberately does not use streaming pull with automatic lease extension: a lease must lapse when a process dies holding a message.

| Step | Pub/Sub operation |
|------|-------------------|
| Receive | `Pull` (`ReturnImmediately`) on each subscription, every `PollInterval` while waiting; then `ModifyAckDeadline` to the lock duration when it differs from the subscription's 30 seconds |
| Keep the lease | `ModifyAckDeadline` to the lock duration |
| Complete after the handler commits | `Acknowledge` |
| Delayed retry | `ModifyAckDeadline` to the delay (at most 10 minutes) |
| Explicit dead letter | Publish a copy with `sf-reason` to `{endpoint}+dlq`, then `Acknowledge` |
| Graceful stop | Every unsettled message's ack deadline is set to 0 (1 second on ordered subscriptions), so it is redelivered at once |

Failure behaviour:

- **Delivery count.** `DeliveryCount` is Pub/Sub's delivery attempt, which is counted because every endpoint subscription has a dead-letter policy.
- **Delivery limit.** Pub/Sub's dead-letter policy accepts 5 to 100 attempts, so the policy holds the clamped value and the `sf-max-deliveries` label holds the topology's limit. The receiver moves a message delivered more often than the label to the dead-letter topic itself, reason `MaxDeliveryAttemptsExceeded`.
- **Lost leases.** Pub/Sub ignores a late acknowledgement and silently accepts a deadline change on an expired lease. The receiver therefore tracks each lease and reports a lost lock on renew once it has passed; the redelivery is absorbed by the inbox.
- **Foreign messages.** A message that is not a SourceFlow CloudEvent is moved to the dead-letter topic at once (`MalformedMessage: …`). On the dead-letter subscription it is hidden for the longest lease (10 minutes) so the ingestor moves on.
- **Broker backstop.** The dead-letter ingestor drains `{endpoint}+dlq` into the store with the envelope intact (reason from `sf-reason`, or `MaxDeliveryAttemptsExceeded`), so `dotnet sourceflow dlq replay` can re-send it.
- **Parking.** A permanent failure, or a transient one after the last delayed retry, is parked in the store and acknowledged. See [SourceFlow.Tool](SourceFlow.Tool-README.md) for `dlq` commands.

## Message size and wire format

`GcpWire` uses the CloudEvents Pub/Sub protocol binding:

| Item | How it travels |
|------|----------------|
| CloudEvents attributes | Message attributes `ce-{attribute}` (`ce-type`, `ce-sftenant`, ...) |
| Long attribute values | Pub/Sub limits a value to 1,024 bytes, so longer values (a long subject, the user headers) are split into `ce-{attribute}#0`, `#1`, ... and joined on receipt |
| Body | The message data, byte for byte |
| Ordering key | The partition key |
| Dead letters | `sf-reason` |

- `MaxBodyBytes` is 9 MiB, so the body, 100 attributes of the largest size and the ordering key always fit the 10 MB publish request.
- An oversized message is refused before it is published and reported per message (`TransportSendException`). The core also checks the body limit when it writes the outbox row.
- There is no claim-check.

## Subscription filters

```csharp
sf.Routing(r =>
{
    r.Subscribe("payments.events", s => s.Only<PaymentCaptured>());                    // Pub/Sub filter
    r.Subscribe("shipping.events", s => s.Where("type LIKE 'shipping.shipment-%'"));   // Pub/Sub filter (prefix)
    r.Subscribe("inventory.events", s => s.Where("sftenant = 'acme'"));                // receiver-side
});
```

- A filter that is a list of types (`type = '…'`, `type IN (…)`) or of type prefixes (`type LIKE 'prefix%'`) becomes a Pub/Sub filter on `attributes.ce-type`, when the translation fits Pub/Sub's 256-byte filter limit. Pub/Sub acknowledges the rest itself.
- Any other filter, or one too long to translate, leaves the subscription unfiltered. The receiver evaluates it for that subscription with the same evaluator and acknowledges non-matching events unseen.
- Filters are immutable on Pub/Sub. Changing a subscription's filter means recreating the subscription; `Validate` reports the drift.

## Cloud KMS key provider

```csharp
builder.Services.AddSourceFlow(sf =>
{
    // ...
    sf.Security(s =>
    {
        s.UseGoogleKms(k => k.KeyName = "projects/acme-prod/locations/europe-west1/keyRings/sourceflow/cryptoKeys/messages");
        s.Protect(p => p.Protect("accounts.events"));
    });
});
```

`GoogleKmsKeyProvider` (name `gcp-kms`) generates each 256-bit data key locally and encrypts it with the crypto key's primary version (`Encrypt`). The ciphertext names the key version, so `Decrypt` on the crypto key unwraps keys wrapped before a rotation. Unavailable, deadline, resource-exhausted and internal errors are transient; permission denied, not found and invalid arguments (a bad wrapped key) are not.

| `GoogleKmsOptions` | Type | Default | Notes |
|--------------------|------|---------|-------|
| `KeyName` | `string?` | required | `projects/{project}/locations/{location}/keyRings/{ring}/cryptoKeys/{key}`. A malformed name fails at startup. |
| `Credential` | `GoogleCredential?` | Application Default Credentials | |

Envelope encryption, reuse limits and erasure are covered in the [Security and Compliance Guide](Security-and-Compliance-Guide.md).

## Health checks

| Check | Registered by | What it does |
|-------|---------------|--------------|
| `sourceflow.transport` (tag `ready`) | `AddHealthChecks().AddSourceFlow()` | `GcpTransport.ProbeAsync`: the endpoint's command subscription exists and Pub/Sub answers. Cached by the core. |
| `sourceflow-gcp` (tag `ready`) | `AddHealthChecks().AddSourceFlowGcp(name)` | The same check as a standalone `GcpTransportHealthCheck`. |

## Local development with the Pub/Sub emulator

The repository pins `gcr.io/google.com/cloudsdktool/google-cloud-cli:590.0.1-emulators`, started for the project `sourceflow-emulator`:

```bash
docker compose -f .github/emulators/pubsub/docker-compose.yml up -d
export PUBSUB_EMULATOR_HOST=localhost:8085
```

```csharp
sf.UseGcpTransport(o => o.ProjectId = "sourceflow-emulator");   // the emulator is picked up from PUBSUB_EMULATOR_HOST
```

- No credentials are needed for the emulator.
- In Development the default mode is `CreateIfMissing`, so topics and subscriptions appear on first start.
- The receiver enforces the delivery limit itself, so poison messages reach `+dlq` on the emulator as they do on Pub/Sub.

See [Cloud Integration Testing](Cloud-Integration-Testing.md) for how the test suites use the emulator.

## Troubleshooting

| Symptom | Likely cause | What to do |
|---------|--------------|------------|
| `ArgumentException: GcpTransportOptions.ProjectId is required` or `... is not a Google Cloud project id` | Missing or malformed project id (a project number or display name) | Use the project id. |
| Startup fails with `TopologyValidationException` (`MissingEndpoint`, `MissingTopic`, `MissingSubscription`) | `Validate` mode and resources not created | Apply the Terraform module from `topology export`, or use `CreateIfMissing` where that is acceptable. |
| `OrderingMismatch` on a subscription | `OrderedEndpoint()` changed after the subscriptions existed | Recreate the subscriptions; ordering is immutable. |
| `FilterDrift` | The routing filter changed | Recreate the topic subscription; filters are immutable. |
| `Misconfigured ... expires after ... without receivers` | A subscription created elsewhere with an expiration policy | Remove the expiration (SourceFlow creates subscriptions that never expire). |
| `ArgumentOutOfRangeException` when the receiver is created | `ReceiveLockDuration` over 10 minutes | Lower it; long handlers keep their lease through renewal. |
| Messages from a busy endpoint arrive late | Each receive pulls the subscriptions in turn and waits `PollInterval` between empty pulls | Lower `GcpTransportOptions.PollInterval` (more empty pull calls), or add instances. |
| Dead-lettering by Pub/Sub does not happen | The service agent lacks the dead-letter grants | Set `grant_service_agent` in the Terraform module; the receiver enforces the limit in any case. |
| Unexpected traffic from the emulator in production | `PUBSUB_EMULATOR_HOST` set in the environment | Unset it, or set `EmulatorDetection = EmulatorDetection.ProductionOnly`. |

## Reference

- Source: [`src/Cloud/SourceFlow.Cloud.Gcp`](../../../src/Cloud/SourceFlow.Cloud.Gcp)
- Architecture: [Cloud Transports](Architecture/07-Cloud-Transports.md)
- Design: [07 Cloud Transports](../Design/07-Cloud-Transports.md), [10 Security and Compliance](../Design/10-Security-and-Compliance.md)
- ADRs: [ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md), [ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md), [ADR-033](../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)
- Infrastructure as code: [`deploy/topology`](../../../deploy/topology/README.md)
- Testing: [Cloud Integration Testing](Cloud-Integration-Testing.md)
- Other transports: [AWS SQS/SNS](SourceFlow.Cloud.Aws-README.md), [Azure Service Bus](SourceFlow.Cloud.Azure-README.md)
