# ADR-033: The final transport SPI: pull receivers, CESQL filters and the in-memory reference broker

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-11 |
| **Deciders** | v3 architecture review; WP-3.1 implementation |
| **Amends** | [doc 07](../Design/07-Cloud-Transports.md) §2, §7; [ADR-014](ADR-014-transport-spi-and-conformance-kit-parity-required.md) |

## Context

Doc 07 §2 sketched a callback-based SPI with a separate sender and provisioner. The core built in M1 uses a pull-based `ITransport`. Here the receive loop, per-key workers, bounded channels and settlement live in the core (`ReceiveEndpoint`, RX-R1..R6).

WP-3.1 has to freeze the SPI before three provider workstreams start in parallel. It also has to turn the 12-case kit into the planned TCK-01..45.

## Decision

1. **Pull, not callbacks.** `ITransportReceiver.ReceiveAsync(max, wait)` stays.
   - Every target broker has a native pull: SQS `ReceiveMessage`, Pub/Sub pull, Service Bus `ReceiveMessagesAsync`.
   - Keeping the loop in the core keeps backpressure, per-key ordering and shutdown identical on every broker.
   - The SPI stays at five types: `ITransport`, `ITransportReceiver`, `ReceivedMessage`, `TransportCapabilities` and `TransportTopology`.
2. **Settlement is complete.** `ReceivedMessage` has `CompleteAsync`, `AbandonAsync(delay)` (native delay), `RenewAsync` (lock, visibility or lease extension) and `DeadLetterAsync(reason)` (the native backstop).
   - A late settlement after a lost lock is ignored.
   - The receive endpoint renews at half `ReceiveLockDuration` while a handler runs.
   - Disposing a receiver releases what it holds unsettled.
3. **Capabilities drive the core.** `TransportCapabilities` keeps the size, attribute, retention and delay limits. It gains:
   - `OrderedDelivery`, `NativeDeadLetter`, `SubscriptionFilters`;
   - `MaxBatchSize`, `Deduplication` (with its window), `MaxLockDuration`.

   The core uses them to decide:
   - client-side filtering when a broker has no native filters (UT-RX-22);
   - whether to run the dead-letter ingestor;
   - how the TCK judges deduplication.
4. **Errors are typed.**
   - `TransportException.IsTransient` (and `RetryAfter` for throttling) drives the error classifier.
   - `TransportSendException` reports a partial batch per message: oversized messages are refused before anything is sent for them, and the rest are sent.
   - `DestinationNotFoundException` names a missing endpoint or topic.
   - `TopologyValidationException.Problems` is the full diff under `Validate`, typed by `TopologyProblemKind`.
5. **The topology carries what brokers need to configure.** `TransportTopology` gains:
   - `OrderedEndpoint` and `OrderedTopics`;
   - `MaxDeliveries`, the dead-letter threshold: the retry budget (1 + delayed retries) plus two;
   - per-topic `Filters`.

   The full topology model and naming conventions remain WP-3.5. `SourceFlowRuntime.Topology()` builds this topology for the host and the CLI.
6. **Subscription filters are CloudEvents SQL.**
   - `CloudEventFilter` parses and evaluates CESQL 1.0 over the CloudEvents attributes: operators, `LIKE`, `IN`, `EXISTS` and the common functions. Errors never match.
   - Routing compiles `Only<T>()` to `type IN (…)` and ANDs it with `Where(…)`.
   - Transports translate `ExactTypes` and `TypePrefixes` natively. Anything else is evaluated client-side with the same evaluator, so results are identical (TCK-28).
7. **The in-memory broker is the reference transport.** It sends messages through the real CloudEvents binary mapping with a 10-attribute budget, the strictest broker's, so the `sfext` overflow runs on every test. It also implements:
   - ordered endpoints (one in-flight message per key, head-of-line within a key);
   - locks with expiry and renewal;
   - a maximum delivery count and a dead-letter queue;
   - filtered fan-out, batches and partial failures;
   - a provisioning diff;
   - faults: outage, throttling, credential expiry, duplicates and dropped acks.

   `StrictDestinations` gives real-broker semantics for unknown destinations. It is off by default, so tests can send to services that start later.
8. **The TCK runs the real core.**
   - `SourceFlow.Testing` carries a small generated domain (`TckModule`). Cases 18, 24, 25 and 45 start real services on the transport under test.
   - The fixture contract adds `Faults` (null on real cloud; those cases are skipped, never weakened), `InjectDefectAsync` for the `Validate` cases, `QuietPeriod` and `DelayTolerance`.
   - Payload protection cases 41–43 cover the transport side: ciphertext and marker are carried byte for byte and never inspected. Decryption and `DecryptionFailed` parking arrive with WP-5.1.

## Consequences

- Provider packages (WP-3.2..3.4) implement one small SPI, and must pass the same cases as the in-memory broker (45 at WP-3.1; 49 since WP-C.6 added TCK-46..49).
- Every in-memory test now exercises the CloudEvents wire mapping, so envelope regressions surface everywhere, not only in provider suites.
- Brokers that cannot filter natively still filter correctly, at the cost of receiving and acknowledging unwanted events.
- The broker dead-letter queue is a backstop. The ingestor moves its messages to the store, where `dlq replay` re-sends them.
