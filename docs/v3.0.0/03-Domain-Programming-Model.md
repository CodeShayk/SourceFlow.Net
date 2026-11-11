# 03 — Domain Programming Model

This document defines what application developers write, and what the source generators produce from it. Design drivers: [ADR-002](ADRs/ADR-002-event-sourcing-replaces-command-sourcing.md), [ADR-003](ADRs/ADR-003-aggregates-as-pure-deciders-decide-evolve.md), [ADR-004](ADRs/ADR-004-source-generation-instead-of-reflection.md), [ADR-005](ADRs/ADR-005-logical-message-type-names-and-registry.md), [ADR-015](ADRs/ADR-015-strongly-typed-caller-assigned-ids-uuidv7-default.md), [ADR-018](ADRs/ADR-018-business-rejections-are-values-not-exceptions.md) and [ADR-020](ADRs/ADR-020-process-managers-with-durable-state-and-timers.md).

## 1. Building blocks, v2 → v3

| v2 concept | v3 concept | Change |
|------------|------------|--------|
| `IEntity` (int `Id`), persisted whole | **Aggregate** state record, rebuilt from its event stream | State comes from events and isn't stored as the source of truth. Ids are strongly typed. |
| `Aggregate<T>` (façade that sends commands) | Removed. Callers use `ICommandSender`. | The façade added no behaviour. |
| `Saga<T>` + `IHandles<C>` (handle, mutate, persist, raise) | **Aggregate `Decide` methods** (pure), or an `ICommandHandler<C, TAggregate>` when dependencies are needed | Persistence and event raising belong to the runtime. |
| `IHandlesWithEvent<C,E>` (reflection-built event) | `Decision.Accept(new E(...))` | The event is built explicitly and type-checked. |
| (none) | **Process manager** | Durable, cross-aggregate coordination with timers ([ADR-020](ADRs/ADR-020-process-managers-with-durable-state-and-timers.md)) |
| `View<TViewModel>` + `IProjectOn<E>` | **Projection** (document, EF Core or custom), inline or async | Checkpointed, rebuildable, partitionable ([ADR-016](ADRs/ADR-016-projections-inline-or-async-with-partitioned-fenced.md)) |
| `Command<TPayload>` + `EntityRef` + `Metadata` | `record` implementing `ICommand<TAggregate>` with a typed id. Metadata moves to the envelope. | No payload wrapper or mutable metadata. |
| `Event<TEntity>` (payload = whole entity) | `record` implementing `IEvent` that carries only the facts that changed | Events are facts, not entity snapshots. |
| (none) | **Integration event** (`IIntegrationEvent`), the public contract | Keeps internal domain events private. |

## 2. Messages

Messages are immutable `sealed record`s with a stable logical name and version ([ADR-005](ADRs/ADR-005-logical-message-type-names-and-registry.md)).

```csharp
// Commands target exactly one aggregate type (or none, see §6.2)
[MessageType("bank.account.open", 1)]
public sealed record OpenAccount(AccountId AccountId, string Holder, decimal InitialDeposit) : ICommand<BankAccount>;

[MessageType("bank.account.deposit", 1)]
public sealed record Deposit(AccountId AccountId, decimal Amount) : ICommand<BankAccount>;

[MessageType("bank.account.withdraw", 1)]
public sealed record Withdraw(AccountId AccountId, decimal Amount) : ICommand<BankAccount>;

[MessageType("bank.account.close", 1)]
public sealed record CloseAccount(AccountId AccountId, string Reason) : ICommand<BankAccount>;

// Domain events: internal facts, stored in the stream
[MessageType("bank.account.opened", 1)]
public sealed record AccountOpened(AccountId AccountId, [PersonalData] string Holder,
                                   decimal InitialDeposit, DateTimeOffset OpenedAt) : IEvent;

[MessageType("bank.account.deposited", 1)]
public sealed record MoneyDeposited(AccountId AccountId, decimal Amount, decimal BalanceAfter) : IEvent;

[MessageType("bank.account.withdrawn", 1)]
public sealed record MoneyWithdrawn(AccountId AccountId, decimal Amount, decimal BalanceAfter) : IEvent;

[MessageType("bank.account.closed", 1)]
public sealed record AccountClosed(AccountId AccountId, string Reason, DateTimeOffset ClosedAt) : IEvent;
```

