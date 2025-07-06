using Microsoft.Extensions.Logging;
using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.Sagas
{
    public class AccountSaga : BaseSaga<BankAccount>
                                 , IEventHandler<AccountCreated>
                                 , IEventHandler<MoneyDeposited>
                                 , IEventHandler<MoneyWithdrawn>
                                 , IEventHandler<AccountClosed>
    {
        public async Task HandleAsync(AccountCreated @event)
        {
            logger.LogInformation("Account created: {AccountId} for {AccountName} with initial balance: {InitialBalance}",
                @event.AggregateId, @event.AccountName, @event.InitialBalance);

            if (string.IsNullOrEmpty(@event.AccountName))
                throw new ArgumentException("Account create requires account holder name.", nameof(@event.AccountName));

            if (@event.InitialBalance <= 0)
                throw new ArgumentException("Account create requires initial amount.", nameof(@event.InitialBalance));

            var account = new BankAccount
            {
                Id = @event.AggregateId,
                AccountName = @event.AccountName,
                Balance = @event.InitialBalance
            };

            await PersistAggregate(account);
        }

        public async Task HandleAsync(MoneyDeposited @event)
        {
            logger.LogInformation("Money deposited: {Amount} to account: {AccountId}", @event.Amount, @event.AggregateId);

            var account = await GetAggregate(@event.AggregateId);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (@event.Amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(@event.Amount));

            var newBalance = account.Balance + @event.Amount;
            account.Balance = newBalance;

            await PersistAggregate(account);
        }

        public async Task HandleAsync(MoneyWithdrawn @event)
        {
            logger.LogInformation("Money withdrawn: {Amount} from account: {AccountId}", @event.Amount, @event.AggregateId);

            var account = await GetAggregate(@event.AggregateId);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot deposit to a closed account");

            if (@event.Amount <= 0)
                throw new ArgumentException("Deposit amount must be positive", nameof(@event.Amount));

            var newBalance = account.Balance - @event.Amount;
            account.Balance = newBalance;

            await PersistAggregate(account);
        }

        public async Task HandleAsync(AccountClosed @event)
        {
            logger.LogInformation("Account closed: {AccountId} for reason: {Reason}", @event.AggregateId, @event.Reason);

            if (string.IsNullOrWhiteSpace(@event.Reason))
                throw new ArgumentException("Reason for closing cannot be empty", nameof(@event.Reason));

            var account = await GetAggregate(@event.AggregateId);

            if (account.IsClosed)
                throw new InvalidOperationException("Cannot close account on a closed account");

            account.ClosureReason = @event.Reason;

            await PersistAggregate(account);
        }
    }
}