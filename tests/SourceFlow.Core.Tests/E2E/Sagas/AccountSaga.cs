using Microsoft.Extensions.Logging;
using SourceFlow.Core.Tests.E2E.Aggregates;
using SourceFlow.Core.Tests.E2E.Commands;
using SourceFlow.Core.Tests.E2E.Events;
using SourceFlow.Messaging.Commands;
using SourceFlow.Messaging.Events;
using SourceFlow.Saga;

namespace SourceFlow.Core.Tests.E2E.Sagas
{
    public class AccountSaga : Saga<BankAccount>,
                               IHandlesWithEvent<CreateAccount, AccountCreated>,
                               IHandlesWithEvent<DepositMoney, AccountUpdated>,
                               IHandlesWithEvent<WithdrawMoney, AccountUpdated>,
                               IHandlesWithEvent<CloseAccount, AccountUpdated>,
                               IHandles<ActivateAccount>
    {
        public AccountSaga(Lazy<ICommandPublisher> commandPublisher, IEventQueue eventQueue, IEntityStoreAdapter repository, ILogger<ISaga> logger) :
            base(commandPublisher, eventQueue, repository, logger)
        {
        }

        public Task Handle(IEntity entity, CreateAccount command)
        {
            logger.LogInformation("Action=Account_Created, Account={AccountId}, Holder={AccountName}, Initial_Balance={InitialBalance}",
                command.Entity.Id, command.Payload.AccountName, command.Payload.InitialAmount);

            if (string.IsNullOrEmpty(command.Payload.AccountName))
                throw new ArgumentException("Account create requires account holder name.", nameof(command.Payload.AccountName));

            if (command.Payload.InitialAmount <= 0)
                throw new ArgumentException("Account create requires initial amount.", nameof(command.Payload.InitialAmount));

            var account = (BankAccount)entity;

            account.AccountName = command.Payload.AccountName;
            account.Balance = command.Payload.InitialAmount;

            return Task.CompletedTask;
        }

        public Task Handle(IEntity entity, ActivateAccount command)
        {
            logger.LogInformation("Action=Account_Activate, ActivatedOn={ActiveOn}, Account={AccountId}", command.Payload.ActiveOn, command.Entity.Id);

            if (command.Payload.ActiveOn == DateTime.MinValue)
                throw new ArgumentException("Deposit amount must be positive", nameof(command.Payload.ActiveOn));

            var account = (BankAccount)entity;

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            account.ActiveOn = command.Payload.ActiveOn;

            return Task.CompletedTask;
        }

        public Task Handle(IEntity entity, DepositMoney command)
        {
            logger.LogInformation("Action=Money_Deposited, Amount={Amount}, Account={AccountId}", command.Payload.Amount, command.Entity.Id);

            if (command.Payload.Amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(command.Payload.Amount));

            var account = (BankAccount)entity;

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            command.Payload.CurrentBalance = account.Balance + command.Payload.Amount;
            account.Balance = command.Payload.CurrentBalance;

            return Task.CompletedTask;
        }

        public Task Handle(IEntity entity, WithdrawMoney command)
        {
            logger.LogInformation("Action=Money_Withdrawn, Amount={Amount}, Account={AccountId}", command.Payload.Amount, command.Entity.Id);

            if (command.Payload.Amount <= 0)
                throw new ArgumentException("Withdrawal amount must be positive", nameof(command.Payload.Amount));

            var account = (BankAccount)entity;

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            command.Payload.CurrentBalance = account.Balance - command.Payload.Amount;
            account.Balance = command.Payload.CurrentBalance;

            return Task.CompletedTask;
        }

        public Task Handle(IEntity entity, CloseAccount command)
        {
            logger.LogInformation("Action=Account_Closed, Account={AccountId}, Reason={Reason}", command.Entity.Id, command.Payload.ClosureReason);

            if (string.IsNullOrWhiteSpace(command.Payload.ClosureReason))
                throw new ArgumentException("Reason for closing cannot be empty", nameof(command.Payload.ClosureReason));

            var account = (BankAccount)entity;

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot close account on a closed account");

            account.ClosureReason = command.Payload.ClosureReason;
            account.IsClosed = command.Payload.IsClosed = true;

            return Task.CompletedTask;
        }
    }
}