# 04 — Messaging Pipeline and Delivery Semantics

Design drivers: [ADR-005](../ADRs/ADR-005-logical-message-type-names-and-registry.md), [ADR-006](../ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md), [ADR-007](../ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md), [ADR-011](../ADRs/ADR-011-cloudevents-envelope-and-w3c-trace-context.md), [ADR-012](../ADRs/ADR-012-one-middleware-abstraction-for-all-stages.md), [ADR-013](../ADRs/ADR-013-use-platform-libraries-delete-bespoke-equivalents.md) and [ADR-018](../ADRs/ADR-018-business-rejections-are-values-not-exceptions.md). Fixes: GAP-01, GAP-09, GAP-10, GAP-11, GAP-19 and NFR-REL-04/06.

## 1. The envelope

Every message, in process, in the outbox, on the wire, or in a dead-letter record, travels in one `MessageEnvelope`.

```csharp
public sealed class MessageEnvelope
{
    public required MessageId MessageId { get; init; }          // UUIDv7, logical id; the idempotency key
    public required MessageTypeName Type { get; init; }         // "bank.account.deposited"
    public required int TypeVersion { get; init; }              // 1
    public required string Source { get; init; }                // "accounts" (service name)
    public required DateTimeOffset Time { get; init; }
    public CorrelationId CorrelationId { get; init; }           // conversation / process id
    public MessageId? CausationId { get; init; }                // the message that caused this one
    public string? Subject { get; init; }                       // stream id for events; target id for commands
    public string? PartitionKey { get; init; }                  // ordering key (defaults to Subject)
    public long? StreamVersion { get; init; }                   // events only
    public string? TenantId { get; init; }                      // ADR-022; drives RLS and query scoping
    public string? Actor { get; init; }                         // who caused it (IActorAccessor), for audit (doc 10 §1)
    public string? TraceParent { get; init; }
    public string? TraceState { get; init; }
    public string ContentType { get; init; } = "application/json";
    public string? Protection { get; init; }                    // e.g. "kms:aws:arn:…:key/…" when encrypted
    public IReadOnlyDictionary<string, string> Headers { get; init; } = Empty;  // user extensions (bounded, §2.3)
    public ReadOnlyMemory<byte> Body { get; init; }             // serialized (and possibly encrypted) payload
}
```

### 1.1 CloudEvents mapping ([ADR-011](../ADRs/ADR-011-cloudevents-envelope-and-w3c-trace-context.md))

| Envelope | CloudEvents attribute | Notes |
|----------|-----------------------|-------|
| `MessageId` | `id` | |
| `Source` | `source` | `urn:sourceflow:{service}` |
| `Type` + `TypeVersion` | `type` | `bank.account.deposited.v1` |
| — | `specversion` | `1.0` |
| `Subject` | `subject` | |
| `Time` | `time` | RFC 3339 |
| `ContentType` | `datacontenttype` | |
| `CorrelationId` | `sfcorrelationid` | extension |
| `CausationId` | `sfcausationid` | extension |
| `PartitionKey` | `sfpartitionkey` | extension |
| `StreamVersion` | `sfstreamversion` | extension |
| `TenantId` | `sftenant` | extension |
| `Actor` | `sfactor` | extension (travels in `sf_ext` on SQS, where attributes are limited) |
| `Protection` | `sfenc` | extension |
| `TraceParent` / `TraceState` | `traceparent` / `tracestate` | distributed-tracing extension |

Transports use **binary mode**: attributes go in native headers (`ce_` prefix where required) and the body is the data. When a broker's header budget is too small, the transport switches to **structured mode** (a CloudEvents JSON document) and sets `content-type: application/cloudevents+json`. Both modes round-trip losslessly, which the conformance kit checks.

## 2. Type registry and serialisation

### 2.1 Registry

- Built at startup from generated module registrations only ([ADR-004](../ADRs/ADR-004-source-generation-instead-of-reflection.md)). It is frozen into a `FrozenDictionary<(string name, int version), MessageTypeInfo>` and a `FrozenDictionary<Type, MessageTypeInfo>`.
- `MessageTypeInfo` holds the CLR type, the `JsonTypeInfo` (source-generated), the kind (command, event or integration event), the upcaster chain, any `[PersonalData]` members, and the size limit.
- **Allow-list semantics**: a received or stored envelope whose `(type, version)` isn't registered is never deserialised. It is parked with reason `UnknownMessageType` (GAP-10).
- Startup validation catches duplicate names across modules, events without upcasters for versions older than the latest when those versions are still in the store (opt-in check), and commands with no owner or route.

### 2.2 Serialiser

