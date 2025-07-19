using Microsoft.Extensions.Logging;
using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.ConsoleApp.Commands;
using SourceFlow.ConsoleApp.Events;
using SourceFlow.Saga;

namespace SourceFlow.ConsoleApp.Sagas
{
    public class AccountSaga : BaseSaga<BankAccount>,
                               IHandles<CreateAccount>,
                               IHandles<ActivateAccount>,
                               IHandles<DepositMoney>,
                               IHandles<WithdrawMoney>,
                               IHandles<CloseAccount>
    {
        public async Task Handle(CreateAccount command)
        {
            logger.LogInformation("Action=Account_Created, Account={AccountId}, Holder={AccountName}, Initial_Balance={InitialBalance}",
                command.Payload.Id, command.Payload.AccountName, command.Payload.InitialAmount);

            if (string.IsNullOrEmpty(command.Payload.AccountName))
                throw new ArgumentException("Account create requires account holder name.", nameof(command.Payload.AccountName));

            if (command.Payload.InitialAmount <= 0)
                throw new ArgumentException("Account create requires initial amount.", nameof(command.Payload.InitialAmount));

            var account = new BankAccount
            {
                Id = command.Payload.Id,
                AccountName = command.Payload.AccountName,
                Balance = command.Payload.InitialAmount
            };

            await repository.Persist(account);

            await Raise(new AccountCreated(account));
        }

        public async Task Handle(ActivateAccount command)
        {
            logger.LogInformation("Action=Account_Activate, ActivatedOn={ActiveOn}, Account={AccountId}", command.Payload.ActiveOn, command.Payload.Id);

            var account = await repository.Get<BankAccount>(command.Payload.Id);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (command.Payload.ActiveOn == DateTime.MinValue)
                throw new ArgumentException("Deposit amount must be positive", nameof(command.Payload.ActiveOn));

            account.ActiveOn = command.Payload.ActiveOn;

            await repository.Persist(account);

            await Raise(new AccountUpdated(account));
        }

        public async Task Handle(DepositMoney command)
        {
            logger.LogInformation("Action=Money_Deposited, Amount={Amount}, Account={AccountId}", command.Payload.Amount, command.Payload.Id);

            var account = await repository.Get<BankAccount>(command.Payload.Id);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (command.Payload.Amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(command.Payload.Amount));

            command.Payload.CurrentBalance = account.Balance + command.Payload.Amount;
            account.Balance = command.Payload.CurrentBalance;

            await repository.Persist(account);

            await Raise(new AccountUpdated(account));
        }

        public async Task Handle(WithdrawMoney command)
        {
            logger.LogInformation("Action=Money_Withdrawn, Amount={Amount}, Account={AccountId}", command.Payload.Amount, command.Payload.Id);

            var account = await repository.Get<BankAccount>(command.Payload.Id);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (command.Payload.Amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(command.Payload.Amount));

            command.Payload.CurrentBalance = account.Balance - command.Payload.Amount;
            account.Balance = command.Payload.CurrentBalance;

            await repository.Persist(account);

            await Raise(new AccountUpdated(account));
        }

        public async Task Handle(CloseAccount command)
        {
            logger.LogInformation("Action=Account_Closed, Account={AccountId}, Reason={Reason}", command.Payload.Id, command.Payload.ClosureReason);

            if (string.IsNullOrWhiteSpace(command.Payload.ClosureReason))
                throw new ArgumentException("Reason for closing cannot be empty", nameof(command.Payload.ClosureReason));

            var account = await repository.Get<BankAccount>(command.Payload.Id);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot close account on a closed account");

            account.ClosureReason = command.Payload.ClosureReason;
            account.IsClosed = command.Payload.IsClosed = true;

            await repository.Persist(account);

            await Raise(new AccountUpdated(account));
        }
    }
}