**Rules**

| Rule | Detail |
|------|--------|
| DM-R1 | The name is lowercase, dot-separated, and unique within the deployment's registry. The version starts at 1. |
| DM-R2 | Additive changes (a new optional member) keep the version. Removing, renaming or changing the meaning of a member needs a new version plus an upcaster (§2.1). |
| DM-R3 | A command targets an aggregate by having exactly one member of that aggregate's id type, or by marking one with `[AggregateId]` when there are several (SF0003). |
| DM-R4 | Message metadata (id, correlation, causation, time, tenant, trace) lives in the **envelope**, never in the record. |
| DM-R5 | Members must be serialisable by the System.Text.Json source generator (SF0010). |
| DM-R6 | Query and consistency metadata is declared on members: `[Queryable]` (indexed and filterable in SFQL), `[Tag("account")]` (DCB tag, [ADR-021](ADRs/ADR-021-dynamic-consistency-boundaries-dcb-in-3-1.md)), `[RenamedFrom("old", Version = n)]` (keeps queries stable across versions). The generator turns these into the schema catalog (doc 14 §2). |

### 2.1 Upcasting

```csharp
// v1 had "Name"; v2 splits it. Old events stay stored as v1 and are upcast on read.
[MessageType("bank.account.opened", 2)]
public sealed record AccountOpened(AccountId AccountId, [PersonalData] string GivenName,
                                   [PersonalData] string FamilyName, decimal InitialDeposit,
                                   DateTimeOffset OpenedAt) : IEvent;

public sealed class AccountOpenedV1ToV2 : IUpcaster<AccountOpenedV1, AccountOpened>
{
    public AccountOpened Upcast(AccountOpenedV1 old)
    {
        var (given, family) = NameParser.Split(old.Holder);
        return new(old.AccountId, given, family, old.InitialDeposit, old.OpenedAt);
    }
}
```

The old record type is kept, renamed and marked `[Obsolete]`: `[MessageType("bank.account.opened", 1)] sealed record AccountOpenedV1(...)`. The registry chains upcasters (v1→v2→v3) when reading **from storage**. Wire messages from other services are *not* upcast, because integration contracts version through new types instead.

## 3. Strongly typed ids

```csharp
[StronglyTypedId]                                     // default backing: Guid (UUIDv7)
public readonly partial record struct AccountId(Guid Value);

[StronglyTypedId(Backing = IdBacking.String, MaxLength = 64)]
public readonly partial record struct Iban(string Value);
```

The generator emits `New()` (using `Guid.CreateVersion7(TimeProvider)`), `Parse`/`TryParse`, `ISpanFormattable`/`IUtf8SpanFormattable`, a JSON converter registered in the generated JSON context, and `ToStreamSegment()`. Default values are rejected when a command is sent ([ADR-015](ADRs/ADR-015-strongly-typed-caller-assigned-ids-uuidv7-default.md), GAP-20).

## 4. Aggregates

