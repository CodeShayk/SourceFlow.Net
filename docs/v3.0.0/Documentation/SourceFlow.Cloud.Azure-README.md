# SourceFlow.Cloud.Azure

`SourceFlow.Cloud.Azure` connects SourceFlow.Net services through Azure Service Bus, and provides Azure Key Vault as the key provider for payload protection and crypto-shredding. A service's endpoint is a Service Bus queue (with sessions when ordered) and its built-in dead-letter queue. A topic is a Service Bus topic, and each subscribing service gets a subscription, named after its endpoint, that forwards to its queue. The transport only moves bytes and headers, settles messages and provisions entities. Retries, the inbox, parking, encryption and telemetry are core behaviour and work the same on every broker (see [Architecture: Cloud Transports](Architecture/07-Cloud-Transports.md)).

## Contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [How SourceFlow maps to Service Bus](#how-sourceflow-maps-to-service-bus)
- [Configuration](#configuration)
- [Connecting and authenticating](#connecting-and-authenticating)
- [Entity names](#entity-names)
- [Ordering (sessions)](#ordering-sessions)
- [Provisioning](#provisioning)
- [Permissions](#permissions)
- [Locks, retries and dead letters](#locks-retries-and-dead-letters)
- [Message size and wire format](#message-size-and-wire-format)
- [Subscription rules](#subscription-rules)
- [Duplicate detection](#duplicate-detection)
- [Azure Key Vault key provider](#azure-key-vault-key-provider)
- [Health checks](#health-checks)
- [Local development with the Service Bus emulator](#local-development-with-the-service-bus-emulator)
- [Troubleshooting](#troubleshooting)
- [Reference](#reference)

## Installation

```bash
dotnet add package SourceFlow.Cloud.Azure
```

The package targets `net10.0` only. It depends on the core package `SourceFlow.Net`, on `Azure.Messaging.ServiceBus` (including its administration client), `Azure.Identity` and `Azure.Security.KeyVault.Keys`.

You also need a storage adapter, for example [SourceFlow.Storage.PostgreSQL](SourceFlow.Storage.PostgreSQL-README.md) or [SourceFlow.Storage.SqlServer](SourceFlow.Storage.SqlServer-README.md), because the outbox and inbox that make delivery effectively-once live in the store.

## Quick start

```csharp
using SourceFlow.Cloud.Azure;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "orders";
    sf.AddModule<OrdersModule>();
    sf.UsePostgreSql(builder.Configuration.GetConnectionString("SourceFlow")!);

    sf.UseAzureTransport(o =>
    {
        o.FullyQualifiedNamespace = "contoso.servicebus.windows.net";   // managed identity through DefaultAzureCredential
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
    .AddSourceFlow()           // sourceflow.ready, .store, .transport, ...
    .AddSourceFlowAzure();     // sourceflow-azure: the endpoint queue exists and Service Bus answers

WebApplication app = builder.Build();
app.Run();
```

`UseAzureTransport` validates the options at once: exactly one of `FullyQualifiedNamespace` and `ConnectionString`, a host name rather than a URI, a well-formed connection string, and every value in range. When the host starts it provisions or validates the service's entities ([Provisioning](#provisioning)), opens one receiver on the endpoint queue, starts the outbox relay, and starts the dead-letter ingestor.

## How SourceFlow maps to Service Bus

| SourceFlow concept | Service Bus entity | Notes |
|--------------------|--------------------|-------|
| Endpoint (a service's input) | Queue `{prefix}{endpoint}` | Receives the service's commands **and** the events forwarded from every subscription. Sessions on when ordered. One receiver per service instance. |
| Dead-letter backstop | The queue's built-in `$DeadLetterQueue` | `MaxDeliveryCount` = the topology's delivery limit; expired messages are dead-lettered too (`DeadLetteringOnMessageExpiration`). |
| Topic | Topic `{prefix}{topic}` | Owned by the publishing service. |
| Subscription | Subscription `{prefix}{endpoint}` on the topic, `ForwardTo` = the endpoint queue | One per subscribing service; instances of a service compete on its queue. Correlation or SQL rules on `cloudEvents:type`. |
| Remote command | `SendMessagesAsync` to the owner's queue | Batches built with `ServiceBusMessageBatch`. |

The capabilities the transport declares to the core (`AzureTransport.Capabilities`):

| Capability | Value |
|------------|-------|
| `SystemName` (`messaging.system`) | `servicebus` |
| `MaxBodyBytes` | 192 KiB (`AzureWire.MaxBodyBytes`; 256 KiB messages on the standard tier) |
| `MaxAttributes` | 64 |
| `MaxRetention` | `MessageTimeToLive` |
| `NativeDelay` / `MaxDelay` | yes / 1 hour (`AzureTransport.MaxDelay`) |
| `OrderedDelivery` | yes (sessions) |
| `NativeDeadLetter` | yes |
| `SubscriptionFilters` | yes (rules, with a receiver-side fallback) |
| `MaxBatchSize` | 100 |
| `Deduplication` | only when `DuplicateDetectionWindow` is set |
| `MaxLockDuration` | 12 hours (the receiver keeps the broker lock renewed) |

## Configuration

`UseAzureTransport(Action<AzureTransportOptions>)` registers `AzureTransport` as a singleton and as the service's `ITransport`.

### `AzureTransportOptions`

| Option | Type | Default | Maps to / rules |
|--------|------|---------|-----------------|
| `FullyQualifiedNamespace` | `string?` | `null` | The namespace host name, for example `contoso.servicebus.windows.net` (not a URI). Exclusive with `ConnectionString`. |
| `Credential` | `TokenCredential?` | `null` (`DefaultAzureCredential`) | The credential for `FullyQualifiedNamespace`. |
| `ConnectionString` | `string?` | `null` | For Development and the emulator. Exclusive with `FullyQualifiedNamespace`. Outside Development the host logs a warning (event id 3401). |
| `AdministrationConnectionString` | `string?` | `null` (uses `ConnectionString`) | The management endpoint when it differs, as on the emulator (`Endpoint=sb://localhost:5300;…`). Only with `ConnectionString`. |
| `TransportType` | `ServiceBusTransportType` | `AmqpTcp` | `ServiceBusClientOptions.TransportType`. `AmqpWebSockets` uses port 443 through proxies. |
| `MaxRetries` | `int` | `3` | `ServiceBusRetryOptions.MaxRetries` (exponential mode) and the administration client's retries; 0 to 10. |
| `TryTimeout` | `TimeSpan` | 60 seconds | `ServiceBusRetryOptions.TryTimeout`; also how long accepting a session waits. 1 second to 10 minutes. |
| `NamePrefix` | `string` | `""` | Prepended to every queue, topic and subscription name. Letters, digits, `-`, `_` and `.`; at most 40 characters. |
| `LockDuration` | `TimeSpan` | 30 seconds | `LockDuration` of new queues; 5 seconds to 5 minutes. Bounds how long a dead process holds a message. |
| `MessageTimeToLive` | `TimeSpan` | 14 days | `DefaultMessageTimeToLive` of new queues, topics and subscriptions; at least 1 second. |
| `DuplicateDetectionWindow` | `TimeSpan?` | `null` (off) | `RequiresDuplicateDetection` with this window on new queues; 20 seconds to 7 days. |
| `MaxConcurrentSessions` | `int` | `8` | How many sessions one receiver of an ordered endpoint works on at once; 1 to 100. |

| Method | Purpose |
|--------|---------|
| `Validate()` | Called by `UseAzureTransport`; throws `ArgumentException` (or `FormatException` for a malformed connection string). |
| `ToClientOptions()` | The `ServiceBusClientOptions` (transport type, exponential retries). |
| `CreateClient()`, `CreateAdministrationClient()` | The clients the options describe. |

### Runtime options that shape the transport

These are core options (`sf.Configure(o => …)`, see [SourceFlow.Net](SourceFlow.Net-README.md)).

| `SourceFlowOptions` | Default | Effect on Service Bus |
|---------------------|---------|------------------------|
| `ReceiveLockDuration` | 30 seconds | The receiver's own lock. When it is longer than the queue's `LockDuration`, the receiver keeps renewing the broker lock until its own lock passes. The receive endpoint renews at half this interval while a handler runs. |
| `PrefetchPerWorker` | 16 | Messages asked for per receive (the receiver itself uses `PrefetchCount = 0`). |
| `ImmediateRetries` | 3 | In-process retries before a delayed retry. |
| `DelayedRetries` | 10 s, 1 min, 10 min, 1 h | Native delays, at most 1 hour each. |
| `IngestBrokerDeadLetters` | `true` | Runs the dead-letter ingestor on receiver instances. |
| `DeadLetterIngestInterval` | 30 seconds | How often the ingestor checks `$DeadLetterQueue` when it was empty. |

The queue's `MaxDeliveryCount` is `1 + DelayedRetries.Count + 2`: 7 with the default schedule.

### Using your own clients

`AzureTransport` has a constructor over an existing `ServiceBusClient` and `ServiceBusAdministrationClient`, which you own and dispose. Only the naming, entity and session options apply then.

```csharp
builder.Services.AddSingleton(sp => new AzureTransport(client, administration, new AzureTransportOptions
{
    FullyQualifiedNamespace = "contoso.servicebus.windows.net",
    NamePrefix = "prod-",
}));
builder.Services.AddSourceFlow(sf =>
{
    // ...
    sf.UseTransport(sp => sp.GetRequiredService<AzureTransport>());
});
```

## Connecting and authenticating

- **Production:** set `FullyQualifiedNamespace` and leave `Credential` unset to use `DefaultAzureCredential` (managed identity, workload identity, Azure CLI), or pass any `TokenCredential`.
- **Development and the emulator:** set `ConnectionString`. In any other environment the transport still works, but the host logs: *"The Service Bus transport uses a connection string in the '{Environment}' environment; use FullyQualifiedNamespace with a managed identity outside Development."*
- A credential that cannot get a token right now (`AuthenticationFailedException`, `CredentialUnavailableException`) is a transient error, so an identity endpoint hiccup recovers without a restart. `UnauthorizedAccessException` (a missing role) is permanent.
- `ServiceBusy` and `QuotaExceeded` are mapped to `TransportThrottledException` with a 10-second `RetryAfter`.

## Entity names

Names come from `AzureNaming`:

| Entity | Name | Limit |
|--------|------|-------|
| Endpoint queue | `{prefix}{endpoint}` | 260 |
| Topic | `{prefix}{topic}` | 260 |
| Subscription on a subscribed topic | `{prefix}{endpoint}` | 50 |

- Characters other than letters, digits, `-`, `_` and `.` become `-`. A name that does not start with a letter or digit gets `sf` in front; one that does not end with one gets `0` appended.
- Long names keep a readable head and an 8-character hash.
- Queues and topics share one name space in a namespace. `Validate` points out a queue that occupies a topic's name.

## Ordering (sessions)

```csharp
sf.Routing(r =>
{
    r.OrderedEndpoint();   // the endpoint queue requires sessions
});
```

- Every message carries a session id: the envelope's partition key (the stream id by default), or the message id when it has none. Keys longer than 128 characters are hashed (`k-{xxhash64}`).
- An ordered endpoint's queue has `RequiresSession = true`. Its receiver (`AzureSessionReceiver`) works on up to `MaxConcurrentSessions` sessions, accepts the next free session in the background, and takes a message from a session only while none of that session's messages is unsettled. A key's next message therefore waits for its head on every instance, even though Service Bus itself would hand it out.
- Session locks of sessions with messages in flight are renewed automatically.
- Sessions are immutable on a queue. Changing the ordering means recreating the queue; `Validate` reports it as `OrderingMismatch`.
- `OrderedTopic(...)` is accepted in routing for portability. On Service Bus, order across instances comes from the subscriber's session queue.

## Provisioning

Provisioning follows [ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md). The host calls `ProvisionAsync(topology, mode)` once at startup.

| `ProvisioningMode` | What happens on Service Bus |
|--------------------|------------------------------|
| `CreateIfMissing` | Creates what is missing: the endpoint queue (sessions when ordered, `MaxDeliveryCount`, `LockDuration`, `DefaultMessageTimeToLive`, dead-lettering on expiry, duplicate detection when configured), published and subscribed topics, and on each subscribed topic a forwarding subscription with the filter's rules. An entity created concurrently by another instance is accepted. Existing entities are never changed. Then validates. |
| `Validate` | Reads the entities and fails startup with a `TopologyValidationException` listing every difference at once. |
| `None` | Only checks the topology itself; no Service Bus call. |

When `SourceFlowBuilder.Provisioning` is `null`, the mode is `CreateIfMissing` in Development (or without a host environment) and `Validate` elsewhere.

What `Validate` checks:

| Problem kind | Detected when |
|--------------|---------------|
| `MissingEndpoint` | The queue does not exist. |
| `OrderingMismatch` | Sessions are on and the endpoint is unordered, or the reverse. |
| `MissingDeadLetter` | `MaxDeliveryCount` differs from the topology's delivery limit. |
| `Misconfigured` | The queue itself forwards elsewhere (its receivers would get nothing); duplicate detection is configured but off on the queue; a subscription forwards somewhere other than the endpoint queue. |
| `MissingTopic` | A published or subscribed topic does not exist (with a hint when a queue has that name). |
| `MissingSubscription` | The topic has no subscription for the endpoint. |
| `FilterDrift` | The subscription's rules select different messages than the routing filter. Rule names do not matter. |

### Infrastructure as code

```bash
dotnet run -- sourceflow topology export --output orders.topology.json
dotnet run -- sourceflow topology validate
```

`AzureTopology.Describe(options, topology)` (or the CLI export) names every entity as a path (`{queue}`, `{topic}/{subscription}`, `{topic}/{subscription}/{rule}`) with ISO 8601 durations as ARM takes them. [`deploy/topology/terraform/azure`](../../../deploy/topology/README.md) and `deploy/topology/bicep/servicebus.bicep` create the entities in an existing namespace from that export. ARM creates a match-everything `$Default` rule with every subscription; the export names each subscription's first rule `$Default` so the modules replace it rather than add one beside it.

## Permissions

| Activity | Needs |
|----------|-------|
| Receive and settle on the endpoint queue | Data Receiver on the queue |
| Send to remote endpoints, publish to topics, schedule delayed retries | Data Sender on those entities (the endpoint's own queue too, for delayed retries) |
| Learn whether the endpoint has sessions, and `Validate` | The Manage right, because Service Bus reads entity descriptions only with Manage |
| `CreateIfMissing` | The Manage right on the namespace |

The receiver learns whether its queue has sessions from provisioning in the same process, or from the queue's description. In practice give each service **Azure Service Bus Data Owner** on its own queue, or let it provision itself (see [`deploy/topology`](../../../deploy/topology/README.md)).

## Locks, retries and dead letters

| Step | Service Bus operation |
|------|-----------------------|
| Receive | `ReceiveMessagesAsync` (peek-lock) on the queue, or on accepted sessions |
| Keep the lock | `RenewMessageLockAsync`, by the receiver's housekeeping and by the receive endpoint's renewals |
| Complete after the handler commits | `CompleteMessageAsync` |
| Delayed retry, no sessions | `ScheduleMessageAsync` of a copy at now + delay (at most 1 hour), then complete the original |
| Delayed retry, sessions | The message keeps its place: it holds its session, lock renewed, until the delay passes, then it is abandoned |
| Zero-delay abandon | `AbandonMessageAsync` |
| Explicit dead letter | `DeadLetterMessageAsync(reason)` |
| Graceful stop | Every unsettled message is abandoned, so another instance gets it at once |

Details that matter in operation:

- **Delivery count across delayed retries.** The scheduled copy carries the deliveries so far in the `sf-deliveries` property and gets a broker message id of its own (`{id}/r{n}`), so duplicate detection keeps it and `DeliveryCount` keeps counting.
- **Late settlement.** A settlement after the lock was lost (`MessageLockLost`, `SessionLockLost`) is ignored; the redelivery is absorbed by the inbox.
- **Broker backstop.** After `MaxDeliveryCount` deliveries Service Bus moves a message to `$DeadLetterQueue`. The dead-letter ingestor drains it into the store with the broker's reason (or `MaxDeliveryCountExceeded`), so `dotnet sourceflow dlq replay` can re-send it.
- **Foreign messages.** A message that is not a SourceFlow CloudEvent is dead-lettered with reason `MalformedMessage`. On the dead-letter queue such a message is deferred, so the ingestor moves on and it stays for inspection by sequence number.
- **Parking.** A permanent failure, or a transient one after the last delayed retry, is parked in the store's dead-letter table and the message is completed. See [SourceFlow.Tool](SourceFlow.Tool-README.md) for `dlq` commands.

## Message size and wire format

`AzureWire` uses the CloudEvents AMQP binding:

| Item | How it travels |
|------|----------------|
| CloudEvents attributes | Application properties `cloudEvents:{attribute}` (`cloudEvents:type`, `cloudEvents:sftenant`, ...) |
| Body | The message body, byte for byte |
| `MessageId` | The envelope's message id |
| `SessionId` | The partition key (or the message id) |
| `Subject` | The CloudEvents type |
| `ContentType` | The envelope's content type |
| Published events | `sf-topic`, the logical topic, for receiver-side filters |

- `MaxBodyBytes` is 192 KiB, so the body and up to 64 KiB of properties fit a standard-tier message. A batch holds at most 100 messages within the broker's size limit.
- A message that does not fit, even alone, is refused before anything is sent for it; the rest of the batch is sent and the failure is reported per message (`TransportSendException`). The core also checks the body limit when it writes the outbox row.
- There is no claim-check: send a reference for large payloads.

## Subscription rules

```csharp
sf.Routing(r =>
{
    r.Subscribe("payments.events", s => s.Only<PaymentCaptured>());                    // correlation rule
    r.Subscribe("shipping.events", s => s.Where("type LIKE 'shipping.shipment-%'"));   // SQL LIKE rule
    r.Subscribe("inventory.events", s => s.Where("sftenant = 'acme'"));                // receiver-side
});
```

| Routing filter | Rules on the subscription (`AzureFilter`) |
|----------------|--------------------------------------------|
| None | One match-all rule (`sf-all`) |
| A list of types (`type = '…'`, `type IN (…)`) | One correlation rule per type on `cloudEvents:type` (`sf-type-0`, `sf-type-1`, ...) |
| Type prefixes (`type LIKE 'prefix%'`) | One SQL rule per prefix: `[cloudEvents:type] LIKE 'prefix%'` (`sf-prefix-0`, ...) |
| Anything else | A match-all rule; the receiver evaluates the filter for that topic (using `sf-topic`) and completes non-matching events unseen |

Results are identical whichever side filters.

## Duplicate detection

Broker duplicate detection is complementary to the inbox and off by default:

```csharp
sf.UseAzureTransport(o =>
{
    o.FullyQualifiedNamespace = "contoso.servicebus.windows.net";
    o.DuplicateDetectionWindow = TimeSpan.FromMinutes(10);
});
```

- It applies to queues the transport creates; it cannot be turned on for an existing queue (`Validate` reports it).
- Within the window the broker also drops a dead-letter replay, which keeps its message id. Replay after the window, or leave it off and rely on the inbox (see the [Message Idempotency Guide](Message-Idempotency-Guide.md)).

## Azure Key Vault key provider

```csharp
builder.Services.AddSourceFlow(sf =>
{
    // ...
    sf.Security(s =>
    {
        s.UseAzureKeyVault(k => k.KeyId = new Uri("https://my-vault.vault.azure.net/keys/messages"));
        s.Protect(p => p.Protect("accounts.events"));
    });
});
```

`AzureKeyVaultKeyProvider` (name `azure-keyvault`) generates each 256-bit data key locally and wraps it with the vault key (`wrapKey`); unwrapping uses `unwrapKey`. The key reference stored with a wrapped key is the versioned key id the vault reports, so a data key wrapped before a rotation still unwraps with the version that wrapped it. Throttling (429), server errors and network failures are transient; access denied, a missing or disabled key and a bad wrapped key are not. The identity needs the `wrapKey` and `unwrapKey` key permissions (for example the Key Vault Crypto User role).

| `AzureKeyVaultOptions` | Type | Default | Notes |
|------------------------|------|---------|-------|
| `KeyId` | `Uri?` | required | `https://{vault}.vault.azure.net/keys/{name}` (the current version wraps new keys) or a versioned id. |
| `Credential` | `TokenCredential?` | `DefaultAzureCredential` | |
| `Algorithm` | `KeyWrapAlgorithm` | `RsaOaep256` | `A256KW` for Managed HSM AES keys. |

Envelope encryption, reuse limits and erasure are covered in the [Security and Compliance Guide](Security-and-Compliance-Guide.md).

## Health checks

| Check | Registered by | What it does |
|-------|---------------|--------------|
| `sourceflow.transport` (tag `ready`) | `AddHealthChecks().AddSourceFlow()` | `AzureTransport.ProbeAsync`: the endpoint queue exists (`QueueExistsAsync`). Cached by the core. |
| `sourceflow-azure` (tag `ready`) | `AddHealthChecks().AddSourceFlowAzure(name)` | The same check as a standalone `AzureTransportHealthCheck`. |

## Local development with the Service Bus emulator

The repository pins `mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1` with its SQL Edge backing store:

```bash
MSSQL_SA_PASSWORD='Your_strong_pw1' docker compose -f .github/emulators/servicebus/docker-compose.yml up -d
curl -s http://localhost:5300/health
```

The emulator serves AMQP on port 5672 and management on port 5300, so it needs both connection strings:

```csharp
sf.UseAzureTransport(o =>
{
    o.ConnectionString = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    o.AdministrationConnectionString = "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    o.MessageTimeToLive = TimeSpan.FromHours(1);   // the emulator's maximum
});
```

- `Config.json` declares the namespace only; entities are created at runtime through the management endpoint, as on Azure, so `CreateIfMissing` (the Development default) works.
- The emulator's limits are tighter than Azure's: a time to live of at most 1 hour, a `MaxDeliveryCount` of at most 10 (the default topology uses 7), a duplicate-detection window of at most 5 minutes, and throttling of a busy namespace.

See [Cloud Integration Testing](Cloud-Integration-Testing.md) for how the test suites use it.

## Troubleshooting

| Symptom | Likely cause | What to do |
|---------|--------------|------------|
| `ArgumentException: Set exactly one of AzureTransportOptions.FullyQualifiedNamespace ... and ConnectionString` | Both or neither are set | Set one. |
| `FullyQualifiedNamespace is a host name ..., not a URI` | `sb://` or `https://` in the value | Use `contoso.servicebus.windows.net`. |
| Warning 3401 at startup | A connection string outside Development | Switch to `FullyQualifiedNamespace` and a managed identity. |
| Startup fails with `TopologyValidationException` (`MissingEndpoint`, `MissingTopic`, `MissingSubscription`) | `Validate` mode and entities not created | Apply the Terraform or Bicep module from `topology export`, or use `CreateIfMissing` where that is acceptable. |
| `OrderingMismatch ... sessions are immutable: recreate the queue` | `OrderedEndpoint()` added or removed after the queue existed | Recreate the queue (drain it first). |
| `Misconfigured ... duplicate detection is configured but the queue has it off` | `DuplicateDetectionWindow` set after creation | Recreate the queue, or remove the option. |
| Receiver fails with an authorization error reading the queue | The identity lacks Manage on the queue | Grant Azure Service Bus Data Owner on the queue, or provision from the service. |
| A replayed dead letter disappears | Duplicate detection dropped it (same message id, within the window) | Replay after the window, or turn duplicate detection off. |
| Ordered messages seem slow under load | One session (partition key) is a serial lane; a receiver holds at most `MaxConcurrentSessions` | Raise `MaxConcurrentSessions` or add instances. |
| `ServiceBusy` / throttled | Namespace quota | The client retries (`MaxRetries`) and the core backs off; consider a higher tier. |

## Reference

- Source: [`src/Cloud/SourceFlow.Cloud.Azure`](../../../src/Cloud/SourceFlow.Cloud.Azure)
- Architecture: [Cloud Transports](Architecture/07-Cloud-Transports.md)
- Design: [07 Cloud Transports](../Design/07-Cloud-Transports.md), [10 Security and Compliance](../Design/10-Security-and-Compliance.md)
- ADRs: [ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md), [ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md), [ADR-033](../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md)
- Infrastructure as code: [`deploy/topology`](../../../deploy/topology/README.md)
- Testing: [Cloud Integration Testing](Cloud-Integration-Testing.md)
- Other transports: [AWS SQS/SNS](SourceFlow.Cloud.Aws-README.md), [Google Cloud Pub/Sub](SourceFlow.Cloud.Gcp-README.md)
