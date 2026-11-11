# ADR-006: Transactional outbox and inbox for effectively-once processing

| | |
|---|---|
| **Status** | Accepted (product sign-off 2026-11-10) |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 publishes to the broker outside any transaction. Idempotency is an opt-in check on transport message ids, and in practice isn't wired (GAP-01, GAP-09).

## Decision

- **Outbox.** Every outbound message (integration events, remote commands, scheduled messages) is written to `sf_outbox` in the *same* transaction as the events that caused it. A relay publishes it and deletes it after the broker acknowledges.
- **Inbox.** Every handled inbound message records `(consumer, messageId)` in `sf_inbox` in the same transaction as its effects. A duplicate is detected inside that transaction (unique key), rolled back, and acknowledged.
- The `messageId` is the **logical** id (UUIDv7 assigned at creation, carried in the envelope), so producer retries are deduplicated too.
- The inbox is always on for transport-received messages, and on for local `SendAsync` when an idempotency key is supplied.

## Alternatives

- (a) Broker-native exactly-once (SQS FIFO dedup, ASB duplicate detection, Pub/Sub exactly-once). Its windows are limited (5 min on SQS FIFO), it is inconsistent across clouds, and it doesn't cover the database side. It stays as a complementary layer.
- (b) Log tailing (reading `$all` to publish) instead of an outbox table. That avoids the double write, but can't carry remote commands or timers, and it couples publication to event types. Rejected for uniformity; [doc 04](../04-Messaging-Pipeline.md#7-outbox) explains why.

## Consequences

- (+) No lost or phantom messages. Effectively-once processing. A broker outage doesn't fail commands.
- (−) One extra insert per outbound message. A relay process to run. Inbox retention must exceed the broker redelivery window (default 14 days).

## Traces

Q7, GAP-01, GAP-09, IDM-04, IDM-05, NFR-REL-03/04.
