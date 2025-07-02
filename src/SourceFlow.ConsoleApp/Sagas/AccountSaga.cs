using Microsoft.Extensions.Logging;
using SourceFlow.ConsoleApp.Aggregates;
using SourceFlow.ConsoleApp.Events;

namespace SourceFlow.ConsoleApp.Sagas
{
    public class AccountSaga : BaseSaga<AccountAggregate>
                                 , IEventHandler<AccountCreated>
                                 , IEventHandler<MoneyDeposited>
                                 , IEventHandler<MoneyWithdrawn>
                                 , IEventHandler<AccountClosed>
    {
        public AccountSaga()

        {
            RegisterEventHandler<AccountCreated>(this);
            RegisterEventHandler<MoneyDeposited>(this);
            RegisterEventHandler<MoneyWithdrawn>(this);
            RegisterEventHandler<AccountClosed>(this);
        }

        public Task HandleAsync(AccountCreated @event)
        {
            logger.LogInformation("Account created: {AccountId} for {AccountHolderName} with initial balance: {InitialBalance}",
                @event.AggregateId, @event.AccountHolderName, @event.InitialBalance);

            return PublishAsync(new AccountActive(@event.AggregateId) { });
        }

        public Task HandleAsync(MoneyDeposited @event)
        {
            logger.LogInformation("Money deposited: {Amount} to account: {AccountId}", @event.Amount, @event.AggregateId);
            return Task.CompletedTask;
        }

        public Task HandleAsync(MoneyWithdrawn @event)
        {
            logger.LogInformation("Money withdrawn: {Amount} from account: {AccountId}", @event.Amount, @event.AggregateId);
            return Task.CompletedTask;
        }

        public Task HandleAsync(AccountClosed @event)
        {
            logger.LogInformation("Account closed: {AccountId} for reason: {Reason}", @event.AggregateId, @event.Reason);
            return Task.CompletedTask;
        }
    }
}