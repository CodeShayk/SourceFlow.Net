using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.Aggregates
{
    // ====================================================================================
    // DOMAIN AGGREGATE
    // ====================================================================================

    public class AccountAggregate : BaseAggregateRoot<BankAccount>
    {
        public AccountAggregate()
        {
        }

        public void AccountCreated()
        {
            PublishAsync(new AccountCreated(State.Id)
            {
                AccountHolderName = State.AccountHolderName,
                InitialBalance = State.Balance
            });
        }

        public void Deposit(decimal amount)
        {
            if (State.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(amount));

            var newBalance = State.Balance + amount;

            base.PublishAsync(new MoneyDeposited(State.Id)
            {
                Amount = amount,
                NewBalance = newBalance
            });
        }

        public void Withdraw(decimal amount)
        {
            if (State.IsClosed)
                throw new InvalidOperationException("Cannot withdraw from a closed account");

            if (amount <= 0)
                throw new ArgumentException("Withdrawal amount must be positive", nameof(amount));

            if (amount > State.Balance)
                throw new InvalidOperationException("Insufficient funds");

            var newBalance = State.Balance - amount;

            PublishAsync(new MoneyWithdrawn(State.Id)
            {
                Amount = amount,
                NewBalance = newBalance
            });
        }

        public void Close(string reason)
        {
            if (State.IsClosed)
                throw new InvalidOperationException("Account is already closed");

            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Reason for closing cannot be empty", nameof(reason));

            PublishAsync(new AccountClosed(State.Id)
            {
                Reason = reason
            });
        }

        public override Task ApplyAsync(IEvent @event)
        {
            switch (@event)
            {
                case AccountCreated created:
                    State.Id = created.AggregateId;
                    State.AccountHolderName = created.AccountHolderName;
                    State.Balance = created.InitialBalance;
                    break;

                case MoneyDeposited deposited:
                    State.Balance = deposited.NewBalance;
                    break;

                case MoneyWithdrawn withdrawn:
                    State.Balance = withdrawn.NewBalance;
                    break;

                case AccountClosed closed:
                    State.IsClosed = true;
                    break;
            }

            return Task.CompletedTask;
        }
    }
}