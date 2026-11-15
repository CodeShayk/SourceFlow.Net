# ADR-014: Transport SPI and conformance kit; parity required for GA

| | |
|---|---|
| **Status** | Accepted (product sign-off 2026-11-10) |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 providers diverge in behaviour (doc 04 §10 parity matrix), ignore options (GAP-03), and the GCP fan-out is broken (GAP-13).

## Decision

- A small SPI ([doc 07](../Design/07-Cloud-Transports.md#2-transport-spi)): send, publish, receive (with settle operations), provision, and capabilities.
- Behaviour that the core can supply (retries, dead-lettering, inbox, encryption, telemetry) is implemented **once in the core**, not in each transport.
- A **Transport Conformance Kit** (about 40 scenarios) must pass for AWS SNS/SQS, Azure Service Bus, Google Pub/Sub and the in-memory transport before GA.

## Consequences

(+) Parity by construction; new transports are cheap. (−) The core can only rely on the lowest common denominator of broker features. Optional capabilities (native delay, sessions) are declared through `TransportCapabilities` and the core falls back when one is missing.

## Product sign-off (2026-11-10)

Q5 and Q6 are answered **"new version 3.0.0"**. AWS SNS/SQS, Azure Service Bus and Google Pub/Sub all ship GA in 3.0.0, and each one blocks the release until it passes the TCK. The earlier option of moving Azure to 3.1 is withdrawn ([doc 12 §5](../Design/12-Migration-Delivery-and-Traceability.md#5-delivery-plan)).

## Traces

Q5, Q6, GAP-01, GAP-02, GAP-03, GAP-13.