```csharp
public enum AccountStatus { None, Open, Closed }

[Aggregate("bank-account", SnapshotEvery = 0)]          // 0 = no snapshots (the default)
public sealed partial record BankAccount : IAggregate<BankAccount, AccountId>
{
    public static BankAccount Initial { get; } = new();

    public AccountStatus Status { get; init; }
    public decimal Balance { get; init; }

    // ---------- Decide: pure, deterministic, no I/O ----------
    public Decision Decide(OpenAccount c, DecisionContext ctx) => Status switch
    {
        AccountStatus.None when c.InitialDeposit <= 0 => Decision.Reject(AccountErrors.InitialDepositRequired),
        AccountStatus.None                            => Decision.Accept(new AccountOpened(c.AccountId, c.Holder, c.InitialDeposit, ctx.Now)),
        _                                             => Decision.Reject(AccountErrors.AlreadyOpen),
    };

    public Decision Decide(Deposit c, DecisionContext ctx) => this switch
    {
        { Status: not AccountStatus.Open } => Decision.Reject(AccountErrors.NotOpen),
        _ when c.Amount <= 0               => Decision.Reject(AccountErrors.AmountMustBePositive),
        _                                  => Decision.Accept(new MoneyDeposited(c.AccountId, c.Amount, Balance + c.Amount)),
    };

    public Decision Decide(Withdraw c, DecisionContext ctx) => this switch
    {
        { Status: not AccountStatus.Open } => Decision.Reject(AccountErrors.NotOpen),
        _ when c.Amount > Balance          => Decision.Reject(AccountErrors.InsufficientFunds),
        _                                  => Decision.Accept(new MoneyWithdrawn(c.AccountId, c.Amount, Balance - c.Amount)),
    };

    public Decision Decide(CloseAccount c, DecisionContext ctx) => Status switch
    {
        AccountStatus.Closed => Decision.NoOp,                // already closed: idempotent, nothing appended
        AccountStatus.Open   => Decision.Accept(new AccountClosed(c.AccountId, c.Reason, ctx.Now)),
        _                    => Decision.Reject(AccountErrors.NotOpen),
    };

    // ---------- Evolve: pure state transitions ----------
    BankAccount When(AccountOpened e)  => this with { Status = AccountStatus.Open, Balance = e.InitialDeposit };
    BankAccount When(MoneyDeposited e) => this with { Balance = e.BalanceAfter };
    BankAccount When(MoneyWithdrawn e) => this with { Balance = e.BalanceAfter };
    BankAccount When(AccountClosed e)  => this with { Status = AccountStatus.Closed };
}

public static class AccountErrors
{
    public static readonly Error InitialDepositRequired = new("account.initial_deposit_required", "An initial deposit is required.");
    public static readonly Error AlreadyOpen            = new("account.already_open", "The account is already open.");
    public static readonly Error NotOpen                = new("account.not_open", "The account is not open.");
    public static readonly Error AmountMustBePositive   = new("account.amount_must_be_positive", "Amount must be positive.");
    public static readonly Error InsufficientFunds      = new("account.insufficient_funds", "Insufficient funds.");
}
```

### 4.1 Core types (namespace `SourceFlow` in `SourceFlow.Net`)

```csharp
public interface IAggregate<TSelf, TId> where TSelf : IAggregate<TSelf, TId> where TId : struct, IStronglyTypedId<TId>
{
    static abstract TSelf Initial { get; }
}

public readonly struct Decision
{
    public static Decision Accept(IEvent @event);
    public static Decision Accept(params ReadOnlySpan<IEvent> events);   // copied into an array
    public static Decision Reject(Error error);
    public static Decision NoOp { get; }
    public DecisionKind Kind { get; }                                    // Accepted | Rejected | NoOp
    public ReadOnlySpan<IEvent> Events { get; }
    public Error? Error { get; }
}

public readonly record struct DecisionContext(
    DateTimeOffset Now,            // from TimeProvider; fixed for the whole decision
    StreamVersion Version,         // StreamVersion.None when the stream does not exist
    MessageId CommandId,
    CorrelationId CorrelationId,
    IReadOnlyDictionary<string, string> Headers)
{
    public bool IsNew => Version == StreamVersion.None;
}

public sealed record Error(string Code, string Message, IReadOnlyDictionary<string, object?>? Details = null);
```

### 4.2 What the generator emits (illustrative)

