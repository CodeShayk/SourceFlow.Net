using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.Aggregates
{
    public class AccountAggregate : BaseAggregate<BankAccount>
    {
        public void CreateAccount(int accountId, string holder, decimal amount)
        {
            PublishAsync(new AccountCreated(new Source(accountId, typeof(BankAccount)))
            {
                AccountName = holder,
                InitialBalance = amount
            });
        }

        public void Deposit(int accountId, decimal amount)
        {
            PublishAsync(new MoneyDeposited(new Source(accountId, typeof(BankAccount)))
            {
                Amount = amount,
            });
        }

        public void Withdraw(int accountId, decimal amount)
        {
            PublishAsync(new MoneyWithdrawn(new Source(accountId, typeof(BankAccount)))
            {
                Amount = amount
            });
        }

        public void Close(int accountId, string reason)
        {
            PublishAsync(new AccountClosed(new Source(accountId, typeof(BankAccount)))
            {
                Reason = reason
            });
        }
    }
}