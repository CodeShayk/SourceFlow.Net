using Bank.Accounts;
using SourceFlow.Tests.Shared;

namespace SourceFlow.Core.Tests.Contracts;

/// <summary>UT-ABS-01..18 (plan 05 §1).</summary>
[Trait("Category", "Unit")]
public sealed class ContractTests
{
    private static readonly AccountId Account = AccountId.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    [Fact]
    [Trait("Id", "UT-ABS-01")]
    [Trait("Covers", "ADR-003")]
    public void Decision_Accept_Single_ExposesEvent()
    {
        var e = new MoneyDeposited(Account, 10m, 10m);

        Decision decision = Decision.Accept(e);

        decision.Kind.ShouldBe(DecisionKind.Accepted);
        decision.Events.ToArray().ShouldBe([e]);
        decision.Error.ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-ABS-02")]
    [Trait("Covers", "ADR-003")]
    public void Decision_Accept_EmptySpan_Throws()
    {
        Should.Throw<ArgumentException>(() => Decision.Accept(ReadOnlySpan<IEvent>.Empty));
    }

    [Fact]
    [Trait("Id", "UT-ABS-03")]
    public void Decision_Accept_NullEvent_Throws()
    {
        Should.Throw<ArgumentNullException>(() => Decision.Accept((IEvent)null!));
        Should.Throw<ArgumentException>(() => Decision.Accept(new MoneyDeposited(Account, 1, 1), null!));
    }

    [Fact]
    [Trait("Id", "UT-ABS-04")]
    [Trait("Covers", "ADR-018")]
    public void Decision_Reject_NullError_Throws()
    {
        Should.Throw<ArgumentNullException>(() => Decision.Reject(null!));
    }

    [Fact]
    [Trait("Id", "UT-ABS-05")]
    [Trait("Covers", "ADR-018")]
    public void Decision_NoOp_HasNoEventsOrError()
    {
        Decision decision = Decision.NoOp;

        decision.Kind.ShouldBe(DecisionKind.NoOp);
        decision.Events.IsEmpty.ShouldBeTrue();
        decision.Error.ShouldBeNull();
    }

    [Fact]
    [Trait("Id", "UT-ABS-06")]
    public void DecisionContext_IsNew_WhenVersionZero()
    {
        var headers = new Dictionary<string, string>();
        var fresh = new DecisionContext(DateTimeOffset.UnixEpoch, StreamVersion.None, MessageId.Empty, CorrelationId.None, headers);
        var existing = fresh with { Version = new StreamVersion(3) };

        fresh.IsNew.ShouldBeTrue();
        existing.IsNew.ShouldBeFalse();
    }

    [Theory]
    [Trait("Id", "UT-ABS-07")]
    [Trait("Covers", "ADR-018")]
    [InlineData(CommandStatus.Rejected)]
    [InlineData(CommandStatus.Conflict)]
    public void CommandResult_ThrowIfNotAccepted_ThrowsForRejectedAndConflict(CommandStatus status)
    {
        var result = new CommandResult(status, StreamVersion.None, AccountErrors.NotOpen, ConsistencyToken.None);

        CommandFailedException failure = Should.Throw<CommandFailedException>(() => result.ThrowIfNotAccepted());
        failure.Result.ShouldBe(result);
    }

    [Theory]
    [Trait("Id", "UT-ABS-08")]
    [InlineData(CommandStatus.Accepted)]
    [InlineData(CommandStatus.NoOp)]
    [InlineData(CommandStatus.Duplicate)]
    [InlineData(CommandStatus.Dispatched)]
    public void CommandResult_IsSuccess_ForAcceptedNoOpDuplicateDispatched(CommandStatus status)
    {
        var result = new CommandResult(status, new StreamVersion(1), null, ConsistencyToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.ThrowIfNotAccepted().ShouldBe(result);
    }

    [Theory]
    [Trait("Id", "UT-ABS-09")]
    [InlineData(17UL, 3UL, null)]
    [InlineData(18446744073709551615UL, 0UL, "tenant-eu")]
    public void ConsistencyToken_RoundTripsThroughString(ulong commit, ulong sequence, string? tenant)
    {
        var token = new ConsistencyToken(new GlobalPosition(commit, sequence), tenant);

        ConsistencyToken.TryParse(token.ToString(), out ConsistencyToken parsed).ShouldBeTrue();
        parsed.ShouldBe(token);
        ConsistencyToken.TryParse("not-a-token", out _).ShouldBeFalse();
        ConsistencyToken.TryParse("1:2@", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-ABS-10")]
    [Trait("Covers", "DC-3;ADR-020")]
    public void Reaction_Send_AndSend_Schedule_AndBecome_AndComplete_Accumulate()
    {
        var withdraw = new Withdraw(Account, 5m);
        var deposit = new Deposit(Account, 5m);
        var state = new object();
        DateTimeOffset at = DateTimeOffset.UnixEpoch.AddMinutes(5);

        Reaction reaction = Reaction.Send(withdraw).AndSend(deposit).Schedule(deposit, at).AndBecome(state).AndComplete();

        reaction.Commands.ShouldBe([withdraw, deposit]);
        reaction.Scheduled.ShouldBe([new ScheduledMessage(deposit, at)]);
        reaction.NewState.ShouldBeSameAs(state);
        reaction.Completes.ShouldBeTrue();
        reaction.IsIgnored.ShouldBeFalse();
    }

    [Fact]
    [Trait("Id", "UT-ABS-11")]
    [Trait("Covers", "ADR-020")]
    public void Reaction_Ignore_IsIgnored()
    {
        Reaction.Ignore.IsIgnored.ShouldBeTrue();
        Reaction.Ignore.Commands.ShouldBeEmpty();
        Reaction.Become(new object()).IsIgnored.ShouldBeFalse();
        Reaction.Complete().Completes.ShouldBeTrue();
    }

    [Fact]
    [Trait("Id", "UT-ABS-12")]
    public void Reaction_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => Reaction.Send(null!));
        Should.Throw<ArgumentNullException>(() => Reaction.Become(null!));
        Should.Throw<ArgumentNullException>(() => Reaction.Complete().AndSend(null!));
        Should.Throw<ArgumentNullException>(() => Reaction.Complete().Schedule(null!, DateTimeOffset.UnixEpoch));
        Should.Throw<ArgumentNullException>(() => Reaction.Complete().AndBecome(null!));
    }

    [Fact]
    [Trait("Id", "UT-ABS-13")]
    public void EventContext_AggregateKey_IsSegmentAfterLastDash()
    {
        StreamId stream = StreamId.For("bank-account", Account);
        var context = new EventContext(MessageId.Empty, stream, new StreamVersion(1), GlobalPosition.Start, DateTimeOffset.UnixEpoch, CorrelationId.None, null, null);

        context.AggregateKey.ShouldBe(Account.ToStreamSegment());
        AccountId.Parse(context.AggregateKey).ShouldBe(Account);
    }

    [Fact]
    [Trait("Id", "UT-ABS-14")]
    [Trait("Covers", "ADR-005")]
    public void MessageTypeAttribute_DefaultsVersionToOne()
    {
        new MessageTypeAttribute("a.b").Version.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-ABS-15")]
    [Trait("Covers", "ADR-016")]
    public void ProjectionAttribute_DefaultsVersionToOne()
    {
        new ProjectionAttribute("p").Version.ShouldBe(1);
    }

    [Fact]
    [Trait("Id", "UT-ABS-16")]
    [Trait("Covers", "ADR-015")]
    public void Error_InvalidCommand_HasStableCode()
    {
        Error.InvalidCommand("x").Code.ShouldBe("sourceflow.invalid_command");
    }

    [Fact]
    [Trait("Id", "UT-ABS-17")]
    public void UnhandledMessageException_CarriesType()
    {
        var exception = new UnhandledMessageException(typeof(Deposit));

        exception.MessageType.ShouldBe(typeof(Deposit));
        exception.Message.ShouldContain(nameof(Deposit));
    }

    [Fact]
    [Trait("Id", "UT-ABS-18")]
    [Trait("Category", "PublicApi")]
    public void PublicApi_IsApproved()
    {
        PublicApiApproval.AssertApproved(typeof(ICommand).Assembly);
    }

    [Fact]
    public void StreamId_RejectsInvalidValues()
    {
        Should.Throw<ArgumentException>(() => new StreamId(" "));
        Should.Throw<ArgumentException>(() => new StreamId(new string('a', StreamId.MaxLength + 1)));
        Should.Throw<ArgumentException>(() => new StreamId("has space"));
        Should.Throw<ArgumentException>(() => StreamId.For("bank-account", default(AccountId)));
    }

    [Fact]
    public void ExpectedVersion_IsSatisfiedBy()
    {
        ExpectedVersion.Any.IsSatisfiedBy(new StreamVersion(5)).ShouldBeTrue();
        ExpectedVersion.NoStream.IsSatisfiedBy(StreamVersion.None).ShouldBeTrue();
        ExpectedVersion.NoStream.IsSatisfiedBy(new StreamVersion(1)).ShouldBeFalse();
        ExpectedVersion.StreamExists.IsSatisfiedBy(StreamVersion.None).ShouldBeFalse();
        ExpectedVersion.Exact(new StreamVersion(2)).IsSatisfiedBy(new StreamVersion(2)).ShouldBeTrue();
        ExpectedVersion.Exact(StreamVersion.None).ShouldBe(ExpectedVersion.NoStream);
    }

    [Fact]
    public void GlobalPosition_OrdersLexicographically()
    {
        new GlobalPosition(1, 9).ShouldBeLessThan(new GlobalPosition(2, 0));
        new GlobalPosition(2, 1).ShouldBeGreaterThan(new GlobalPosition(2, 0));
        GlobalPosition.TryParse("x:1", out _).ShouldBeFalse();
    }

    [Fact]
    public void BankAccount_Decide_FollowsTheV2Lifecycle()
    {
        var context = new DecisionContext(DateTimeOffset.UnixEpoch, StreamVersion.None, MessageId.Empty, CorrelationId.None, new Dictionary<string, string>());
        BankAccount state = BankAccount.Initial;

        state.Decide(new OpenAccount(Account, "Ada", 0), context).Error.ShouldBe(AccountErrors.InitialDepositRequired);
        Decision opened = state.Decide(new OpenAccount(Account, "Ada", 100), context);
        state = state.Evolve(opened.Events[0]);
        state.Balance.ShouldBe(100);
        state.Decide(new Withdraw(Account, 101), context).Error.ShouldBe(AccountErrors.InsufficientFunds);
        state.Decide(new CloseAccount(Account, "x"), context).Kind.ShouldBe(DecisionKind.Accepted);
        state.Evolve(new AccountClosed(Account, "x", DateTimeOffset.UnixEpoch)).Decide(new CloseAccount(Account, "x"), context).Kind.ShouldBe(DecisionKind.NoOp);
    }
}
