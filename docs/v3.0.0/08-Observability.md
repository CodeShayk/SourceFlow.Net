# 08 — Observability

Design driver: [ADR-019](ADRs/ADR-019-telemetry-always-on-with-standard-semantic-conventions.md). Fixes: OBS-01, OBS-04, CTL-02, CTL-03, GAP-14, NFR-OPS-01/02/05.

**Goal.** An operator can answer "is it healthy, is it fast, is it fresh, and where did *this* message go?" from standard OpenTelemetry signals, without reading SourceFlow's code.

## 1. Instrumentation sources

| Source | Name | Version |
|--------|------|---------|
| `ActivitySource` | `SourceFlow` | package version |
| `Meter` | `SourceFlow` | package version |
| Logger categories | `SourceFlow.*` (for example `SourceFlow.Commands`, `SourceFlow.Subscriptions`, `SourceFlow.Transport.Aws`) | — |

Both are **always created** ([ADR-019](ADRs/ADR-019-telemetry-always-on-with-standard-semantic-conventions.md)). Without a listener, a span start costs one null check and an instrument update is a no-op. The one-line opt-in for users is:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSourceFlowInstrumentation())     // AddSource("SourceFlow") + sampler hints
    .WithMetrics(m => m.AddSourceFlowInstrumentation());     // AddMeter("SourceFlow") + histogram views