```csharp
partial record BankAccount : IAggregateRuntime<BankAccount, AccountId>
{
    public const string AggregateName = "bank-account";

    // Evolve: unknown events are ignored (tolerant), so streams may carry audit-only events
    public BankAccount Evolve(IEvent e) => e switch
    {
        AccountOpened x  => When(x),
        MoneyDeposited x => When(x),
        MoneyWithdrawn x => When(x),
        AccountClosed x  => When(x),
        _                => this,
    };

    public static Decision Decide(BankAccount s, ICommand c, in DecisionContext ctx) => c switch
    {
        OpenAccount x  => s.Decide(x, ctx),
        Deposit x      => s.Decide(x, ctx),
        Withdraw x     => s.Decide(x, ctx),
        CloseAccount x => s.Decide(x, ctx),
        _              => throw new UnhandledCommandException(c.GetType()),   // unreachable: the registry routes only these
    };

    public static AccountId TargetOf(ICommand c) => c switch
    {
        OpenAccount x => x.AccountId, Deposit x => x.AccountId, Withdraw x => x.AccountId, CloseAccount x => x.AccountId,
        _ => throw new UnhandledCommandException(c.GetType()),
    };

    public static StreamId StreamOf(AccountId id) => StreamId.For(AggregateName, id);   // "bank-account-0193…"
}
```

The pattern-matching `switch` over sealed types compiles to type checks with no reflection or boxing ([ADR-004](ADRs/ADR-004-source-generation-instead-of-reflection.md)).

## 5. Command processing

### 5.1 Sending

```csharp
public interface ICommandSender
{
    ValueTask<CommandResult> SendAsync<TCommand>(TCommand command, SendOptions? options = null, CancellationToken ct = default)
        where TCommand : class, ICommand;
}

public sealed record SendOptions
{
    public string? IdempotencyKey { get; init; }       // becomes the MessageId (UUIDv5 of the key); enables the inbox
    public StreamVersion? ExpectedVersion { get; init; } // caller-side optimistic check (e.g. from If-Match)
    public CorrelationId? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public readonly record struct CommandResult(CommandStatus Status, StreamVersion Version, Error? Error,
                                           ConsistencyToken Token)   // position + bucket + tenant; read-your-writes (doc 14 §6)
{
    // Status: Accepted | NoOp | Rejected | Conflict | Duplicate | Dispatched (sent to a remote owner)
    public CommandResult ThrowIfNotAccepted();
}
```

With `SourceFlow.AspNetCore`, the endpoint below is one line: `app.MapCommand<Deposit>("/accounts/{accountId}/deposits")`. It binds the route and body to the command, maps `Idempotency-Key` → `IdempotencyKey` and `If-Match` → `ExpectedVersion`, returns `ETag` (stream version) and `SourceFlow-Consistency` (token) headers, and turns rejections into RFC 9457 ProblemDetails (422) and conflicts into 409/412. The hand-written equivalent:

ASP.NET Core usage:

```csharp
app.MapPost("/accounts/{id}/deposits", async (Guid id, DepositRequest body, HttpRequest http, ICommandSender sender, CancellationToken ct) =>
{
    var result = await sender.SendAsync(new Deposit(new AccountId(id), body.Amount),
        new SendOptions { IdempotencyKey = http.Headers["Idempotency-Key"] }, ct);

    return result.Status switch
    {
        CommandStatus.Accepted or CommandStatus.NoOp or CommandStatus.Duplicate
                                 => Results.Ok(new { version = result.Version.Value }),
        CommandStatus.Rejected   => Results.Problem(result.Error!.Message, statusCode: 422, title: result.Error.Code),
        CommandStatus.Conflict   => Results.Conflict(),
        _                        => Results.Accepted(),
    };
});
```

### 5.2 The command processor algorithm

