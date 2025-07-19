using SourceFlow.Aggregate;
using SourceFlow.ConsoleApp.Commands;
using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.Aggregates
{
    public class AccountAggregate : Aggregate<BankAccount>,
                                    ISubscribes<AccountCreated>

    {
        public void CreateAccount(int accountId, string holder, decimal amount)
        {
            Send(new CreateAccount(new Payload
            {
                Id = accountId,
                AccountName = holder,
                InitialAmount = amount
            }));
        }

        public void Deposit(int accountId, decimal amount)
        {
            Send(new DepositMoney(new TransactPayload
            {
                Id = accountId,
                Amount = amount,
                Type = TransactionType.Deposit
            }));
        }

        public void Withdraw(int accountId, decimal amount)
        {
            Send(new WithdrawMoney(new TransactPayload
            {
                Id = accountId,
                Amount = amount,
                Type = TransactionType.Withdrawal
            }));
        }

        public void Close(int accountId, string reason)
        {
            Send(new CloseAccount(new ClosurePayload
            {
                Id = accountId,
                ClosureReason = reason
            }));
        }

        public Task Handle(AccountCreated @event)
        {
            return Send(new ActivateAccount(new ActivationPayload
            {
                Id = @event.Payload.Id,
                ActiveOn = DateTime.UtcNow,
            }));
        }
    }
}