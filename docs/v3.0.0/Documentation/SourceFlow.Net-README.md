# SourceFlow.Net

`SourceFlow.Net` is the single core package of SourceFlow.Net v3, a .NET 10 framework for event-sourced, message-driven services built with DDD and CQRS. It contains the domain contracts you write against (aggregates as pure deciders, commands, events, process managers, projections), the runtime that executes them (command processor, outbox relay, receive endpoint, subscription engine, process-manager runner), an in-memory store and broker, telemetry and health checks, and the extensibility interfaces that storage, cloud and security adapters implement. The source generator and analyzers that wire your domain at compile time ship inside the same package.

---

## Contents

- [What changed from v2](#what-changed-from-v2)
- [Installation](#installation)
- [Quick start](#quick-start)
- [The domain programming model](#the-domain-programming-model)
  - [Messages](#messages)
  - [Message type names and versioning](#message-type-names-and-versioning)
  - [Strongly typed ids](#strongly-typed-ids)
  - [Aggregates](#aggregates)
  - [Decisions and rejections](#decisions-and-rejections)
  - [DecisionContext](#decisioncontext)
  - [Member attributes](#member-attributes)
- [Command handlers](#command-handlers)
- [Process managers](#process-managers)
- [Event handlers](#event-handlers)
- [Projections](#projections)
- [Modules and the source generator](#modules-and-the-source-generator)
- [Analyzer diagnostics](#analyzer-diagnostics)
- [Routing](#routing)
- [Sending commands](#sending-commands)
- [Middleware and error classification](#middleware-and-error-classification)
- [The SourceFlow builder](#the-sourceflow-builder)
- [Options](#options)
- [In-memory adapters](#in-memory-adapters)
- [Telemetry](#telemetry)
- [Health checks](#health-checks)
- [Time and testing hooks](#time-and-testing-hooks)
- [Troubleshooting](#troubleshooting)
- [Related documentation](#related-documentation)

---

## What changed from v2

v3 is a redesign, not an upgrade. v2 stored commands and the current state of entities; v3 stores **events** and rebuilds state from them ([ADR-002](../ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md)).

| v2 | v3 |
|----|----|
| `IEntity` with an `int` id, persisted whole | An aggregate state `record`, rebuilt from its event stream, with a strongly typed id |
| `Aggregate<T>` façade that sends commands | Removed. Callers use `ICommandSender` |
| `Saga<T>` + `IHandles<C>` (handle, mutate, persist, raise) | Pure `Decide` methods on the aggregate, or an `ICommandHandler<TCommand, TAggregate>` when I/O is needed |
| Events raised through reflection | `Decision.Accept(new SomethingHappened(...))`, type-checked |
| `View<T>` + `IProjectOn<E>` | Projections: document, session (EF Core) or custom; inline or async; checkpointed and rebuildable |
| `Command<TPayload>` + `EntityRef` + `Metadata` | A `sealed record` implementing `ICommand<TAggregate>`; metadata lives in the envelope |
| Reflection and assembly scanning | A Roslyn source generator; no reflection in dispatch or registration |
| Several middleware abstractions, opt-in "enhanced" pipeline | One `IMessageMiddleware`; inbox, outbox, retries and dead-lettering are always on |
| Exceptions for business failures | `Decision.Reject(Error)`: rejections are values with stable codes |
| Multi-target (netstandard2.0 to net10.0) | `net10.0` only |

Existing v2 services migrate data and wire traffic with the `migrate v2` tooling; see [Migrating a v2 service](../guides/migrating-from-v2.md).

---

## Installation

```bash
dotnet add package SourceFlow.Net
```

Every v3 package targets **`net10.0` only**. The generator inside the package is a Roslyn compiler plugin (it targets `netstandard2.0` as all compiler plugins must) and is added to your build automatically from the package's `analyzers/dotnet/cs` folder. There is nothing else to install for code generation.

A typical service adds one storage adapter and, when it talks to other services, one cloud transport:

```bash
dotnet add package SourceFlow.Storage.PostgreSQL     # or SourceFlow.Storage.SqlServer
dotnet add package SourceFlow.Cloud.Aws              # or SourceFlow.Cloud.Azure / SourceFlow.Cloud.Gcp
dotnet add package SourceFlow.AspNetCore             # MapCommand<T>() endpoints and the query API
dotnet add package SourceFlow.Testing                # in test projects
```

Domain assemblies reference only `SourceFlow.Net` and use only the `SourceFlow` and `SourceFlow.Workflows` namespaces. This keeps them free of infrastructure without a separate abstractions package ([ADR-027](../ADRs/ADR-027-single-core-package-with-extensibility-spis-and-categorised-adapters.md)).

To see what the generator writes, add this to the project file and look under `obj/<Configuration>/net10.0/generated/`:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

---

## Quick start

The example is the Accounts module of the Bank domain the tests use ([tests/Fixtures/Bank/Bank.Accounts](../../../tests/Fixtures/Bank/Bank.Accounts)), trimmed down. For a complete application, see the [TravelBooking sample](../../../samples/TravelBooking/README.md).

### 1. Id, commands and events

```csharp
using System.Text.Json.Serialization;
using SourceFlow;

namespace Bank.Accounts;

[StronglyTypedId]
public readonly partial record struct AccountId(Guid Value);

[MessageType("bank.account.open", 1)]
public sealed record OpenAccount(AccountId AccountId, string Holder, decimal InitialDeposit) : ICommand<BankAccount>;

[MessageType("bank.account.withdraw", 1)]
public sealed record Withdraw(AccountId AccountId, decimal Amount) : ICommand<BankAccount>;

[MessageType("bank.account.opened", 1)]
public sealed record AccountOpened(AccountId AccountId, [property: PersonalData] string Holder, decimal InitialDeposit, DateTimeOffset OpenedAt) : IEvent;

[MessageType("bank.account.withdrawn", 1)]
public sealed record MoneyWithdrawn(AccountId AccountId, decimal Amount, decimal BalanceAfter) : IEvent;
```

### 2. The aggregate: pure `Decide` and `When`

```csharp
public enum AccountStatus { None = 0, Open = 1, Closed = 2 }

[Aggregate("bank-account")]
public sealed partial record BankAccount : IAggregate<BankAccount, AccountId>
{
    public static BankAccount Initial { get; } = new();

    public AccountStatus Status { get; init; }
    public decimal Balance { get; init; }

    public Decision Decide(OpenAccount c, DecisionContext ctx)
    {
        return Status switch
        {
            AccountStatus.None when c.InitialDeposit <= 0 => Decision.Reject(AccountErrors.InitialDepositRequired),
            AccountStatus.None => Decision.Accept(new AccountOpened(c.AccountId, c.Holder, c.InitialDeposit, ctx.Now)),
            _ => Decision.Reject(AccountErrors.AlreadyOpen),
        };
    }

    public Decision Decide(Withdraw c, DecisionContext ctx)
    {
        return this switch
        {
            { Status: not AccountStatus.Open } => Decision.Reject(AccountErrors.NotOpen),
            _ when c.Amount > Balance => Decision.Reject(AccountErrors.InsufficientFunds),
            _ => Decision.Accept(new MoneyWithdrawn(c.AccountId, c.Amount, Balance - c.Amount)),
        };
    }

    private BankAccount When(AccountOpened e) => this with { Status = AccountStatus.Open, Balance = e.InitialDeposit };

    private BankAccount When(MoneyWithdrawn e) => this with { Balance = e.BalanceAfter };
}

public static class AccountErrors
{
    public static readonly Error InitialDepositRequired = new("account.initial_deposit_required", "An initial deposit is required.");
    public static readonly Error AlreadyOpen = new("account.already_open", "The account is already open.");
    public static readonly Error NotOpen = new("account.not_open", "The account is not open.");
    public static readonly Error InsufficientFunds = new("account.insufficient_funds", "Insufficient funds.");
}
```

### 3. A projection

```csharp
public sealed record AccountSummary(AccountId Id, string Holder, decimal Balance, [property: Queryable] AccountStatus Status, long Version);

[Projection("account-summary")]
public sealed partial class AccountSummaryProjection : IDocumentProjection<AccountSummary, AccountId>
{
    private static AccountSummary When(AccountOpened e, AccountSummary? current, EventContext ctx)
        => new(e.AccountId, e.Holder, e.InitialDeposit, AccountStatus.Open, ctx.StreamVersion.Value);

    private static AccountSummary? When(MoneyWithdrawn e, AccountSummary? current, EventContext ctx)
        => current is null ? null : current with { Balance = e.BalanceAfter, Version = ctx.StreamVersion.Value };
}
```

### 4. The module and its JSON context

The generator fills in the module's registration. The JSON context lists every message, document and state type, so serialization needs no reflection (analyzer SF0010 checks it and offers a code fix).

```csharp
[SourceFlowModule(typeof(AccountsJsonContext))]
public sealed partial class AccountsModule;

[JsonSerializable(typeof(OpenAccount))]
[JsonSerializable(typeof(Withdraw))]
[JsonSerializable(typeof(AccountOpened))]
[JsonSerializable(typeof(MoneyWithdrawn))]
[JsonSerializable(typeof(AccountSummary))]
[JsonSerializable(typeof(BankAccount))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class AccountsJsonContext : JsonSerializerContext;
```

### 5. Host it and send commands

This console host uses the in-memory store and broker. `Host.CreateApplicationBuilder` comes from the `Microsoft.Extensions.Hosting` package (a Web SDK project has it already).

```csharp
using Bank.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SourceFlow;
using SourceFlow.Hosting;
using SourceFlow.Query;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "accounts";
    sf.AddModule<AccountsModule>();
    sf.Projections(p => p.Add<AccountSummaryProjection>().Inline());   // read-your-writes
    sf.UseInMemoryStorage();
    sf.UseInMemoryTransport();
});

using IHost host = builder.Build();
await host.StartAsync();

ICommandSender commands = host.Services.GetRequiredService<ICommandSender>();
AccountId id = AccountId.New();

CommandResult opened = await commands.SendAsync(new OpenAccount(id, "Ada", 100m));
Console.WriteLine($"{opened.Status} at version {opened.Version.Value}, token {opened.Token}");   // Accepted at version 1

CommandResult withdrawn = await commands.SendAsync(new Withdraw(id, 500m));
Console.WriteLine($"{withdrawn.Status}: {withdrawn.Error?.Code}");                                // Rejected: account.insufficient_funds

IAggregateReader aggregates = host.Services.GetRequiredService<IAggregateReader>();
AggregateSnapshot<BankAccount> account = await aggregates.LoadAsync<BankAccount, AccountId>(id);
Console.WriteLine($"Balance {account.State.Balance} at version {account.Version.Value}");

await host.StopAsync();
```

For production, replace the in-memory adapters with a storage package (`sf.UsePostgreSql(connectionString)`) and a cloud transport. The TravelBooking sample's Bookings service ([Program.cs](../../../samples/TravelBooking/Bookings.Api/Program.cs), with [ServiceHost.cs](../../../samples/TravelBooking/Shared/ServiceHost.cs)) shows a complete ASP.NET Core host with PostgreSQL, a cloud transport, OpenTelemetry, health checks, `MapCommand<T>()` endpoints and the query API.

---

## The domain programming model

Design rationale: [Design 03 — Domain Programming Model](../Design/03-Domain-Programming-Model.md), [ADR-003](../ADRs/ADR-003-aggregates-as-pure-deciders-decide-evolve.md), [ADR-005](../ADRs/ADR-005-logical-message-type-names-and-registry.md), [ADR-015](../ADRs/ADR-015-strongly-typed-caller-assigned-ids-uuidv7-default.md), [ADR-018](../ADRs/ADR-018-business-rejections-are-values-not-exceptions.md).

### Messages

Messages are immutable `sealed record`s. Each implements one marker interface:

| Interface | Kind (`MessageKind`) | Meaning |
|-----------|----------------------|---------|
| `ICommand<TAggregate>` | `Command` | A request to change exactly one aggregate of type `TAggregate` |
| `IEvent` | `Event` | A domain event: an internal fact appended to an aggregate's stream |
| `IIntegrationEvent` | `IntegrationEvent` | A public, versioned contract published to other services |
| `ITimeout` | `Timeout` | A message a process manager schedules to itself (a durable timer) |
| `CommandRejected<TCommand>` | `Reply` | The reply a process manager receives when a command it sent was rejected |

Message metadata (id, correlation, causation, time, tenant, actor, trace context) is never a member of the record. It travels in the [`MessageEnvelope`](Architecture/04-Messaging-Pipeline-and-Middleware.md#the-envelope).

A command targets its aggregate through the one member whose type is the aggregate's id type. When a command has several members of that type, mark the target with `[AggregateId]` (otherwise analyzer SF0003 reports an error).

```csharp
[MessageType("bank.transfer.request", 1)]
public sealed record RequestTransfer(TransferId TransferId, AccountId From, AccountId To, decimal Amount) : ICommand<Transfer>;
```

Here `Transfer` is keyed by `TransferId`, so `TransferId` is the target; `From` and `To` are plain data.

### Message type names and versioning

`[MessageType(name, version)]` gives every message a stable logical name ([ADR-005](../ADRs/ADR-005-logical-message-type-names-and-registry.md)):

- The name uses lowercase letters, digits, `.`, `-` and `_`, does not start or end with a dot, and has no empty segment, for example `bank.account.deposited`.
- The version starts at 1 (the default).
- A `(name, version)` pair is unique across every module in the deployment. Duplicates in one compilation are SF0002; duplicates across assemblies fail startup.
- On the wire the CloudEvents `type` is `{name}.v{version}`, for example `bank.account.deposited.v1`.

The names form a frozen **allow-list**: an envelope whose `(type, version)` is not registered is never deserialized. It is parked as a dead letter with reason `UnknownMessageType`.

**Upcasting.** A breaking change to an event needs a new version plus an `IUpcaster<TFrom, TTo>` from each version to the next. Keep the old record (renamed) with its original `[MessageType]`:

```csharp
[MessageType("bank.account.opened", 1)]
public sealed record AccountOpenedV1(AccountId AccountId, string Holder, decimal InitialDeposit, DateTimeOffset OpenedAt) : IEvent;

[MessageType("bank.account.opened", 2)]
public sealed record AccountOpened(AccountId AccountId, string GivenName, string FamilyName, decimal InitialDeposit, DateTimeOffset OpenedAt) : IEvent;

public sealed class AccountOpenedV1ToV2 : IUpcaster<AccountOpenedV1, AccountOpened>
{
    public AccountOpened Upcast(AccountOpenedV1 old)
    {
        string[] parts = old.Holder.Split(' ', 2);
        return new(old.AccountId, parts[0], parts.Length > 1 ? parts[1] : string.Empty, old.InitialDeposit, old.OpenedAt);
    }
}
```

The generator registers upcasters it finds in the module's assembly (they need a public parameterless constructor). Rules enforced at startup:

- An upcaster goes from one version of a name to the next (`v1 → v2`, `v2 → v3`); chains are applied in order.
- An event with versions `n` and `n + 1` registered but no upcaster between them fails startup.
- Upcasting applies to events **read from storage** (aggregate loads, subscriptions, `IAggregateReader`). Messages received from other services are not upcast: integration contracts version through new types.

### Strongly typed ids

Mark a `readonly partial record struct` with a single positional `Value` parameter:

```csharp
[StronglyTypedId]                                            // Guid backing (default)
public readonly partial record struct AccountId(Guid Value);

[StronglyTypedId(Backing = IdBacking.String, MaxLength = 32)]
public readonly partial record struct Iban(string Value);

[StronglyTypedId(Backing = IdBacking.Int64)]
public readonly partial record struct LegacyNumber(long Value);
```

| `StronglyTypedIdAttribute` property | Default | Meaning |
|-------------------------------------|---------|---------|
| `Backing` | `IdBacking.Guid` | `Guid`, `String` or `Int64`; must match the `Value` parameter type |
| `MaxLength` | `64` | Maximum length of a `String`-backed id |

The generator implements `IStronglyTypedId<TSelf>` and emits:

| Member | Notes |
|--------|-------|
| `New()` and `New(TimeProvider)` | Guid ids only; UUIDv7 (time-ordered). Use `New(TimeProvider)` when tests control the clock |
| `Parse`, `TryParse` | The default value (`Guid.Empty`, `0`, empty string) does not parse |
| `IsDefault` | True for the default value, which is never a valid target |
| `ToStreamSegment()` | The id as it appears in a stream id: Guid `N` format, the number, or the string |
| `ToString()`, `ISpanFormattable`, `IComparable<T>` | Guid ids format as `D` |
| `SourceFlowJsonConverter` | A nested JSON converter, registered by the generated module; malformed values raise `JsonException` |

String-backed ids cannot contain a dash, whitespace or control characters (the generated `IsValid` checks this), because the stream id is `{aggregate}-{segment}` and the segment is the text after the last dash.

A command whose target id is the default value is rejected before anything is loaded, with error code `sourceflow.invalid_command`.

### Aggregates

An aggregate is a `sealed partial record` marked `[Aggregate(name)]` that implements `IAggregate<TSelf, TId>`:

- `static TSelf Initial { get; }` is the state before the first event.
- `Decide(TCommand c, DecisionContext ctx)` methods return a `Decision`. They are pure and synchronous: no I/O, no clock or random calls (use `ctx.Now`). Analyzers SF0007 and SF0009 enforce this.
- `When(TEvent e)` methods return the next state (`this with { ... }`). They are pure and never assign fields or properties (SF0004). They may be private.
- Properties use `init`, never `set` (SF0008).

| `AggregateAttribute` property | Default | Meaning |
|-------------------------------|---------|---------|
| `Name` (constructor) | — | The stream type name; stream ids are `{Name}-{id segment}` (at most 200 characters) |
| `SnapshotEvery` | `0` | Write a snapshot every N events; `0` disables snapshots |
| `SnapshotVersion` | `1` | Bump to invalidate stored snapshots when `When` logic changes without a shape change |

The generator adds `IAggregateRuntime<TSelf, TId>`: the aggregate name, the decided and targeting command lists, a `Decide` switch over command types, `TargetOf` (the target id of a command) and an `Evolve` switch over the `When` methods. `Evolve` ignores events it has no `When` for, so a stream may carry audit-only events; SF0005 warns when a `Decide` produces an event the aggregate has no `When` for.

Aggregates are never DI services and are never mutated. Because `Decide` is pure, the runtime can run it again on fresh state after a concurrency conflict ([ADR-008](../ADRs/ADR-008-optimistic-concurrency-with-automatic-re-decide.md)).

### Decisions and rejections

`Decision` is a value with one of three kinds (`DecisionKind`):

| Factory | Kind | Effect |
|---------|------|--------|
| `Decision.Accept(event)`, `Decision.Accept(e1, e2, ...)` | `Accepted` | The events are appended atomically. At least one event is required |
| `Decision.Reject(Error)` | `Rejected` | Nothing is appended; the caller gets `CommandStatus.Rejected` and the error |
| `Decision.NoOp` | `NoOp` | Nothing changes; the command is already satisfied (idempotent) |

Business failures are values, not exceptions ([ADR-018](../ADRs/ADR-018-business-rejections-are-values-not-exceptions.md)). An `Error` has a stable dot-separated `Code`, a human-readable `Message` and optional `Details`. Keep errors as static fields so codes stay stable; the code is what callers, HTTP problem details and process managers branch on.

The runtime itself uses three codes:

| Code | When | Status |
|------|------|--------|
| `sourceflow.invalid_command` | The target id is the default value (`Error.InvalidCommand(...)`) | `Rejected` |
| `sourceflow.version_mismatch` | `SendOptions.ExpectedVersion` does not match the stream (`Error.VersionMismatch`) | `Conflict` |
| `sourceflow.concurrency_conflict` | Re-decides after concurrency conflicts were exhausted (`Error.ConcurrencyConflict`) | `Conflict` |

### DecisionContext

Every `Decide` (and `ICommandHandler`) receives a `DecisionContext`, fixed for the whole decision:

| Member | Meaning |
|--------|---------|
| `Now` | The decision time, from the registered `TimeProvider`. Recorded as the event time |
| `Version` | The stream version the state was loaded at; `StreamVersion.None` (0) for a new stream |
| `IsNew` | True when the stream does not exist yet |
| `CommandId` | The command's message id; a natural idempotency key for external calls |
| `CorrelationId` | The conversation the command belongs to |
| `Headers` | User headers from the envelope |

### Member attributes

| Attribute | Applies to | Effect |
|-----------|------------|--------|
| `[PersonalData]` (optional `Subject = nameof(Member)`) | Members of messages and documents | Classified as `SourceFlowTaxonomy.PersonalData`: redacted in logs and telemetry, and encrypted per subject when personal-data encryption is on. Without `Subject`, the event's stream is the subject. See the [Security and Compliance Guide](Security-and-Compliance-Guide.md) |
| `[Secret]` | Members | Classified as `SourceFlowTaxonomy.Secret`: always redacted |
| `[Queryable]` | Members of events and documents | Indexed and filterable in SFQL. No effect on `[PersonalData]` members (SF0017). See [Query side and SFQL](Architecture/08-Query-Side-and-SFQL.md) |
| `[RenamedFrom("old", Version = n)]` | Members | Keeps queries stable when a member is renamed between versions |
| `[Tag("name")]` | Members | Declares a tag for dynamic consistency boundaries ([ADR-021](../ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md), planned for 3.1) |
| `[AggregateId]` | Command members | Picks the target id when a command has several members of the id type |
| `[Emits(typeof(E))]`, `[Rejects("code")]` | `Decide` methods, handlers | Declares outcomes static analysis cannot see, so the service manifest is exact (SF0019) |

On positional records, target the property: `[property: PersonalData] string Holder`.

---

## Command handlers

When a decision needs I/O (a rate lookup, a payment gateway), implement `ICommandHandler<TCommand, TAggregate>` instead of a `Decide` method. From the OrderFulfilment services the tests use ([Payments.cs](../../../tests/Fixtures/OrderFulfilment/Payments/Payments.cs)):

```csharp
public sealed class ChargeHandler(IPaymentGateway gateway) : ICommandHandler<Charge, Payment>
{
    public async ValueTask<Decision> HandleAsync(Charge command, Payment state, DecisionContext context, CancellationToken cancellationToken)
    {
        if (state.PaymentId is not null)
        {
            return Decision.NoOp;   // captured already
        }

        if (command.Amount <= 0)
        {
            return Decision.Reject(new Error(PaymentErrors.InvalidAmount, "The amount must be positive."));
        }

        GatewayOutcome outcome = await gateway.CaptureAsync(command.OrderId.Value, command.Amount, context.CommandId.ToString(), cancellationToken);
        return outcome switch
        {
            GatewayOutcome.Approved => Decision.Accept(new PaymentCaptured(context.CommandId.Value, command.OrderId, command.Amount)),
            GatewayOutcome.FraudSuspected => Decision.Reject(new Error(PaymentErrors.FraudSuspected, "The payment gateway suspects fraud.")),
            _ => Decision.Reject(new Error(PaymentErrors.Declined, "The card was declined.")),
        };
    }
}
```

- The generator registers the handler; the runtime adds it to DI as **scoped** (`TryAddScoped`) and resolves it from a per-message scope, so constructor dependencies have correct lifetimes.
- A command has either a `Decide` method or one handler, never both. Two handlers for one command fail startup.
- A handler can run more than once for one command (after a concurrency conflict, or when a received command is redelivered). Make external calls idempotent, for example with `context.CommandId` as the key, as above.
- Do not read through the query side in a handler (SF0016 warns): decisions on eventually consistent reads are a design smell.

---

## Process managers

A process manager coordinates several aggregates or services over time: it reacts to events, sends commands, schedules durable timers and keeps its own state. It is a `sealed partial record` marked `[ProcessManager(name)]` that implements `IProcessManager<TSelf, TId>` with pure `React` methods returning a `Reaction`. From the Bank domain the tests use ([Transfers.cs](../../../tests/Fixtures/Bank/Bank.Transfers/Transfers.cs)):

```csharp
[ProcessManager("money-transfer")]
public sealed partial record MoneyTransfer : IProcessManager<MoneyTransfer, TransferId>
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    public static MoneyTransfer Initial { get; } = new();

    public TransferStage Stage { get; init; }
    public AccountId From { get; init; }
    public AccountId To { get; init; }
    public decimal Amount { get; init; }

    public static TransferId CorrelateBy(TransferRequested e) => e.TransferId;   // starting events name the instance

    public Reaction React(TransferRequested e, ReactionContext<TransferId> ctx)
        => Reaction.Send(new Withdraw(e.From, e.Amount))
            .Schedule(new TransferTimedOut(e.TransferId), ctx.Now + Timeout)
            .AndBecome(this with { Stage = TransferStage.Withdrawing, From = e.From, To = e.To, Amount = e.Amount });

    public Reaction React(MoneyWithdrawn e, ReactionContext<TransferId> ctx)
        => Stage == TransferStage.Withdrawing
            ? Reaction.Send(new Deposit(To, Amount)).AndBecome(this with { Stage = TransferStage.Depositing })
            : Reaction.Ignore;

    public Reaction React(CommandRejected<Withdraw> r, ReactionContext<TransferId> ctx)
        => Stage == TransferStage.Withdrawing
            ? Reaction.Send(new FailTransfer(ctx.Id, r.Error.Code)).AndComplete()
            : Reaction.Ignore;

    // ... React(MoneyDeposited), React(TransferTimedOut) with compensation
}
```

| `Reaction` member | Meaning |
|-------------------|---------|
| `Reaction.Send(command)`, `Reaction.Send(command, messageId)` | Send a command (through the outbox); a given `MessageId` makes re-sends idempotent at the owner |
| `Reaction.Become(state)` | Move to a new state (must be the same type) |
| `Reaction.Complete()` | Complete the instance; later messages for it are ignored |
| `Reaction.Ignore` | Do nothing |
| `.AndSend(...)`, `.Schedule(message, deliverAt)`, `.AndBecome(...)`, `.AndComplete()` | Chain further effects |

`ReactionContext<TId>` carries the instance id (`Id`), the reaction time (`Now`), the message id and `IsNew`. Messages correlate through a static `CorrelateBy` method (returning the id type or a `string` that parses to it) or, by default, through the envelope correlation id, which the runtime sets to the instance id on every command and timer the instance sends.

A process manager receives `CommandRejected<TCommand>` when a command it sent is rejected: always for commands owned by the same service, and for remote commands when the route uses `ReplyOnReject()`. List `CommandRejected<TCommand>` in the module's JSON context (SF0010 reports it otherwise).

Process managers can also be generated from a YAML workflow (`*.sfflow.yaml`) and designed in SourceFlow Studio. Runtime semantics, persistence, timers and workflows are covered in [Process Managers and Workflows](Architecture/09-Process-Managers-and-Workflows.md) and the [workflows guide](../guides/workflows.md).

---

## Event handlers

`IEventHandler<TEvent>` runs a side effect for an event. It is resolved from a per-message DI scope and runs **at least once**, so make it idempotent with `EventContext.EventId`. From the Notifications module of the Bank domain the tests use ([`tests/Fixtures/Bank/Bank.Notifications`](../../../tests/Fixtures/Bank/Bank.Notifications)):

```csharp
public sealed class AccountOpenedHandler(ICommandSender commands) : IEventHandler<AccountOpenedV1>
{
    public async ValueTask HandleAsync(AccountOpenedV1 @event, EventContext context, CancellationToken cancellationToken)
    {
        await commands.SendAsync(
            new SendWelcome(new CustomerId(@event.AccountId), @event.OpenedAt),
            new SendOptions { IdempotencyKey = "welcome:" + context.EventId },   // effectively once
            cancellationToken);
    }
}
```

Handlers of local domain events (`IEvent`) run in a subscription over the event log; handlers of messages that arrive from the broker (integration events, timeouts) run in the receive endpoint with their own inbox entry. See [Event Flow](Architecture/03-Event-Flow.md).

---

## Projections

A projection turns events into read models. Two shapes are generated:

- **Document projections**: `IDocumentProjection<TDocument, TKey>` with `When(TEvent e, TDocument? current, EventContext ctx)` methods that return the new document, or `null` to delete it. The key comes from a `KeyOf(e)` method, a member of the key type, or the source stream's aggregate id. See the quick start.
- **Session projections**: `ISessionProjection<TUnitOfWork>` with `When(TEvent e, TUnitOfWork unitOfWork, EventContext context, CancellationToken ct)` methods, for example writing EF Core entities through `SourceFlow.Storage.EntityFrameworkCore`.

`[Projection(name, Version = n)]` names the projection; bumping `Version` builds a new read-model generation blue/green and switches reads once it has caught up.

Projections run **async** (in a subscription, eventually consistent) unless configured **inline** (in the command transaction, read-your-writes, same database required):

```csharp
sf.Projections(p =>
{
    p.Add<AccountSummaryProjection>().Inline();
    p.Add<BranchTotalsProjection>().Async().OnFailure(SubscriptionFailurePolicy.Park);
    p.Remove<OldSummaryProjection>();   // leave out a projection a module registers
});
```

Async projections default to `SubscriptionFailurePolicy.Stop`: a failing event halts its partition (with backoff) until it is fixed or skipped. Details: [Projections and Read Models](Architecture/06-Projections-and-Read-Models.md).

---

## Modules and the source generator

Mark one `partial class` per assembly with `[SourceFlowModule(typeof(YourJsonContext))]` and add it to the host with `sf.AddModule<TModule>()`. There is no assembly scanning: the module registers exactly what the generator found at compile time ([ADR-004](../ADRs/ADR-004-source-generation-instead-of-reflection.md)).

| You write | Generated |
|-----------|-----------|
| `[StronglyTypedId] readonly partial record struct` | `New()`, parsing, formatting, `ToStreamSegment()`, a JSON converter |
| `[Aggregate] sealed partial record ... : IAggregate<,>` | `IAggregateRuntime<,>`: name, command lists, `Decide` and `Evolve` switches, `TargetOf` |
| `[ProcessManager] sealed partial record ... : IProcessManager<,>` | `IProcessManagerRuntime<,>`: handled messages, `TryCorrelate`, a `React` switch |
| `[Projection] partial class ... : IDocumentProjection<,>` | `IDocumentProjectionRuntime<,,>`: handled events, `TryGetKey`, `FormatKey`, an `Apply` switch |
| `[Projection] partial class ... : ISessionProjection<>` | `ISessionProjectionRuntime<,>`: handled events, an `ApplyAsync` switch |
| `[DomainQuery] partial record ... : IDomainQuery<TResult>` | `IDomainQueryRuntime<,>`: parameters, result columns, materialization |
| `*.sfflow.yaml` workflow files in the project | A `[ProcessManager("name@v")]` record that runs the workflow engine |
| `[SourceFlowModule] partial class` | `ISourceFlowModule.Register`: the JSON context, id converters, every message (with its name and version), `CommandRejected<T>` replies, upcasters, aggregates, command handlers, domain queries, process managers, projections and event handlers |
| (every compilation) | `[assembly: SourceFlowManifest(json)]`: the static service manifest (aggregates, commands with what they emit and reject, handled events, process managers, projections) used by SourceFlow Studio and `dotnet sourceflow manifest export` |

All generated dispatch is a `switch` over types, sorted for deterministic output. Walk through real output in [Reading generated code](../guides/reading-generated-code.md).

**Contracts-only modules.** A service that only *sends* another service's command needs the command's type registered, but not the aggregate that decides it. Implement `ISourceFlowModule` by hand, as the TravelBooking sample does for the Hotels contract, which the Bookings
service registers so its workflow can send the Hotels commands and read the Hotels events
([Hotels.cs](../../../samples/TravelBooking/Hotels/Hotels.cs)):

```csharp
public sealed class HotelsContractsModule : ISourceFlowModule
{
    public static string ModuleName => nameof(HotelsContractsModule);

    public static void Register(ISourceFlowModuleBuilder builder)
    {
        builder.AddMessage<ReserveRoom>("hotels.reserve-room", 1);
        builder.AddMessage<ReleaseRoom>("hotels.release-room", 1);
        builder.AddMessage<ConfirmReservation>("hotels.confirm-reservation", 1);
        builder.AddMessage<RoomReserved>("hotels.room-reserved", 1);
        builder.AddMessage<RoomReleased>("hotels.room-released", 1);
        builder.AddMessage<ReservationConfirmed>("hotels.reservation-confirmed", 1);
        builder.AddJsonTypeInfoResolver(HotelsJsonContext.Default);
        builder.AddJsonConverter(new HotelId.SourceFlowJsonConverter());
        builder.AddJsonConverter(new BookingId.SourceFlowJsonConverter());
    }
}
```

Adding the same module twice is harmless; it is registered once.

---

## Analyzer diagnostics

The analyzers run in the IDE and the build. Ids are stable and never reused (SF0012 is not assigned).

| Id | Severity | Meaning |
|----|----------|---------|
| SF0001 | Error | A type implements a SourceFlow message interface but has no `[MessageType]` |
| SF0002 | Error | Two types declare the same message name and version |
| SF0003 | Error | A command's target id member is missing or ambiguous; give it exactly one member of the id type or mark one `[AggregateId]` |
| SF0004 | Error | A `When` method has side effects (it is async or assigns state); return `this with { ... }` |
| SF0005 | Warning | `Decide` produces an event the aggregate has no `When` for; the state ignores it |
| SF0006 | Warning | A command targets an aggregate but has no `Decide` and no `ICommandHandler`; add one or route it (startup fails otherwise) |
| SF0007 | Error | `Decide` is not synchronous; use an `ICommandHandler` for I/O |
| SF0008 | Error | An aggregate or process manager has the wrong shape: it must be a top-level `sealed partial record` with `init` properties |
| SF0009 | Warning | A non-deterministic API (`DateTime.Now`/`UtcNow`/`Today`, `DateTimeOffset.Now`/`UtcNow`, `Guid.NewGuid()`, `Random`, `Environment.TickCount`) is used in `Decide`, `When` or `React`; use the context |
| SF0010 | Error | A message, state, document or `CommandRejected<T>` type is missing from the module's JSON context. Code fix: add `[JsonSerializable(typeof(T))]` |
| SF0011 | Error | `[PersonalData(Subject = "...")]` names no member of the event |
| SF0013 | Warning | `Publish<T>().AsIs()` publishes an event that holds `[PersonalData]`; map it to an integration event instead |
| SF0014 | Error | A command is handled in this compilation and also routed with `Command<T>().To(...)` |
| SF0015 | Error | `IQueryService` or `IAggregateReader` is used in `Decide`, `When`, `React` or projection code |
| SF0016 | Warning | A command handler reads through the query side (eventually consistent decisions) |
| SF0017 | Warning | `[Queryable]` on a `[PersonalData]` member has no effect (ciphertext cannot be indexed) |
| SF0018 | Error | A `[DomainQuery]` is not valid (shape at build time; binding against the catalog fails startup) |
| SF0019 | Warning | Static analysis cannot list the events or rejection codes of a `Decide`; declare `[Emits]` / `[Rejects]` |
| SF0020 | Error | A workflow (`*.sfflow.yaml`) fails DSL validation; reported at the YAML line |
| SF0021 | Error | A workflow `$hook` has no partial method implementation |

SF0014 only sees routes written with constant expressions in the same compilation, and ignores aggregates from referenced assemblies (another service's contracts). Startup validation always catches the rule.

---

## Routing

A command type is either **owned** (it has a `Decide` or a handler in this service) or **routed** to the one remote service that owns it, never both and never neither ([ADR-007](../ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md)). Domain events leave a service only through explicit publications, and other services' events arrive through the service's own subscriptions.

```csharp
sf.Routing(r =>
{
    r.Endpoint("accounts");                                    // input endpoint (default: the service name)
    r.OrderedEndpoint();                                       // per-partition-key order across instances

    r.Command<RequestStatement>().To("notifications");         // owned by another service
    r.Command<Charge>().To("payments").ReplyOnReject();        // the owner replies CommandRejected<Charge>

    r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events")   // map to a public contract
        .OrderedBy(e => e.AccountId);                          // ordering key (default: the stream id)
    r.Publish<AccountClosed>().AsIs().To("accounts.audit");    // publish unchanged (SF0013 if it holds personal data)
    r.OrderedTopic("accounts.events");                         // per-key order for every subscriber

    r.Subscribe("payments.events", s => s.Only<PaymentCaptured>());
    r.Subscribe("orders.events", s => s.Where("type LIKE 'orders.%' AND sftenant = 'eu'"));
});
```

| Method | Meaning |
|--------|---------|
| `Endpoint(name)` | The service's input queue for commands, timers, replies and subscribed events. Defaults to `ServiceName` |
| `OrderedEndpoint()` | Messages with the same partition key are delivered in order across instances (FIFO queues, sessions, ordering keys). Order inside one instance is always kept |
| `Command<T>().To(endpoint)` | Routes a command to its remote owner's endpoint |
| `.ReplyOnReject()` | The owner replies `CommandRejected<T>` to this service when it rejects the command (process managers react to it). Call after `To` |
| `Publish<TEvent>().As<TContract>(map).To(topic)` | Maps a domain event to an `IIntegrationEvent` inside the command transaction and writes it to the outbox |
| `Publish<TEvent>().AsIs().To(topic)` | Publishes the domain event unchanged |
| `.OrderedBy(key)` | The partition key of the published message (default: the stream id) |
| `OrderedTopic(topic)` | Makes a topic this service publishes to ordered; declaring it for an unpublished topic fails startup |
| `Subscribe(topic, s => ...)` | This service's own subscription to a topic. `Only<T>()` restricts it to types; `Where(cesql)` filters with a CloudEvents SQL expression. Filters run on the broker where supported, otherwise on receipt |

Names are logical (`accounts.events`, not a URL or ARN; those are rejected). Transports map them to physical resources. Routing is validated against the runtime model when `AddSourceFlow` runs; topology is provisioned or validated when the host starts. See [Cloud Transports](Architecture/07-Cloud-Transports.md).

---

## Sending commands

`ICommandSender` is the single entry point for commands:

```csharp
public interface ICommandSender
{
    ValueTask<CommandResult> SendAsync<TCommand>(TCommand command, SendOptions? options = null, CancellationToken cancellationToken = default)
        where TCommand : class, ICommand;
}
```

| `SendOptions` | Meaning |
|---------------|---------|
| `IdempotencyKey` | Becomes the message id (a UUIDv5 of the service name and the key) and turns on the inbox: a repeat returns the original result with status `Duplicate` |
| `ExpectedVersion` | A caller-side optimistic check (for example from an HTTP `If-Match` header); a mismatch returns `Conflict` with `sourceflow.version_mismatch` |
| `CorrelationId` | The conversation this command belongs to (default: the command's own id) |
| `Headers` | User headers carried in the envelope: at most 8, each name and value at most 256 bytes UTF-8 |

The result is a `CommandResult(Status, Version, Error, Token)`:

| `CommandStatus` | Meaning | `IsSuccess` |
|-----------------|---------|-------------|
| `Accepted` | Events were appended; `Version` is the new stream version | yes |
| `NoOp` | The decision changed nothing | yes |
| `Rejected` | A business rejection; `Error` holds the code | no |
| `Conflict` | The expected version did not match, or concurrency retries were exhausted | no |
| `Duplicate` | A repeat of an already processed idempotency key; `Version`, `Token` and the error code come from the original | yes |
| `Dispatched` | The command was sent to its remote owner; the outcome is decided there | yes |

`result.ThrowIfNotAccepted()` returns the result when `IsSuccess` is true and throws `CommandFailedException` (with the result) otherwise. A `Duplicate` counts as a success even when the original request was rejected; its `Error` then carries the original error code.

**Consistency tokens.** `Token` is a `ConsistencyToken`: the global position of the last event written plus the tenant, in the string form `{commit}:{sequence}` or `{commit}:{sequence}@{tenant}`. Pass it to a query with `at_least(@token)` consistency to read your own writes from async read models. `ConsistencyToken.TryParse` reads it back.

**Over HTTP.** `SourceFlow.AspNetCore` maps a command to an endpoint in one line, `app.MapCommand<Deposit>("/accounts/{accountId}/deposits")`. It maps `Idempotency-Key` to `IdempotencyKey` and `If-Match` to `ExpectedVersion`, returns the stream version as an `ETag` and the token as a `SourceFlow-Consistency` header, and translates statuses: `Accepted`/`NoOp`/`Duplicate` to 200, `Dispatched` to 202, `Rejected` to 422 problem details, `Conflict` to 409 (412 for a failed `If-Match`). See the [SourceFlow.AspNetCore README](SourceFlow.AspNetCore-README.md).

The full path of a command, including concurrency and remote routing, is in [Command Flow](Architecture/02-Command-Flow.md).

---

## Middleware and error classification

One abstraction serves every stage ([ADR-012](../ADRs/ADR-012-one-middleware-abstraction-for-all-stages.md)):

```csharp
public interface IMessageMiddleware
{
    ValueTask InvokeAsync(MessageContext context, MessageDelegate next);
}
```

Register it with the stages it applies to, outermost first. The factory runs per message with the message's DI scope:

```csharp
sf.UseMiddleware(sp => new AuditMiddleware(sp.GetRequiredService<ILogger<AuditMiddleware>>()),
    PipelineStage.Send | PipelineStage.Receive,
    filter: ctx => ctx.Envelope.Type.StartsWith("bank.", StringComparison.Ordinal));
```

```csharp
public sealed class AuditMiddleware(ILogger<AuditMiddleware> logger) : IMessageMiddleware
{
    public async ValueTask InvokeAsync(MessageContext context, MessageDelegate next)
    {
        await next(context);   // skip the call to short-circuit
        logger.LogInformation("{Type} {Id}: {Outcome}", context.Envelope.Type, context.Envelope.MessageId, context.Outcome);
    }
}
```

`MessageContext` exposes the `Stage`, the `Envelope` (settable), the deserialized `Message` and `TypeInfo` once known, the per-message `Services`, `Outcome`, `Exception`, `Result` (for sends) and an `Items` bag. In 3.0.0 the runtime runs the pipeline for `Send` (a routed command), `Send | Handle` (a locally owned command) and `Receive` (every message from the transport). The stage and outcome model is described in [Messaging Pipeline and Middleware](Architecture/04-Messaging-Pipeline-and-Middleware.md).

**Errors.** Exceptions thrown while handling a received message are classified as transient (retried, then parked) or permanent (parked at once). Unknown exceptions are transient. Throw `PermanentException` from your code to park without retries, or change the rules:

```csharp
sf.Errors(e => e
    .Treat<GatewayDownException>(HandlingOutcome.Transient)
    .Treat<ContractViolationException>(HandlingOutcome.Permanent));
```

---

## Fluent configuration

Every builder method returns the builder, so a service and the features it uses are enabled in one chain. Each feature is
off until a call enables it:

```csharp
builder.Services.AddSourceFlow(sf => sf
    .WithServiceName("bookings")                       // required: endpoint, envelope source, inbox consumer
    .AddModule<BookingsModule>()                       // the generated module: aggregates, handlers, workflows, messages
    .Projections(p => p.Add<BookingSummaryProjection>().Inline())
    .Routing(r => r.Command<ReserveRoom>().To("hotels").ReplyOnReject())
    .UsePostgreSql(connectionString)                   // storage: or UseSqlServer, UseInMemoryStorage
    .UseAwsTransport(o => o.Region = "eu-west-1")      // broker: or UseAzureTransport, UseGcpTransport, UseInMemoryTransport
    .WithProvisioning(ProvisioningMode.Validate)       // create, validate or leave transport resources
    .WithRoles(SourceFlowRoles.All)                    // what this node runs besides the API
    .WithHealthChecks()                                // store, transport, outbox, subscriptions, leases, keys
    .Security(s => s.UseAwsKms(k => k.KeyId = "alias/bookings").EncryptPersonalData())
    .Configure(o => o.PollInterval = TimeSpan.FromMilliseconds(200))
    .When(configuration.GetValue<bool>("Features:QueryApi"), s => s.WithQueryApi()));   // SourceFlow.AspNetCore
```

- **`When(condition, configure)`** applies a step only when the condition holds, so features follow configuration without
  leaving the chain.
- **A service's own setup** is best written as an extension method that returns the builder, as the TravelBooking sample's
  `AddBookingsService()` and `UseInfrastructure(configuration)` do
  ([Bookings.cs](../../../samples/TravelBooking/Bookings/Bookings.cs), [ServiceHost.cs](../../../samples/TravelBooking/Shared/ServiceHost.cs)).
- **In ASP.NET Core**, `WithQueryApi(...)` enables the query API, and `app.MapSourceFlowHealthChecks()` maps `/health`,
  `/health/ready` and `/health/sourceflow` ([SourceFlow.AspNetCore](SourceFlow.AspNetCore-README.md)).
- **OpenTelemetry** stays with the OpenTelemetry SDK: `AddSource(SourceFlowTelemetry.ActivitySourceName)` and
  `AddMeter(SourceFlowTelemetry.MeterName)` ([Observability](Observability-and-Performance.md)).

## The SourceFlow builder

`services.AddSourceFlow(sf => ...)` composes a service. Modules, routing and projection configuration are validated when it runs; options and topology when the host starts.

| Member | Meaning |
|--------|---------|
| `WithServiceName(name)`, `ServiceName` | Required. Lowercase letters, digits and dashes, starting with a letter (max 63). Used as the envelope `source`, the default endpoint and the inbox consumer |
| `WithRoles(roles)`, `Roles` | `SourceFlowRoles`: `Api` (send only), `Receiver`, `Relay`, `Subscriptions`, or `All` (default). Lets API and worker instances scale separately |
| `WithProvisioning(mode)`, `Provisioning` | `ProvisioningMode` (`None`, `Validate`, `CreateIfMissing`). Default: `CreateIfMissing` in Development, with the in-memory transport or without a host environment; `Validate` otherwise |
| `WithHealthChecks(prefix)` | Adds the SourceFlow health checks (`{prefix}.ready`, `.store`, `.transport`, `.outbox`, `.subscriptions`, `.leases`, `.keys`; default prefix `sourceflow`) |
| `When(condition, configure)` | Applies `configure` only when `condition` is true |
| `AddModule<TModule>()` | Adds a generated (or hand-written) module |
| `Configure(o => ...)` | Sets `SourceFlowOptions` (below) |
| `Routing(r => ...)` | Routes, publications and subscriptions |
| `Projections(p => ...)` | `Add<T>().Inline()` / `.Async()` / `.OnFailure(policy)`, `Remove<T>()` |
| `UseMiddleware(factory, stages, filter)` | Adds middleware |
| `Errors(e => ...)` | Exception classification rules |
| `UseStorage(factory)`, `UseInMemoryStorage(store)` | The persistence slot. Storage packages add `UsePostgreSql`, `UseSqlServer`; EF Core read models add `UseEntityFrameworkProjections<TDbContext>` |
| `UseTransport(factory)`, `UseInMemoryTransport(broker)` | The broker slot (default: a private in-memory broker). Cloud packages add `UseAwsTransport`, `UseAzureTransport`, `UseGcpTransport` |
| `Security(s => ...)` | Key provider (`UseAwsKms`, `UseAzureKeyVault`, `UseGoogleKms`, `UseKeyProvider`), payload protection (`Protect`) and personal-data encryption (`EncryptPersonalData`) |
| `UseRedaction(r => ...)` | Redactors for the SourceFlow taxonomy (default: erase personal data and secrets) |
| `MigrateFromV2(v2 => ...)` | Registers the v2 data migration |
| `Services` | The underlying `IServiceCollection` |

Register `IActorAccessor` (who is acting, for audit) and `ITenantResolver` (the current tenant) in DI to override the defaults, which return null.

---

## Options

`sf.Configure(o => ...)` sets `SourceFlowOptions` (namespace `SourceFlow.Runtime`). Every option is honoured or rejected: validation runs when the host starts (`ValidateOnStart`) and fails with the list of problems.

| Option | Default | Validation | Meaning |
|--------|---------|------------|---------|
| `ServiceName` | from `sf.ServiceName` | required, `^[a-z][a-z0-9-]{0,62}$` | Set through the builder |
| `DefaultTenant` | `"-"` | required | Tenant when no `ITenantResolver` supplies one |
| `MaxConcurrencyRetries` | `5` | 0–50 | Re-decides after a concurrency conflict |
| `OutboxPartitions` | `16` | 1–1024 | Outbox partitions (ordering and relay parallelism) |
| `OutboxBatchSize` | `100` | 1–10,000 | Rows per outbox claim |
| `OutboxMaxAttempts` | `10` | 1–1000 | Publish attempts before an outbox row is parked |
| `PollInterval` | 1 s | 10 ms–10 min | Fallback wake-up for background loops without a commit notification |
| `LeaseTtl` | 30 s | 1 s–1 h, greater than `PollInterval` | Lease time-to-live for outbox and subscription partitions |
| `NodeHeartbeatInterval` | 10 s | — | Node heartbeat for lease fair-share; a node is live for three intervals |
| `SchemaRegistration` | `Enforce` | — | `Enforce`, `Warn` or `Off`: what startup does when a published schema changed without a new version |
| `LogPayloads` | `false` | — | Log the redacted payload of each received message that is parked (`Debug` level) |
| `ReceiveConcurrency` | `max(2, 2 × processors)` | 1–1024 | Concurrent receive workers |
| `PrefetchPerWorker` | `16` | 1–10,000 | Bounded channel size per worker |
| `ImmediateRetries` | `3` | 0–10 | In-process retries of a transient failure before a delayed retry |
| `DelayedRetries` | 10 s, 1 min, 10 min, 1 h | positive delays | Delayed retry schedule (a list you can edit); exhausting it parks the message |
| `SubscriptionBatchSize` | `1000` | 1–10,000 | Largest subscription batch (batches start at 100 and grow while full) |
| `SubscriptionPartitions` | `1` | 1–1024, power of two | Partitions of a subscription that has no stored count yet; override per projection with `Partitions(n)`. The first node stores the count, and a node configured otherwise refuses that subscription (doc 06 §3). |
| `SwitchLagThreshold` | `100` | 0–1,000,000 | A new read-model generation takes over once every partition is this close to the log |
| `RetainPreviousFor` | 7 days | 0–3650 days | How long a retired read-model generation is kept |
| `ReceiveLockDuration` | 30 s | 1 s–12 h | Message lock / visibility timeout; renewed at half this interval |
| `IngestBrokerDeadLetters` | `true` | — | Move messages from the broker's native DLQ into the dead-letter store |
| `DeadLetterIngestInterval` | 30 s | 100 ms–1 h | How often an empty native DLQ is checked |
| `MonitorInterval` | 15 s | 100 ms–1 h | Sampling interval for gauges and health checks |
| `AggregateCache` | `Off` | — | `Off`, `ReadTail` or `Optimistic` in-process aggregate state cache |
| `AggregateCacheSize` | `10000` | 1–10,000,000 | States the cache holds (LRU) |
| `SnapshotQueueCapacity` | `1024` | 1–1,000,000 | Pending background snapshot writes (oldest dropped when full) |
| `InboxRetention` | 14 days | 1 min–3650 days | How long processed inbox rows are kept for deduplication |
| `DeadLetterRetention` | 30 days | 1 min–3650 days | How long replayed or discarded dead letters are kept (parked ones are never purged) |
| `RetentionInterval` | 1 h | 1 s–7 days | How often the relay role runs retention cleanup |
| `RetentionBatchSize` | `5000` | 1–100,000 | Rows deleted per cleanup statement |
| `ShutdownTimeout` | 30 s | 0–10 min | How long shutdown waits for in-flight work |
| `NodeId` | `{machine}-{pid}-{random}` | required | The lease owner id of this process |
| `Health` | see below | each degraded threshold below its unhealthy one | Health check thresholds |
| `Telemetry.ParentBasedOnMessage` | `false` | — | Make the received message's `process` span a child of its `create` span instead of linking to it |

`Health` (`HealthThresholds`):

| Threshold | Default |
|-----------|---------|
| `StoreLatencyDegraded` | 250 ms |
| `TransportCacheDuration` | 30 s |
| `OutboxAgeDegraded` / `OutboxAgeUnhealthy` | 10 s / 5 min |
| `LagTimeDegraded` | 30 s |
| `HorizonAgeDegraded` | 30 s |
| `StalledUnhealthy` | 5 min |
| `UnderShareIntervals` | 2 |

```csharp
sf.Configure(o =>
{
    o.PollInterval = TimeSpan.FromMilliseconds(200);
    o.SubscriptionPartitions = 4;
    o.AggregateCache = AggregateCacheMode.ReadTail;
    o.DelayedRetries.Clear();
    o.DelayedRetries.Add(TimeSpan.FromSeconds(5));
    o.DelayedRetries.Add(TimeSpan.FromMinutes(5));
});
```

---

## In-memory adapters

The core ships complete in-memory adapters. They are the reference implementations the conformance kits compare every other adapter against, not mocks.

- **`InMemoryStore`** (`SourceFlow.Storage.InMemory`) implements every storage port: events, global log, sessions, outbox, inbox, checkpoints, leases, dead letters, snapshots, documents, notifications, retention, node registry, schemas, aliases, monitoring and personal-data keys. `sf.UseInMemoryStorage()` creates one; pass an instance to share it.
- **`InMemoryBroker`** (`SourceFlow.Transport.InMemory`) is a broker with CloudEvents binary wire format under a 10-attribute budget, competing consumers, locks, maximum delivery counts with a dead-letter queue, ordered endpoints, filtered topic fan-out and native delay. Pass one broker to several services' `sf.UseInMemoryTransport(broker)` to connect them in one process.

Both take an optional `TimeProvider`, and both expose fault injection (`InMemoryStore.Faults`, `InMemoryBroker.Faults`) for tests. Data does not survive the process.

---

## Telemetry

Telemetry is always on and uses the platform APIs only ([ADR-019](../ADRs/ADR-019-telemetry-always-on-with-standard-semantic-conventions.md)). SourceFlow publishes one `ActivitySource` and one `Meter`, both named `SourceFlow` (`SourceFlowTelemetry.ActivitySourceName`, `SourceFlowTelemetry.MeterName`); subscribe with any OpenTelemetry SDK:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(SourceFlowTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(SourceFlowTelemetry.MeterName))
    .UseOtlpExporter();
```

Spans include `handle {Command}`, `sourceflow.eventstore.load`, `sourceflow.eventstore.append`, `create {type}`, `send {destination}`, `process {endpoint}`, `project {subscription}` and `react {process}`. Instruments include `sourceflow.command.duration`, `sourceflow.eventstore.concurrency_conflicts`, `sourceflow.outbox.age`, `sourceflow.subscription.lag`, `sourceflow.deadletter.parked` and the standard `messaging.*` instruments. `SourceFlowTelemetry.Catalogue` lists every instrument. Log messages are source-generated (`LoggerMessage`); payloads are logged only when `LogPayloads` is on, and then with `[PersonalData]` and `[Secret]` members redacted.

See [Observability and Performance](Observability-and-Performance.md) and the [OpenTelemetry wiring guide](../guides/opentelemetry.md).

---

## Health checks

```csharp
builder.Services.AddHealthChecks().AddSourceFlow();   // prefix "sourceflow"

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });                       // liveness
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.ReadyTag) });
```

| Check | Tags | Reports |
|-------|------|---------|
| `sourceflow.ready` | `ready`, `sourceflow` | Startup completed (validation, provisioning, receivers started) |
| `sourceflow.store` | `ready`, `sourceflow` | The store answers with the expected schema; degraded when slower than `StoreLatencyDegraded` |
| `sourceflow.transport` | `ready`, `sourceflow` | The broker answers for the endpoint (cached); degraded when throttling |
| `sourceflow.outbox` | `sourceflow` | Age of the oldest due outbox message |
| `sourceflow.subscriptions` | `sourceflow` | Lag, stalled partitions and the log horizon |
| `sourceflow.leases` | `sourceflow` | The node owns its fair share of subscription leases |
| `sourceflow.keys` | `sourceflow` | The key service issues data keys (healthy when payload protection is off) |

Map liveness without dependency checks, so a database or broker outage does not restart the service.

---

## Time and testing hooks

- **`TimeProvider`.** SourceFlow registers `TimeProvider.System` only when no `TimeProvider` is registered. Register your own (for example `FakeTimeProvider`) to control decision time (`DecisionContext.Now`, event times), UUIDv7 message ids, timers and retention cutoffs. Infrastructure timing (poll fallback, re-decide backoff, lease heartbeats) deliberately uses the system clock, so a frozen test clock never stalls the runtime. After moving a fake clock, call `SourceFlowHost.Pulse()` to wake the background loops.
- **`IFaultInjector`.** Register an implementation to inject crashes or delays at the runtime's crash-sensitive points (`FaultPoints`: `session.beforeCommit`, `session.afterCommitBeforeAck`, `reaction.beforeCommit`, `relay.afterPublishBeforeDelete`, `subscription.afterApplyBeforeCommit`, `lease.beforeRenew`, `receiver.beforeComplete`). Without one, each point costs a null check.
- **`SourceFlow.Testing`.** `AggregateSpec`, `ProcessManagerSpec` and `ProjectionSpec` run the generated dispatch Given/When/Then style, and `SourceFlowTestHost` starts a service on the in-memory adapters:

```csharp
AccountId id = AccountId.New();
new AggregateSpec<BankAccount, AccountId>()
    .Given(new AccountOpened(id, "Ada", 100m, DateTimeOffset.UnixEpoch))
    .When(new Withdraw(id, 500m))
    .ThenRejected("account.insufficient_funds");
```

See the [SourceFlow.Testing README](SourceFlow.Testing-README.md).

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `SourceFlowBuilder.ServiceName is required.` | `sf.ServiceName` was not set | Set it; it must match `^[a-z][a-z0-9-]{0,62}$` |
| `No storage is configured. Call UseInMemoryStorage() or a storage package's Use* method.` | No storage slot | Add `UseInMemoryStorage()`, `UsePostgreSql(...)` or `UseSqlServer(...)` |
| `NoRouteException`: "has no local owner and no route" | A command was sent that this service neither decides nor routes | Add the owning module, or `r.Command<T>().To(endpoint)` |
| Startup: "is decided locally by ... and also routed" | A command is both owned and routed (SF0014) | Remove the route, or stop registering the owner's module in this service |
| Startup: "targets aggregate ... but has no Decide method, no ICommandHandler and no route" | SF0006 at runtime | Add a `Decide`, a handler or a route |
| Startup: "No JSON metadata for '...'" | A type is missing from the module's JSON context (SF0010) | Add `[JsonSerializable(typeof(T))]`; the IDE code fix does it |
| Startup: "Message type (...) is declared by both ..." | The same name and version in two assemblies | Give one a different name or version |
| Startup: "Command X has both a Decide method and the handler Y" | A command is decided twice | Keep either the `Decide` method or the `ICommandHandler` |
| Startup: "Projections ... share the name ..." | Two versions of one projection are registered | Leave the old one out with `p.Remove<TOld>()` |
| Startup: "has versions n and n+1 but no upcaster between them" | A new event version without an upcaster | Add an `IUpcaster<TOld, TNew>` in the module's assembly |
| Options validation errors at host start | An option is out of range, `LeaseTtl` is not above `PollInterval`, or `SubscriptionPartitions` is not a power of two | Fix the option named in the message |
| "Projection X is configured but not registered by any module." | `sf.Projections(p => p.Add<X>())` without the module | Add the module that contains the projection |
| A command is `Rejected` with `sourceflow.invalid_command` | Its target id is the default value | Create ids with `New()` or parse real values |
| A command returns `Conflict` with `sourceflow.concurrency_conflict` | Sustained contention on one stream beyond `MaxConcurrencyRetries` | Reduce contention (smaller aggregates, ordered endpoint per key) or raise the retry count |
| `MessageTooLargeException` when sending | A body exceeds the transport's `MaxBodyBytes` | Publish less (map to a slimmer integration event) |
| `ArgumentException` about headers | More than 8 headers, or one above 256 bytes | Trim the headers |
| "A middleware short-circuited the send without setting a result." | A `Send`/`Handle` middleware returned without calling `next` or setting `context.Result` | Set `context.Result` when short-circuiting a send |
| Received messages parked as `UnknownMessageType` | The receiver has not registered the message type (or that version) | Register the contract module in the receiving service |
| A process manager throws "became X; it must stay Y" | `Become` was given a different type | Return the same record type with `this with { ... }` |
| An async projection stops advancing; `sourceflow.subscription.stalled` is 1 | A poison event under the default `Stop` policy | Fix the projection and redeploy, or skip the event (`ISubscriptionAdmin.SkipAsync`, `dotnet sourceflow projection skip`) |

---

## Related documentation

- Architecture: [Overview](Architecture/01-Architecture-Overview.md), [Command Flow](Architecture/02-Command-Flow.md), [Event Flow](Architecture/03-Event-Flow.md), [Messaging Pipeline and Middleware](Architecture/04-Messaging-Pipeline-and-Middleware.md), [Event Store and Persistence](Architecture/05-Event-Store-and-Persistence.md), [Projections and Read Models](Architecture/06-Projections-and-Read-Models.md), [Process Managers and Workflows](Architecture/09-Process-Managers-and-Workflows.md)
- Packages: [PostgreSQL](SourceFlow.Storage.PostgreSQL-README.md), [SQL Server](SourceFlow.Storage.SqlServer-README.md), [EF Core](SourceFlow.Storage.EntityFrameworkCore-README.md), [AWS](SourceFlow.Cloud.Aws-README.md), [Azure](SourceFlow.Cloud.Azure-README.md), [Google Cloud](SourceFlow.Cloud.Gcp-README.md), [ASP.NET Core](SourceFlow.AspNetCore-README.md), [Testing](SourceFlow.Testing-README.md), [CLI](SourceFlow.Tool-README.md), [Studio](SourceFlow.Studio-README.md)
- Guides: [Message idempotency](Message-Idempotency-Guide.md), [Security and compliance](Security-and-Compliance-Guide.md), [Reading generated code](../guides/reading-generated-code.md), [Migrating from v2](../guides/migrating-from-v2.md), [Workflows](../guides/workflows.md)
- Design: [01 Architecture Overview](../Design/01-Architecture-Overview.md), [03 Domain Programming Model](../Design/03-Domain-Programming-Model.md), [04 Messaging Pipeline](../Design/04-Messaging-Pipeline.md)
- Sample: [TravelBooking](../../../samples/TravelBooking/README.md). Test fixtures: [Bank](../../../tests/Fixtures/Bank), [OrderFulfilment](../../../tests/Fixtures/OrderFulfilment)
