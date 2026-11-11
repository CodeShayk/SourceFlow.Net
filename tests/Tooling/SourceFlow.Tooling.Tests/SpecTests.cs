using Bank.Accounts;
using Bank.Transfers;
using SourceFlow.Testing;

namespace SourceFlow.Tooling.Tests;

/// <summary>UT-TST (plan 05): the spec library, tested with the Bank domain.</summary>
[Trait("Category", "Unit")]
public sealed class SpecTests
{
    private static readonly AccountId Account = AccountId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly AccountId Other = AccountId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5c");
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Id", "UT-TST-01")]
    public void AggregateSpec_Then_PassesForTheProducedEvents()
    {
        new AggregateSpec<BankAccount, AccountId>()
            .At(Now)
            .When(new OpenAccount(Account, "Ada", 10m))
            .Then(new AccountOpened(Account, "Ada", 10m, Now))
            .ThenState(s => s.Balance == 10m && s.Status == AccountStatus.Open);
    }

    [Fact]
    [Trait("Id", "UT-TST-02")]
    public void AggregateSpec_ThenRejected_AndThenNoOp()
    {
        new AggregateSpec<BankAccount, AccountId>()
            .Given(new AccountOpened(Account, "Ada", 10m, Now))
            .When(new Withdraw(Account, 11m))
            .ThenRejected(AccountErrors.InsufficientFunds.Code);

        new AggregateSpec<BankAccount, AccountId>()
            .Given(new AccountOpened(Account, "Ada", 10m, Now), new AccountClosed(Account, "x", Now))
            .When(new CloseAccount(Account, "again"))
            .ThenNoOp();
    }

    [Fact]
    [Trait("Id", "UT-TST-03")]
    public void AggregateSpec_Mismatch_FailsWithAReadableMessage()
    {
        AggregateSpec<BankAccount, AccountId> spec = new AggregateSpec<BankAccount, AccountId>().When(new Deposit(Account, 1m));

        Should.Throw<SpecificationException>(() => spec.Then(new MoneyDeposited(Account, 1m, 1m))).Message.ShouldContain("account.not_open");
        Should.Throw<SpecificationException>(() => spec.ThenNoOp());
        Should.Throw<SpecificationException>(() => new AggregateSpec<BankAccount, AccountId>().ThenNoOp()).Message.ShouldContain("When");
    }

    [Fact]
    [Trait("Id", "UT-TST-04")]
    [Trait("Covers", "ADR-020")]
    public void ProcessManagerSpec_HappyPath_SendsAndCompletes()
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
    [Trait("Id", "UT-TST-05")]
    [Trait("Covers", "ADR-020")]
    public void ProcessManagerSpec_TimeoutWhileDepositing_Compensates()
    {
        var transfer = TransferId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5d");

        new ProcessManagerSpec<MoneyTransfer, TransferId>(transfer)
            .Given(new TransferRequested(transfer, Account, Other, 5m), new MoneyWithdrawn(Account, 5m, 5m))
            .WhenTimeAdvances(MoneyTransfer.Timeout)
            .When(new TransferTimedOut(transfer))
            .ThenSends(new Deposit(Account, 5m), new FailTransfer(transfer, "timeout"))
            .ThenCompleted();
    }

    [Fact]
    [Trait("Id", "UT-TST-06")]
    public void ProjectionSpec_BuildsTheDocument()
    {
        StreamId stream = StreamId.For(BankAccount.AggregateName, Account);

        new ProjectionSpec<AccountSummaryProjection, AccountSummary, AccountId>()
            .Given(stream, new AccountOpened(Account, "Ada", 10m, Now), new MoneyDeposited(Account, 5m, 15m))
            .Then(Account, new AccountSummary(Account, "Ada", 15m, AccountStatus.Open, 2))
            .Then(Other, null);
    }

    [Fact]
    [Trait("Id", "UT-TST-07")]
    public void Approvals_MismatchWritesReceivedFile()
    {
        string name = "approval-selftest-" + Environment.ProcessId;
        string received = Path.Combine(Path.GetDirectoryName(ThisFile())!, "Approved", name + ".received.txt");

        ApprovalMismatchException mismatch = Should.Throw<ApprovalMismatchException>(() => Approvals.AssertMatches("content", name));

        mismatch.ReceivedPath.ShouldBe(received);
        File.ReadAllText(received).ShouldBe("content\n");
        File.Delete(received);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "")
    {
        return path;
    }
}