```csharp
public interface IMessageSerializer
{
    string ContentType { get; }
    void Serialize(object message, MessageTypeInfo type, IBufferWriter<byte> destination);
    object Deserialize(ReadOnlySequence<byte> source, MessageTypeInfo type);
}
```

- The default is `SystemTextJsonMessageSerializer`. It uses the module-declared, source-generated `JsonSerializerContext` (no reflection; completeness checked by SF0010, see DC-1 in ADR-004), camelCase names, strict number handling, and rejects duplicate properties. It writes into a pooled `IBufferWriter<byte>` and reads from `ReadOnlySequence<byte>`, so large bodies don't need to be contiguous.
- The serialiser is pluggable per content type (for example Protobuf), but only JSON ships in 3.0 (YAGNI).

### 2.3 Size and header limits

| Limit | Default | Enforcement |
|-------|---------|-------------|
| Body size | Transport capability (SNS 256 KB, SQS 1 MiB, ASB 256 KB Standard / 100 MB Premium, Pub/Sub 10 MB) minus envelope overhead | Checked **before** the outbox insert, so the command fails with `MessageTooLarge` instead of being parked later. NFR-PERF-07 |
| User headers | 8 entries, 256 bytes each | Validated in the Send stage |

The transport capability values come from each transport's `TransportCapabilities`, so they aren't hard-coded in the core.

## 3. Routing

```csharp
sf.Routing(r =>
{
    r.Endpoint("accounts");                                         // default: ServiceName. Receives commands + subscribed events
    r.Command<SendStatement>().To("notifications")                  // remote owner's endpoint
                              .ReplyOnReject();                     // owner replies CommandRejected<T> to the correlation
    r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events")
                              .OrderedBy(e => e.AccountId);         // partition key (default: stream id)
    r.Subscribe("payments.events", s => s.Only<PaymentSettledV1>()); // filter on the subscription where supported
    r.Subscribe("orders.events", s => s.Filter("type LIKE 'orders.order-%' AND sftenant = 'eu'")); // CESQL (doc 14 §5.1)
});
```

| Rule | Detail |
|------|--------|
| RT-R1 | A command type is **owned** (it has a local `Decide` or handler) or **routed** (`.To(endpoint)`). Never both, never neither ([ADR-007](../ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md), SF0014). |
| RT-R2 | An owner receives remote commands on its endpoint, and they go through the same command processor as local ones. The owner sequences them, logs them and can replay its streams (GAP-19). |
| RT-R3 | Domain events are published **only** through `Publish<T>()` mappings, and therefore only through the outbox. |
| RT-R4 | Each service has **its own subscription** per subscribed topic. Fan-out is to every subscribing service; instances of one service compete (doc 07). Fixes GAP-02 and GAP-13. |
| RT-R5 | Endpoint and topic names are logical. The transport maps them to physical names with a configurable naming convention (prefix, environment, FIFO suffix). |
| RT-R6 | Routing and topology are validated at startup. Under `ProvisioningMode.Validate`, the physical resources are validated too ([ADR-017](../ADRs/ADR-017-provisioning-modes-and-topology-as-data.md)). |

## 4. Middleware pipeline ([ADR-012](../ADRs/ADR-012-one-middleware-abstraction-for-all-stages.md))

```csharp
public interface IMessageMiddleware
{
    ValueTask InvokeAsync(MessageContext context, MessageDelegate next);
}

public delegate ValueTask MessageDelegate(MessageContext context);

public sealed class MessageContext
{
    public PipelineStage Stage { get; }                 // Send | Handle | Receive | Publish | Project
    public MessageEnvelope Envelope { get; }
    public object? Message { get; set; }                // deserialized payload (null until the Deserialize step)
    public MessageTypeInfo TypeInfo { get; }
    public IServiceProvider Services { get; }           // scope for this message
    public IStorageSession? Session { get; }            // set inside transactional stages
    public Activity? Activity { get; }
    public CancellationToken CancellationToken { get; }
    public HandlingOutcome Outcome { get; set; }        // set by the terminal step
    public IDictionary<string, object?> Items { get; }  // per-message bag for middleware
}
```

Registration: `sf.Pipeline(p => p.Use<TenantMiddleware>(PipelineStage.Handle | PipelineStage.Receive).For<ICommand>())`.

Composition: for each `(stage, message type)` the runtime builds the delegate chain once, the first time it is needed, and caches it. Middleware instances are resolved from the per-message scope, unless registered as singletons.

### 4.1 Built-in middleware order (outermost first)

