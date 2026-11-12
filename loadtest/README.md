# Load tests

`k6/bank-tier-m.js` drives the Bank API at capacity tier M (doc 09 §5: 1,000 commands/s at peak) and checks the SLOs
of doc 08 §6 at the HTTP edge. It is the WP-4.7 load scenario; results are in
[`docs/v3.0.0/implementation/load-test-report.md`](../docs/v3.0.0/implementation/load-test-report.md).

## What it does

| Scenario | Load | Measures |
|----------|------|----------|
| `commands` | Ramps to `RATE` commands/s, then holds it for `DURATION`. 30 % open an account, 55 % deposit and 15 % withdraw. Every virtual user works on its own accounts, so the streams are distinct. | `command_duration`, `command_failed` |
| `freshness` | Every 2 s, opens an account and polls the query API until its summary appears | `read_model_freshness` |
| `health` | Every 1 s, reads `/health/sourceflow` | `sourceflow_healthy`: store, transport, outbox age, subscriptions and leases are all Healthy |

| Threshold | SLO (doc 08 §6) |
|-----------|-----------------|
| `command_failed` rate < 0.1 % | write availability 99.9 % |
| `command_duration` p99 < 50 ms | write latency |
| `read_model_freshness` p99 < 5 s | read-model freshness |
| `sourceflow_healthy` > 99 % | delivery durability: the outbox age stays under the health threshold, and nothing is stalled |

k6 exits non-zero when a threshold fails.

## Running it

Start PostgreSQL and the Bank API from this checkout, then run k6 on the same Docker network:

```bash
docker compose up -d --build postgres bank-api
docker run --rm -i --network sourceflow-demo_default \
  -e BASE_URL=http://bank-api:8080 -e RATE=1000 -e DURATION=5m \
  grafana/k6:2.3.0 run - < loadtest/k6/bank-tier-m.js
```

| Variable | Default | Meaning |
|----------|---------|---------|
| `BASE_URL` | `http://localhost:8080` | The Bank API |
| `RATE` | `1000` | Commands per second at the plateau |
| `DURATION` | `5m` | Length of the plateau |
| `RAMP` | `30s` | Time to ramp up to `RATE` |

The freshness probe needs the sample query endpoint, which `docker-compose.yml` turns on (`Demo__QueryApi=true`).

Run k6 inside the Docker network, not from Windows or macOS against a published port. The desktop port forwarding adds
hundreds of milliseconds at p95 and measures the forwarding, not the service.

For the acceptance run (PERF-09), give the service a 4-vCPU limit (`docker run --cpus 4 …`, or a 4-vCPU instance) and
give PostgreSQL its own machine. On a shared laptop, PostgreSQL saturates first.

### Connection pool sizing

`Maximum Pool Size` in the connection string must leave room under PostgreSQL's `max_connections` (100 by default) for
every service instance. The store also uses a few connections of its own: one for LISTEN and one for notifications. A
pool that exceeds the server limit fails with `53300: too many clients`. The reconnect churn that follows can exhaust
the client's ephemeral ports. The reference run uses `Maximum Pool Size=60`.

## Nightly

The `load test (tier M)` job in `.github/workflows/v3-nightly.yml` runs the scenario for 5 minutes against the image
built from the checkout, and uploads the k6 summary. On the hosted runner, the service, PostgreSQL and k6 share 4
vCPUs, so the job reports results and does not fail the build.

## Why k6 and not NBomber

The plan named NBomber. NBomber 6.x requires accepting a commercial licence, and organisations need a paid
subscription. k6 is open source, runs as a container, and adds no dependency to the repository.