```text
Process(command, envelope):
  route = Router.Resolve(command.Type)                      // local XOR remote (ADR-007)
  if route.IsRemote:
      if ambient StorageSession exists: Outbox.Add(envelope) // inside handler / PM transaction
      else: Transport.Send(envelope) with resilience pipeline // direct send from API
      return Dispatched

  id      = Generated.TargetOf(command); reject InvalidCommand if id is default
  stream  = Generated.StreamOf(id)
  for attempt in 0..MaxConcurrencyRetries:
      using session = Storage.BeginSession()                 // 1 DB transaction
      if envelope.HasInboxKey and session.Inbox.Seen(consumer, envelope.MessageId):
          return Duplicate(previous result stored in inbox row)
      (state, version) = AggregateStore.Load(stream)         // cache → snapshot → tail
      if options.ExpectedVersion is set and != version: return Conflict
      ctx      = new DecisionContext(time.GetUtcNow(), version, envelope.MessageId, …)
      decision = handler?.HandleAsync(...) ?? Generated.Decide(state, command, ctx)
      if decision.Rejected: record inbox(result) if keyed; commit; return Rejected
      if decision.NoOp:     record inbox(result) if keyed; commit; return NoOp
      events   = Envelope(decision.Events, causation = envelope)  // ids, metadata, trace
      session.Append(stream, expected = version, events)          // first write in tx (doc 05 §5)
      session.Outbox.Add(IntegrationMapper.Map(events))           // routed publications
      session.Inbox.Record(consumer, envelope.MessageId, result)  // if keyed
      session.Projections.ApplyInline(events)
      try   { session.Commit(); AggregateStore.CacheSet(stream, newState, version + n); return Accepted(version + n) }
      catch ConcurrencyException: Telemetry.Conflict(); await Backoff(attempt); continue
  return Conflict
```

Properties: one transaction; one round trip on the PostgreSQL adapter (the stream update, event insert, outbox insert and inbox insert are batched); `Decide` re-runs on fresh state; no side effects before commit.

### 5.3 Handlers with dependencies

```csharp
// Only when Decide needs I/O inputs. It must be read-only and safe to re-run (it can run again on a conflict).
public sealed class DepositForeignHandler(IFxRates rates) : ICommandHandler<DepositForeign, BankAccount>
{
    public async ValueTask<Decision> HandleAsync(DepositForeign c, BankAccount state, DecisionContext ctx, CancellationToken ct)
    {
        var rate = await rates.GetRateAsync(c.Currency, "GBP", ctx.Now, ct);
        return state.Decide(new Deposit(c.AccountId, decimal.Round(c.Amount * rate, 2)), ctx);
    }
}
```

Handlers are resolved from a DI **scope** created per message, so their lifetimes are always correct (GAP-08). Aggregates are never DI services.

### 5.4 Temporal reads (state as of a version or time)

```csharp
var atMonthEnd = await aggregates.LoadAsync<BankAccount>(accountId, AsOf.Time(monthEnd), ct);   // fold events with time ≤ monthEnd
var atV10      = await aggregates.LoadAsync<BankAccount>(accountId, AsOf.Version(10), ct);
```

`IAggregateReader` is part of the **read side**. It is strongly consistent, never writes, and uses snapshots whose version is at or below the bound. SFQL exposes the same capability as `from aggregate … as of …` (doc 14 §5.6).

## 6. Reacting to events

### 6.1 Process managers

