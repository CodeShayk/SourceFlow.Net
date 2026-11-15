# ADR-013: Use platform libraries; delete bespoke equivalents

| | |
|---|---|
| **Status** | Proposed |
| **Date** | 2026-11-10 |
| **Deciders** | v3 architecture review |

## Context

v2 ships its own circuit breaker, log masker, buffer pools and telemetry wrappers. That code duplicates libraries the .NET platform already maintains, carries its own bugs (for example the metrics in GAP-14), and is unfamiliar to users who already know the platform versions.

## Decision

Replace every bespoke utility with the platform standard:

| v2 bespoke | v3 uses |
|---|---|
| `CircuitBreaker`, ad-hoc retries | Polly v8 `ResiliencePipeline` through `Microsoft.Extensions.Resilience` |
| `SensitiveDataMasker` | `Microsoft.Extensions.Compliance.Redaction` with data-classification attributes |
| `DateTime.UtcNow` | `TimeProvider` (testable with `FakeTimeProvider`) |
| Manual metric and tracing wrappers | `ActivitySource`, `Meter`, `InstrumentAdvice`, OpenTelemetry SDK extension points |
| `ByteArrayPool`, `TaskBufferPool` | `ArrayPool<T>`, `IBufferWriter<byte>`, `RecyclableMemoryStream` where streams are required |
| Reflection-based option binding | The configuration binding source generator and `[OptionsValidator]` |

## Alternatives

Keep and fix the v2 utilities. Rejected: no product requirement is met better by owning them (DRY, YAGNI).

## Consequences

- (+) Less code to own. Users already know these libraries. Bug fixes and security patches come from the platform.
- (−) A dependency on Polly, which is already the norm in .NET services.

## Traces

GAP-14, RES-01, SEC-04, PERF-01, PERF-02.
