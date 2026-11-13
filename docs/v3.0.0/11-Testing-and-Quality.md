# 11 — Testing and Quality Engineering

v2's most serious defects were behaviours that unit tests couldn't see: features that weren't wired, options that were ignored, ordering and race windows, and providers that diverged. v3's quality strategy targets exactly those failure classes. It relies on **conformance kits, property-based concurrency tests and fault injection**, not on coverage percentages alone.

## 1. Test portfolio

| Layer | Purpose | Tooling | Runs |
|-------|---------|---------|------|
| Domain specs | Aggregates, process managers and projections as pure functions | `SourceFlow.Testing` specs (no DI, no I/O) | Every build |
| Unit | Runtime components in isolation | xUnit v3, Shouldly, `FakeTimeProvider` | Every build |
| Generator and analyzer | Generated code and diagnostics | `Microsoft.CodeAnalysis.Testing`, approved-file snapshots | Every build |
| Storage conformance | Every storage adapter satisfies the port contracts | Abstract suites plus Testcontainers (PostgreSQL 15/16/17, SQL Server 2022/2025) | Every PR (PostgreSQL 17), nightly (full matrix) |
| Transport conformance (TCK) | Every transport satisfies the SPI contract (doc 07 §7) | Abstract suites plus emulators (LocalStack, Service Bus emulator, Pub/Sub emulator) | Every PR (emulators); nightly against real cloud accounts |
| Property-based | Linearisability, gap-freedom, ordering under random interleavings | CsCheck | Every PR (bounded), nightly (long) |
| Fault injection and chaos | Crash consistency, outages, failover | `IFaultInjector` hooks, Toxiproxy, container kill | Nightly and before each milestone |
| Architecture | Dependency rules (doc 01 §5) | NetArchTest | Every build |
| Public API | Detect breaking changes | PublicApiGenerator + approved files | Every PR |
| Performance | Regression gates (doc 09 §7) | BenchmarkDotNet, NBomber | PR (micro), nightly (component), milestone (system) |
| Mutation | Strength of the test suite | Stryker.NET on `SourceFlow.Net` core | Weekly; target mutation score ≥ 70 % |

## 2. Domain specs (shipped to users)

```csharp
public class BankAccountSpecs
{
    static readonly AccountId Id = AccountId.New();

    [Fact]
    public void Deposit_increases_balance() =>
        AggregateSpec.For<BankAccount>()
            .Given(new AccountOpened(Id, "Ada", 100m, T0))
            .When(new Deposit(Id, 50m))
            .Then(new MoneyDeposited(Id, 50m, 150m));

    [Fact]
    public void Withdraw_more_than_balance_is_rejected() =>
        AggregateSpec.For<BankAccount>()
            .Given(new AccountOpened(Id, "Ada", 100m, T0))
            .When(new Withdraw(Id, 500m))
            .ThenRejected(AccountErrors.InsufficientFunds);

    [Fact]
    public void Transfer_times_out_and_compensates() =>
        ProcessManagerSpec.For<MoneyTransfer>()
            .Given(new TransferRequested(TId, From, To, 10m), new MoneyWithdrawn(From, 10m, 90m))
            .WhenTimeAdvances(TimeSpan.FromMinutes(5))
            .ThenSends(new Deposit(From, 10m), new FailTransfer(TId, "timeout"))
            .ThenCompleted();

    [Fact]
    public void Summary_tracks_balance() =>
        ProjectionSpec.For<AccountSummaryProjection>()
            .Given(new AccountOpened(Id, "Ada", 100m, T0), new MoneyDeposited(Id, 50m, 150m))
            .ThenDocument(Id, d => d.Balance.ShouldBe(150m));
}
```

The specs run the **generated** `Decide`, `Evolve` and `React` code, so they test exactly what runs in production. They also check that each event round-trips through the serialiser and that `Evolve` is deterministic (by applying the events twice).

## 3. Integration test host

```csharp
await using var host = await SourceFlowTestHost.StartAsync(sf =>
{
    sf.AddModule<AccountsModule>();
    sf.UseInMemory();                                   // store + transport, with fault injection
}, time: new FakeTimeProvider(T0));

await host.Send(new OpenAccount(Id, "Ada", 100m));
await host.WaitForIdleAsync();                          // drains relays, subscriptions and timers deterministically
host.Documents<AccountSummary>().Get(Id).Balance.ShouldBe(100m);
host.Outbox.Published<AccountOpenedV1>().ShouldHaveSingleItem();
```

