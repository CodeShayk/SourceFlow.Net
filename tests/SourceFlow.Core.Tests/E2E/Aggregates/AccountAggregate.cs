using Microsoft.Extensions.Logging;
using SourceFlow.Aggregate;
using SourceFlow.Core.Tests.E2E.Commands;
using SourceFlow.Core.Tests.E2E.Events;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.E2E.Aggregates
{
    public class AccountAggregate : Aggregate<BankAccount>,
                                    ISubscribes<AccountCreated>, IAccountAggregate
    {
        public AccountAggregate(Lazy<ICommandPublisher> commandPublisher, ILogger<IAggregate> logger) :
            base(commandPublisher, logger)
        {
        }

        public Task CreateAccount(int accountId, string holder, decimal amount)
        {
            var command = new CreateAccount(new Payload
            {
                AccountName = holder,
                InitialAmount = amount
            });

            command.Entity.Id = accountId;

            return Send(command);
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
            // To prevent infinite loops, this method does nothing
            // Activation should happen through commands, not through event handling cycles
            return Task.CompletedTask;
        }

        public Task RepayHistory(int accountId)
        {
            return ReplayCommands(accountId);
        }
    }
}