using Microsoft.Extensions.Logging;
using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.Sagas
{
    public class AccountSaga : BaseSaga<BankAccount>,
                               IEventHandler<AccountCreated>,
                               IEventHandler<MoneyDeposited>,
                               IEventHandler<MoneyWithdrawn>,
                               IEventHandler<AccountClosed>
    {
        public async Task HandleAsync(AccountCreated @event)
        {
            logger.LogInformation("Account created: {AccountId} for {AccountName} with initial balance: {InitialBalance}",
                @event.Entity.Id, @event.Payload.AccountName, @event.Payload.InitialAmount);

            if (string.IsNullOrEmpty(@event.Payload.AccountName))
                throw new ArgumentException("Account create requires account holder name.", nameof(@event.Payload.AccountName));

            if (@event.Payload.InitialAmount <= 0)
                throw new ArgumentException("Account create requires initial amount.", nameof(@event.Payload.InitialAmount));

            var account = new BankAccount
            {
                Id = @event.Entity.Id,
                AccountName = @event.Payload.AccountName,
                Balance = @event.Payload.InitialAmount
            };

            await PersistAggregate(account);
        }

        public async Task HandleAsync(MoneyDeposited @event)
        {
            logger.LogInformation("Money deposited: {Amount} to account: {AccountId}", @event.Payload.Amount, @event.Entity.Id);

            var account = await GetAggregate(@event.Entity.Id);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (@event.Payload.Amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(@event.Payload.Amount));

            var newBalance = account.Balance + @event.Payload.Amount;
            account.Balance = newBalance;

            await PersistAggregate(account);
        }

        public async Task HandleAsync(MoneyWithdrawn @event)
        {
            logger.LogInformation("Money withdrawn: {Amount} from account: {AccountId}", @event.Payload.Amount, @event.Entity.Id);

            var account = await GetAggregate(@event.Entity.Id);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (@event.Payload.Amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(@event.Payload.Amount));

            var newBalance = account.Balance - @event.Payload.Amount;
            account.Balance = newBalance;

            await PersistAggregate(account);
        }

        public async Task HandleAsync(AccountClosed @event)
        {
            logger.LogInformation("Account closed: {AccountId} for reason: {Reason}", @event.Entity.Id, @event.Payload.ClosureReason);

            if (string.IsNullOrWhiteSpace(@event.Payload.ClosureReason))
                throw new ArgumentException("Reason for closing cannot be empty", nameof(@event.Payload.ClosureReason));

            var account = await GetAggregate(@event.Entity.Id);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot close account on a closed account");

            account.ClosureReason = @event.Payload.ClosureReason;

            await PersistAggregate(account);
        }
    }
}