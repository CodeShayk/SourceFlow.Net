using System.Diagnostics.Metrics;
using Bank.Accounts;
using Bank.Notifications;
using Bank.Transfers;
using SourceFlow.Diagnostics;
using SourceFlow.Messaging;
using SourceFlow.Query;
using SourceFlow.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace SourceFlow.EndToEnd.Tests;

/// <summary>E2E scenarios over the Bank sample, in memory (plan 06 §4).</summary>
[Trait("Category", "E2E")]
public sealed class BankScenarios
{
    [Fact]
    [Trait("Id", "E2E-01")]
    [Trait("Covers", "ADR-002;ADR-016")]
    public async Task AccountLifecycle_OpenDepositWithdrawClose_SummaryIsCorrect()
    {
        await using BankSystem bank = await BankSystem.StartAsync();
        AccountId id = await bank.OpenAsync(100m);

        (await bank.Accounts.SendAsync(new Deposit(id, 50m))).Version.ShouldBe(new StreamVersion(2));
        (await bank.Accounts.SendAsync(new Withdraw(id, 30m))).Version.ShouldBe(new StreamVersion(3));
        (await bank.Accounts.SendAsync(new CloseAccount(id, "moving"))).Status.ShouldBe(CommandStatus.Accepted);

        AccountSummary summary = (await bank.SummaryAsync(id)).ShouldNotBeNull();
        summary.ShouldBe(new AccountSummary(id, "Ada", 120m, AccountStatus.Closed, 4));
        (await bank.Accounts.EventsAsync(StreamId.For(BankAccount.AggregateName, id))).Select(e => e.GetType()).ShouldBe(
            [typeof(AccountOpened), typeof(MoneyDeposited), typeof(MoneyWithdrawn), typeof(AccountClosed)]);
    }