```csharp
[StronglyTypedId] public readonly partial record struct TransferId(Guid Value);

[MessageType("bank.transfer.request", 1)]
public sealed record RequestTransfer(TransferId TransferId, AccountId From, AccountId To, decimal Amount) : ICommand<Transfer>;
// … Transfer is a small aggregate that emits TransferRequested / TransferCompleted / TransferFailed

[ProcessManager("money-transfer")]
public sealed partial record MoneyTransfer : IProcessManager<MoneyTransfer, TransferId>
{
    public static MoneyTransfer Initial { get; } = new();
    public TransferStage Stage { get; init; }
    public AccountId From { get; init; }
    public AccountId To { get; init; }
    public decimal Amount { get; init; }

    // Correlation: by default the envelope CorrelationId (set from the PM instance on every command it sends).
    // Starting events declare it explicitly:
    public static TransferId CorrelateBy(TransferRequested e) => e.TransferId;

    public Reaction React(TransferRequested e, ReactionContext<TransferId> ctx) =>
        Reaction.Send(new Withdraw(e.From, e.Amount))
                .Schedule(new TransferTimedOut(e.TransferId), ctx.Now.AddMinutes(5))
                .AndBecome(this with { Stage = TransferStage.Withdrawing, From = e.From, To = e.To, Amount = e.Amount });

    public Reaction React(MoneyWithdrawn e, ReactionContext<TransferId> ctx) => Stage switch
    {
        TransferStage.Withdrawing => Reaction.Send(new Deposit(To, Amount)).AndBecome(this with { Stage = TransferStage.Depositing }),
        _                         => Reaction.Ignore,
    };

    public Reaction React(MoneyDeposited e, ReactionContext<TransferId> ctx) => Stage switch
    {
        TransferStage.Depositing => Reaction.Send(new CompleteTransfer(ctx.Id)).AndComplete(),
        _                        => Reaction.Ignore,
    };

    public Reaction React(CommandRejected<Withdraw> r, ReactionContext<TransferId> ctx) =>
        Reaction.Send(new FailTransfer(ctx.Id, r.Error.Code)).AndComplete();

    public Reaction React(TransferTimedOut t, ReactionContext<TransferId> ctx) => Stage switch
    {
        TransferStage.Withdrawing => Reaction.Send(new FailTransfer(ctx.Id, "timeout")).AndComplete(),
        TransferStage.Depositing  => Reaction.Send(new Deposit(From, Amount)).AndSend(new FailTransfer(ctx.Id, "timeout")).AndComplete(), // compensate
        _                         => Reaction.Ignore,
    };
}
```

**Two ways to author a process manager.** Write the record by hand, as above, or describe the workflow in the YAML DSL and let the generator produce it ([doc 15](15-Workflow-DSL-and-Studio.md), [ADR-024](ADRs/ADR-024-workflow-dsl-compiled-to-process-managers.md)). Generated workflows add declarative business retries, reverse-order compensation, a pivot and manual intervention, and can be visualised and simulated in SourceFlow Studio. Both run on the same runtime.

`ReactionContext<TId>` carries the strongly typed instance id (`ctx.Id`), the reaction time, the message id and whether the message starts the instance. `Reaction` API (DC-3): static entry points `Reaction.Send(…)`, `Reaction.Become(…)`, `Reaction.Complete()` and `Reaction.Ignore`; chained instance methods `AndSend`, `Schedule`, `AndBecome` and `AndComplete`. C# can't have a static and an instance method with the same signature, so the earlier `.Send(…).Send(…)` shape isn't possible.

**Runtime semantics** ([ADR-020](ADRs/ADR-020-process-managers-with-durable-state-and-timers.md)):

1. The process manager subscribes to every event type it has a `React` for. The source can be local (the subscription engine) or remote (the receive pipeline).
2. For each event, in one transaction:
   - load the instance state (or `Initial` if this event starts it);
   - call `React`;
   - persist the new state with optimistic concurrency;
   - add the commands and timers to the outbox;
   - record the inbox entry and the checkpoint.
3. `Complete()` marks the instance complete. Later events for it are ignored, and the state is kept for `CompletedRetention` (default 30 days) for diagnostics.
4. **Rejections as inputs.** When a command sent by a process manager is rejected by its owner, the owner sends a `CommandRejected<T>` reply to the correlation (if `ReplyOnReject` is set on the route). This closes the loop for compensation without exceptions ([ADR-018](ADRs/ADR-018-business-rejections-are-values-not-exceptions.md)).

### 6.2 Event handlers (side effects)

