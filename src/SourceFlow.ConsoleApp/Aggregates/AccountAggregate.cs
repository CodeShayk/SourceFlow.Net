using SourceFlow.Aggregate;
using SourceFlow.ConsoleApp.Commands;
using SourceFlow.ConsoleApp.Events;
using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Aggregates
{
    public class AccountAggregate : BaseAggregate<BankAccount>,
                                    ISubscribes<AccountCreated>

    {
        public void CreateAccount(int accountId, string holder, decimal amount)
        {
            Send(new CreateAccount
            {
                Entity = new Source(accountId, typeof(BankAccount)),
                Payload = new AccountPayload
                {
                    AccountName = holder,
                    InitialAmount = amount
                }
            });
        }

        public void Deposit(int accountId, decimal amount)
        {
            Send(Command.For<BankAccount>(accountId)
                              .Create<DepositMoney, TransactPayload>(new TransactPayload
                              {
                                  Amount = amount,
                                  Type = TransactionType.Deposit
                              }));
        }

        public void Withdraw(int accountId, decimal amount)
        {
            Send(Command.For<BankAccount>(accountId)
                              .Create<WithdrawMoney, TransactPayload>(new TransactPayload
                              {
                                  Amount = amount,
                                  Type = TransactionType.Withdrawal
                              }));
        }

        public void Close(int accountId, string reason)
        {
            Send(Command.For<BankAccount>(accountId)
                              .Create<CloseAccount, ClosurePayload>(new ClosurePayload
                              {
                                  ClosureReason = reason
                              }));
        }

        public Task Handle(AccountCreated @event)
        {
            return Send(Command.For<BankAccount>(@event.Payload.Id)
                                    .Create<ActivateAccount, ActivationPayload>(new ActivationPayload
                                    {
                                        ActiveOn = DateTime.UtcNow,
                                    }));
        }
    }
}