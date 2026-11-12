# Performance baseline (WP-2.10)

This report records the first component baseline against the doc 09 §7.1 targets. It covers the PERF ids in plan 06 §8 that can run before M3 and M4.

- **Run it with** `dotnet run -c Release --project benchmarks/SourceFlow.Benchmarks -- baseline`. That runs the BenchmarkDotNet suite plus the measurement harness and writes `benchmarks/baselines/benchmarks.json` and `measurements.json`. Use `bench` or `measure` to run one half.
- **Environment:** a developer workstation running Windows 11 and .NET 10, with PostgreSQL 17 in local Docker Desktop (WSL 2) through Testcontainers. Numbers are indicative only. The reference runner (plan 08 §4: 4 vCPU, database in the same availability zone) replaces them when the nightly job runs there.
- **BenchmarkDotNet job:** short (3 warm-ups, 5 iterations). Baselines change only through a PR labelled `perf-baseline`.

## Results

| ID | Benchmark | Target | Baseline | Status |
|----|-----------|--------|----------|--------|
| PERF-01 | In-process command (in-memory, 1 event) | p50 ≤ 20 µs, ≤ 2 KB/op | 4.4 µs (p95 4.5 µs), 6.2 KB/op | Time met. **Allocation deviation** |
| PERF-02 | Middleware overhead (3 pass-through) | ≤ 1 µs, 0 B steady state | +38 ns; 40 B bare, 352 B with 3 | Time met. **Allocation deviation** |
| PERF-03 | PostgreSQL commit, 1/3/10 events + outbox | p99 ≤ 10 ms; 1 round trip | mean 2.2 / 2.5 / 2.7 ms, p95 2.7 / 3.0 / 2.8 ms; 1 round trip (SCK-20) | Met |
| PERF-04 | Load with 0/100/1,000 events | linear without, flat with snapshots | without: 2.1 → 3.4 → 9.5 ms (1.3 MB at 1,000); with: 3.1 → 3.4 → 3.1 ms (13 KB) | Met |
| PERF-05 | Document projection rebuild, one partition | ≥ 20,000 events/s | 32,654 events/s with the default adaptive batches (up to 1,000); 14,848 events/s when capped at 256 | Met |
| PERF-06 | Global log read | ≥ 50,000 events/s | 6.7 µs/event ≈ 148,000 events/s | Met |
| PERF-07 | Outbox commit → broker acknowledgement | p95 ≤ 250 ms | — | Needs the cloud emulators: M3 (WP-3.x) |
| PERF-08 | Event → async projection latency (LISTEN/NOTIFY) | p95 ≤ 100 ms | p95 14.8 ms, p50 9.3 ms (includes the command) | Met |
| PERF-09 | Commands/s per 4-vCPU instance | ≥ 5,000/s | 1,000/s sustained (SLOs met); about 2,700/s peak, limited by the shared PostgreSQL, with the API at about 2 of 4 cores | Not yet demonstrated: needs a database sized per doc 09 §5 (load-test-report.md) |
| PERF-10 | Startup to ready (JIT / AOT) | ≤ 150 ms / ≤ 50 ms | JIT first start 99 ms in the recorded run (99–180 ms across runs on this workstation); warm ≈ 1–2 ms | **Borderline**. AOT waits for Bank.Api (WP-4.2) |

## Changes made by this work package

- **One-round-trip commit (PERF-03, SCK-20).** The conditional writes assert in SQL and `COMMIT` rides in the batch (ADR-028 §6, migration V003).
- **Batch-aware document projections (PERF-05, doc 06 §85).**
  - A batch prefetches its documents with one `GetManyAsync`.
  - Events apply to the deserialized documents in memory.
  - Each changed document is written once, through a multi-row `unnest` upsert on PostgreSQL.
  - Rebuild throughput at 256-event batches rose from 4,100 to about 15,000 events/s.
- **Adaptive subscription batches (doc 06 §2).** Each partition starts at 100 events, doubles while batches come back full, and drops back once caught up. `SubscriptionBatchSize` is now the maximum, with a default of 1,000 as doc 06 specifies. About five round trips per batch are fixed cost, so larger catch-up batches deliver the rebuild target.

## Deviations

| ID | Deviation | Owner | Plan |
|----|-----------|-------|------|
| PERF-01 | 6.1 KB allocated per in-process command against a 2 KB target | WP-6.2 (performance hardening) | Pool envelopes and event arrays. Avoid the per-command DI scope when no handler needs one. Cache the routing lookups. |
| PERF-02 | 40 B per pipeline call with no middleware; 352 B with three | WP-6.2 | Compose and cache the delegate chain per stage for singleton middleware. Pass the `next` continuation as a struct instead of a closure. |
| PERF-10 | First start in process ranges 99–180 ms on the workstation against 150 ms (noisy) | WP-6.2 | Re-measure on the reference runner. ReadyToRun and trimming are the fallbacks. The AOT measurement comes with Bank.Api. |
