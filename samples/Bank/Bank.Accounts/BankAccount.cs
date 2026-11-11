using SourceFlow;

namespace Bank.Accounts;

/// <summary>The account id (UUIDv7).</summary>
[StronglyTypedId]
public readonly partial record struct AccountId(Guid Value);

/// <summary>The account lifecycle (ported from the v2 E2E BankAccount entity).</summary>
public enum AccountStatus
{
    /// <summary>Not opened yet.</summary>
    None = 0,

    /// <summary>Open for deposits and withdrawals.</summary>
    Open = 1,

    /// <summary>Closed; no further changes.</summary>
    Closed = 2,
}

/// <summary>Opens an account with an initial deposit.</summary>
[MessageType("bank.account.open", 1)]
public sealed record OpenAccount(AccountId AccountId, string Holder, decimal InitialDeposit) : ICommand<BankAccount>;

/// <summary>Deposits money.</summary>
[MessageType("bank.account.deposit", 1)]
public sealed record Deposit(AccountId AccountId, decimal Amount) : ICommand<BankAccount>;

/// <summary>Withdraws money.</summary>
[MessageType("bank.account.withdraw", 1)]
public sealed record Withdraw(AccountId AccountId, decimal Amount) : ICommand<BankAccount>;

/// <summary>Closes the account.</summary>
[MessageType("bank.account.close", 1)]
public sealed record CloseAccount(AccountId AccountId, string Reason) : ICommand<BankAccount>;

/// <summary>The account was opened.</summary>
[MessageType("bank.account.opened", 1)]
public sealed record AccountOpened(AccountId AccountId, [property: PersonalData] string Holder, decimal InitialDeposit, DateTimeOffset OpenedAt) : IEvent;

/// <summary>Money was deposited.</summary>
[MessageType("bank.account.deposited", 1)]
public sealed record MoneyDeposited(AccountId AccountId, decimal Amount, decimal BalanceAfter) : IEvent;

/// <summary>Money was withdrawn.</summary>
[MessageType("bank.account.withdrawn", 1)]
public sealed record MoneyWithdrawn(AccountId AccountId, decimal Amount, decimal BalanceAfter) : IEvent;

/// <summary>The account was closed.</summary>
[MessageType("bank.account.closed", 1)]
public sealed record AccountClosed(AccountId AccountId, string Reason, DateTimeOffset ClosedAt) : IEvent;

/// <summary>The bank account aggregate: pure deciders (ADR-003).</summary>
[Aggregate("bank-account")]
public sealed partial record BankAccount : IAggregate<BankAccount, AccountId>
{
    /// <summary>The state before the first event.</summary>
    public static BankAccount Initial { get; } = new();

    /// <summary>The lifecycle status.</summary>
    public AccountStatus Status { get; init; }

    /// <summary>The current balance.</summary>
    public decimal Balance { get; init; }

    /// <summary>Opens the account.</summary>
    public Decision Decide(OpenAccount c, DecisionContext ctx)
    {
        return Status switch
        {
            AccountStatus.None when c.InitialDeposit <= 0 => Decision.Reject(AccountErrors.InitialDepositRequired),
            AccountStatus.None => Decision.Accept(new AccountOpened(c.AccountId, c.Holder, c.InitialDeposit, ctx.Now)),
            _ => Decision.Reject(AccountErrors.AlreadyOpen),
        };
    }

    /// <summary>Deposits money.</summary>
    public Decision Decide(Deposit c, DecisionContext ctx)
    {
        return this switch
        {
            { Status: not AccountStatus.Open } => Decision.Reject(AccountErrors.NotOpen),
            _ when c.Amount <= 0 => Decision.Reject(AccountErrors.AmountMustBePositive),
            _ => Decision.Accept(new MoneyDeposited(c.AccountId, c.Amount, Balance + c.Amount)),
        };
    }

    /// <summary>Withdraws money.</summary>
    public Decision Decide(Withdraw c, DecisionContext ctx)
    {
        return this switch
        {
            { Status: not AccountStatus.Open } => Decision.Reject(AccountErrors.NotOpen),
            _ when c.Amount <= 0 => Decision.Reject(AccountErrors.AmountMustBePositive),
            _ when c.Amount > Balance => Decision.Reject(AccountErrors.InsufficientFunds),
            _ => Decision.Accept(new MoneyWithdrawn(c.AccountId, c.Amount, Balance - c.Amount)),
        };
    }

    /// <summary>Closes the account; closing a closed account is a no-op.</summary>
    public Decision Decide(CloseAccount c, DecisionContext ctx)
    {
        return Status switch
        {
            AccountStatus.Closed => Decision.NoOp,
            AccountStatus.Open => Decision.Accept(new AccountClosed(c.AccountId, c.Reason, ctx.Now)),
            _ => Decision.Reject(AccountErrors.NotOpen),
        };
    }

    private BankAccount When(AccountOpened e)
    {
        return this with { Status = AccountStatus.Open, Balance = e.InitialDeposit };
    }

    private BankAccount When(MoneyDeposited e)
    {
        return this with { Balance = e.BalanceAfter };
    }

    private BankAccount When(MoneyWithdrawn e)
    {
        return this with { Balance = e.BalanceAfter };
    }

    private BankAccount When(AccountClosed e)
    {
        return this with { Status = AccountStatus.Closed };
    }
}

/// <summary>Business errors of the account aggregate.</summary>
public static class AccountErrors
{
    /// <summary>An initial deposit is required.</summary>
    public static readonly Error InitialDepositRequired = new("account.initial_deposit_required", "An initial deposit is required.");

    /// <summary>The account is already open.</summary>
    public static readonly Error AlreadyOpen = new("account.already_open", "The account is already open.");

    /// <summary>The account is not open.</summary>
    public static readonly Error NotOpen = new("account.not_open", "The account is not open.");

    /// <summary>The amount must be positive.</summary>
    public static readonly Error AmountMustBePositive = new("account.amount_must_be_positive", "Amount must be positive.");

    /// <summary>The balance is too low.</summary>
    public static readonly Error InsufficientFunds = new("account.insufficient_funds", "Insufficient funds.");
}
