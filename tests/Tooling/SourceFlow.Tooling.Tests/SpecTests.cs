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
        var spec = ProcessManagerSpec.For<MoneyTransfer, TransferId, TransfersModule>().WithModule<AccountsModule>();

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

        ProcessManagerSpec.For<MoneyTransfer, TransferId, TransfersModule>().WithModule<AccountsModule>()
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

        ApprovalMismatchException mismatch = Should.Throw<ApprovalMismatchException>(() => Approvals.AssertMatches("content", name));

        // Next to this test file, wherever the build put the compiler-supplied path (CI maps it to /_/…).
        mismatch.ReceivedPath.ShouldEndWith(Path.Combine("SourceFlow.Tooling.Tests", "Approved", name + ".received.txt"));
        File.ReadAllText(mismatch.ReceivedPath).ShouldBe("content\n");
        File.Delete(mismatch.ReceivedPath);
    }

    [Fact]
    [Trait("Id", "UT-TST-08")]
    public void Approvals_ResolveDeterministicSourcePaths_ToTheFileInTheRepository()
    {
        // ContinuousIntegrationBuild maps source paths to /_/…; the approved folder is still found in the checkout.
        string name = "approval-mapped-" + Environment.ProcessId;
        const string Mapped = "/_/tests/Tooling/SourceFlow.Tooling.Tests/SpecTests.cs";

        ApprovalMismatchException mismatch = Should.Throw<ApprovalMismatchException>(() => Approvals.AssertMatches("content", name, Mapped));

        mismatch.ReceivedPath.ShouldNotStartWith("/_/");
        File.Exists(mismatch.ReceivedPath).ShouldBeTrue();
        Path.GetDirectoryName(Path.GetDirectoryName(mismatch.ReceivedPath)).ShouldBe(Path.GetDirectoryName(ThisFile()));
        File.Delete(mismatch.ReceivedPath);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "")
    {
        return Approvals.ResolveSourcePath(path);
    }
    [Fact]
    [Trait("Id", "UT-TST-21")]
    [Trait("Covers", "doc 11 2")]
    public void ProcessManagerSpec_FailsWhereAServiceWould()
    {
        var transfer = TransferId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5d");
        var other = TransferId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5e");

        // A first message that can't start an instance: MoneyWithdrawn has no CorrelateBy, and nothing caused it.
        Should.Throw<SpecificationException>(() => ProcessManagerSpec.For<MoneyTransfer, TransferId, TransfersModule>().WithModule<AccountsModule>()
            .When(new MoneyWithdrawn(Account, 5m, 5m))).Message.ShouldContain("doesn't correlate");

        // A message that correlates to another instance: a service would apply it there, not here.
        ProcessManagerSpec<MoneyTransfer, TransferId> started = ProcessManagerSpec.For<MoneyTransfer, TransferId, TransfersModule>().WithModule<AccountsModule>()
            .Given(new TransferRequested(transfer, Account, Other, 5m));
        started.Id.ShouldBe(transfer);
        Should.Throw<SpecificationException>(() => started.When(new TransferRequested(other, Account, Other, 5m))).Message.ShouldContain("not to this spec's");

        // A message the process manager doesn't handle is never delivered to it.
        Should.Throw<SpecificationException>(() => started.When(new OpenAccount(Account, "Ada", 1m))).Message.ShouldContain("doesn't handle");

        // Sending a command whose contract the service doesn't register fails, as at runtime.
        Should.Throw<SourceFlow.Messaging.UnknownMessageTypeException>(() => ProcessManagerSpec.For<MoneyTransfer, TransferId, TransfersModule>()
            .When(new TransferRequested(transfer, Account, Other, 5m)));

        // The state is what was stored: written as JSON and read back.
        started.State.Stage.ShouldBe(TransferStage.Withdrawing);
        started.State.Amount.ShouldBe(5m);
    }
}