```

The library depends on `System.Diagnostics` only. The OpenTelemetry SDK is the *user's* dependency, through a tiny `SourceFlow.OpenTelemetry` extension package that holds those two methods.

## 2. Tracing

### 2.1 Spans

Spans follow the OpenTelemetry **messaging** semantic conventions at a pinned semconv version. Upgrades are opt-in through `OTEL_SEMCONV_STABILITY_OPT_IN`.

| Span name | Kind | When | Key attributes |
|-----------|------|------|----------------|
| `send {destination}` | Producer | Outbox relay publishes; direct remote send | `messaging.system`, `messaging.operation.type=send`, `messaging.destination.name`, `messaging.message.id`, `messaging.message.conversation_id` (correlation), `messaging.batch.message_count` |
| `create {message type}` | Producer | A message is *created* (command sent, event appended, integration event enqueued). This is the context stored in the envelope's `traceparent`. | `messaging.operation.type=create`, `sourceflow.message.type` |
| `process {destination}` | Consumer | The receive pipeline handles one message | `messaging.operation.type=process`, `messaging.consumer.group.name` (consumer), `messaging.message.id`, delivery attempt. **Linked** to the `create` span from `traceparent`, and its child when `ParentBasedOnMessage=true`. |
| `handle {command type}` | Internal | Command processor (local or received) | `sourceflow.aggregate.type`, `sourceflow.stream.id`, `sourceflow.expected_version`, `sourceflow.outcome` (accepted, rejected, noop, conflict, duplicate), `sourceflow.concurrency.retries` |
| `sourceflow.eventstore.append` | Client | Session commit | `db.system.name`, `db.operation.name=append`, `sourceflow.events.count`, `sourceflow.outbox.count` |
| `sourceflow.eventstore.load` | Client | Aggregate load | `sourceflow.load.source` (cache, snapshot, events), `sourceflow.load.events` |
| `project {subscription}` | Consumer | One subscription batch | `sourceflow.subscription`, `sourceflow.partition`, `messaging.batch.message_count`, up to 32 **links** to producer contexts |
| `react {process manager}` | Internal | Process manager reaction | `sourceflow.process.id`, `sourceflow.process.completed` |
| `settle {destination}` | Client | Complete, retry or dead-letter | `messaging.operation.type=settle`, `sourceflow.settle.action` |

**Cardinality rule.** Stream ids, message ids and process ids appear **only** on spans and logs, never as metric tags.

### 2.2 Propagation

- The `traceparent` and `tracestate` of the `create` span are stored in the event metadata and the outbox attributes. They are emitted as CloudEvents distributed-tracing attributes on the wire.
- A trace therefore covers: HTTP request → `handle` → `append` → (later) `send` → broker → `process` in service B → `handle` in B → B's `project` batch (link). This gives the end-to-end trace that v2 lacked (CTL-03).
- `Baggage` entries named in `sf.Telemetry.PropagateBaggage` (default: `tenant`) are copied to the envelope and restored on receive.

### 2.3 Sampling guidance

The library does not sample. The documentation recommends a parent-based sampler, with a tail-sampling collector that keeps every trace containing `sourceflow.outcome ∈ {conflict, failed}` or a dead-letter settle.

## 3. Metrics

Units follow UCUM. Histogram buckets are set through `InstrumentAdvice`: duration buckets from 0.0005 s to 10 s, and count buckets 1, 2, 5 … 1,000.

### 3.1 Standard messaging metrics (semconv names)

| Instrument | Type | Unit | Tags |
|------------|------|------|------|
| `messaging.client.operation.duration` | Histogram | s | `messaging.system`, `messaging.operation.name`, `messaging.destination.name`, `error.type` |
| `messaging.client.sent.messages` | Counter | {message} | `messaging.system`, `messaging.destination.name`, `error.type` |
| `messaging.client.consumed.messages` | Counter | {message} | `messaging.system`, `messaging.destination.name`, `messaging.consumer.group.name` |
| `messaging.process.duration` | Histogram | s | `messaging.system`, `messaging.destination.name`, `messaging.consumer.group.name`, `error.type` |

### 3.2 SourceFlow metrics

| Instrument | Type | Unit | Tags | Why it exists |
|------------|------|------|------|---------------|
| `sourceflow.command.duration` | Histogram | s | `sourceflow.command.type`, `sourceflow.outcome` | Write latency SLO |
| `sourceflow.eventstore.append.duration` | Histogram | s | `db.system.name` | Database health |
| `sourceflow.eventstore.append.events` | Histogram | {event} | `sourceflow.aggregate.type` | Event volume and shape |
| `sourceflow.eventstore.concurrency_conflicts` | Counter | {conflict} | `sourceflow.aggregate.type` | Hot-stream detection ([ADR-008](ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md)) |
| `sourceflow.aggregate.load.events` | Histogram | {event} | `sourceflow.aggregate.type`, `sourceflow.load.source` | Snapshot tuning (doc 05 §8) |
| `sourceflow.aggregate.cache.hits` | Counter | {hit} | `sourceflow.aggregate.type`, `result` | Cache effectiveness |
| `sourceflow.outbox.pending` | Observable gauge | {message} | `partition` | Backlog |
| `sourceflow.outbox.age` | Observable gauge | s | `partition` | **Durability SLO**: oldest undelivered message |
| `sourceflow.outbox.dispatch.duration` | Histogram | s | `messaging.system` | Relay health |
| `sourceflow.inbox.duplicates` | Counter | {message} | `messaging.consumer.group.name` | Redelivery and producer-retry visibility |
| `sourceflow.retry.attempts` | Counter | {attempt} | `stage`, `retry.kind` (immediate, delayed) | Instability signal |
| `sourceflow.deadletter.parked` | Counter | {message} | `consumer`, `reason`, `sourceflow.message.type` | **Alert on any increase** |
| `sourceflow.deadletter.count` | Observable gauge | {message} | `consumer` | Operator backlog |
| `sourceflow.subscription.lag` | Observable gauge | {event} | `subscription`, `partition` | Freshness |
| `sourceflow.subscription.lag_time` | Observable gauge | s | `subscription`, `partition` | **Freshness SLO** |
| `sourceflow.subscription.horizon_age` | Observable gauge | s | — | Long-transaction detector (doc 05 §5.4) |
| `sourceflow.subscription.stalled` | Observable gauge | {partition} | `subscription` | Poison-event stop |
| `sourceflow.subscription.batch.duration` | Histogram | s | `subscription` | Projection cost |
| `sourceflow.process.active` | Observable gauge | {instance} | `process.type` | Process-manager load |
| `sourceflow.lease.owned` | Observable gauge | {lease} | `lease.kind` | Work distribution |
| `sourceflow.workflow.transitions` | Counter | {transition} | `workflow`, `version`, `kind` | DSL workflow progress, failures, retries and compensations (doc 15 §7) |
| `sourceflow.workflow.active` | Observable gauge | {instance} | `workflow`, `status` | Instances per status |
| `sourceflow.workflow.manual_intervention` | Counter | {instance} | `workflow` | **Alert on any increase**: a compensation failed or a step needs an operator |

Each measurement is recorded exactly once, at one well-defined place, and the metric unit tests assert it. That rules out v2's double counting and never-recorded metrics (GAP-14).

## 4. Logging

- All log statements use **source-generated `[LoggerMessage]`** methods, so the hot path allocates nothing when the level is off. Event ids are grouped in ranges (1000–1999 commands, 2000–2999 store, 3000–3999 transport, 4000–4999 subscriptions, 5000–5999 security).
- Logs are structured, with stable property names that match span attributes (`MessageId`, `MessageType`, `CorrelationId`, `StreamId`, `Outcome`).
- Payloads are **never logged by default**. When `LogPayloads = true` is set for debugging, payloads go through `Microsoft.Extensions.Compliance.Redaction`: members marked `[PersonalData]` or classified with a data-classification attribute are redacted by the configured redactor ([ADR-013](ADRs/ADR-013-use-platform-libraries-delete-bespoke-equivalents.md)).
- Log levels: `Information` for lifecycle (start, stop, provisioning, lease changes); `Debug` per message; `Warning` for retries and conflicts beyond the threshold; `Error` for parks and stalls.

## 5. Health checks

`builder.Services.AddHealthChecks().AddSourceFlow()` registers these checks (tags allow liveness and readiness separation):

| Check | Tag | Healthy | Degraded | Unhealthy |
|-------|-----|---------|----------|-----------|
| `sourceflow.ready` | ready | Startup validation and provisioning done; receivers started | — | Still starting or failed |
| `sourceflow.store` | ready | `SELECT 1` and schema version match | Latency > 250 ms | Unreachable or schema mismatch |
| `sourceflow.transport` | ready | Broker reachable (cheap describe call, cached 30 s) | Throttled | Unreachable |
| `sourceflow.outbox` | — | `outbox.age` < 10 s | < 5 min | ≥ 5 min |
| `sourceflow.subscriptions` | — | Every partition: `lag_time` < 30 s and not stalled | Lag above threshold, or `horizon_age` > 30 s | Any partition stalled > 5 min |
| `sourceflow.leases` | — | Owns its fair share, or there are no leases to own | Under share for > 2 renew intervals | — |

Liveness should **not** include store or transport checks, so that a dependency outage doesn't cause restart loops.

## 6. SLOs and alerting (shipped as guidance plus dashboards)

| SLO | Indicator | Starting objective |
|-----|-----------|--------------------|
| Write availability | `sourceflow.command.duration` count where `outcome ∉ {failed}` ÷ total | 99.9 % / 30 days |
| Write latency | p99 `sourceflow.command.duration` | < 50 ms |
| Read-model freshness | p99 `sourceflow.subscription.lag_time` | < 5 s |
| Delivery durability | max `sourceflow.outbox.age` | < 60 s |
| Correctness | `sourceflow.deadletter.parked` increase | 0 (page on any) |

Grafana dashboards (JSON) and Prometheus alert rules are published in `deploy/observability/`. They are generated from the instrument list above, so they can't drift from it. An Aspire dashboard works out of the box through OTLP.
