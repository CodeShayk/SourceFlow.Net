# SourceFlow AWS Cloud Integration

**Package:** `SourceFlow.Cloud.AWS`
**Version:** 2.0.0
**Targets:** `netstandard2.1` · `net8.0` · `net9.0` · `net10.0`

---

## Table of Contents

1. [Overview](#1-overview)
2. [Architecture](#2-architecture)
3. [Installation & Dependencies](#3-installation--dependencies)
4. [Setup & Registration](#4-setup--registration)
5. [Bus Configuration (Routing)](#5-bus-configuration-routing)
6. [Bootstrap Process](#6-bootstrap-process)
7. [Command Messaging — SQS](#7-command-messaging--sqs)
8. [Event Messaging — SNS/SQS](#8-event-messaging--snssqs)
9. [Basic vs Enhanced Tier](#9-basic-vs-enhanced-tier)
10. [Serialization](#10-serialization)
11. [Idempotency](#11-idempotency)
12. [Resilience — Circuit Breaker](#12-resilience--circuit-breaker)
13. [Security — KMS Envelope Encryption](#13-security--kms-envelope-encryption)
14. [Security — Sensitive Data Masking](#14-security--sensitive-data-masking)
15. [Dead Letter Queue Monitoring](#15-dead-letter-queue-monitoring)
16. [Observability](#16-observability)
17. [Health Checks](#17-health-checks)
18. [IAM Permissions Reference](#18-iam-permissions-reference)
19. [Configuration Reference](#19-configuration-reference)

---

## 1. Overview

`SourceFlow.Cloud.AWS` provides a production-ready, code-first integration between the SourceFlow domain model and AWS messaging infrastructure. It maps:

- **Commands** → Amazon SQS (FIFO or standard queues)
- **Events** → Amazon SNS topics, delivered via SQS subscriptions

The integration is built around three design principles:

1. **Provider boundary.** All cloud abstractions (`ICommandDispatcher`, `IEventDispatcher`, `IIdempotencyService`, `IDeadLetterStore`, `ICircuitBreaker`, `IMessageEncryption`) live in `SourceFlow/Cloud` with zero AWS coupling. AWS-specific code is entirely in `SourceFlow.Cloud.AWS`.

2. **Code-first routing.** Queue and topic *names* are declared in C# at startup. Full SQS URLs and SNS ARNs are resolved (or the resources are created) automatically by the bootstrapper before any message is sent.

3. **Two-tier messaging.** A **basic** tier handles simple send/receive. An **enhanced** tier adds circuit breaker, distributed tracing, metrics, encryption, and idempotency — all opt-in.

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Application Layer                            │
│   ICommandDispatcher.Dispatch<T>()  /  IEventDispatcher.Dispatch<T>│
└────────────────────────┬───────────────────────┬────────────────────┘
                         │                       │
          ┌──────────────▼──────────┐ ┌──────────▼──────────────┐
          │  AwsSqsCommandDispatcher│ │ AwsSnsEventDispatcher   │
          │  (basic / enhanced)     │ │ (basic / enhanced)      │
          └──────────────┬──────────┘ └──────────┬──────────────┘
                         │ JSON + attrs           │ JSON + attrs
                    ┌────▼────┐             ┌─────▼─────┐
                    │   SQS   │             │    SNS    │
                    │  Queue  │◄────────────│   Topic   │
                    └────┬────┘  subscribe  └───────────┘
                         │
          ┌──────────────▼──────────────────────────────┐
          │  AwsSqsCommandListener / AwsSnsEventListener │
          │  (BackgroundService — long-poll loop)        │
          └──────────────┬──────────────────────────────┘
                         │
                ┌────────▼────────┐
                │ ICommandSubscriber /  │
                │ IEventSubscriber      │
                └────────────────────┘

Cross-cutting (enhanced tier only):
  CircuitBreaker ─ IMessageEncryption ─ IIdempotencyService
  CloudTelemetry ─ CloudMetrics ─ SensitiveDataMasker
  IDeadLetterStore ─ AwsDeadLetterMonitor
```

### Startup Sequence

```
1. UseSourceFlowAws() called in Program.cs / Startup
   └─ BusConfiguration built from fluent API (short names only)
   └─ IHostedService registrations queued

2. AwsBusBootstrapper.StartAsync() runs first
   └─ Validates: topics without queues → InvalidOperationException
   └─ Resolves each queue name → GetQueueUrlAsync (or CreateQueueAsync)
   └─ Resolves each topic name → CreateTopicAsync (idempotent)
   └─ Subscribes topics → first command queue (SQS protocol)
   └─ Calls BusConfiguration.Resolve() — injects full URLs/ARNs

3. AwsSqsCommandListener.ExecuteAsync() starts
   └─ Reads resolved queue URLs from ICommandRoutingConfiguration
   └─ Spawns one long-poll Task per queue

4. AwsSnsEventListener.ExecuteAsync() starts
   └─ Reads resolved event-listening URLs
   └─ Spawns one long-poll Task per queue
```

---

## 3. Installation & Dependencies

### NuGet Package

```xml
<PackageReference Include="SourceFlow.Cloud.AWS" Version="2.0.0" />
```

### Pulled-in AWS SDK packages

| Package | Purpose |
|---------|---------|
| `AWSSDK.SQS` | Queue send/receive/delete |
| `AWSSDK.SimpleNotificationService` | Topic publish/subscribe |
| `AWSSDK.KeyManagementService` | Envelope encryption (optional) |
| `AWSSDK.Extensions.NETCore.Setup` | `AddAWSService<T>()` DI integration |

### Other dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Hosting` | BackgroundService, IHostedService |
| `Microsoft.Extensions.Caching.Memory` | DEK caching in KMS encryption |
| `Microsoft.Extensions.HealthChecks` | AwsHealthCheck |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | Options binding |

---

## 4. Setup & Registration

### Minimal setup

```csharp
// Program.cs
builder.Services.UseSourceFlowAws(
    options => options.Region = RegionEndpoint.USEast1,
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events"))
        .Listen.To.CommandQueue("orders.fifo")
        .Subscribe.To.Topic("order-events"));
```

This single call:
- Creates `AwsOptions` and registers it as a singleton
- Registers `IAmazonSQS` and `IAmazonSimpleNotificationService` via `AddAWSService<T>()`
- Builds `BusConfiguration` and registers it under three interfaces
- Registers in-memory `IIdempotencyService` + cleanup hosted service
- Registers `ICommandDispatcher` → `AwsSqsCommandDispatcher` (scoped)
- Registers `IEventDispatcher` → `AwsSnsEventDispatcher` (singleton)
- Registers `AwsBusBootstrapper` as the first hosted service
- Registers `AwsSqsCommandListener` and `AwsSnsEventListener` as hosted services
- Registers `AwsHealthCheck`

### With Entity Framework idempotency (multi-instance deployments)

```csharp
builder.Services.UseSourceFlowAws(
    options => options.Region = RegionEndpoint.EUWest1,
    bus => bus
        .Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
        .Send.Command<UpdateOrderCommand>(q => q.Queue("orders.fifo"))
        .Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events"))
        .Listen.To.CommandQueue("orders.fifo")
        .Subscribe.To.Topic("order-events"),
    idempotency => idempotency.UseEFIdempotency(
        builder.Configuration.GetConnectionString("IdempotencyDb")));
```

### Pre-registering idempotency separately

```csharp
// Register idempotency separately (e.g. from a shared infrastructure module)
builder.Services.AddSourceFlowIdempotency(
    builder.Configuration.GetConnectionString("IdempotencyDb"));

// Then register AWS without re-configuring idempotency
builder.Services.UseSourceFlowAws(
    options => options.Region = RegionEndpoint.USEast1,
    bus => bus.Send.Command<CreateOrderCommand>(q => q.Queue("orders.fifo")));
// UseSourceFlowAws sees IIdempotencyService already registered via TryAddSingleton
```

### AWS Credentials

Credentials are resolved via the standard **AWS SDK credential chain** in priority order:

1. Environment variables (`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`)
2. AWS credentials file (`~/.aws/credentials`)
3. IAM instance role (EC2, ECS, Lambda)
4. IAM role for service accounts (EKS)

> **Note:** The `AwsOptions.AccessKeyId`, `SecretAccessKey`, and `SessionToken` properties are marked `[Obsolete]`. Do not store credentials in `appsettings.json`. Use the credential chain.

---

## 5. Bus Configuration (Routing)

The `BusConfigurationBuilder` provides a fluent, compile-time-safe API for declaring all routing. It enforces two rules:

- **No URLs or ARNs at configuration time.** Pass only short names like `"orders.fifo"` or `"order-events"`. The builder throws `ArgumentException` if a URL (`https://`) or ARN (`arn:`) is passed.
- **Topics require queues.** Subscribing to topics via `.Subscribe.To.Topic()` requires at least one `.Listen.To.CommandQueue()`. Validated at bootstrap time.

### Fluent API Reference

| Section | Method | Effect |
|---------|--------|--------|
| `.Send` | `.Command<TCommand>(q => q.Queue("name"))` | Routes outbound command type to named SQS queue |
| `.Raise` | `.Event<TEvent>(t => t.Topic("name"))` | Routes outbound event type to named SNS topic |
| `.Listen.To` | `.CommandQueue("name")` | Declares a queue this service polls for inbound commands |
| `.Subscribe.To` | `.Topic("name")` | Declares an SNS topic this service subscribes to for events |

Multiple commands can share a queue. Multiple events can share a topic. Chaining is fully supported:

```csharp
bus =>
    bus.Send
           .Command<CreateOrderCommand>(q => q.Queue("orders.fifo"))
           .Command<UpdateOrderCommand>(q => q.Queue("orders.fifo"))
           .Command<CancelOrderCommand>(q => q.Queue("orders.fifo"))
       .Raise.Event<OrderCreatedEvent>(t => t.Topic("order-events"))
       .Raise.Event<OrderUpdatedEvent>(t => t.Topic("order-events"))
       .Raise.Event<OrderCancelledEvent>(t => t.Topic("order-events"))
       .Listen.To
           .CommandQueue("orders.fifo")
           .CommandQueue("inventory.fifo")
       .Subscribe.To
           .Topic("order-events")
           .Topic("payment-events")
```

### Two-phase resolution

`BusConfiguration` holds only short names at build time. The full URLs/ARNs are injected by `AwsBusBootstrapper.Resolve()` during startup. Any attempt to call `ICommandRoutingConfiguration.GetQueueName<T>()` or similar before bootstrap throws `InvalidOperationException` with a descriptive message:

```
BusConfiguration has not been bootstrapped yet. Ensure the bus bootstrapper
(registered as IHostedService) completes before dispatching commands or events.
```

---

## 6. Bootstrap Process

`AwsBusBootstrapper` is registered as the first `IHostedService` and runs once during `StartAsync`. It bridges the gap between short names and live AWS resources.

### Steps

```
Step 0 — Validate
  If subscribedTopics.Count > 0 && commandListeningQueues.Count == 0
    → throw InvalidOperationException

Step 1 — Collect unique queue names
  Union of: CommandTypeToQueueName.Values + CommandListeningQueueNames

Step 2 — Resolve / create each SQS queue
  For each queue name:
    → GetQueueUrlAsync(name)         [queue exists → use URL]
    → on QueueDoesNotExistException:
        CreateQueueAsync(name)       [auto-create]
        If name ends with ".fifo":
          attributes: FifoQueue=true, ContentBasedDeduplication=true
  Errors at this stage are logged with the queue name, then re-thrown.

Step 3 — Collect unique topic names
  Union of: EventTypeToTopicName.Values + SubscribedTopicNames

Step 4 — Resolve / create each SNS topic
  CreateTopicAsync(name)             [idempotent — returns existing ARN]

Step 5 — Subscribe topics → first command queue
  For each subscribed topic ARN:
    GetQueueAttributesAsync → extract QueueArn
    SubscribeAsync(topicArn, protocol="sqs", endpoint=queueArn)
                                     [idempotent — returns existing subscription ARN]

Step 6 — Call BusConfiguration.Resolve()
  Injects full URLs/ARNs into BusConfiguration
  From this point listeners can read resolved URLs
```

### Idempotency

`CreateTopicAsync` and `SubscribeAsync` are idempotent AWS API calls — safe to call on every restart even when resources already exist.

`GetQueueUrlAsync` + `CreateQueueAsync` on `QueueDoesNotExistException` achieves the same effect for queues.

### FIFO queue auto-detection

Any queue name ending in `.fifo` is created with:
```
FifoQueue                 = "true"
ContentBasedDeduplication = "true"
```

---

## 7. Command Messaging — SQS

### Dispatching commands

Commands implement `ICommand` from the core `SourceFlow` package. Dispatchers are registered as `ICommandDispatcher`.

```csharp
// Inject and use
public class OrderService(ICommandDispatcher dispatcher)
{
    public Task CreateOrder(CreateOrderRequest req) =>
        dispatcher.Dispatch(new CreateOrderCommand { /* ... */ });
}
```

### Message format

Each SQS message carries:

| Attribute | Value |
|-----------|-------|
| `MessageBody` | JSON-serialized command (camelCase, nulls omitted) |
| `CommandType` | `typeof(TCommand).AssemblyQualifiedName` |
| `EntityId` | `command.Entity?.Id.ToString()` |
| `SequenceNo` | `command.Metadata?.SequenceNo.ToString()` |
| `MessageGroupId` | `command.Entity?.Id` or new `Guid` (FIFO ordering) |
| `traceparent` | W3C trace context (enhanced tier only) |
| `AWSTraceHeader` | X-Ray trace header (enhanced tier only) |

### Receiving commands

`AwsSqsCommandListener` (a `BackgroundService`) long-polls each configured queue in parallel:

```
1. ReceiveMessageAsync
     WaitTimeSeconds   = AwsOptions.SqsReceiveWaitTimeSeconds (default 20)
     MaxNumberOfMessages = AwsOptions.SqsMaxNumberOfMessages (default 10)
     VisibilityTimeout = AwsOptions.SqsVisibilityTimeoutSeconds (default 300)

2. For each message:
     a. Read CommandType attribute
     b. Resolve CLR type via ConcurrentDictionary cache → Type.GetType()
     c. Deserialize JSON body to resolved type
     d. Create DI scope
     e. Resolve ICommandSubscriber from scope
     f. Invoke Subscribe<TCommand>(command) via cached MethodInfo
     g. DeleteMessageAsync on success

3. On OperationCanceledException → exit loop
4. On any other exception → exponential backoff (2^retry seconds, max 60s), retry
```

> **Error handling (basic tier):** `JsonException` during deserialization deletes the message to prevent indefinite retries blocking a FIFO queue. Handler exceptions return the message to the queue (visibility timeout expiry), eventually moving it to the AWS-native DLQ.

### Type caching

Both dispatchers and listeners maintain two static `ConcurrentDictionary` caches per class:

```csharp
static readonly ConcurrentDictionary<string, Type?>     _typeCache       = new();
static readonly ConcurrentDictionary<Type, MethodInfo?> _methodInfoCache = new();
```

This means `Type.GetType()` and `MethodInfo.MakeGenericMethod()` are only called once per type encountered, not on every message.

---

## 8. Event Messaging — SNS/SQS

### Dispatching events

Events implement `IEvent`. Dispatchers are registered as `IEventDispatcher`.

```csharp
public class OrderService(IEventDispatcher dispatcher)
{
    public Task PublishOrderCreated(Order order) =>
        dispatcher.Dispatch(new OrderCreatedEvent(order));
}
```

### Message format

Each SNS publish carries:

| Attribute | Value |
|-----------|-------|
| `Message` | JSON-serialized event body (camelCase, nulls omitted) |
| `Subject` | `event.Name` |
| `EventType` | `typeof(TEvent).AssemblyQualifiedName` |
| `EventName` | `event.Name` |
| `SequenceNo` | `event.Metadata?.SequenceNo.ToString()` |
| `traceparent` | W3C trace context (enhanced tier only) |

### Receiving events

SNS delivers to the subscribed SQS queue wrapped in a notification envelope:

```json
{
  "Type": "Notification",
  "MessageId": "...",
  "TopicArn": "arn:aws:sns:...",
  "Subject": "OrderCreatedEvent",
  "Message": "{...event JSON...}",
  "MessageAttributes": {
    "EventType": { "Type": "String", "Value": "Acme.Orders.OrderCreatedEvent, ..." }
  }
}
```

`AwsSnsEventListener` processes this envelope:

```
1. ReceiveMessageAsync from SQS queue subscribed to SNS

2. For each message:
     a. Deserialize SNS notification wrapper (SnsNotification)
        → JsonException: delete message (malformed wrapper, prevent retries)
     b. Read EventType from MessageAttributes
     c. Resolve CLR type via cache → Type.GetType()
        → null: delete message (unresolvable type)
     d. Deserialize snsNotification.Message to resolved event type
        → JsonException: delete message (malformed payload)
     e. Create DI scope
     f. Resolve all IEventSubscriber registrations from scope
     g. Invoke Subscribe<TEvent>(event) via cached MethodInfo on each subscriber
     h. Await all subscriber tasks (Task.WhenAll)
     i. DeleteMessageAsync

3. On exception → exponential backoff, retry
```

### Fan-out pattern

When multiple services subscribe to the same SNS topic, each service has its own SQS queue subscribed to the topic. SNS delivers one copy of each event to every subscriber's queue. The bootstrapper subscribes the first command-listening queue to each declared topic — this is also used as the event-listening queue.

```
Producer Service
   │
   └─ SNS Topic "order-events"
        ├─ SQS Queue "orders.fifo"     → Order Service listener
        ├─ SQS Queue "invoicing.fifo"  → Invoicing Service listener
        └─ SQS Queue "analytics.fifo"  → Analytics Service listener
```

---

## 9. Basic vs Enhanced Tier

Every dispatcher and listener exists in two variants:

| Class | Tier | Extra Capabilities |
|-------|------|--------------------|
| `AwsSqsCommandDispatcher` | Basic | Route check, serialize, send |
| `AwsSqsCommandDispatcherEnhanced` | Enhanced | + Circuit breaker, tracing, metrics, encryption, masker |
| `AwsSnsEventDispatcher` | Basic | Route check, serialize, publish |
| `AwsSnsEventDispatcherEnhanced` | Enhanced | + Circuit breaker, tracing, metrics, encryption, masker |
| `AwsSqsCommandListener` | Basic | Deserialize, invoke handler, delete |
| `AwsSqsCommandListenerEnhanced` | Enhanced | + Idempotency, tracing, metrics, decryption, DLQ records |
| `AwsSnsEventListener` | Basic | Unwrap SNS envelope, invoke handler, delete |
| `AwsSnsEventListenerEnhanced` | Enhanced | + Idempotency, tracing, metrics, decryption, DLQ records |

`UseSourceFlowAws()` registers the **basic** tier by default. To use the enhanced tier, register the enhanced classes manually or extend `IocExtensions`.

### Enhanced dispatcher flow (command example)

```
Dispatch<TCommand>(command)
│
├─ ShouldRoute<TCommand>() → false → return (no-op)
│
├─ StartCommandDispatch() → Activity started
│
└─ circuitBreaker.ExecuteAsync(async () =>
      1. JsonSerializer.Serialize(command)
      2. if encryption != null → EncryptAsync(json)
      3. CloudMetrics.RecordMessageSize(bodyLength)
      4. Build MessageAttributes dict
      5. InjectTraceContext(activity, attributes)
      6. sqsClient.SendMessageAsync(request)
      return true
   )
   │
   ├─ success → RecordSuccess(activity), RecordCommandDispatched(),
   │            RecordDispatchDuration(), RecordAwsCommandDispatched()
   │            Log (with MaskLazy for sensitive data)
   │
   ├─ CircuitBreakerOpenException → RecordError(activity), log warning, re-throw
   │
   └─ Exception → RecordError(activity), log error, re-throw
```

### Enhanced listener flow (command example)

```
ProcessMessage(message, queueUrl, ct)
│
├─ 1. Read CommandType attribute → missing → CreateDeadLetterRecord, return
├─ 2. Resolve CLR type → null → CreateDeadLetterRecord, return
├─ 3. Extract traceparent
├─ 4. StartCommandProcess() → Activity started
├─ 5. HasProcessedAsync(key) → true → log duplicate, delete, return
├─ 6. if encryption → DecryptAsync(body)
├─ 7. RecordMessageSize()
├─ 8. Deserialize to commandType → null → CreateDeadLetterRecord, return
├─ 9. Create DI scope
├─ 10. Invoke ICommandSubscriber.Subscribe<T>(command)
├─ 11. MarkAsProcessedAsync(key, ttl=24h)
├─ 12. DeleteMessageAsync (success)
├─ 13. RecordSuccess(), RecordCommandProcessed(), RecordProcessingDuration()
│      Log (with MaskLazy)
│
└─ Exception:
     RecordError(), RecordCommandProcessed(success=false)
     if receiveCount > 3 → CreateDeadLetterRecord(exception)
     (message returns to queue via visibility timeout)
```

---

## 10. Serialization

### Default JSON options

All serializers use:

```csharp
new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
}
```

### Custom converters

Three converters handle SourceFlow-specific polymorphic types:

| Converter | Handles | Format |
|-----------|---------|--------|
| `CommandPayloadConverter` | `IPayload` | `{ "$type": "AssemblyQualifiedName", "$value": { ...payload... } }` |
| `EntityConverter` | `IEntity` | `{ "$type": "AssemblyQualifiedName", "$value": { ...entity... } }` |
| `MetadataConverter` | `Metadata` | `{ "eventId": ..., "isReplay": ..., "occurredOn": ..., "sequenceNo": ..., "properties": { ... } }` |

`CommandPayloadConverter` and `EntityConverter` preserve the concrete type by embedding `$type` (AssemblyQualifiedName) alongside the `$value` so the reader can reconstruct the original type.

### PolymorphicJsonConverter base

`PolymorphicJsonConverter<T>` is an abstract base for custom polymorphic converters. It:

- **Writes:** embeds `$type` discriminator (AssemblyQualifiedName), then serializes remaining properties
- **Reads:** reads `$type`, calls `Type.GetType(typeIdentifier)`, throws `JsonException` with the unresolved name if null, then deserializes the JSON as the concrete type

---

## 11. Idempotency

Idempotency prevents a command or event from being processed twice if SQS delivers it more than once (at-least-once delivery guarantee).

### In-memory (default — single instance)

```
HasProcessedAsync(key)
  → ConcurrentDictionary.TryGetValue(key, record)
  → if found && record.ExpiresAt > UtcNow → true (duplicate)
  → if found && expired → remove, return false
  → not found → false

MarkAsProcessedAsync(key, ttl)
  → stores IdempotencyRecord { ExpiresAt = UtcNow + ttl }
```

A `InMemoryIdempotencyCleanupService` (hosted service) runs every minute and removes expired records.

**Limitation:** resets on restart; does not share state between instances. Suitable for single-instance deployments or stateful compute (EC2 auto-scaling groups with sticky sessions).

### Entity Framework (multi-instance)

```csharp
idempotency => idempotency.UseEFIdempotency(connectionString)
```

Backed by a SQL table. Safe across restarts and multiple service instances. Requires the `SourceFlow.Stores.EntityFramework` package.

### Custom implementation

```csharp
idempotency => idempotency.UseCustom<MyRedisIdempotencyService>()
// or factory:
idempotency => idempotency.UseCustom(sp =>
    new MyRedisIdempotencyService(sp.GetRequiredService<IConnectionMultiplexer>()))
```

### Idempotency key

In the enhanced listeners the key is:

```
"{CommandTypeName}:{MessageId}"
// e.g. "CreateOrderCommand:abc-123-def"
```

TTL defaults to **24 hours**.

### Statistics

```csharp
var stats = await idempotencyService.GetStatisticsAsync();
// stats.TotalChecks       - total HasProcessedAsync calls
// stats.DuplicatesDetected - how many returned true
// stats.UniqueMessages    - TotalChecks - DuplicatesDetected
// stats.CacheSize         - number of live records in store
```

---

## 12. Resilience — Circuit Breaker

The enhanced dispatchers wrap every AWS call in a `ICircuitBreaker.ExecuteAsync()`. The circuit breaker implements the standard three-state machine.

### State machine

```
          FailureThreshold consecutive failures
Closed ──────────────────────────────────────► Open
  ▲                                              │
  │    SuccessThreshold successes                │ OpenDuration elapsed
  │                                              ▼
  └──────────────────────────────────── HalfOpen
          (any failure → back to Open)
```

### Default options

| Option | Default | Description |
|--------|---------|-------------|
| `FailureThreshold` | 5 | Consecutive failures before opening |
| `OpenDuration` | 1 minute | Time before transitioning to HalfOpen |
| `SuccessThreshold` | 2 | Successes in HalfOpen before closing |
| `OperationTimeout` | 30 seconds | Max time for a single operation |
| `HandledExceptions` | `[]` (all) | If set, only these types count as failures |
| `IgnoredExceptions` | `[]` | These types are never counted as failures |
| `EnableFallback` | false | Triggers fallback logic on open (app-level) |

### Configuration

```csharp
services.Configure<CircuitBreakerOptions>(options =>
{
    options.FailureThreshold = 3;
    options.OpenDuration = TimeSpan.FromSeconds(30);
    options.SuccessThreshold = 1;
    options.OperationTimeout = TimeSpan.FromSeconds(10);
    options.HandledExceptions = new[] { typeof(AmazonSQSException) };
    options.IgnoredExceptions = new[] { typeof(OperationCanceledException) };
});
services.AddSingleton<ICircuitBreaker, CircuitBreaker>();
```

### Monitoring

```csharp
var stats = circuitBreaker.GetStatistics();
// stats.CurrentState          - Closed / Open / HalfOpen
// stats.TotalCalls            - total ExecuteAsync calls
// stats.SuccessfulCalls       - operations that completed
// stats.FailedCalls           - operations that threw a counted exception
// stats.RejectedCalls         - calls blocked because circuit was Open
// stats.LastStateChange       - when state last changed
// stats.LastFailure           - timestamp of most recent failure

// Forcibly change state (e.g. from a management endpoint)
circuitBreaker.Reset(); // → Closed
circuitBreaker.Trip();  // → Open

// Subscribe to transitions
circuitBreaker.StateChanged += (_, args) =>
    logger.LogWarning("Circuit {From} → {To}", args.PreviousState, args.NewState);
```

---

## 13. Security — KMS Envelope Encryption

`AwsKmsMessageEncryption` implements `IMessageEncryption` using AWS KMS with the **envelope encryption pattern**:

```
Encrypt(plaintext)
│
├─ 1. KMS GenerateDataKeyAsync → { PlaintextKey (32 bytes), EncryptedKey }
├─ 2. AES-256-GCM encrypt plaintext using PlaintextKey
│       nonce = 12 random bytes
│       ciphertext + 16-byte authentication tag
├─ 3. Build envelope:
│       { "encryptedDataKey": base64, "nonce": base64,
│         "tag": base64, "ciphertext": base64 }
└─ 4. base64( JSON(envelope) ) → stored as message body

Decrypt(envelopeBase64)
│
├─ 1. Decode base64 → JSON → EnvelopeData
├─ 2. KMS DecryptAsync(encryptedDataKey) → PlaintextKey
├─ 3. AES-256-GCM decrypt(ciphertext, nonce, tag) → plaintext
└─ 4. Return UTF-8 string
```

### DEK caching

To avoid a KMS API call on every message, the data encryption key (DEK) is cached in `IMemoryCache`:

```csharp
// CacheDataKeySeconds = 300 (5 minutes default)
// CacheDataKeySeconds = 0   → no caching, new DEK per message
```

On cache eviction, `Array.Clear()` zeros the plaintext key bytes to prevent it lingering in memory.

### Configuration

```csharp
services.AddSingleton(new AwsKmsOptions
{
    MasterKeyId = "arn:aws:kms:us-east-1:123456789:key/abc-def",
    CacheDataKeySeconds = 300
});
services.AddMemoryCache();
services.AddSingleton<IMessageEncryption, AwsKmsMessageEncryption>();
```

### Error handling

If KMS reports a tampered or wrong-key ciphertext (`InvalidCiphertextException`), it is wrapped in `MessageDecryptionException` with a safe, sanitised message (raw ciphertext bytes are not included in the exception).

---

## 14. Security — Sensitive Data Masking

`SensitiveDataMasker` masks sensitive fields in objects before they are written to logs. It uses `[SensitiveData]` attribute on model properties.

### Supported masking types

| `SensitiveDataType` | Input example | Output |
|--------------------|---------------|--------|
| `CreditCard` | `4111111111111234` | `************1234` |
| `Email` | `user@example.com` | `***@example.com` |
| `PhoneNumber` | `+44 7911 123456` | `***-***-3456` |
| `SSN` | `123-45-6789` | `***-**-6789` |
| `PersonalName` | `John Smith` | `J*** S****` |
| `IPAddress` | `192.168.1.100` | `192.*.*.*` |
| `Password` | `s3cr3t!` | `********` |
| `ApiKey` | `sk-abcdefghijklmnop` | `sk-a...mnop` |

### Usage

```csharp
// Decorate model properties
public class PaymentCommand : ICommand
{
    [SensitiveData(SensitiveDataType.CreditCard)]
    public string CardNumber { get; set; }

    [SensitiveData(SensitiveDataType.Email)]
    public string CustomerEmail { get; set; }
}

// Direct masking (allocates immediately)
var masked = dataMasker.Mask(command);

// Lazy masking (allocates only if the log level is active)
logger.LogInformation("Processing {Command}", dataMasker.MaskLazy(command));
```

`MaskLazy` returns a `LazyMaskValue` struct whose `ToString()` only calls `Mask()` when the logging framework evaluates the argument. This avoids serialising large objects when debug logging is disabled.

### Nested objects

The masker walks the JSON representation recursively. A `[SensitiveData]` attribute on a property inside a nested object is also respected.

---

## 15. Dead Letter Queue Monitoring

`AwsDeadLetterMonitor` is an optional background service that watches configured DLQ URLs and:

1. Polls queue depth via `GetQueueAttributesAsync`
2. Updates `CloudMetrics.UpdateDlqDepth(count)`
3. Receives messages and creates `DeadLetterRecord` objects
4. Stores records in `IDeadLetterStore` (in-memory or custom)
5. Optionally logs a WARN alert when depth exceeds `AlertThreshold`
6. Optionally deletes messages after processing (`DeleteAfterProcessing`)
7. Exposes `ReplayMessagesAsync()` for controlled message replay

### Configuration

```csharp
services.AddSingleton(new AwsDeadLetterMonitorOptions
{
    Enabled = true,
    DeadLetterQueues = new List<string>
    {
        "https://sqs.us-east-1.amazonaws.com/123456/orders-dlq",
        "https://sqs.us-east-1.amazonaws.com/123456/inventory-dlq"
    },
    CheckIntervalSeconds = 60,
    BatchSize = 10,
    StoreRecords = true,
    SendAlerts = true,
    AlertThreshold = 10,
    DeleteAfterProcessing = false
});
services.AddHostedService<AwsDeadLetterMonitor>();
```

### Message replay

```csharp
// Inject AwsDeadLetterMonitor
var replayed = await monitor.ReplayMessagesAsync(
    deadLetterQueueUrl: "https://sqs.us-east-1.amazonaws.com/123456/orders-dlq",
    targetQueueUrl:     "https://sqs.us-east-1.amazonaws.com/123456/orders.fifo",
    maxMessages: 10,
    cancellationToken: ct);
```

Replay sends the original message body and attributes to the target queue, then deletes it from the DLQ. If the delete fails after a successful send, a `LogWarning` is emitted noting the risk of double-processing so it can be detected in logs.

### DeadLetterRecord fields

| Field | Type | Description |
|-------|------|-------------|
| `Id` | `string` (Guid) | Unique record identifier |
| `MessageId` | `string` | Original SQS message ID |
| `Body` | `string` | Raw message body (may be encrypted) |
| `MessageType` | `string` | CommandType or EventType attribute value |
| `Reason` | `string` | Why it was dead-lettered |
| `ErrorDescription` | `string?` | Human-readable description |
| `OriginalSource` | `string` | Source queue URL |
| `DeadLetterSource` | `string` | DLQ URL |
| `CloudProvider` | `string` | `"aws"` |
| `DeadLetteredAt` | `DateTime` | UTC timestamp |
| `DeliveryCount` | `int` | ApproximateReceiveCount from SQS |
| `ExceptionType/Message/StackTrace` | `string?` | Last exception details |
| `Metadata` | `Dictionary<string, string>` | All SQS message attributes + system attributes |
| `Replayed` | `bool` | Set to true by MarkAsReplayedAsync |
| `ReplayedAt` | `DateTime?` | When replayed |

### IDeadLetterStore query

```csharp
var records = await store.QueryAsync(new DeadLetterQuery
{
    MessageType  = "CreateOrderCommand",
    CloudProvider = "aws",
    Replayed     = false,
    FromDate     = DateTime.UtcNow.AddDays(-7),
    Skip = 0,
    Take = 50
});

var count = await store.GetCountAsync(new DeadLetterQuery { Replayed = false });
await store.MarkAsReplayedAsync(messageId);
await store.DeleteOlderThanAsync(DateTime.UtcNow.AddDays(-30));
```

---

## 16. Observability

### Distributed Tracing (OpenTelemetry)

`CloudTelemetry` creates `Activity` objects using `ActivitySource("SourceFlow.Cloud", "1.0.0")`. Activities follow W3C trace context and use OpenTelemetry semantic conventions.

| Method | Activity name | Kind |
|--------|---------------|------|
| `StartCommandDispatch` | `{CommandType}.Dispatch` | Producer |
| `StartCommandProcess` | `{CommandType}.Process` | Consumer |
| `StartEventPublish` | `{EventType}.Publish` | Producer |
| `StartEventReceive` | `{EventType}.Receive` | Consumer |

**Tags set on each activity:**

```
messaging.system          = "aws"
messaging.destination     = queue URL or topic ARN
messaging.destination_kind = "queue" or "topic"
messaging.operation       = "send" / "receive" / "process" / "publish"
sourceflow.command.type   = command type name
sourceflow.entity.id      = entity ID (if present)
sourceflow.sequence_no    = sequence number (if present)
cloud.provider            = "aws"
cloud.queue / cloud.topic = destination
```

**Trace propagation:**

```csharp
// On dispatch — inject into message attributes
_cloudTelemetry.InjectTraceContext(activity, traceDict);
// → messageAttributes["traceparent"] = activity.Id

// On receive — extract from message attributes
var traceParent = _cloudTelemetry.ExtractTraceParent(messageAttributes);
// → used as parentTraceId in StartCommandProcess()
```

### Metrics (OpenTelemetry)

`CloudMetrics` uses `System.Diagnostics.Metrics.Meter("SourceFlow.Cloud", "1.0.0")`.

| Metric | Type | Description |
|--------|------|-------------|
| `sourceflow.commands.dispatched` | Counter | Commands sent to SQS |
| `sourceflow.commands.processed` | Counter | Commands processed (tagged with success) |
| `sourceflow.commands.processed.success` | Counter | Successful command executions |
| `sourceflow.commands.failed` | Counter | Failed command executions |
| `sourceflow.events.published` | Counter | Events published to SNS |
| `sourceflow.events.received` | Counter | Events received from SQS |
| `sourceflow.duplicates.detected` | Counter | Idempotency hits |
| `sourceflow.command.dispatch.duration` | Histogram (ms) | End-to-end dispatch time |
| `sourceflow.command.processing.duration` | Histogram (ms) | Handler execution time |
| `sourceflow.event.publish.duration` | Histogram (ms) | End-to-end publish time |
| `sourceflow.message.size` | Histogram (bytes) | Payload size |
| `sourceflow.queue.depth` | Observable Gauge | Current SQS queue depth |
| `sourceflow.dlq.depth` | Observable Gauge | Current DLQ depth |
| `sourceflow.processors.active` | Observable Gauge | Messages being processed |

**AWS-specific counters** (`Meter("SourceFlow.Cloud.AWS", "1.0.0")`):

| Metric | Description |
|--------|-------------|
| `aws.sqs.commands.dispatched` | Commands sent per command type + queue |
| `aws.sns.events.published` | Events published per event type + topic |

### Connecting to an OpenTelemetry collector

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("SourceFlow.Cloud")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter("SourceFlow.Cloud")
        .AddMeter("SourceFlow.Cloud.AWS")
        .AddOtlpExporter());
```

---

## 17. Health Checks

`AwsHealthCheck` (implements `IHealthCheck`) verifies AWS connectivity:

1. If command queues are configured → `GetQueueAttributesAsync(firstQueue, ["QueueArn"])`
2. If event queues are configured → `ListTopicsAsync()`
3. Returns `Healthy` if both succeed, `Unhealthy` with the exception message otherwise

The health check is registered via `TryAddEnumerable` (avoids duplicate registration).

```csharp
// Register health check endpoint (standard ASP.NET Core)
builder.Services.AddHealthChecks();
app.MapHealthChecks("/healthz");
```

---

## 18. IAM Permissions Reference

### Minimum permissions for command publishing only

```json
{
  "Effect": "Allow",
  "Action": [
    "sqs:SendMessage",
    "sqs:GetQueueUrl",
    "sqs:CreateQueue",
    "sqs:GetQueueAttributes"
  ],
  "Resource": "arn:aws:sqs:*:*:*"
}
```

### Minimum permissions for command and event consuming

```json
{
  "Effect": "Allow",
  "Action": [
    "sqs:ReceiveMessage",
    "sqs:DeleteMessage",
    "sqs:GetQueueUrl",
    "sqs:GetQueueAttributes",
    "sqs:CreateQueue",
    "sns:CreateTopic",
    "sns:Subscribe",
    "sns:ListTopics"
  ],
  "Resource": "*"
}
```

### Additional permissions for KMS encryption

```json
{
  "Effect": "Allow",
  "Action": [
    "kms:GenerateDataKey",
    "kms:Decrypt"
  ],
  "Resource": "arn:aws:kms:*:*:key/YOUR-KEY-ID"
}
```

---

## 19. Configuration Reference

### AwsOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Region` | `RegionEndpoint` | `USEast1` | AWS region for SQS and SNS clients |
| `EnableCommandRouting` | `bool` | `true` | Enables SQS command dispatch |
| `EnableEventRouting` | `bool` | `true` | Enables SNS event dispatch |
| `SqsReceiveWaitTimeSeconds` | `int` | `20` | Long-poll wait time (0–20 seconds) |
| `SqsVisibilityTimeoutSeconds` | `int` | `300` | How long a received message is hidden |
| `SqsMaxNumberOfMessages` | `int` | `10` | Messages per receive call (max 10) |
| `MaxRetries` | `int` | `3` | SDK-level retry count |
| `RetryDelay` | `TimeSpan` | `1 second` | Initial retry delay |
| `AccessKeyId` *(Obsolete)* | `string` | — | Use credential chain instead |
| `SecretAccessKey` *(Obsolete)* | `string` | — | Use credential chain instead |
| `SessionToken` *(Obsolete)* | `string` | — | Use credential chain instead |

### CircuitBreakerOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `FailureThreshold` | `int` | `5` | Consecutive failures to open circuit |
| `OpenDuration` | `TimeSpan` | `1 minute` | Duration circuit stays open |
| `SuccessThreshold` | `int` | `2` | Successes in HalfOpen to close |
| `OperationTimeout` | `TimeSpan` | `30 seconds` | Max operation duration |
| `HandledExceptions` | `Type[]` | `[]` (all count) | Only these types count as failures |
| `IgnoredExceptions` | `Type[]` | `[]` (none ignored) | These types never count |
| `EnableFallback` | `bool` | `false` | App-level fallback on Open |

### AwsKmsOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MasterKeyId` | `string` | `""` | KMS key ID or ARN |
| `CacheDataKeySeconds` | `int` | `300` | DEK cache TTL (0 = no caching) |

### AwsDeadLetterMonitorOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `true` | Whether monitoring is active |
| `DeadLetterQueues` | `List<string>` | `[]` | DLQ URLs to monitor |
| `CheckIntervalSeconds` | `int` | `60` | Polling frequency |
| `BatchSize` | `int` | `10` | Messages per receive (max 10) |
| `StoreRecords` | `bool` | `true` | Persist to IDeadLetterStore |
| `SendAlerts` | `bool` | `true` | Log WARN on threshold breach |
| `AlertThreshold` | `int` | `10` | Message count to trigger alert |
| `DeleteAfterProcessing` | `bool` | `false` | Remove from DLQ after storing |
