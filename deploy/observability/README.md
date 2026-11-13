# Dashboards and alerts

Generated from the instrument catalogue (`SourceFlowTelemetry.Catalogue`, doc 08 §3) by:

```bash
dotnet sourceflow observability export --output deploy/observability
```

Do not edit these files by hand. A test fails when they differ from what the generator writes, and `promtool` checks
the rules in CI.

| File | What it is |
|------|------------|
| `sourceflow-dashboard.json` | A Grafana dashboard. The first row shows the service-level objectives of doc 08 §6: write availability, p99 write latency, read-model freshness, delivery durability, and parked messages. After that there is one panel per instrument, grouped by area. Import it and choose your Prometheus data source. The `Service` variable is the `job` label (from `service.name`). |
| `sourceflow-alerts.yaml` | Prometheus alert rules for the same objectives, plus stalled subscriptions, a held log horizon and DSL workflow instances that need an operator (`SourceFlowWorkflowManualIntervention`). Severity is `page` or `warning`. |

## Metric names

The queries use the names the OpenTelemetry Prometheus exporter and the Collector produce:

- Dots become underscores.
- Seconds add `_seconds`.
- Counters end in `_total`.
- Histograms are read through `_bucket`, `_sum` and `_count`.

For example, `sourceflow.command.duration` becomes `sourceflow_command_duration_seconds_bucket`, and
`sourceflow.deadletter.parked` becomes `sourceflow_deadletter_parked_total`. If your pipeline names metrics differently,
for example without unit suffixes, adjust the generated files or the exporter.

## Gauges

Gauges are sampled by each instance on `SourceFlowOptions.MonitorInterval` (15 s by default):

- **Outbox, dead letters, subscription lag, horizon age and active process managers** are read from the store. Every
  instance reports the same value, so the dashboard and alerts take the `max`.
- **`sourceflow.lease.owned`** is per instance, so it is summed by `instance`.

## Without Prometheus

The Bank demo (`docker compose up -d` at the repository root) sends traces and metrics over OTLP to the Aspire
dashboard at <http://localhost:18888>. The health endpoints are:

- `/health`: liveness. It runs no dependency checks.
- `/health/ready`: SourceFlow started, and the store and broker answer.
- `/health/sourceflow`: every SourceFlow check (doc 08 §5).
