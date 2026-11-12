# M4 load test report (WP-4.7)

The tier-M load test of the Bank API ([doc 09 §5](../09-Performance-and-Scalability.md#5-capacity-model): 1,000 commands/s
at peak) against the SLOs of [doc 08 §6](../08-Observability.md#6-slos-and-alerting-shipped-as-guidance-plus-dashboards).
The scenario and how to run it are in [`loadtest/README.md`](../../../loadtest/README.md).

## Result

**Tier M meets every SLO.** It ran at 1,000 commands/s for 3 minutes after a 30-second ramp, which is 196,498 commands.

| SLO | Objective | Measured | |
|-----|-----------|----------|-|
| Write availability | ≥ 99.9 % | 100 % (0 of 196,498 commands failed) | met |
| Write latency | p99 < 50 ms | p99 **37.7 ms** (p50 7.4 ms, p95 22.3 ms, max 209 ms) | met |
| Read-model freshness | p99 < 5 s | p99 **247 ms**, from the command to the summary visible through the query API | met |
| Delivery durability and health | outbox age < 60 s; nothing stalled | `/health/sourceflow` Healthy in 180 of 180 samples | met |
| Correctness | no parked messages | no errors logged by the service | met |

Resources at the plateau:

| Component | CPU |
|-----------|-----|
| Bank API container (limited to 4 CPUs) | 1.8 cores |
| PostgreSQL | 3.6 cores |

## Environment

The reference run was on a developer machine, not a dedicated load rig:

| Item | Setting |
|------|---------|
| Host | 20 logical CPUs, 16 GB of RAM given to Docker Desktop (WSL 2) |
| Database | PostgreSQL 17.11 (alpine), with default settings and `max_connections` 100 |
| Service | The Bank API image built from this checkout, with the in-memory transport, `--cpus 4` and `Maximum Pool Size=60` |
| Load generator | k6 2.3.0 on the same Docker network |

The service, PostgreSQL and k6 shared the machine, so these numbers are a floor, not a ceiling.

## PERF-09: commands per second per 4-vCPU instance (objective ≥ 5,000/s)

PERF-09 is **not demonstrated on this machine**. The database saturates before the service does:

| Offered load | Sustained | API CPU | PostgreSQL CPU | p99 latency |
|--------------|-----------|---------|----------------|-------------|
| 1,000/s | 1,000/s | 1.8 cores | 3.6 cores | 37.7 ms |
| 3,000/s | about 2,700/s | 2.0 cores | 10.2 cores | 577 ms |
| 5,000/s | about 2,100/s | 1.5 cores | 5.9 cores | 3.8 s |

Beyond 1,000/s:

- **Database.** PostgreSQL, with default settings on the shared disk, is the bottleneck: WAL flushes and lock waits.
- **Service.** The API stays at about 2 of its 4 cores. That is about 0.8 ms of CPU per command, or roughly 5,000 commands/s per 4 vCPUs if the database keeps up.
- **Sizing.** Doc 09 §5 sizes the database for this rate at 16 vCPU on NVMe.
- **Acceptance.** The PERF-09 acceptance run needs that database. The nightly job reports the trend on hosted runners.

## PERF-07: outbox commit to broker acknowledgement (objective p95 ≤ 250 ms)

Met on all three emulators: 100 publishes each, one at a time, on PostgreSQL (`RelayLatencyScenarios`, nightly).

| Broker | p50 | p95 | max |
|--------|-----|-----|-----|
| AWS (LocalStack, SNS) | 61 ms | 138 ms | 216 ms |
| Google Cloud (Pub/Sub emulator) | 38 ms | 85 ms | 341 ms |
| Azure (Service Bus emulator) | 70 ms | 167 ms | 269 ms |

## Problems the load test found, and fixed

1. **NOTIFY serialised every commit.** The PostgreSQL commit batch sent `pg_notify` inside the transaction. PostgreSQL holds a database-wide lock on the notification queue from the NOTIFY until the commit ends, including the WAL flush.
   - **Symptom.** Every appending transaction committed one at a time. Throughput was capped at about 270 commits/s, and 57 of 60 connections waited on `Lock:object` at `COMMIT`.
   - **Fix.** The store now notifies other processes after the commit, outside the transaction. One `NOTIFY` covers every commit made while the previous one was being sent.
   - **Unchanged.** In-process subscribers are still signalled immediately. A notification lost in a crash only delays other nodes until their next poll.
   - **Result.** Throughput rose from about 270/s to the full 1,000/s, with p99 falling from 5.5 s to 38 ms.
2. **Connection pool larger than the server allows.** With `Maximum Pool Size=200` against `max_connections=100`, the server refused connections (`53300: too many clients`). The reconnect churn then exhausted the client's ephemeral ports. This is a configuration rule, not a code change. It is now documented in `loadtest/README.md`.

The chaos suite (CH-01..10, same work package) found two more problems:

3. **The outbox age read zero during a broker outage.** A failed publish moves `deliver_at` to the retry time, so the retried message stopped counting as due. The delivery-durability SLO could not fire during an outage.
   - **Fix.** `first_due_at` keeps the time a message first became due. It is PostgreSQL migration V005, a column in the SQL Server initial schema, and `OutboxRecord.FirstDueAt` in memory.
   - **Tests.** SCK-31 and CH-07.
4. **Every node start altered the tenant tables.** The PostgreSQL migrator ran `ALTER TABLE … ROW LEVEL SECURITY` on every start. That takes an ACCESS EXCLUSIVE lock, which queues behind any open reader, so a node starting next to a busy one hung.
   - **Fix.** The migrator now alters only the tables whose state differs.
   - **Test.** CH-09.
