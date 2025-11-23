using Microsoft.Extensions.Logging;
using SourceFlow.Aggregate;
using SourceFlow.ConsoleApp.Commands;
using SourceFlow.ConsoleApp.Events;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.ConsoleApp.Aggregates
{
    public class AccountAggregate : Aggregate<BankAccount>,
                                    ISubscribes<AccountCreated>, IAccountAggregate
    {
        public AccountAggregate(Lazy<ICommandPublisher> commandPublisher, ILogger<AccountAggregate> logger)
            : base(commandPublisher, logger)
        {
        }

        public Task CreateAccount(int accountId, string holder, decimal amount)
        {
            return Send(new CreateAccount(new Payload
            {
                AccountName = holder,
                InitialAmount = amount
            }));
        }

        public Task Deposit(int accountId, decimal amount)
        {
            return Send(new DepositMoney(accountId, new TransactPayload
            {
                Amount = amount,
                Type = TransactionType.Deposit
            }));
        }

        public Task Withdraw(int accountId, decimal amount)
        {
            return Send(new WithdrawMoney(accountId, new TransactPayload
            {
                Amount = amount,
                Type = TransactionType.Withdrawal
            }));
        }

        public Task CloseAccount(int accountId, string reason)
        {
            return Send(new CloseAccount(accountId, new ClosurePayload
            {
                ClosureReason = reason
            }));
        }

        public Task Handle(AccountCreated @event)
        {
            return Send(new ActivateAccount(@event.Payload.Id, new ActivationPayload
            {
                ActiveOn = DateTime.UtcNow,
            }));
        }

        public Task RepayHistory(int accountId) {
            return commandPublisher.Value.ReplayCommands(accountId);
        }
    }
}