| # | Middleware | Stages | Purpose |
|---|------------|--------|---------|
| 1 | `TelemetryMiddleware` | all | Starts the span, records duration and outcome metrics, links to the producer span (doc 08) |
| 2 | `LoggingScopeMiddleware` | all | Log scope with message id, type, correlation and tenant (redacted) |
| 3 | `ErrorClassificationMiddleware` | Receive, Project | Maps exceptions to `Transient`, `Permanent` or `Concurrency` (§9) |
| 4 | `PayloadProtectionMiddleware` | Send/Publish (encrypt), Receive (decrypt) | KMS envelope encryption (doc 10) |
| 5 | `DeserializeMiddleware` | Receive | Registry lookup (allow-list), then upcast and deserialise |
| — | *user middleware* | as registered | |
| 6 | `ValidationMiddleware` *(optional)* | Send, Receive | Runs `IValidator<T>` (a FluentValidation or DataAnnotations adapter) and turns failures into a `Rejected` result |
| 7 | `TransactionMiddleware` | Handle, Receive, Project | Opens the `IStorageSession`. Commits after the terminal step. |
| 8 | `InboxMiddleware` | Receive, Handle when keyed | Duplicate check and record in the same session |
| 9 | Terminal | — | Command processor, event handler, process manager, or projection apply |

## 5. Receive pipeline

```mermaid
flowchart LR
    T[Transport receiver<br/>prefetch N] --> C{Partitioned dispatcher<br/>hash(partitionKey) % W}
    C --> W1[Worker 1<br/>bounded channel]
    C --> W2[Worker 2]
    C --> Wn[Worker W]
    W1 --> P[Middleware pipeline<br/>Receive stage]
    P --> S{Outcome}
    S -- Completed/Rejected/Duplicate --> ACK[settle: complete/ack/delete]
    S -- Transient --> RETRY[settle: delay / abandon]
    S -- Permanent or retries exhausted --> PARK[park to dead letters, then ack]
```

| Rule | Detail |
|------|--------|
| RX-R1 | **Ordering.** Messages with the same partition key are handled one at a time, in arrival order, by the same worker. Different keys run in parallel (`MaxConcurrency`, default `Environment.ProcessorCount * 2`). |
| RX-R2 | **Backpressure.** Each worker's channel is bounded (`PrefetchPerWorker`, default 16). The receiver stops pulling when channels are full. |
| RX-R3 | **Settle after commit.** The broker is acknowledged only after the handler transaction commits. A crash before the ack causes redelivery, which the inbox deduplicates. |
| RX-R4 | **Lock renewal.** Transports with message locks or visibility timeouts extend them while a message is in flight (SQS `ChangeMessageVisibility`, ASB lock renewal, Pub/Sub ack-deadline extension). |
| RX-R5 | **One input endpoint, one receiver.** Commands and events arriving on the same endpoint are told apart by envelope `type`, never by competing listeners. Fixes GAP-02. |
| RX-R6 | **Graceful shutdown.** Stop pulling, finish in-flight messages within `ShutdownTimeout`, and abandon the rest so they are redelivered quickly. |

## 6. Inbox

- The key is `(consumer, message_id)`. `consumer` is `{service}` for command handling, `{service}:{projection|pm|handler name}` for subscribers, and `{service}:local` for keyed local sends.
- It is written in the **same transaction** as the effects. A duplicate shows up as a unique-key violation at insert, or as an early `Seen` check that avoids wasted work. Either way the transaction is rolled back and the message is acknowledged as `Duplicate`.
- **Stored result**: for command consumers the inbox row keeps `(status, version, error_code)`, so a duplicate `SendAsync` with the same idempotency key returns the original `CommandResult`. This is the HTTP `Idempotency-Key` semantics.
- **Retention**: `InboxRetention` (default 14 days) MUST be at least the longest broker redelivery or retention window. This is validated at startup against `TransportCapabilities.MaxRetention`. Cleanup is a batched background delete.

## 7. Outbox

- Rows are written by the command processor (integration events, routed commands sent from handlers), by process managers (commands, timers), and by handlers (`ctx.Session.Send/Publish/Schedule`).
- **Partitioning**: `partition = hash(partition_key) mod OutboxPartitions` (default 16). Order is guaranteed **per partition key**.
- **Relay algorithm** (per instance):

```text
loop:
  for each outbox partition p where this instance holds lease(p) (fenced, doc 06 §4):
     rows = SELECT … WHERE partition = p AND deliver_at <= now ORDER BY seq LIMIT BatchSize
     group rows by destination; publish each group with the transport's batch API (in seq order per key)
     on success: DELETE rows (same fencing token)
     on transient failure: increment attempts, set next deliver_at with backoff; stop this partition's batch
       (a later row with the same key must not overtake a failed one)
     on permanent failure (e.g. destination missing): move to dead letters with reason OutboxPublishFailed
  wait for wake-up (NOTIFY / in-process signal after commit) or PollInterval (default 1 s, adaptive)
```

