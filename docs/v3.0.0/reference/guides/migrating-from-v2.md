# Migrating a v2 service

SourceFlow v2 stored **commands** and the **current state** of each entity. v3 stores **events**. Replaying v2 commands to
derive events would repeat the non-determinism v3 removes (ADR-002). So a v2 service moves to v3 in two parts:

- **Data.** Import each entity's current state as **genesis events**, then archive the v2 command history (doc 12 §3).
- **Wire.** While v2 producers still run, the v3 service accepts their messages through an explicit allow-list (doc 07 §8,
  doc 12 §4).

Migrate one bounded context at a time. The v2 database is read with plain SQL; no v2 assembly is referenced.

## 1. Register the migration

Add the v2 database and one import per v2 entity type to the v3 service:

```csharp
using SourceFlow.Migration;

builder.Services.AddSourceFlow(sf =>
{
    sf.AddModule<AccountsModule>();
    sf.UsePostgreSql(o => o.ConnectionString = v3Connection);

    sf.MigrateFromV2(v2 => v2
        .From(new V2Source(SqlClientFactory.Instance.CreateDataSource(v2Connection), V2Dialect.SqlServer))
        .Import(new BankAccountImport())
        .Ignore("AccountViewModel")                                     // v2 view models hold no entities
        .NotRebuildable("MyApp.Commands.ApplyInterest, MyApp"));        // GAP-04: v2 couldn't rebuild these
});
```

`V2Source` reads PostgreSQL or SQL Server, the databases v2's EF Core store ran on. Set `Schema` and `CommandTable` when
v2 used a `TableNamingConvention`.

