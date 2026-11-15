# ADR-019: Telemetry always on, with standard semantic conventions

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 telemetry is off by default (OBS-01), some metrics are wrong (GAP-14), and the cloud path isn't traced (CTL-03).

## Decision

- The `SourceFlow` ActivitySource and Meter are always created. Their cost without a listener is close to zero (a null-activity check, and instruments without listeners).
- Spans and metrics follow the OpenTelemetry messaging semantic conventions, at a pinned semconv version. Upgrades are opt-in through `OTEL_SEMCONV_STABILITY_OPT_IN`.
- High-cardinality values (stream id, message id) go only on spans, **never** as metric tags.

## Consequences

(+) Observability out of the box. (−) Semconv changes need managing. A pinned version and the opt-in env var follow OTel guidance.