- **Leases** spread partitions across instances, so relay throughput grows with instance count (up to the number of partitions).
- **Why an outbox table rather than log tailing.** Tailing the event log avoids a second write, but it can only publish *events*. Remote commands, replies and timers would still need a table. Tailing also forces the relay to read every event in order to find the few that are published. One mechanism for every outbound message is simpler (KISS) and lets publication order follow the partition key rather than global commit order. The cost is about one small insert per published message. Measured cost and the alternative are revisited in [doc 09](09-Performance-and-Scalability.md).

## 8. Scheduler (durable timers and delayed delivery)

- `Schedule(message, at)` writes an outbox row with `deliver_at = at`. Delivery uses the same relay. Timers addressed to the local service are delivered to its own endpoint (or handled locally when the transport is in-memory).
- Precision is ±`PollInterval`. Timers survive restarts and scale out with the outbox partitions.
- Cancelling a timer: process managers ignore a timeout whose stage no longer matches (idempotent by design). `Reaction.CancelSchedule(id)` deletes the row if it hasn't been sent yet.

## 9. Failure handling

### 9.1 Classification

| Class | Examples | Action |
|-------|----------|--------|
| **Rejected** (handled outcome) | `Decision.Reject`, validation failure | Commit inbox and result, ack, optional `CommandRejected<T>` reply. No retry. |
| **Concurrency** | `ConcurrencyException` after the processor's own retries | Immediate retries, then delayed retries |
| **Transient** | Timeouts, `DbException.IsTransient`, broker throttling, HTTP 5xx/429 | Immediate retries (Polly, default 3, jittered 50–500 ms), then **delayed retries** through the transport (default schedule 10 s, 1 min, 10 min, 1 h) |
| **Permanent** | Unknown type, deserialisation failure, decryption failure, `PermanentException`, `ArgumentException` from user code (configurable) | Park immediately |
| **Exhausted** | A transient failure after every delayed retry | Park |

The exception→class mapping is configurable through `sf.Errors(e => e.Treat<MyGatewayDownException>().AsTransient())`.

### 9.2 Delayed retries

The runtime prefers the broker's native delay: SQS `ChangeMessageVisibility` (up to 12 h), ASB scheduled re-send, Pub/Sub nack with the subscription's retry policy. When the transport lacks a capability, the fallback is an outbox row with `deliver_at` addressed to the same endpoint, carrying an incremented `sfattempt` header. The delay schedule is identical on every transport ([ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md)).

### 9.3 Dead letters (parking)

- **One place to look.** Parked messages go to `sf_dead_letters` in the service's own database (doc 05 §7.5), with the full envelope, the exception chain, attempt history and the consumer.
- **The native broker DLQ is a backstop.** Native DLQs are configured with `maxReceiveCount` greater than the runtime's own budget. They only catch messages that crash the process before the runtime can park them. A `DeadLetterIngestor` background service drains native DLQs into `sf_dead_letters`, so operators still have one view (GAP-11).
- **Operations** (CLI `dotnet sourceflow dlq …` and the `IDeadLetterManager` API): list and filter, show, **replay** (re-sends the original envelope to its original endpoint with a new delivery attempt; the message id is kept, so the inbox still protects against duplicates), discard (with a reason, audited), and purge older than a cutoff.
- **Metrics**: `sourceflow.deadletter.parked` (counter, by consumer, type and reason) and `sourceflow.deadletter.count` (gauge). Alert on any increase.

## 10. Delivery semantics summary

| Path | Guarantee | Mechanism |
|------|-----------|-----------|
| Local `SendAsync` → aggregate | Atomic. Effectively-once when keyed. | One transaction. Inbox on the idempotency key. |
| Command → remote owner | At-least-once delivery, effectively-once effect, per-key order | Outbox → broker → inbox at the owner |
| Integration event → subscribers | At-least-once delivery, effectively-once effect per subscribing service, per-key order | Outbox → topic → per-service subscription → inbox |
| Event → inline projection | Atomic with the append | Same transaction |
| Event → async projection or process manager (same database) | Exactly-once effect, per-stream order | Checkpoint in the same transaction, fenced leases |
| Event → async projection (other database) | At-least-once; idempotent by stream version | `version` guard in the read model (doc 06 §5) |
| Event → `IEventHandler` with external side effect | At-least-once | The user makes it idempotent with `EventId` |
| Timer | At-least-once at or after `deliver_at` | Outbox and scheduler |
