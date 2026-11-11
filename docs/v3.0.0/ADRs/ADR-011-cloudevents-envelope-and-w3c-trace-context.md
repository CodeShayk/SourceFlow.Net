# ADR-011: CloudEvents 1.0 envelope and W3C trace context

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 uses provider-specific attributes (`CommandType` as an AQN, `EventName`). Formats differ between providers, and trace context is mostly not propagated (CTL-03).

## Decision

- One `MessageEnvelope` model, mapped to CloudEvents 1.0 attributes: `id`, `source`, `type`, `specversion`, `subject`, `time`, `datacontenttype`, and the `dataschema` version.
- SourceFlow extensions: `sfcorrelationid`, `sfcausationid`, `sfpartitionkey`, `sfstreamversion`, `sfenc`, `sftenant`.
- Trace context uses the CloudEvents distributed-tracing extension (`traceparent`, `tracestate`).
- Transports use **binary content mode** (attributes in native headers, data in the body) where the broker supports enough headers, and structured mode otherwise.

## Alternatives

A bespoke envelope: no interoperability with non-.NET consumers.

## Consequences

(+) Interoperable with CloudEvents SDKs in any language, and observable end to end. (−) Header budget limits: SQS has 10 message attributes, which is enough for the 8–9 mapped attributes; anything more goes in structured mode.
