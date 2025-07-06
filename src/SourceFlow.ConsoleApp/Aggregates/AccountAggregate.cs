using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.Aggregates
{
    public class AccountAggregate : BaseAggregate<BankAccount>
    {
        public void CreateAccount(int accountId, string holder, decimal amount)
        {
            PublishAsync(Event.For<BankAccount>(accountId)
                              .Create<AccountCreated, AccountPayload>(new AccountPayload
                              {
                                  AccountName = holder,
                                  InitialAmount = amount
                              }));
        }

        public void Deposit(int accountId, decimal amount)
        {
            PublishAsync(Event.For<BankAccount>(accountId)
                              .Create<MoneyDeposited, TransactPayload>(new TransactPayload
                              {
                                  Amount = amount
                              }));
        }

        public void Withdraw(int accountId, decimal amount)
        {
            PublishAsync(Event.For<BankAccount>(accountId)
                              .Create<MoneyWithdrawn, TransactPayload>(new TransactPayload
                              {
                                  Amount = amount
                              }));
        }

        public void Close(int accountId, string reason)
        {
            PublishAsync(Event.For<BankAccount>(accountId)
                              .Create<AccountClosed, ClosurePayload>(new ClosurePayload
                              {
                                  IsClosed = true,
                                  ClosureReason = reason
                              }));
        }
    }
}