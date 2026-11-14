# SourceFlow.Testing

`SourceFlow.Testing` is the test toolkit for SourceFlow services. It has Given/When/Then specs for aggregates, process
managers, projections and DSL workflows; an in-memory test host that runs the real runtime with deterministic waits;
approved-file snapshots; a fault injector for chaos tests; and the storage and transport conformance kits that every
adapter must pass. It does not depend on a test framework: specs throw `SpecificationException`, conformance cases throw
`ConformanceException`, and you run them from xUnit, NUnit or MSTest as you like.

| | |
|---|---|
| Package | `SourceFlow.Testing` |
| Target | `net10.0` only |
| Depends on | `SourceFlow.Net`, `Microsoft.Extensions.DependencyInjection` |
| Namespaces | `SourceFlow.Testing`, `SourceFlow.Testing.Conformance` |

## Contents

- [Installation](#installation)
- [Which tool for which test](#which-tool-for-which-test)
- [Aggregate specs](#aggregate-specs)
- [Process manager specs](#process-manager-specs)
- [Projection specs](#projection-specs)
- [Workflow specs](#workflow-specs)
- [The in-memory test host](#the-in-memory-test-host)
- [Controlling time](#controlling-time)
- [Approved-file snapshots](#approved-file-snapshots)
- [Fault injection](#fault-injection)
- [Conformance kits](#conformance-kits)
- [API reference](#api-reference)
- [Troubleshooting](#troubleshooting)
- [Links](#links)

## Installation

```bash
dotnet add package SourceFlow.Testing
# your test framework and assertion library, for example:
dotnet add package xunit.v3
dotnet add package Shouldly
# for controlled time in host tests:
dotnet add package Microsoft.Extensions.TimeProvider.Testing
```

The test project targets `net10.0` and references the projects (or packages) that hold your modules.

## Which tool for which test

| You want to test | Use | I/O | Speed |
|------------------|-----|-----|-------|
| An aggregate's decisions: given past events, a command produces events, a rejection or nothing | `AggregateSpec<TAggregate, TId>` | none | microseconds |
| A process manager's reactions: commands sent, messages scheduled, completion | `ProcessManagerSpec<TProcess, TId>` | none | microseconds |
| A document projection: events in, documents out | `ProjectionSpec<TProjection, TDocument, TKey>` | none | microseconds |
| A generated DSL workflow, path by path | `WorkflowSpec<TWorkflow>` | none | microseconds |
| A whole service: routing, outbox, inbox, subscriptions, queries, several services on one broker | `SourceFlowTestHost` | in-memory store and broker | milliseconds |
| A storage or transport adapter you wrote | `StorageConformance`, `TransportConformance` | your database or broker | seconds |

The specs call the **generated** `Decide`, `Evolve`, `React` and `Apply` dispatch, so they test the code that runs in
production. They need no dependency injection, no store and no clock.

## Aggregate specs

```csharp
public sealed class AggregateSpec<TAggregate, TId>
    where TAggregate : class, IAggregateRuntime<TAggregate, TId>
    where TId : struct, IStronglyTypedId<TId>
```

`Given` folds past events with the generated `Evolve`. `When` decides one command against that state. Then assert one
of the three possible outcomes. These examples are from
[`tests/Tooling/SourceFlow.Tooling.Tests/SpecTests.cs`](../../../tests/Tooling/SourceFlow.Tooling.Tests/SpecTests.cs),
using the Bank domain the tests use ([`tests/Fixtures/Bank`](../../../tests/Fixtures/Bank)). The TravelBooking sample's
aggregate specs are in
[`samples/TravelBooking/TravelBooking.Tests/DomainTests.cs`](../../../samples/TravelBooking/TravelBooking.Tests/DomainTests.cs):

```csharp
using Bank.Accounts;
using SourceFlow.Testing;

public sealed class BankAccountSpecs
{
    private static readonly AccountId Account = AccountId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opening_an_account_produces_AccountOpened()
    {
        new AggregateSpec<BankAccount, AccountId>()
            .At(Now)                                                       // the decision time (ctx.Now)
            .When(new OpenAccount(Account, "Ada", 10m))
            .Then(new AccountOpened(Account, "Ada", 10m, Now))
            .ThenState(s => s.Balance == 10m && s.Status == AccountStatus.Open);
    }

    [Fact]
    public void Withdrawing_more_than_the_balance_is_rejected()
    {
        new AggregateSpec<BankAccount, AccountId>()
            .Given(new AccountOpened(Account, "Ada", 10m, Now))
            .When(new Withdraw(Account, 11m))
            .ThenRejected(AccountErrors.InsufficientFunds.Code);
    }

    [Fact]
    public void Closing_a_closed_account_does_nothing()
    {
        new AggregateSpec<BankAccount, AccountId>()
            .Given(new AccountOpened(Account, "Ada", 10m, Now), new AccountClosed(Account, "x", Now))
            .When(new CloseAccount(Account, "again"))
            .ThenNoOp();
    }
}
```

| Member | Meaning |
|--------|---------|
| `Given(params IEvent[])` | Past events, folded in order. Call it more than once to add more. |
| `At(DateTimeOffset)` | The decision time passed in the `DecisionContext`. Default `2026-01-01T00:00:00Z`. |
| `When(ICommand)` | Decides the command. The context has the stream version equal to the number of given events. |
| `State` | The state folded from the given events. |
| `Then(params IEvent[])` | Expects the decision to be accepted with exactly these events, in order (record equality). |
| `ThenRejected(string code)` | Expects a rejection with this error code. |
| `ThenNoOp()` | Expects no change. |
| `ThenState(Func<TAggregate, bool>, string description = "state")` | Applies the produced events to the state and checks the predicate. |

A failed expectation throws `SpecificationException` with a readable message, for example
`Expected events, but the decision was Rejected(account.not_open)`. Calling a `Then…` method before `When` throws too.

## Process manager specs

```csharp
public sealed class ProcessManagerSpec<TProcess, TId>
    where TProcess : class, IProcessManagerRuntime<TProcess, TId>
    where TId : struct, IStronglyTypedId<TId>
```

A process manager spec is created for one instance id. Each message goes through the generated `React`; the new state
is kept, and the instance stops reacting once it completes.

```csharp
[Fact]
public void A_transfer_withdraws_deposits_and_completes()
{
    var transfer = TransferId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5d");
    var spec = new ProcessManagerSpec<MoneyTransfer, TransferId>(transfer);

    spec.When(new TransferRequested(transfer, Account, Other, 5m))
        .ThenSends(new Withdraw(Account, 5m))
        .ThenSchedules(new TransferTimedOut(transfer), new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero));
    spec.When(new MoneyWithdrawn(Account, 5m, 5m)).ThenSends(new Deposit(Other, 5m));
    spec.When(new MoneyDeposited(Other, 5m, 5m)).ThenSends(new CompleteTransfer(transfer)).ThenCompleted();
    spec.When(new TransferTimedOut(transfer)).ThenIgnored();
}

[Fact]
public void A_timeout_while_depositing_compensates()
{
    var transfer = TransferId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5d");

    new ProcessManagerSpec<MoneyTransfer, TransferId>(transfer)
        .Given(new TransferRequested(transfer, Account, Other, 5m), new MoneyWithdrawn(Account, 5m, 5m))
        .WhenTimeAdvances(MoneyTransfer.Timeout)
        .When(new TransferTimedOut(transfer))
        .ThenSends(new Deposit(Account, 5m), new FailTransfer(transfer, "timeout"))
        .ThenCompleted();
}
```

| Member | Meaning |
|--------|---------|
| `new ProcessManagerSpec<TProcess, TId>(TId id)` | A spec for one instance. |
| `Given(params IMessage[])` | Messages applied in order before the one under test. |
| `WhenTimeAdvances(TimeSpan)` | Moves the reaction clock forward. The clock starts at `2026-01-01T00:00:00Z`. |
| `When(IMessage)` | Reacts to one message. |
| `ThenSends(params ICommand[])` | Expects exactly these commands, in order. |
| `ThenSchedules(IMessage, DateTimeOffset)` | Expects this message to be scheduled at this time. |
| `ThenCompleted()` | Expects the instance to be complete. |
| `ThenIgnored()` | Expects the message to be ignored. |
| `State`, `IsCompleted` | The current state and completion flag. |

## Projection specs

```csharp
public sealed class ProjectionSpec<TProjection, TDocument, TKey>
    where TProjection : class, IDocumentProjectionRuntime<TProjection, TDocument, TKey>, new()
    where TDocument : class
    where TKey : notnull
```

`Given(stream, events)` applies events from one stream through the generated key selection and `Apply`. Stream
versions and global positions are assigned in order. A `When` method that returns `null` deletes the document.

```csharp
[Fact]
public void The_summary_tracks_the_balance()
{
    StreamId stream = StreamId.For(BankAccount.AggregateName, Account);

    new ProjectionSpec<AccountSummaryProjection, AccountSummary, AccountId>()
        .Given(stream, new AccountOpened(Account, "Ada", 10m, Now), new MoneyDeposited(Account, 5m, 15m))
        .Then(Account, new AccountSummary(Account, "Ada", 15m, AccountStatus.Open, 2))
        .Then(Other, null);                                         // no document for another account
}
```

| Member | Meaning |
|--------|---------|
| `Given(StreamId, params IEvent[])` | Applies events of one stream. Call it once per stream. |
| `Document(TKey)` | The document for a key, or `null`. Use it with your own assertions. |
| `Then(TKey, TDocument?)` | Expects the document to equal this one (record equality), or to be absent when `null`. |

## Workflow specs

Workflows written in the SourceFlow workflow DSL (`*.sfflow.yaml`) are compiled to process managers. `WorkflowSpec`
drives a generated workflow with messages given by CloudEvents type name and JSON data, so a spec reads like the path
it tests. SourceFlow Studio generates these specs, one per path of a minimal path cover; see
[`samples/TravelBooking/TravelBooking.Tests/Workflows/TripBookingV1PathTests.cs`](../../../samples/TravelBooking/TravelBooking.Tests/Workflows/TripBookingV1PathTests.cs),
the six specs of the TravelBooking sample's trip-booking workflow.

```csharp
using SourceFlow.Testing;
using SourceFlow.Workflows;

/// <summary>P02 hold-room: success → await-guest: success → confirm-reservation: rejected hotels.hold_lost ⇒ ManualIntervention</summary>
[Fact]
public void P02_ManualIntervention()
{
    var spec = WorkflowSpec.For<Bookings.TripBookingWorkflowV1, Bookings.BookingsModule>();
    spec.Start("bookings.booking-requested.v1", """{"bookingId":"bdcbea6c-2dc6-58e2-8f01-846c12116012","hotelId":"71905a65-5f08-5214-94c5-0c0cdfe13aac","guest":"sample-guest","nights":1}""")
        .ThenSends("hotels.reserve-room.v1");
    spec.Succeed("hotels.room-reserved.v1", """{"hotelId":"71905a65-5f08-5214-94c5-0c0cdfe13aac","bookingId":"bdcbea6c-2dc6-58e2-8f01-846c12116012","nights":1}""")
        .ThenSends();
    spec.Succeed("bookings.guest-confirmed.v1", """{"bookingId":"bdcbea6c-2dc6-58e2-8f01-846c12116012"}""")
        .ThenSends("hotels.confirm-reservation.v1");
    spec.Reject("hotels.hold_lost")
        .ThenSends();
    spec.ThenStatus(WorkflowStatus.ManualIntervention);
}
```

`WorkflowSpec.For<TWorkflow, TModule>()` builds the runtime model of `TModule`, so message types are read with the
module's own JSON metadata, as the runtime does. The module must register every message the workflow sends or awaits,
including the other services' contracts (the TravelBooking `BookingsModule` registers the Hotels contract).

| Member | Meaning |
|--------|---------|
| `Start(type, json)` | The start message. It must correlate to an instance (`TryCorrelate`). |
| `Succeed(type, json)`, `Fail(type, json)` | An awaited success event or a failure event arrives. |
| `Reject(code, message = null)` | The last command sent is rejected with this code. The workflow must handle rejections of that command (a `rejected:` failure on the step). |
| `FireTimer()` | The pending timer fires (a timeout, or the end of a retry delay), and the clock moves to its due time. |
| `Control(action, step = null, reason = null, compensate = false)` | An operator action (`WorkflowControl.ResumeAction`, `RetryStepAction`, `AbortAction`). |
| `ThenSends(params string[] types)` | Expects the last message to have sent exactly these command types (`{name}.v{version}`), in order. `ThenSends()` with no arguments expects nothing sent. |
| `ThenStatus(WorkflowStatus)` | Expects `Running`, `Compensating`, `ManualIntervention`, `Completed`, `Compensated` or `Aborted`. |
| `Status()`, `State`, `Now`, `Sent`, `LastReaction` | The current status, state, simulated time, every command sent with its message id, and the last reaction. |

Regenerate the specs from Studio after changing the workflow; the generated file says so in its header. The
[workflows guide](../guides/workflows.md) walks through the TravelBooking sample's trip-booking workflow and its specs.

## The in-memory test host

`SourceFlowTestHost` runs one SourceFlow service with the real runtime, an `InMemoryStore` and an `InMemoryBroker`. It
starts every hosted service (relay, receivers, subscriptions, monitor) and gives you a deterministic wait instead of
sleeps.

```csharp
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using SourceFlow.Query;
using SourceFlow.Testing;

[Fact]
public async Task A_low_balance_account_is_found_by_the_domain_query()
{
    await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
    {
        sf.ServiceName = "accounts";
        sf.AddModule<AccountsModule>();
    });
    var low = AccountId.New();
    var high = AccountId.New();
    await host.SendAsync(new OpenAccount(low, "Ada", 20m));
    await host.SendAsync(new OpenAccount(high, "Grace", 900m));
    await host.WaitForIdleAsync();

    IQueryService queries = host.Services.GetRequiredService<IQueryService>();
    IReadOnlyList<LowBalanceAccount> rows = await queries.ExecuteAsync(new LowBalanceAccounts(Below: 100m));

    rows.ShouldBe([new LowBalanceAccount(low, "Ada", 20m)]);
}
```

(From `tests/Core/SourceFlow.Core.Tests/Query/DomainQueryTests.cs`, UT-QL-90.)

`StartAsync` configures `UseInMemoryStorage`, `UseInMemoryTransport` and a 50 ms `PollInterval`, then runs your
`configure` callback, so anything you set there wins. The container is built with `ValidateOnBuild` and
`ValidateScopes`.

```csharp
public static Task<SourceFlowTestHost> StartAsync(
    Action<SourceFlowBuilder> configure,
    TimeProvider? time = null,
    InMemoryBroker? broker = null,
    InMemoryStore? store = null,
    Action<IServiceCollection>? services = null,
    CancellationToken cancellationToken = default);
```

| Member | Meaning |
|--------|---------|
| `SendAsync(command, options, ct)` | Sends a command through `ICommandSender` and returns the `CommandResult`. |
| `WaitForIdleAsync(TimeSpan? timeout = null)` | Waits until no outbox row is due, the broker has no active messages, nothing is being received, no snapshot is pending, and every subscription has caught up, three checks in a row. Default timeout 30 s; on timeout a `TimeoutException` describes what was still busy. |
| `WaitForIdleAsync(IReadOnlyList<SourceFlowTestHost>, TimeSpan?)` | The same across several hosts that share a broker. |
| `DocumentAsync<TDocument>(collection, id, tenant = null)` | Reads a document from a collection. |
| `DocumentAsync<TProjection, TDocument>(id, tenant = null)` | Reads a projection's document from the generation that serves reads. |
| `EventsAsync(StreamId, tenant = null)` | A stream's events, decrypted, deserialised and upcast. |
| `Services`, `Commands`, `Runtime`, `DeadLetters` | The container, the command sender, the runtime context and the dead-letter manager. |
| `Store`, `Broker`, `Time` | The in-memory store, the (possibly shared) broker and the clock. |

Dispose the host (`await using`) to stop its hosted services.

### Several services on one broker

Pass the same `InMemoryBroker` to each host. This is how the end-to-end tests run the Bank domain ([`tests/Fixtures/Bank`](../../../tests/Fixtures/Bank)) as two services
([`tests/EndToEnd/SourceFlow.EndToEnd.Tests/BankSystem.cs`](../../../tests/EndToEnd/SourceFlow.EndToEnd.Tests/BankSystem.cs)):

```csharp
var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero));
var broker = new InMemoryBroker(time);

SourceFlowTestHost accounts = await SourceFlowTestHost.StartAsync(sf =>
{
    sf.ServiceName = "accounts";
    sf.AddModule<ContractsModule>();
    sf.AddModule<AccountsModule>();
    sf.AddModule<TransfersModule>();
    sf.Routing(r => r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events"));
    sf.Projections(p => p.Add<AccountSummaryProjection>().Inline());
}, time, broker);

SourceFlowTestHost notifications = await SourceFlowTestHost.StartAsync(sf =>
{
    sf.ServiceName = "notifications";
    sf.AddModule<ContractsModule>();
    sf.AddModule<NotificationsModule>();
    sf.Routing(r => r.Subscribe("accounts.events"));
}, time, broker);

await accounts.SendAsync(new OpenAccount(AccountId.New(), "Ada", 100m));
await SourceFlowTestHost.WaitForIdleAsync([accounts, notifications]);
```

### Sharing a store across restarts

Pass the same `InMemoryStore` to a second host to test what happens after a restart or a new deployment: the second
host sees the first host's events, checkpoints and read-model aliases. The blue/green generation tests do this to start
version 2 of a projection beside version 1.

## Controlling time

Pass a `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`) as `time`. The host registers it as the
service's `TimeProvider`, and the store and broker it creates use it too, so `ctx.Now`, scheduled messages, timers,
delayed retries and retention all follow your clock:

```csharp
var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
{
    sf.ServiceName = "accounts";
    sf.AddModule<AccountsModule>();
}, time: time);

await host.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10m));
await host.WaitForIdleAsync();

time.Advance(TimeSpan.FromDays(1));   // due timers and scheduled messages become deliverable
await host.WaitForIdleAsync();
```

If you pass your own store or broker, create them with the same clock (`new InMemoryStore(time)`,
`new InMemoryBroker(time)`). `WaitForIdleAsync` measures its own timeout on the system clock, so a fake clock never
makes it wait forever.

The pure specs have their own clocks: `AggregateSpec.At`, `ProcessManagerSpec.WhenTimeAdvances` and
`WorkflowSpec.FireTimer`.

## Approved-file snapshots

`Approvals.AssertMatches(received, name)` compares text with `Approved/<name>.approved.txt` next to the calling test
file. On a mismatch it writes `<name>.received.txt` and throws `ApprovalMismatchException`, whose message shows the first
differing line. Line endings are normalised. Set `SOURCEFLOW_APPROVE=1` to accept every received file, then review the
diff before committing.

```csharp
using SourceFlow.Transport;

[Fact]
public async Task The_topology_export_is_stable()
{
    await using SourceFlowTestHost host = await SourceFlowTestHost.StartAsync(sf =>
    {
        sf.ServiceName = "accounts";
        sf.AddModule<AccountsModule>();
    });

    string json = TopologyExport.ToJson(host.Runtime.TopologyDocument());
    Approvals.AssertMatches(json, "accounts-topology");
}
```

SourceFlow's own tests use it for the public API (`SourceFlow.Net.PublicApi.approved.txt`), generator output and CLI
output.

## Fault injection

The runtime calls `IFaultInjector.InjectAsync(point, ct)` at named points. Without an injector this is a null check.
`ChaosInjector` is an injector for chaos tests: register one per node and arm it.

| Fault point (`FaultPoints`) | Value |
|-----------------------------|-------|
| `SessionBeforeCommit` | `session.beforeCommit` |
| `SessionAfterCommitBeforeAck` | `session.afterCommitBeforeAck` |
| `ReactionBeforeCommit` | `reaction.beforeCommit` |
| `RelayAfterPublishBeforeDelete` | `relay.afterPublishBeforeDelete` |
| `SubscriptionAfterApplyBeforeCommit` | `subscription.afterApplyBeforeCommit` |
| `LeaseBeforeRenew` | `lease.beforeRenew` |
| `ReceiverBeforeComplete` | `receiver.beforeComplete` |

```csharp
using SourceFlow.Runtime;      // IFaultInjector, FaultPoints

var faults = new ChaosInjector().CrashAt(FaultPoints.SessionAfterCommitBeforeAck);
await using SourceFlowTestHost node = await SourceFlowTestHost.StartAsync(
    sf => { sf.ServiceName = "accounts"; sf.AddModule<AccountsModule>(); },
    services: s => s.AddSingleton<IFaultInjector>(faults));

// The command commits, then the node "dies" before the caller gets the result.
await Should.ThrowAsync<NodeCrashedException>(async () => await node.SendAsync(new OpenAccount(AccountId.New(), "Ada", 10m)));
(await faults.Triggered).ShouldBe(FaultPoints.SessionAfterCommitBeforeAck);
faults.IsDead.ShouldBeTrue();    // from now on it writes nothing; its leases expire as after a kill
```

In a real chaos test you then start a second node on the same store (pass the first host's `Store`) and check that
nothing was lost, and that a retry with the same idempotency key is absorbed.

- `CrashAt(point, hit = 1)`: at the n-th hit the call throws `NodeCrashedException`, and every later point blocks until
  the node stops.
- `FreezeAt(point, hit = 1)`: the node stalls at the n-th hit until `Resume()`, as under SIGSTOP, then continues with
  whatever it held, which fencing must reject.
- `Triggered`, `IsDead` and `Hits(point)` let the test observe it.

The chaos suite CH-01..10 (`tests/EndToEnd/SourceFlow.EndToEnd.Tests/ChaosScenarios.cs`) uses it against PostgreSQL.

## Conformance kits

The conformance kits define what "a storage adapter" and "a transport" mean. Each is a list of cases with stable ids;
an adapter is done only when every case it can run passes. Run each case against a fresh fixture.

### Storage conformance (SCK)

Implement `IStorageFixture` for your store (one fresh, isolated store per fixture: every storage port, plus a clock)
and run `StorageConformance.Cases`. The in-memory runner in
[`tests/Core/SourceFlow.Core.Tests/Storage/InMemoryStorageConformanceTests.cs`](../../../tests/Core/SourceFlow.Core.Tests/Storage/InMemoryStorageConformanceTests.cs)
is the template:

```csharp
public sealed class MyStoreConformanceTests
{
    public static TheoryData<string> CaseIds => new(StorageConformance.Cases.Select(c => c.Id));

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task Sck(string id)
    {
        ConformanceCase<IStorageFixture> @case = StorageConformance.Cases.Single(c => c.Id == id);
        await using var fixture = new MyStoreFixture();          // implements IStorageFixture
        Assert.SkipWhen(@case.RequiresClockControl && !fixture.SupportsClockControl, "needs clock control");

        await @case.Run(fixture, TestContext.Current.CancellationToken);
    }
}
```

The cases cover appends and optimistic concurrency, stream reads, the gap-free global log, atomic sessions (outbox,
inbox, documents, process state, checkpoints), leases and fencing, the outbox head-of-key rule, inbox deduplication,
dead letters, snapshots, retention and read-model aliases. The in-memory, PostgreSQL (with and without row-level
security) and SQL Server stores run them. `PersonalDataKeyStoreConformance.RunAsync(store, now, ct)` checks an
`IPersonalDataKeyStore` the same way. Details per adapter are in
[Architecture/05 Event store and persistence](Architecture/05-Event-Store-and-Persistence.md).

### Transport conformance (TCK)

Implement `ITransportFixture` (the transport, a unique name prefix, receive timeouts, optional clock control, optional
`ITransportFaults` for outages, throttling, credential expiry and duplicates, and optional `InjectDefectAsync` for
topology validation) and run `TransportConformance.Cases` (TCK-01..45). Cases that need clock control or fault
injection are marked (`RequiresClockControl`, `RequiresFaultInjection`) and skipped on fixtures that cannot drive them,
never weakened. `TopologyFixtures` holds the named topologies (`minimal`, `full`, `ordered`, `filtered`, `publisher`,
`subscriber`) that the kit and the infrastructure-as-code modules in `deploy/topology` are tested with.

How the AWS, Azure and Google Cloud transports run the TCK on emulators and real accounts is in
[Cloud integration testing](Cloud-Integration-Testing.md).

`Check` (`True`, `Equal`, `SequenceEqual`, `Null`, `NotNull`, `Throws`, `ThrowsAsync`) is the small assertion helper the
kits use; it throws `ConformanceException`.

## API reference

| Type | Namespace | Purpose |
|------|-----------|---------|
| `AggregateSpec<TAggregate, TId>` | `SourceFlow.Testing` | Given/When/Then for an aggregate. |
| `ProcessManagerSpec<TProcess, TId>` | `SourceFlow.Testing` | Given/When/Then for a process manager instance. |
| `ProjectionSpec<TProjection, TDocument, TKey>` | `SourceFlow.Testing` | Given/Then for a document projection. |
| `WorkflowSpec`, `WorkflowSpec<TWorkflow>` | `SourceFlow.Testing` | Path specs for a generated DSL workflow. |
| `SpecificationException` | `SourceFlow.Testing` | A failed spec expectation. |
| `SourceFlowTestHost` | `SourceFlow.Testing` | One service in memory, with deterministic waits. |
| `Approvals`, `ApprovalMismatchException` | `SourceFlow.Testing` | Approved-file snapshots. |
| `ChaosInjector`, `NodeCrashedException` | `SourceFlow.Testing` | Crash and freeze a node at a fault point. |
| `StorageConformance`, `IStorageFixture` | `SourceFlow.Testing.Conformance` | The storage conformance kit. |
| `TransportConformance`, `ITransportFixture`, `ITransportFaults` | `SourceFlow.Testing.Conformance` | The transport conformance kit. |
| `PersonalDataKeyStoreConformance` | `SourceFlow.Testing.Conformance` | Key store checks for crypto-shredding. |
| `TopologyFixtures`, `TopologyFixture` | `SourceFlow.Testing.Conformance` | Named topologies for transports and IaC. |
| `ConformanceCase<TFixture>`, `Check`, `ConformanceException` | `SourceFlow.Testing.Conformance` | Case records and assertions. |

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `SpecificationException: Call When(command) first.` | A `Then…` method ran before `When` | Call `When` (or `When(message)`) first. |
| `Expected [...] but got [...]` with events that look equal | An event has a member without value equality (a list or array) | Use immutable collections with value equality in events, or assert with `ThenState`. |
| `'x.y.v1' is not a registered message type.` in a workflow spec | The module passed to `WorkflowSpec.For` does not register that message | Register the other services' contract modules in the owning module. |
| `... doesn't handle rejections of ...` from `Reject` | The workflow has no `rejected:` failure for the last command | Add the failure to the workflow, or deliver a failure event instead. |
| `TimeoutException: SourceFlow did not become idle within 00:00:30: ...` | Work is still pending: a message keeps failing and retrying, a subscription is stalled, or a timer is due in the future on a fake clock | Read the description in the message; check `host.DeadLetters`; advance the fake clock. |
| `InvalidOperationException` at `StartAsync` mentioning `SF0018` | A domain query does not bind against the catalog | Fix the query text or its result record (ADR-030). |
| A projection document is `null` in a host test | The projection is asynchronous and the test did not wait | Call `WaitForIdleAsync` before reading, or register it `.Inline()`. |
| An approval test fails on another machine with only line-ending differences | Should not happen: line endings are normalised | Check for trailing whitespace or encoding differences in the received file. |

## Links

- [SourceFlow.Net README](SourceFlow.Net-README.md): aggregates, process managers, projections.
- [Cloud integration testing](Cloud-Integration-Testing.md): the TCK on emulators and real clouds.
- [Architecture/05 Event store and persistence](Architecture/05-Event-Store-and-Persistence.md): what the SCK protects.
- [Architecture/09 Process managers and workflows](Architecture/09-Process-Managers-and-Workflows.md).
- [SourceFlow.Studio README](SourceFlow.Studio-README.md): generating workflow specs.
- Design: [doc 11 Testing and quality](../Design/11-Testing-and-Quality.md),
  [doc 15 Workflow DSL and Studio](../Design/15-Workflow-DSL-and-Studio.md).
- ADRs: [ADR-014](../ADRs/ADR-014-transport-spi-and-conformance-kit-parity-required.md),
  [ADR-033](../ADRs/ADR-033-final-transport-spi-pull-receivers-and-the-in-memory-reference-broker.md).
- Guides: [workflows](../guides/workflows.md), [OpenTelemetry (testing your telemetry)](../guides/opentelemetry.md#testing-your-telemetry).
- Sample: [`samples/TravelBooking/TravelBooking.Tests`](../../../samples/TravelBooking/TravelBooking.Tests).
