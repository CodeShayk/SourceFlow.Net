# Message Idempotency Guide

Brokers deliver at least once, clients retry HTTP requests, nodes crash between a database commit and a broker acknowledgement. SourceFlow.Net v3 turns that at-least-once world into **effectively-once processing**: every message has a stable logical id, every outbound message is written in the same transaction as the state change that caused it (the transactional outbox), and every inbound message is recorded in the same transaction as its effects (the inbox). A duplicate is detected inside the consumer's transaction, rolled back and acknowledged. This guide explains how that works as built, what you configure, what you still have to do yourself (external side effects), how to test it, and exactly what is and is not guaranteed.

The decision is [ADR-006](../ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md). In v2, idempotency was an opt-in service (`EfIdempotencyService`, `InMemoryIdempotencyService`) keyed on transport message ids; it is gone. In v3 there is nothing to register: the outbox and inbox are part of every storage adapter.

## Contents

- [The model in one picture](#the-model-in-one-picture)
- [Message ids](#message-ids)
- [Producer side: the transactional outbox](#producer-side-the-transactional-outbox)
- [Consumer side: the inbox](#consumer-side-the-inbox)
- [Idempotent commands from callers](#idempotent-commands-from-callers)
- [Local subscriptions: checkpoints instead of an inbox](#local-subscriptions-checkpoints-instead-of-an-inbox)
- [External side effects](#external-side-effects)
- [Retention and cleanup](#retention-and-cleanup)
- [Broker-native deduplication](#broker-native-deduplication)
- [Testing duplicates](#testing-duplicates)
- [What is and is not guaranteed](#what-is-and-is-not-guaranteed)
- [Monitoring](#monitoring)
- [Troubleshooting](#troubleshooting)
- [Related documents](#related-documents)

## The model in one picture

```mermaid
sequenceDiagram
    participant P as Producer service
    participant PDB as Producer database
    participant R as Outbox relay
    participant B as Broker
    participant C as Consumer service
    participant CDB as Consumer database

    P->>PDB: BEGIN; append events; INSERT outbox rows; COMMIT
    Note over P,PDB: state change and outbound messages commit together
    R->>PDB: claim head rows (fenced lease)
    R->>B: publish / send (message id kept)
    B-->>R: acknowledged
    R->>PDB: DELETE delivered rows
    Note over R,B: crash before DELETE: the row is sent again, same id
    B->>C: deliver (at least once)
    C->>CDB: BEGIN; inbox seen (consumer, id)?
    alt first delivery
        C->>CDB: apply effects; INSERT inbox row; COMMIT
    else duplicate
        C->>CDB: ROLLBACK (nothing applied)
    end
    C->>B: acknowledge
```

The producer can publish the same message more than once; the consumer applies it once. "Once" means once per **consumer**: two services that both subscribe to an event each process it once.

## Message ids

Every envelope carries a `MessageId`, its **logical** id. It is assigned when the message is created and travels unchanged through the outbox, the broker, redeliveries and dead-letter replays. Deduplication never uses a transport's own message id.

| Message | Id |
|---------|----|
| A new command, event or timer | UUIDv7 (`MessageIds.New`), time-ordered |
| A command sent with an idempotency key | UUIDv5 of `{service}\n{key}` (`MessageIds.FromKey(scope, key)`), deterministic |
| A command a process manager or workflow sends with `Reaction.Send(command, messageId)` | The given deterministic id, so a re-send is the same message |
| A message replayed from dead letters (`dlq replay`) | The original id; the inbox still protects |

Because the id is fixed at creation and the outbox row is written in the creating transaction, a producer retry (the relay sending again after a crash) carries the same id, and the consumer sees it as a duplicate.

## Producer side: the transactional outbox

Every outbound message is an outbox row written in the same storage session as the events that caused it:

- **Integration events** a command publishes (`Routing(r => r.Publish<T>()…)`).
- **Commands to other services** sent by process managers, workflows and command handlers.
- **Scheduled messages** (timers, delayed retries).
- **Rejection replies** (`CommandRejected<T>`) committed with the inbox record of the rejected command ([ADR-018](../ADRs/ADR-018-business-rejections-are-values-not-exceptions.md)).

The commit is atomic: events, outbox rows, inbox row and inline read models commit together, or none of them do. A broker outage therefore never fails a command, and a crash never leaves an event without its message or a message without its event.

The **outbox relay** (`OutboxRelay`, run by the `Relay` role) delivers the rows:

1. For each of the `OutboxPartitions` partitions (16 by default) it acquires the fenced lease `outbox:{partition}`. One node relays a partition at a time.
2. It claims due rows in sequence order. Only the **head row of each partition key** is eligible, so messages with the same key (by default the envelope's subject, the stream id) are delivered in order even across failures.
3. It sends or publishes each row, then deletes the delivered rows.
4. On a failure it reschedules the row with exponential backoff (2^attempt seconds, capped at 5 minutes) and the failed head blocks only its own key. After `OutboxMaxAttempts` attempts (10 by default), or at once for a permanent error, the row is parked as a dead letter with consumer `outbox`.

Because the delete follows the broker's acknowledgement, a crash between the two re-sends the row. **The outbox is at-least-once**; the inbox on the other side makes it effectively-once. Outbox rows do not need a retention setting: they are deleted on delivery.

## Consumer side: the inbox

Every message the receive pipeline handles is recorded in the inbox table, keyed by `(consumer, message_id)`, in the same transaction as its effects. Each consumer has its own entry:

| Received message | Inbox consumer | Stored result |
|------------------|----------------|---------------|
| A command this service owns | the service name (`SourceFlowOptions.ServiceName`) | status, stream version, error code |
| An event or timeout for a process manager | `{service}:pm:{process-manager}` | accepted |
| An event for an `IEventHandler<T>` | `{service}:handler:{handler}` | accepted |

Because each process manager and handler has its own entry and its own transaction, a redelivery re-runs only the consumers that failed the first time.

How a duplicate is absorbed:

1. **Seen before.** The consumer opens a session and calls `InboxSeenAsync(consumer, id)`. If the row exists, nothing is applied. For a command, the stored result is returned as `CommandStatus.Duplicate`, with the original stream version and error code. `sourceflow.inbox.duplicates` is incremented.
2. **Concurrent duplicate.** Two copies can pass the check at the same time on different workers or nodes. Both apply their effects and queue an inbox row; the second commit hits the inbox primary key, the storage adapter raises `DuplicateMessageException`, and that transaction rolls back, events and outbox rows included. The command processor then reads and returns the first copy's stored result.
3. **Acknowledge.** In both cases the message is completed on the broker, so it is not redelivered again.

The receive endpoint settles a message only after the handler's transaction commits. A crash before that leaves the message unacknowledged, the broker redelivers it, and the inbox decides.

## Idempotent commands from callers

Commands received from a broker are always deduplicated. Commands sent in-process with `ICommandSender.SendAsync` are deduplicated **only when the caller supplies an idempotency key**, because without one there is nothing that identifies a retry:

```csharp
CommandResult result = await commands.SendAsync(
    new Deposit(accountId, 50m),
    new SendOptions { IdempotencyKey = request.IdempotencyKey },
    cancellationToken);

switch (result.Status)
{
    case CommandStatus.Accepted:   // first time: events appended
    case CommandStatus.Duplicate:  // a repeat: the original outcome, nothing appended
        return Results.Ok(new { version = result.Version.Value });
    case CommandStatus.Rejected:
        return Results.UnprocessableEntity(result.Error);
    default:
        return Results.Conflict(result.Error);
}
```

- The key becomes the command's message id: UUIDv5 of the service name and the key. The same key in the same service always maps to the same id; the same key in another service does not collide.
- The first send records the outcome (accepted, rejected or no-op) in the inbox. A repeat returns `CommandStatus.Duplicate` with that outcome; a rejection is reported with its original error code.
- Keys are remembered for `InboxRetention` (14 days by default). A retry after that is processed as new.

Over HTTP, `MapCommand<TCommand>` in `SourceFlow.AspNetCore` reads the `Idempotency-Key` header into `SendOptions.IdempotencyKey`, and the [TravelBooking sample](../../../samples/TravelBooking)'s endpoints accept it. See [SourceFlow.AspNetCore](SourceFlow.AspNetCore-README.md).

An idempotency key is not the same as an expected version. `SendOptions.ExpectedVersion` (HTTP `If-Match`) protects against deciding on stale state; the key protects against deciding twice. Use both for "update if unchanged, exactly once" semantics.

## Local subscriptions: checkpoints instead of an inbox

Events produced by this service reach its own async projections, process managers and event handlers through the subscription engine, not the broker. These subscriptions (`projection:{generation}`, `pm:{name}`, `handler:{name}`) have no inbox; they are deduplicated by their **checkpoint**:

- Each subscription partition holds a fenced lease and reads the commit-ordered log after its checkpoint.
- The batch's database effects (read-model writes, process-manager state, outbox rows for the commands it sends) and the new checkpoint commit in **one transaction**. The checkpoint save is fenced by the lease token, so a paused former owner cannot commit after a new owner has taken over.
- A crash before the commit rolls everything back and the batch is read again. A crash after it moves on.

Database effects of local subscriptions are therefore exactly-once. See [Architecture/06 Projections and Read Models](Architecture/06-Projections-and-Read-Models.md).

## External side effects

The inbox and the checkpoint cover writes to the SourceFlow database. They cannot cover a call to an outside system (a payment gateway, an e-mail provider, a REST API), because that call is not part of the transaction. Any code that calls out can run more than once:

| Where the call is made | Why it can repeat |
|------------------------|-------------------|
| A command handler (`ICommandHandler<TCommand, TState>`) | The command is re-decided after a concurrency conflict (up to `MaxConcurrencyRetries`, 5); a crash after the call but before the commit leads to redelivery. |
| An event handler (`IEventHandler<T>`) | The handler runs before the checkpoint or inbox commit; a crash or a failed commit re-runs it. Event handlers are at-least-once by contract. |
| A process-manager reaction | Reactions should not call out at all; they return commands, which go through the outbox. |

Make the outside call idempotent with a key that is stable across those repeats:

| Code | Stable key |
|------|------------|
| Command handler | `DecisionContext.CommandId`: the command's message id, the same on every re-decide and every redelivery |
| Event handler | `EventContext.EventId`: the event's id, the same on every delivery |

The Payments service of the OrderFulfilment domain the tests use ([`tests/Fixtures/OrderFulfilment/Payments/Payments.cs`](../../../tests/Fixtures/OrderFulfilment/Payments/Payments.cs)) does exactly this. Every gateway call carries the command id as the gateway's idempotency key:

```csharp
/// <summary>A payment service provider. Every call carries an idempotency key, so a repeated call has no second effect.</summary>
public interface IPaymentGateway
{
    ValueTask<GatewayOutcome> CaptureAsync(Guid order, decimal amount, string idempotencyKey, CancellationToken cancellationToken);
    ValueTask RefundAsync(Guid payment, decimal amount, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>Charges through the gateway. The command's id is the idempotency key, so a redelivered command charges once.</summary>
public sealed class ChargeHandler(IPaymentGateway gateway) : ICommandHandler<Charge, Payment>
{
    public async ValueTask<Decision> HandleAsync(Charge command, Payment state, DecisionContext context, CancellationToken cancellationToken)
    {
        if (state.PaymentId is not null)
        {
            return Decision.NoOp;   // captured already
        }

        if (command.Amount <= 0)
        {
            return Decision.Reject(new Error(PaymentErrors.InvalidAmount, "The amount must be positive."));
        }

        GatewayOutcome outcome = await gateway.CaptureAsync(command.OrderId.Value, command.Amount, context.CommandId.ToString(), cancellationToken);
        return outcome switch
        {
            GatewayOutcome.Approved => Decision.Accept(new PaymentCaptured(context.CommandId.Value, command.OrderId, command.Amount)),
            GatewayOutcome.FraudSuspected => Decision.Reject(new Error(PaymentErrors.FraudSuspected, "The payment gateway suspects fraud.")),
            _ => Decision.Reject(new Error(PaymentErrors.Declined, "The card was declined.")),
        };
    }
}
```

Three layers work together here:

1. **The inbox** drops a redelivered `Charge` that was already committed: the handler does not run again.
2. **The state check** (`state.PaymentId is not null` returns `Decision.NoOp`) covers a *new* command for an order that is already paid.
3. **The gateway key** covers the window the inbox cannot: the handler called the gateway, then the node crashed or the append conflicted before the commit. The repeat carries the same `CommandId`, so the gateway returns the original outcome instead of charging again. The `PaymentCaptured` event also uses the command id as its payment id, so the repeat produces the same event.

For an outside system without idempotency keys, record the call yourself before and after (for example "requested" and "confirmed" events) and reconcile, or accept and document the at-least-once behaviour.

## Retention and cleanup

| Data | Default retention | Setting | Cleanup |
|------|-------------------|---------|---------|
| Processed inbox rows | 14 days | `SourceFlowOptions.InboxRetention` (1 minute to 3,650 days) | Deleted by the relay role every `RetentionInterval` (1 hour), in batches of `RetentionBatchSize` (5,000) rows per statement |
| Outbox rows | until delivered | none | Deleted on delivery; parked after `OutboxMaxAttempts` |
| Replayed or discarded dead letters | 30 days | `DeadLetterRetention` | Same retention loop |
| Parked dead letters | forever | none | Only by an operator (`dlq replay`, `dlq discard`, then purge) |

Retention runs only on nodes with the `Relay` role and only when the adapter implements `IStoreRetention` (in-memory, PostgreSQL and SQL Server all do). Deletes are bounded batches so cleanup never holds long locks.

**Choose `InboxRetention` longer than any window in which a duplicate can still arrive.** A redelivery older than the retention is processed as new. Consider:

- the broker's redelivery and dead-letter windows (a message parked in a native dead-letter queue and replayed days later);
- the outbox: a row that fails for days during an outage is still sent with its original id when the broker returns;
- how long HTTP clients may retry an idempotency key;
- operator replays from SourceFlow dead letters (`dlq replay` keeps the id).

[ADR-006](../ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md) requires the inbox retention to exceed the broker's redelivery window. The default of `InboxRetention` is 14 days, and startup compares it with the transport's `MaxRetention`. A shorter retention fails startup when provisioning is `Validate` (the production default) and logs a warning (event 1405) otherwise. Raise it when your windows are longer:

```csharp
sf.Configure(o =>
{
    o.InboxRetention = TimeSpan.FromDays(30);
    o.RetentionInterval = TimeSpan.FromHours(1);
    o.RetentionBatchSize = 5_000;
});
```

The inbox table is narrow (consumer, id, timestamp and the stored result), so a long retention is cheap. Its `processed_at` index keeps cleanup efficient.

## Broker-native deduplication

Amazon SQS FIFO deduplication, Azure Service Bus duplicate detection and Google Pub/Sub exactly-once delivery are complementary, not a replacement. Their windows are short (5 minutes on SQS FIFO), they differ per cloud, and they say nothing about the database side of a consumer. SourceFlow does not depend on them; the inbox works the same on every transport, including the in-memory one. See the transport READMEs ([AWS](SourceFlow.Cloud.Aws-README.md), [Azure](SourceFlow.Cloud.Azure-README.md), [Google Cloud](SourceFlow.Cloud.Gcp-README.md)) and [Architecture/07 Cloud Transports](Architecture/07-Cloud-Transports.md) for what each broker offers.

## Testing duplicates

### Duplicate delivery end to end

The end-to-end suite wraps the real transport in a `DuplicatingTransport` that sends and publishes every message twice ([`tests/EndToEnd/SourceFlow.EndToEnd.Tests/BrokerConnection.cs`](../../../tests/EndToEnd/SourceFlow.EndToEnd.Tests/BrokerConnection.cs)):

```csharp
/// <summary>Sends and publishes every message twice: duplicate delivery the receiving services must absorb.</summary>
internal sealed class DuplicatingTransport(ITransport inner) : ITransport, IAsyncDisposable
{
    public string SystemName => inner.SystemName;
    public TransportCapabilities Capabilities => inner.Capabilities;

    public async ValueTask SendAsync(string endpoint, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        await inner.SendAsync(endpoint, messages, cancellationToken);
        await inner.SendAsync(endpoint, messages, cancellationToken);
    }

    public async ValueTask PublishAsync(string topic, IReadOnlyList<MessageEnvelope> messages, CancellationToken cancellationToken)
    {
        await inner.PublishAsync(topic, messages, cancellationToken);
        await inner.PublishAsync(topic, messages, cancellationToken);
    }

    public ValueTask<ITransportReceiver> CreateReceiverAsync(ReceiverOptions options, CancellationToken cancellationToken) => inner.CreateReceiverAsync(options, cancellationToken);
    public ValueTask<ITransportReceiver> CreateDeadLetterReceiverAsync(string endpoint, CancellationToken cancellationToken) => inner.CreateDeadLetterReceiverAsync(endpoint, cancellationToken);
    public ValueTask ProvisionAsync(TransportTopology topology, ProvisioningMode mode, CancellationToken cancellationToken) => inner.ProvisionAsync(topology, mode, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        switch (inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
```

Plug it in with `sf.UseTransport(_ => new DuplicatingTransport(innerTransport))` and assert on outcomes, not on deliveries. The scenarios that use it:

- **Cross-service** (`CrossServiceScenarios`): Accounts sends every message twice; the owner, Notifications, receives each statement request twice (4 deliveries, 2 duplicates) and decides each once, and a subscriber that only listens absorbs the duplicated publication too.
- **OrderFulfilment, WF-E2E-11** (`OrderFulfilmentScenarios`): every service sends and publishes everything twice on AWS, Azure and Google Cloud emulators. The gateway is charged once per order (`Attempts(order) == 1`), each awaited event moves the workflow instance once, and the payments service reports the copy of `payments.charge.v1` as a duplicate.
- **Chaos, CH-01..10** (`ChaosScenarios`): nodes crash at each fault point (before commit, after commit before acknowledgement, after publish before the outbox delete, and so on), and the assertions check that nothing is lost and every duplicate is absorbed (one welcome per account, balances applied once).

Two habits make such tests meaningful: wait for a quiet period before asserting "once", so late duplicates have had their chance, and count side effects at the outside boundary (a fake gateway's attempt count), not handler invocations.

### Unit and adapter level

- **Keyed sends.** In a `SourceFlowTestHost`, send the same command twice with the same `SendOptions.IdempotencyKey` and assert `Accepted`, then `Duplicate`, with one event appended.
- **Storage conformance kit.** SCK-34 (the inbox records the result, per consumer), SCK-35 (a duplicate inbox row fails the commit with `DuplicateMessageException`), SCK-29 (outbox rows commit atomically with the append), SCK-30 and SCK-31 (head-of-key ordering and failure blocking) and SCK-37 (batched retention) run against every adapter. See [SourceFlow.Testing](SourceFlow.Testing-README.md) and [Architecture/05](Architecture/05-Event-Store-and-Persistence.md#the-storage-conformance-kit).
- **External effects.** Give your fake outside system an attempt counter keyed by the idempotency key, as the end-to-end `Gateway` does, and assert it is called once per key.

## What is and is not guaranteed

| Guarantee | Holds | Conditions |
|-----------|-------|------------|
| A committed state change always gets its outbound messages | Yes | The outbox row is in the same transaction |
| No outbound message without its state change | Yes | Same reason; a rolled-back command leaves no row |
| Outbound delivery | At least once | The relay re-sends after a crash between publish and delete |
| Per-key delivery order | Yes, per outbox partition key | Messages with the same partition key (default: the stream id) |
| A received command's effects applied once per owner | Yes | Within `InboxRetention` |
| A received event's process-manager and handler database effects applied once per consumer | Yes | Within `InboxRetention`; each consumer separately |
| Local projections, process managers and handlers: database effects once | Yes | Effects and fenced checkpoint commit together in the event store's database |
| A repeated in-process `SendAsync` deduplicated | Only with `SendOptions.IdempotencyKey` | Without a key every send is a new command |
| Calls to outside systems once | **No** | At least once; make them idempotent with `CommandId` or `EventId` |
| Read models in another database once | **No** | At least once; guard writes by version |
| Duplicates older than `InboxRetention` detected | **No** | Processed as new |
| The same message to two different consumers processed once overall | **No, by design** | Each consumer processes it once |

## Monitoring

| Signal | Meaning |
|--------|---------|
| `sourceflow.inbox.duplicates` (counter, tag: consumer group) | Duplicates absorbed by the inbox. A steady trickle is normal on at-least-once brokers; a spike points at relay restarts, broker redelivery storms or clients retrying keys. |
| `sourceflow.outbox.pending`, `sourceflow.outbox.age` | Undelivered outbox rows and how long the oldest due one has waited (kept across retries). |
| `sourceflow.deadletter.parked`, `sourceflow.deadletter.count` | Messages parked by the relay, receivers or subscriptions. |
| Health check `sourceflow.outbox` | Degraded after `Health.OutboxAgeDegraded` (10 s), unhealthy after `Health.OutboxAgeUnhealthy` (5 min). |

See [Observability and Performance](Observability-and-Performance.md).

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| An outside system was called twice | The call is made in a handler that can repeat, without an idempotency key. | Pass `context.CommandId` or `ctx.EventId` to the outside system. |
| A client retry created a second command | The client did not send the same idempotency key, or the key expired. | Send `Idempotency-Key`; raise `InboxRetention` beyond the client's retry window. |
| A redelivered message ran again days later | Older than `InboxRetention`. | Raise `InboxRetention` above the broker's redelivery and dead-letter windows. |
| `DuplicateMessageException` in logs or custom code | Two copies were processed concurrently; the second rolled back. | Expected. Custom code calling `RecordInbox` should treat it as "already processed". |
| `CommandStatus.Duplicate` with an error | The first attempt with this key was rejected; the repeat reports the original rejection code. | Use a new key for a genuinely new attempt. |
| Messages stuck in the outbox | The broker is down, the destination is missing, or no node runs the `Relay` role. | Check `sourceflow.outbox.age`, the relay logs and `dlq list --consumer outbox`. |
| Inbox table grows without bound | No node runs the `Relay` role, so retention never runs. | Run at least one node with `SourceFlowRoles.Relay`. |

## Related documents

- [Architecture/04 Messaging Pipeline and Middleware](Architecture/04-Messaging-Pipeline-and-Middleware.md) (receive pipeline, retries, dead letters)
- [Architecture/05 Event Store and Persistence](Architecture/05-Event-Store-and-Persistence.md) (sessions, outbox and inbox tables, leases)
- [Architecture/06 Projections and Read Models](Architecture/06-Projections-and-Read-Models.md) (checkpoints, fencing)
- [SourceFlow.Storage.PostgreSQL](SourceFlow.Storage.PostgreSQL-README.md), [SourceFlow.Storage.SqlServer](SourceFlow.Storage.SqlServer-README.md)
- [SourceFlow.AspNetCore](SourceFlow.AspNetCore-README.md) (`Idempotency-Key`, `If-Match`)
- Design: [doc 04 Messaging Pipeline](../Design/04-Messaging-Pipeline.md), [doc 05 §7 and §9](../Design/05-Event-Store-and-Persistence.md#7-supporting-tables-postgresql-sql-server-equivalents-are-analogous)
- ADRs: [ADR-006](../ADRs/ADR-006-transactional-outbox-and-inbox-for-effectively-once.md), [ADR-008](../ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md), [ADR-018](../ADRs/ADR-018-business-rejections-are-values-not-exceptions.md)
