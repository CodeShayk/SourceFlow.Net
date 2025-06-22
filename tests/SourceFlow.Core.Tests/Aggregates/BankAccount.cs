using SourceFlow.Core;
using SourceFlow.Core.Tests.Events;

namespace SourceFlow.Core.Tests.Aggregates
{
    // ====================================================================================
    // DOMAIN AGGREGATE
    // ====================================================================================

    public class BankAccount : AggregateRoot
    {
        public string AccountHolderName { get; private set; } = string.Empty;
        public decimal Balance { get; private set; }
        public bool IsClosed { get; private set; }

        // Required for repository reconstruction
        public BankAccount()
        { }

        // Factory method for creating new accounts
        public static BankAccount Create(string accountId, string accountHolderName, decimal initialBalance)
        {
            if (string.IsNullOrWhiteSpace(accountId))
                throw new ArgumentException("Account ID cannot be empty", nameof(accountId));

            if (string.IsNullOrWhiteSpace(accountHolderName))
                throw new ArgumentException("Account holder name cannot be empty", nameof(accountHolderName));

            if (initialBalance < 0)
                throw new ArgumentException("Initial balance cannot be negative", nameof(initialBalance));

            var account = new BankAccount();
            account.RaiseEvent(new BankAccountCreated
            {
                AccountId = accountId,
                AccountHolderName = accountHolderName,
                InitialBalance = initialBalance
            });

            return account;
        }

        public void Deposit(decimal amount)
        {
            if (IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(amount));

            var newBalance = Balance + amount;

            RaiseEvent(new MoneyDeposited
            {
                AccountId = Id,
                Amount = amount,
                NewBalance = newBalance
            });
        }

        public void Withdraw(decimal amount)
        {
            if (IsClosed)
                throw new InvalidOperationException("Cannot withdraw from a closed account");

            if (amount <= 0)
                throw new ArgumentException("Withdrawal amount must be positive", nameof(amount));

            if (amount > Balance)
                throw new InvalidOperationException("Insufficient funds");

            var newBalance = Balance - amount;

            RaiseEvent(new MoneyWithdrawn
            {
                AccountId = Id,
                Amount = amount,
                NewBalance = newBalance
            });
        }

        public void Close(string reason)
        {
            if (IsClosed)
                throw new InvalidOperationException("Account is already closed");

            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Reason for closing cannot be empty", nameof(reason));

            RaiseEvent(new AccountClosed
            {
                AccountId = Id,
                Reason = reason
            });
        }

        protected override void ApplyEvent(IEvent @event)
        {
            switch (@event)
            {
                case BankAccountCreated created:
                    Id = created.AccountId;
                    AccountHolderName = created.AccountHolderName;
                    Balance = created.InitialBalance;
                    break;

                case MoneyDeposited deposited:
                    Balance = deposited.NewBalance;
                    break;

                case MoneyWithdrawn withdrawn:
                    Balance = withdrawn.NewBalance;
                    break;

                case AccountClosed closed:
                    IsClosed = true;
                    break;
            }
        }
    }
}