```csharp
public sealed class WelcomeEmailHandler(IEmailGateway email) : IEventHandler<AccountOpened>
{
    public async ValueTask HandleAsync(AccountOpened e, EventContext ctx, CancellationToken ct)
        => await email.SendWelcomeAsync(e.AccountId, idempotencyKey: ctx.EventId.ToString(), ct);
}
```

Event handlers run on the subscription engine with **at-least-once** semantics. A handler whose side effect is external must be idempotent; `ctx.EventId` is the natural key. A side effect that can be expressed as a command to another service should use `ctx.Session.Send(...)` instead, which goes through the outbox and so becomes effectively-once.

### 6.3 Projections

```csharp
public sealed record AccountSummary(AccountId Id, string Holder, decimal Balance, AccountStatus Status, long Version);

[Projection("account-summary", Version = 1)]                    // bump Version → blue/green rebuild (doc 06)
public sealed partial class AccountSummaryProjection : IDocumentProjection<AccountSummary, AccountId>
{
    // Key defaults to the aggregate id of the source stream; override with KeyOf(e) if needed.
    public AccountSummary When(AccountOpened e, AccountSummary? _, EventContext ctx)
        => new(e.AccountId, e.Holder, e.InitialDeposit, AccountStatus.Open, ctx.StreamVersion.Value);

    public AccountSummary? When(MoneyDeposited e, AccountSummary? doc, EventContext ctx)
        => doc is null ? null : doc with { Balance = e.BalanceAfter, Version = ctx.StreamVersion.Value };

    public AccountSummary? When(MoneyWithdrawn e, AccountSummary? doc, EventContext ctx)
        => doc is null ? null : doc with { Balance = e.BalanceAfter, Version = ctx.StreamVersion.Value };

    public AccountSummary? When(AccountClosed e, AccountSummary? doc, EventContext ctx)
        => doc is null ? null : doc with { Status = AccountStatus.Closed, Version = ctx.StreamVersion.Value };
}
```

An EF Core projection writes relational read models in the projection's transaction:

```csharp
public sealed class BranchTotalsProjection : IEfProjection<ReportingDbContext>
{
    public async ValueTask When(MoneyDeposited e, ReportingDbContext db, EventContext ctx, CancellationToken ct)
        => await db.BranchTotals.Where(b => b.AccountId == e.AccountId.Value)
                   .ExecuteUpdateAsync(s => s.SetProperty(b => b.Total, b => b.Total + e.Amount), ct);
}
```

Projection functions are deterministic and contain no I/O except through the store they are given. That makes them replay-safe, so a rebuild can never cause a side effect.

## 7. Integration events

Domain events are private to a service. What leaves the service is an explicit, versioned contract:

```csharp
[MessageType("accounts.account-opened", 1)]
public sealed record AccountOpenedV1(Guid AccountId, DateTimeOffset OpenedAt) : IIntegrationEvent;   // no PII by design

sf.Routing(r => r.Publish<AccountOpened>()
                 .As(e => new AccountOpenedV1(e.AccountId.Value, e.OpenedAt))
                 .To("accounts.events"));
```

The mapping runs **inside the command transaction**, and its output goes to the outbox. Publishing a domain event unchanged needs an explicit `.AsIs()`, and the analyzer warns (SF0013) when that event contains `[PersonalData]`.

## 8. Composition and host configuration