Run the commands below inside the service: `dotnet run --project <service> -- sourceflow migrate v2 …`. The service's
`Program.cs` must call `SourceFlowCli.TryRunAsync` (the TravelBooking sample's services do). `plan`, `scaffold` and `archive` also run from
the global tool with `--v2-connection`, `--v2-provider postgresql|sqlserver` and `--v2-schema`.

## 2. Plan

```text
$ sourceflow migrate v2 plan
v2 commands: 54 (GAP-20, logged under entity id 0: 2; GAP-04, not rebuildable: 2)
        43 SourceFlow.Core.Tests.E2E.Commands.CreateAccount, SourceFlow.Core.Tests  id 0: 2
        ...
Entities:
  BankAccount -> bank-account (BankAccountImport): 43 stream(s), 51 command(s)
Estimated import: 00:00:01
Lost history: the commands above can't be attributed or rebuilt; the current state is still migrated.
```

The plan exits with 1 while a v2 table has neither an import nor `Ignore(…)`.

What it reports as lost history:

- **GAP-20.** Commands logged under entity id 0 can't be attributed to an entity.
- **GAP-04.** Commands v2 couldn't rebuild: those with an empty payload, and the types you declare with `NotRebuildable`.

In both cases the *current state* is still migrated; only that part of the history is lost.

## 3. Scaffold the imports

```text
$ sourceflow migrate v2 scaffold --table BankAccount --namespace Accounts.Migration --output Migration/
```

Scaffold writes two things per table:

- **The row type.** An `IV2Entity` record built from the table's columns, with a `Read(V2Row)` method.
- **An import stub.** When a v3 aggregate is named like the table, the stub uses its types; otherwise it emits
  placeholders and a `#warning`.

Complete the stub:

```csharp
public sealed class BankAccountImport : IV2Import<V2BankAccount, BankAccount, AccountId>
{
    public AccountId MapId(V2BankAccount e) => V2Ids.FromLegacy<AccountId>(e.Id);   // UUIDv5 of the v2 id

    public IEnumerable<IEvent> ToEvents(V2BankAccount e, V2ImportContext ctx)
    {
        yield return new AccountImported(MapId(e), e.AccountName ?? "", e.Balance, e.IsClosed, ctx.ImportedAt);
    }

    public IEnumerable<string> Compare(V2BankAccount e, BankAccount state)
    {
        if (state.Balance != e.Balance) yield return $"Balance: v2 {e.Balance}, v3 {state.Balance}";
    }

    // Optional: when several v2 entity types share the command table (ids overlap per type).
    public bool OwnsCommand(string commandType) => commandType.StartsWith("MyApp.Accounts.", StringComparison.Ordinal);
}
```

The aggregate and its projections need a `When(AccountImported)` handler. Existing events also work: the test suite
imports v2 accounts as `AccountOpened`, plus `AccountClosed` for closed accounts.

`V2Ids.FromLegacy` is deterministic. Pass a `scope` when two v2 entity types are imported into the same aggregate id
space, and use the same scope in the wire bridge's converters.

## 4. Import

Either freeze writes to the bounded context, or run the wire bridge (section 7) and import in waves.

```text
$ sourceflow migrate v2 import --batch 1000            # or --limit 10000 for a wave
Imported 43 stream(s); 0 already existed.
```

How the import works:

- **One session per stream.** Each stream is written in its own storage session with `ExpectedVersion.NoStream`.
- **Idempotent and resumable.** A stream that already exists is skipped. After an interruption, run the command again: it
  continues, and nothing is written twice.
- **What each event carries.**
  - A deterministic id.
  - Actor `sourceflow-migrate-v2`.
  - The headers `migratedFrom: v2`, `v2CommandCount`, `v2LastSequenceNo` and `v2Id`.
- **Like any append.** Imported events are encrypted per subject when `EncryptPersonalData()` is on, and inline
  projections apply them.
- **Not published.** Imported events don't go to integration topics; other services keep their own v2-era state.
- **Async projections** build from the imported events on their own.

## 5. Archive

```text
$ sourceflow migrate v2 archive
Archived 54 command(s); sourceflow_v2_archive.commands holds 54 of 54.
```

The command table is copied into the v2 database, as `sourceflow_v2_archive.commands` (or `--v2-archive-schema`):

- **Read-only.** A trigger refuses updates and deletes, so the copy can serve as an audit record.
- **Re-runnable.** Running the command again copies only new rows.

## 6. Verify and cut over

```text
$ sourceflow migrate v2 verify
Verified 43 entities: no mismatches.
```

`verify` loads each v3 aggregate and compares it with its v2 row through `Compare`:

- A stream that was never imported is reported as a mismatch.
- Mismatches go to standard error, and the exit code is 1.

Use the exit code to gate the cut-over.

## 7. The v2 wire bridge (AWS)

v2 producers that still run send v2-shaped messages, which the v3 service accepts through an explicit allow-list:

```csharp
sf.UseAwsTransport(o => o.AcceptV2Messages(map => map
    .Type("MyApp.Commands.CreateAccount, MyApp", "bank.account.open", 1, v2 => new JsonObject
    {
        ["accountId"] = V2Ids.FromLegacy(v2.EntityId ?? 0).ToString(),
        ["holder"] = v2.Payload!["accountName"]!.GetValue<string>(),
        ["initialDeposit"] = v2.Payload!["initialAmount"]!.GetValue<decimal>(),
    })));
```

What the bridge accepts:

- **Commands.** The v2 shape is a `CommandType` attribute plus `EntityId` and `SequenceNo`.
- **Events.** The v2 shape is an `EventType` attribute, either raw on SQS or inside an SNS notification. To receive v2
  events, subscribe the v3 endpoint queue to the v2 SNS topic.

What it does with a message:

- **Matching.** Types match without assembly version, culture or public key token.
- **Body.** The converter turns the v2 JSON into the v3 record's JSON. Without a converter, the v2 `payload` object is
  used as is.
- **Message id.** It is UUIDv5 of the v2 message id, so a redelivered v2 message is a duplicate for the inbox.
- **Unmapped types.** A v2 type that isn't on the allow-list is parked as `UnknownMessageType`. The dead letter keeps the
  v2 type name, the body and the header `sourceflow-v2-unmapped`.
- **Off by default.** Without `AcceptV2Messages`, v2 messages are left for the queue's redrive policy.

**v3 to v2.** A v2 consumer can't read CloudEvents. Until it is migrated, publish a v2-shaped integration event with
`.As(…)` to the legacy topic.

The bridge is planned for removal in 4.0.
