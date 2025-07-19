using SourceFlow.ConsoleApp.Commands;

namespace SourceFlow.ConsoleApp.Aggregates
{
    public class AccountAggregate : BaseAggregate<BankAccount>
    {
        public void CreateAccount(int accountId, string holder, decimal amount)
        {
            PublishAsync(Command.For<BankAccount>(accountId)
                              .Create<CreateAccount, AccountPayload>(new AccountPayload
                              {
                                  AccountName = holder,
                                  InitialAmount = amount
                              }));
        }

        public void Deposit(int accountId, decimal amount)
        {
            PublishAsync(Command.For<BankAccount>(accountId)
                              .Create<DepositMoney, TransactPayload>(new TransactPayload
                              {
                                  Amount = amount,
                                  Type = TransactionType.Deposit
                              }));
        }

        public void Withdraw(int accountId, decimal amount)
        {
            PublishAsync(Command.For<BankAccount>(accountId)
                              .Create<WithdrawMoney, TransactPayload>(new TransactPayload
                              {
                                  Amount = amount,
                                  Type = TransactionType.Withdrawal
                              }));
        }

        public void Close(int accountId, string reason)
        {
            PublishAsync(Command.For<BankAccount>(accountId)
                              .Create<CloseAccount, ClosurePayload>(new ClosurePayload
                              {
                                  ClosureReason = reason
                              }));
        }
    }
}