    [Fact]
    [Trait("Id", "E2E-02")]
    [Trait("Covers", "ADR-018")]
    public async Task BusinessRejections_AppendNothing_AndReturnErrors()
    {
        await using BankSystem bank = await BankSystem.StartAsync();
        AccountId id = await bank.OpenAsync(10m);

        CommandResult overdraw = await bank.Accounts.SendAsync(new Withdraw(id, 11m));
        await bank.Accounts.SendAsync(new CloseAccount(id, "x"));
        CommandResult afterClose = await bank.Accounts.SendAsync(new Deposit(id, 1m));

        overdraw.Status.ShouldBe(CommandStatus.Rejected);
        overdraw.Error.ShouldBe(AccountErrors.InsufficientFunds);
        afterClose.Error.ShouldBe(AccountErrors.NotOpen);
        (await bank.Accounts.EventsAsync(StreamId.For(BankAccount.AggregateName, id))).Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Id", "E2E-03")]
    [Trait("Covers", "ADR-006;GAP-09")]
    public async Task IdempotencyKey_SameDepositTwice_OneEvent_SecondIsDuplicateWithOriginalVersion()
    {
        await using BankSystem bank = await BankSystem.StartAsync();
        AccountId id = await bank.OpenAsync();
        var options = new SendOptions { IdempotencyKey = "deposit-42" };

        CommandResult first = await bank.Accounts.SendAsync(new Deposit(id, 5m), options);
        CommandResult second = await bank.Accounts.SendAsync(new Deposit(id, 5m), options);

        first.Status.ShouldBe(CommandStatus.Accepted);
        second.Status.ShouldBe(CommandStatus.Duplicate);
        second.Version.ShouldBe(first.Version);
        (await bank.SummaryAsync(id))!.Balance.ShouldBe(105m);
    }

    [Fact]
    [Trait("Id", "E2E-04")]
    [Trait("Covers", "ADR-008;GAP-05")]
    public async Task FiftyParallelDeposits_AllAccepted_BalanceExact_ConflictsRecorded()
    {
        await using BankSystem bank = await BankSystem.StartAsync(accounts: sf => sf.Configure(o => o.MaxConcurrencyRetries = 50));
        AccountId id = await bank.OpenAsync(0.01m);
        long conflicts = 0;
        Meter meter = bank.Accounts.Services.GetRequiredService<SourceFlowMetrics>().Meter;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter) && instrument.Name == "sourceflow.eventstore.concurrency_conflicts")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref conflicts, value));
        listener.Start();

        CommandResult[] results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () => await bank.Accounts.SendAsync(new Deposit(id, 1m)))));

        results.ShouldAllBe(r => r.Status == CommandStatus.Accepted);
        (await bank.SummaryAsync(id))!.Balance.ShouldBe(50.01m);
        results.Select(r => r.Version.Value).Distinct().Count().ShouldBe(50);
        Interlocked.Read(ref conflicts).ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Id", "E2E-05")]
    public async Task ExpectedVersionMismatch_IsConflict_AndAppendsNothing()
    {
        await using BankSystem bank = await BankSystem.StartAsync();
        AccountId id = await bank.OpenAsync();

        CommandResult result = await bank.Accounts.SendAsync(new Deposit(id, 1m), new SendOptions { ExpectedVersion = new StreamVersion(7) });

        result.Status.ShouldBe(CommandStatus.Conflict);
        result.Error.ShouldBe(Error.VersionMismatch);
        (await bank.Accounts.EventsAsync(StreamId.For(BankAccount.AggregateName, id))).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "E2E-06")]
    [Trait("Covers", "ADR-016")]
    public async Task AsyncProjection_CatchesUp_AfterTheCommandToken()
    {
        await using BankSystem bank = await BankSystem.StartAsync(inlineSummary: false);
        AccountId id = await bank.OpenAsync(10m);
        CommandResult deposit = await bank.Accounts.SendAsync(new Deposit(id, 5m));

        await bank.IdleAsync();

        deposit.Token.Position.ShouldBeGreaterThan(GlobalPosition.Start);
        GlobalPosition checkpoint = await bank.Accounts.Store.GetAsync("projection:account-summary@1", 0, TestContext.Current.CancellationToken);
        checkpoint.ShouldBeGreaterThanOrEqualTo(deposit.Token.Position);
        (await bank.SummaryAsync(id))!.Balance.ShouldBe(15m);
    }

    [Fact]
    [Trait("Id", "E2E-07")]
    public async Task TemporalRead_AsOfVersion_AndAsOfTime()
    {
        await using BankSystem bank = await BankSystem.StartAsync();
        AccountId id = await bank.OpenAsync(100m);
        await bank.Accounts.SendAsync(new Deposit(id, 10m));
        DateTimeOffset monthEnd = bank.Time.GetUtcNow();
        bank.Time.Advance(TimeSpan.FromDays(1));
        await bank.Accounts.SendAsync(new Deposit(id, 1000m));
        IAggregateReader reader = bank.Accounts.Services.GetRequiredService<IAggregateReader>();

        (await reader.LoadAsync<BankAccount, AccountId>(id, AsOf.Version(1))).State.Balance.ShouldBe(100m);
        AggregateSnapshot<BankAccount> atMonthEnd = await reader.LoadAsync<BankAccount, AccountId>(id, AsOf.Time(monthEnd));
        atMonthEnd.State.Balance.ShouldBe(110m);
        atMonthEnd.Version.ShouldBe(new StreamVersion(2));
        (await reader.LoadAsync<BankAccount, AccountId>(id)).State.Balance.ShouldBe(1110m);
    }

    [Fact]
    [Trait("Id", "E2E-11")]
    [Trait("Covers", "ADR-016")]
    public async Task FourPartitionAsyncProjection_EveryEventAppliedExactlyOnce()
    {
        await using BankSystem bank = await BankSystem.StartAsync(inlineSummary: false, partitions: 4);
        AccountId[] ids = new AccountId[200];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = await bank.OpenAsync(1m);
            (await bank.Accounts.SendAsync(new Deposit(ids[i], i + 1))).Status.ShouldBe(CommandStatus.Accepted);
        }

        await bank.IdleAsync();

        foreach ((AccountId id, int i) in ids.Select((id, i) => (id, i)))
        {
            AccountSummary summary = (await bank.SummaryAsync(id)).ShouldNotBeNull();
            summary.Balance.ShouldBe(2m + i);
            summary.Version.ShouldBe(2);
        }

        for (int p = 0; p < 4; p++)
        {
            (await bank.Accounts.Store.GetAsync("projection:account-summary@1", p, TestContext.Current.CancellationToken)).ShouldNotBe(GlobalPosition.Start);
        }
    }

    [Fact]
    [Trait("Id", "E2E-13")]
    [Trait("Covers", "ADR-020")]
    public async Task MoneyTransfer_HappyPath_RequestWithdrawDepositComplete()
    {
        await using BankSystem bank = await BankSystem.StartAsync();
        AccountId from = await bank.OpenAsync(100m);
        AccountId to = await bank.OpenAsync(0.5m);
        var transfer = TransferId.New(bank.Time);

        (await bank.Accounts.SendAsync(new RequestTransfer(transfer, from, to, 40m))).Status.ShouldBe(CommandStatus.Accepted);
        await bank.IdleAsync();

        (await bank.SummaryAsync(from))!.Balance.ShouldBe(60m);
        (await bank.SummaryAsync(to))!.Balance.ShouldBe(40.5m);
        (await bank.Accounts.EventsAsync(StreamId.For(Transfer.AggregateName, transfer))).Last().ShouldBeOfType<TransferCompleted>();
    }

    [Fact]
    [Trait("Id", "E2E-14")]
    [Trait("Covers", "ADR-020")]
    public async Task MoneyTransfer_DepositRejected_TimesOut_AndCompensates()
    {
        // The deposit to a closed account is rejected and the process manager doesn't react to that rejection, so the
        // timeout fires while depositing: the withdrawal is compensated and the transfer fails.
        await using BankSystem bank = await BankSystem.StartAsync();
        AccountId from = await bank.OpenAsync(100m);
        AccountId to = await bank.OpenAsync(1m);
        (await bank.Accounts.SendAsync(new CloseAccount(to, "moved"))).Status.ShouldBe(CommandStatus.Accepted);
        var transfer = TransferId.New(bank.Time);

        await bank.Accounts.SendAsync(new RequestTransfer(transfer, from, to, 40m));
        await bank.IdleAsync();
        (await bank.SummaryAsync(from))!.Balance.ShouldBe(60m);
        (await bank.Accounts.EventsAsync(StreamId.For(Transfer.AggregateName, transfer))).Last().ShouldBeOfType<TransferRequested>();

        bank.Time.Advance(MoneyTransfer.Timeout + TimeSpan.FromSeconds(1));
        await bank.IdleAsync();

        (await bank.Accounts.EventsAsync(StreamId.For(Transfer.AggregateName, transfer))).Last().ShouldBe(new TransferFailed(transfer, "timeout"));
        (await bank.SummaryAsync(from))!.Balance.ShouldBe(100m);   // compensated
        (await bank.SummaryAsync(to))!.Balance.ShouldBe(1m);
    }

    [Fact]
    [Trait("Id", "E2E-15")]
    [Trait("Covers", "ADR-018;ADR-020")]
    public async Task MoneyTransfer_InsufficientFunds_RejectionReply_FailsTheTransfer()
    {
        await using BankSystem bank = await BankSystem.StartAsync();
        AccountId from = await bank.OpenAsync(10m);
        AccountId to = await bank.OpenAsync(1m);
        var transfer = TransferId.New(bank.Time);

        await bank.Accounts.SendAsync(new RequestTransfer(transfer, from, to, 40m));
        await bank.IdleAsync();   // no time passes: the CommandRejected<Withdraw> reply fails the transfer

        (await bank.Accounts.EventsAsync(StreamId.For(Transfer.AggregateName, transfer))).Last()
            .ShouldBe(new TransferFailed(transfer, AccountErrors.InsufficientFunds.Code));
        (await bank.SummaryAsync(from))!.Balance.ShouldBe(10m);
        (await bank.SummaryAsync(to))!.Balance.ShouldBe(1m);

        // The timeout fires later and is ignored: the instance is complete.
        bank.Time.Advance(MoneyTransfer.Timeout + TimeSpan.FromSeconds(1));
        await bank.IdleAsync();
        (await bank.Accounts.EventsAsync(StreamId.For(Transfer.AggregateName, transfer))).OfType<TransferFailed>().ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Id", "E2E-17")]
    [Trait("Covers", "RT-R4;GAP-13")]
    public async Task CrossService_AccountOpenedV1_ReachesNotifications_Once()
    {
        await using BankSystem bank = await BankSystem.StartAsync(withNotifications: true);
        bank.Broker.Faults.DuplicateNext(1);

        AccountId id = await bank.OpenAsync();
        await bank.IdleAsync();

        var customer = new CustomerId(id.Value);
        IReadOnlyList<IEvent> inbox = await bank.Notifications!.EventsAsync(StreamId.For(CustomerInbox.AggregateName, customer));
        inbox.Count.ShouldBe(1);
        inbox[0].ShouldBeOfType<WelcomeSent>();
    }

    [Fact]
    [Trait("Id", "E2E-18")]
    [Trait("Covers", "GAP-10;GAP-11")]
    public async Task UnknownMessageType_IsParked_NotDeserialized_AndCanBeDiscarded()
    {
        await using BankSystem bank = await BankSystem.StartAsync();
        var envelope = new MessageEnvelope
        {
            MessageId = MessageIds.New(bank.Time),
            Type = "evil.payload",
            TypeVersion = 1,
            Source = "attacker",
            Time = bank.Time.GetUtcNow(),
            Body = "{\"$type\":\"System.Diagnostics.Process\"}"u8.ToArray(),
        };

        bank.Broker.Send("accounts", envelope);
        await bank.IdleAsync();

        IReadOnlyList<DeadLetter> parked = await bank.Accounts.DeadLetters.ListAsync(new DeadLetterQuery());
        parked.ShouldHaveSingleItem().Reason.ShouldBe("UnknownMessageType");
        parked[0].Envelope.ShouldBe(envelope);
        await bank.Accounts.DeadLetters.DiscardAsync(parked[0].Id, "attack test");
        (await bank.Accounts.DeadLetters.ListAsync(new DeadLetterQuery())).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Id", "E2E-19")]
    [Trait("Covers", "ADR-006")]
    public async Task DroppedAck_Redelivery_IsAbsorbedByTheInbox()
    {
        await using BankSystem bank = await BankSystem.StartAsync(withNotifications: true);
        bank.Broker.Faults.DropNextAcks(3);

        AccountId id = await bank.OpenAsync();
        await bank.IdleAsync();

        (await bank.Notifications!.EventsAsync(StreamId.For(CustomerInbox.AggregateName, new CustomerId(id.Value)))).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "E2E-20")]
    [Trait("Covers", "GAP-20")]
    public async Task DefaultTargetId_IsRejected_AsInvalidCommand()
    {
        await using BankSystem bank = await BankSystem.StartAsync();

        CommandResult result = await bank.Accounts.SendAsync(new Deposit(default, 1m));

        result.Status.ShouldBe(CommandStatus.Rejected);
        result.Error!.Code.ShouldBe("sourceflow.invalid_command");
    }
}
