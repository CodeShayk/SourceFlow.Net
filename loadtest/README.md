# Load tests

`k6/travel-tier-m.js` drives the TravelBooking sample's Bookings service at capacity tier M (doc 09 §5: 1,000 commands/s
at peak), with the trip-booking workflow running to the Hotels service over the broker, and checks the SLOs of doc 08 §6
at the HTTP edge. It is the WP-4.7 load scenario; the results of its first version, against the Bank sample that the
TravelBooking sample replaced, are in
[`docs/v3.0.0/implementation/load-test-report.md`](../docs/v3.0.0/implementation/load-test-report.md).

## What it does

| Scenario | Load | Measures |
|----------|------|----------|
| `setup` | Registers one hotel with more rooms than the test can book, so no booking is declined as sold out | |
| `commands` | Ramps to `RATE` commands/s, then holds it for `DURATION`. 60 % request a booking (each starts a workflow) and 40 % confirm one of the virtual user's own bookings as the guest. | `command_duration`, `command_failed` |
| `workflow` | Every 2 s, requests a booking and polls the `workflow-instances` read model until the room is held: the hold went to Hotels over the broker and Hotels' event came back | `workflow_room_held` |
| `health` | Every 1 s, reads the Bookings service's `/health/ready` | `sourceflow_healthy`: the store and the transport are Healthy |

| Threshold | SLO (doc 08 §6) |
|-----------|-----------------|
| `command_failed` rate < 0.1 % | write availability 99.9 % |
| `command_duration` p99 < 50 ms | write latency |
| `workflow_room_held` p99 < 5 s | cross-service latency: one workflow step, there and back |
| `sourceflow_healthy` > 99 % | the service stays ready |

k6 exits non-zero when a threshold fails.

## Running it

Start the two services, PostgreSQL and LocalStack from this checkout, then run k6 on the same Docker network:

```bash
docker compose up -d --build --wait postgres localstack hotels bookings
docker run --rm -i --network sourceflow-demo_default \
  -e BOOKINGS_URL=http://bookings:8080 -e HOTELS_URL=http://hotels:8080 -e RATE=1000 -e DURATION=5m \
  grafana/k6:2.3.0 run - < loadtest/k6/travel-tier-m.js
```

| Variable | Default | Meaning |
|----------|---------|---------|
| `BOOKINGS_URL` | `http://localhost:8080` | The Bookings service |
| `HOTELS_URL` | `http://localhost:8081` | The Hotels service (the setup registers the hotel there) |
| `RATE` | `1000` | Commands per second at the plateau |
| `DURATION` | `5m` | Length of the plateau |
| `RAMP` | `30s` | Time to ramp up to `RATE` |

The workflow probe needs the sample query endpoint, which the compose file turns on (`Demo__QueryApi=true`).

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

The `load test (tier M)` job in `.github/workflows/v3-nightly.yml` runs the scenario for 5 minutes against the images
built from the checkout, and uploads the k6 summary. On the hosted runner, the service, PostgreSQL and k6 share 4
vCPUs, so the job reports results and does not fail the build.

## Why k6 and not NBomber

The plan named NBomber. NBomber 6.x requires accepting a commercial licence, and organisations need a paid
subscription. k6 is open source, runs as a container, and adds no dependency to the repository.