```csharp
[SourceFlowModule(typeof(AccountsJsonContext))] // generator fills in handlers, aggregates, PMs, projections, registry (DC-1)
public sealed partial class AccountsModule;

[JsonSerializable(typeof(OpenAccount))] /* … every message, document and process-state type; SF0010 checks completeness */
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class AccountsJsonContext : JsonSerializerContext;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "accounts";                                 // envelope 'source', default endpoint, consumer names
    sf.AddModule<AccountsModule>();
    sf.UsePostgreSql(builder.Configuration.GetConnectionString("accounts")!);
    sf.UseAmazonSqs(aws => aws.Region = "eu-west-1");            // honoured: becomes the client config (GAP-03)

    sf.Routing(r =>
    {
        r.Command<SendStatement>().To("notifications");          // owned by another service
        r.Publish<AccountOpened>().As(AccountContracts.ToV1).To("accounts.events");
        r.Subscribe("payments.events");                          // integration events from others
    });

    sf.Projections(p =>
    {
        p.Add<AccountSummaryProjection>().Inline();              // read-your-writes
        p.Add<BranchTotalsProjection>().Async(partitions: 4);
    });

    sf.ProcessManagers(pm => pm.Add<MoneyTransfer>());
});

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(SourceFlowTelemetry.ActivitySourceName).AddOtlpExporter())
    .WithMetrics(m => m.AddMeter(SourceFlowTelemetry.MeterName).AddOtlpExporter());

builder.Services.AddHealthChecks().AddSourceFlow();              // readiness, store, transport, lag, outbox age
```

The generated module registers **only** what it found at compile time, with no assembly scanning (GAP-16).

## 9. Analyzers

| Id | Severity | Rule |
|----|----------|------|
| SF0001 | Error | A type implementing `ICommand`, `IEvent` or `IIntegrationEvent` lacks `[MessageType]`. |
| SF0002 | Error | Duplicate `(name, version)` within the compilation. Cross-assembly duplicates are caught at startup. |
| SF0003 | Error | A command's target id is missing or ambiguous: no member, or several members, of the aggregate's id type. |
| SF0004 | Error | A `When` method has side effects: it calls I/O, assigns fields, or has an `async` modifier. |
| SF0005 | Warning | An event produced by `Decide` has no `When` in that aggregate. The state ignores it. |
| SF0006 | Warning | A command type has no `Decide` or handler in this compilation, and no route. Startup turns this into an error. |
| SF0007 | Error | `Decide` is `async`, or returns `Task`/`ValueTask`. Use `ICommandHandler` for I/O. |
| SF0008 | Error | An aggregate or process manager is not a `sealed record`, or has `set` accessors (only `init` is allowed). |
| SF0009 | Warning | Non-deterministic API in `Decide`, `When` or `React` (`DateTime.*Now`, `Guid.NewGuid`, `Random`, `Environment.TickCount`). Use `ctx`. |
| SF0010 | Error | A message member type is not supported by the System.Text.Json source generator. |
| SF0011 | Error | `[PersonalData]` is used in an event without a resolvable subject key (doc 10 §4). |
| SF0012 | Error | A process manager handles an event without a correlation (no `CorrelateBy` and not a reply). |
| SF0013 | Warning | `.AsIs()` publishes an event containing `[PersonalData]`. |
| SF0014 | Error | A command type is both handled locally and routed remotely ([ADR-007](ADRs/ADR-007-single-command-ownership-route-local-xor-remote.md)). The analyzer catches it when routing uses constant expressions in the same compilation. Startup validation always catches it. |
| SF0015 | Error | `IQueryService` or `IAggregateReader` used inside `Decide`, `When`, `React` or projection code. CQRS separation (doc 14 Q-R1). |
| SF0016 | Warning | `IQueryService` used in an `ICommandHandler`: the decision depends on eventually consistent reads. |
| SF0017 | Warning | `[Queryable]` on a `[PersonalData]` member. It has no effect, because ciphertext can't be indexed. |
| SF0019 | Warning | Static analysis can't determine the events or rejection codes a `Decide` produces; declare `[Emits(typeof(…))]` / `[Rejects("code")]` so service manifests are exact (doc 15 §3.2) |
| SF0020 | Error | Workflow DSL validation failure (rule WF-V01…V16 named in the message), reported at the `.sfflow.yaml` line (doc 15 §5.4) |
| SF0021 | Error | A workflow `$hook` partial method has no implementation (doc 15 §5.5) |
| SF0018 | Error | A `[DomainQuery]` doesn't bind against the service's own catalog, or its result columns don't match the result record (doc 14 §1.1, Q-B4). |
