using System.Text.Json.Serialization;
using Bank.Contracts;
using SourceFlow;

namespace Bank.Accounts;

/// <summary>The account summary read model (ported from the v2 AccountView).</summary>
public sealed record AccountSummary(AccountId Id, string Holder, decimal Balance, AccountStatus Status, long Version);

/// <summary>Maintains <see cref="AccountSummary"/> documents.</summary>
[Projection("account-summary")]
public sealed partial class AccountSummaryProjection : IDocumentProjection<AccountSummary, AccountId>
{
    private static AccountSummary When(AccountOpened e, AccountSummary? current, EventContext ctx)
    {
        return new AccountSummary(e.AccountId, e.Holder, e.InitialDeposit, AccountStatus.Open, ctx.StreamVersion.Value);
    }

    private static AccountSummary? When(MoneyDeposited e, AccountSummary? current, EventContext ctx)
    {
        return current is null ? null : current with { Balance = e.BalanceAfter, Version = ctx.StreamVersion.Value };
    }

    private static AccountSummary? When(MoneyWithdrawn e, AccountSummary? current, EventContext ctx)
    {
        return current is null ? null : current with { Balance = e.BalanceAfter, Version = ctx.StreamVersion.Value };
    }

    private static AccountSummary? When(AccountClosed e, AccountSummary? current, EventContext ctx)
    {
        return current is null ? null : current with { Status = AccountStatus.Closed, Version = ctx.StreamVersion.Value };
    }
}

/// <summary>
/// Accounts below a balance: a named, versioned domain query (doc 14 Q-B4), the stable contract other services and UIs
/// use instead of ad-hoc queries.
/// </summary>
/// <param name="Below">The balance threshold.</param>
[DomainQuery("accounts.low-balance", 1)]
public sealed partial record LowBalanceAccounts(decimal Below) : IDomainQuery<LowBalanceAccount>
{
    /// <summary>The query text.</summary>
    public const string Sfql = """
        from readmodel "account-summary"
        | where status = 1 and balance < @below
        | project accountId = id, holder, balance
        | order by balance
        """;
}

/// <summary>A row of <see cref="LowBalanceAccounts"/>.</summary>
/// <param name="AccountId">The account.</param>
/// <param name="Holder">The holder.</param>
/// <param name="Balance">The balance.</param>
public sealed record LowBalanceAccount(AccountId AccountId, string Holder, decimal Balance);

/// <summary>Maps domain events to the public contract (doc 03 §7).</summary>
public static class AccountContracts
{
    /// <summary>Maps <see cref="AccountOpened"/> to <see cref="AccountOpenedV1"/>.</summary>
    public static AccountOpenedV1 ToV1(AccountOpened e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new AccountOpenedV1(e.AccountId.Value, e.OpenedAt);
    }
}

/// <summary>The Accounts module: the generator registers everything in this assembly.</summary>
[SourceFlowModule(typeof(AccountsJsonContext))]
public sealed partial class AccountsModule;

/// <summary>Source-generated JSON metadata for every message, document and state type (DC-1).</summary>
[JsonSerializable(typeof(OpenAccount))]
[JsonSerializable(typeof(Deposit))]
[JsonSerializable(typeof(Withdraw))]
[JsonSerializable(typeof(CloseAccount))]
[JsonSerializable(typeof(AccountOpened))]
[JsonSerializable(typeof(MoneyDeposited))]
[JsonSerializable(typeof(MoneyWithdrawn))]
[JsonSerializable(typeof(AccountClosed))]
[JsonSerializable(typeof(AccountSummary))]
[JsonSerializable(typeof(BankAccount))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class AccountsJsonContext : JsonSerializerContext;
