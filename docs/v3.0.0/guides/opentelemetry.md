# Wiring OpenTelemetry

SourceFlow.Net emits traces and metrics through `System.Diagnostics`: one `ActivitySource` and one `Meter`, both named
`SourceFlow`. There is no SourceFlow package for OpenTelemetry, and the core never references the OpenTelemetry SDK
(ADR-027 §5). You subscribe with the SDK your application already uses. This page shows how, and what you get.

## Subscribe

Add the OpenTelemetry hosting package and an exporter to the application, then name the SourceFlow source and meter:

```csharp
using SourceFlow.Diagnostics;

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("accounts"))
    .WithTracing(t => t
        .AddSource(SourceFlowTelemetry.ActivitySourceName)   // "SourceFlow"
        .AddAspNetCoreInstrumentation())                     // optional: the HTTP span becomes the root
    .WithMetrics(m => m
        .AddMeter(SourceFlowTelemetry.MeterName))            // "SourceFlow"
    .UseOtlpExporter();                                      // OTEL_EXPORTER_OTLP_ENDPOINT
```

That is all the wiring there is. The TravelBooking sample's services do exactly this
([`samples/TravelBooking/Shared/ServiceHost.cs`](../../../samples/TravelBooking/Shared/ServiceHost.cs)), and
`docker compose up -d --build` at the repository root sends their telemetry to an Aspire dashboard on
<http://localhost:18888>, where one trace follows a booking through both services and the broker.

You do not need views for histogram buckets. Every SourceFlow histogram declares its boundaries with
`InstrumentAdvice<T>`, which the SDK applies:

- durations: 0.5 ms to 10 s;
- counts: 1 to 1,000.

To use different boundaries, add a view for that instrument as usual. A view overrides the advice.

## What you get

| Span | Kind | When |
|------|------|------|
| `handle {command}` | Internal | A command is processed, locally or received. Carries `sourceflow.outcome`. |
| `sourceflow.eventstore.load` | Client | The aggregate is loaded (`sourceflow.load.source`: cache, snapshot or events). |
| `sourceflow.eventstore.append` | Client | The session commits: events, outbox, inbox and inline projections in one transaction. |
| `create {message type}` | Producer | A message is created. Its context is stored in the envelope's `traceparent`. |
| `send {destination}` | Producer | The outbox relay publishes. It is a child of the `create` span, even though it runs later on a background loop. |
| `process {endpoint}` | Consumer | A received message is handled. |
| `project {subscription}` | Consumer | One subscription batch. |
| `react {process manager}` | Internal | A process manager reacts. |

The metrics, with their units and tags, are listed in doc 08 §3 and in `SourceFlowTelemetry.Catalogue`. Stream ids,
message ids and process ids only appear on spans, never as metric tags, so metric cardinality stays bounded.

### One trace across services

By default, the `process` span in the receiving service starts a new trace that links to the sender's `create` span.
This is the messaging conventions' recommendation: a message redelivered or replayed days later does not grow the
original trace. If you want a single trace from the HTTP request in one service to the handler in the next, make the
`process` span a child of the `create` span instead:

```csharp
builder.Services.AddSourceFlow(sf =>
{
    sf.Configure(o => o.Telemetry.ParentBasedOnMessage = true);
});
```

### Sampling

SourceFlow does not sample. Use a parent-based sampler in the services. In the Collector, add a tail-sampling policy
that keeps every trace with `sourceflow.outcome` = `conflict` or `failed`.

## Semantic conventions

Attribute names follow the OpenTelemetry messaging semantic conventions, pinned to version **1.27.0**
(`SemanticConventions.PinnedVersion`). The pin keeps the names your dashboards query stable when you upgrade SourceFlow.
The version is also the telemetry schema URL of the source and the meter.

A later SourceFlow release may know newer conventions. You choose when to adopt them with the standard environment
variable:

| `OTEL_SEMCONV_STABILITY_OPT_IN` | SourceFlow emits |
|-------------------------------|------------------|
| unset, or no `messaging` entry | the pinned version |
| `messaging` | the newest messaging conventions this release knows (`SemanticConventions.LatestVersion`) |
| `messaging/dup` | the newest conventions, plus the deprecated names, so old and new dashboards both work while you migrate |

Other entries, such as `http` or `database`, belong to other instrumentations, and SourceFlow ignores them. In
`messaging/dup` mode, spans also carry the pre-1.26 attribute `messaging.operation`, for example `publish` for a send.

## Dashboards and alerts

`dotnet sourceflow observability export` generates a Grafana dashboard and Prometheus alert rules from the instrument
catalogue. See `deploy/observability/README.md`.

## Testing your telemetry

The SDK's in-memory exporter works in tests without any SourceFlow test hook. This is how the UT-OTEL tests check
spans and histogram boundaries:

```csharp
var spans = new List<Activity>();
using TracerProvider tracer = Sdk.CreateTracerProviderBuilder()
    .AddSource(SourceFlowTelemetry.ActivitySourceName)
    .AddInMemoryExporter(spans)
    .Build();

await host.SendAsync(new OpenAccount(id, "Ada", 10m));
tracer.ForceFlush();
spans.ShouldContain(s => s.DisplayName == "handle OpenAccount");
```