`WaitForIdleAsync` removes `Task.Delay`-based flakiness: the in-memory adapters expose quiescence counters. The same host can run against Testcontainers PostgreSQL (`sf.UsePostgreSql(container.ConnectionString)`).

## 4. Storage conformance kit (selected mandatory cases)

| Area | Cases |
|------|-------|
| Append | `Exact`/`NoStream` success and conflict. The affected-row check rolls back every queued write. ES-R1 (a prior write in the session is rejected). ES-R2 (one stream per session). Unique backstop maps to `ConcurrencyException`. |
| Read | Stream slices and backwards reads. Upcasting on read. A missing stream is `null` (pinned contract; fixes GAP-15). |
| Global log | **Gap-freedom**: 32 concurrent writers with random delays between insert and commit; one reader checkpointing continuously; the reader sees the exact set of committed events, each once (property-based). Per-stream order is preserved. Bucket filtering is correct. |
| Session | Outbox, inbox, documents, process state and checkpoint commit atomically with the append, or not at all (fault injected before and after each statement). |
| Leases | Acquire, renew, expire, steal. **Fencing**: a stale token can't commit (checkpoint and read model rolled back). |
| Outbox | The head-of-key rule holds under failures. `deliver_at` is honoured. Partition assignment is stable. |
| Inbox | Duplicates are detected under concurrent delivery of the same message id to two workers. The stored result is returned. |
| Migrations | Fresh install, upgrade from every previous schema version, concurrent migrators serialised. |

## 5. Property-based and model-based tests

- **Linearisable append.** A random schedule of N clients doing load→decide→append against a shared counter aggregate, compared with a sequential model. The final state equals the sum of accepted commands, and no accepted command is lost.
- **Exactly-once effect.** Random message duplication, reordering *across* keys, and crash points in the pipeline. The read model equals the model's projection of the accepted commands.
- **Per-key order.** Random interleavings through the partitioned dispatcher, the outbox relay and the TCK ordered endpoint. Order is checked per key.

## 6. Fault injection and chaos

`IFaultInjector` (compiled into Debug builds and the `SourceFlow.Testing` adapters, a no-op in Release) exposes named points:

`session.beforeCommit`, `session.afterCommitBeforeAck`, `reaction.beforeCommit` (a process manager or event handler reaction, WP-S.13), `relay.afterPublishBeforeDelete`, `subscription.afterApplyBeforeCommit`, `lease.beforeRenew`, `receiver.beforeComplete`.

Chaos scenarios (nightly, Testcontainers + Toxiproxy):

| Scenario | Expected |
|----------|----------|
| Kill the process at each injection point, then restart | No lost events or messages. Duplicates are absorbed by the inbox. Projections converge. |
| Broker unavailable for 10 min under load | Commands keep succeeding. `outbox.age` grows, then drains in order after recovery. |
| PostgreSQL failover (primary restart) | Commands fail fast and are retried by callers. Subscriptions and relays resume from checkpoints. No duplicate effects. |
| A paused node (SIGSTOP longer than the lease TTL) | Its leases move to other nodes. On resume, its commits fail on fencing. No double application. |
| A long-running write transaction held for 2 min | Subscriptions pause. `horizon_age` alerts. No events are skipped after release. |

## 7. CI pipeline

```text
PR:      build (warnings as errors, analyzers, trimming/AOT analysis) → unit + specs + generator snapshots
         → architecture + API approval → storage CK (PostgreSQL 17) → TCK (emulators) → micro-benchmarks (gate)
Nightly: full DB matrix → TCK on real AWS/Azure/GCP test accounts → property-based (long) → chaos → component benchmarks
Release: everything above + system load test + SBOM + signing + publish (tag-driven, as in v2 GitVersion flow)
```

## 8. Definition of Done (per feature)

- [ ] Behaviour covered by specs or unit tests, plus the relevant conformance-kit cases.
- [ ] Telemetry: spans, metrics and logs per [doc 08](08-Observability.md), with metric unit tests asserting single recording.
- [ ] Options validated (no ignored options). Analyzer coverage where the rule can be checked statically.
- [ ] Public API approved. XML docs complete. Docs page updated.
- [ ] Trimming and AOT clean. No new allocations on the hot path (benchmark evidence).
- [ ] Failure modes listed and tested (retry, park or stop, with visible signals).
