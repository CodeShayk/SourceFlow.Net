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
            //return PublishAsync(new AccountCreated
            //{
            //    AggregateId = @event.AggregateId,
            //    InitialBalance = @event.InitialBalance
            //});
            throw new NotImplementedException();
        }

        public Task HandleAsync(MoneyDeposited @event)
        {
            throw new NotImplementedException();
        }

        public Task HandleAsync(MoneyWithdrawn @event)
        {
            throw new NotImplementedException();
        }

        public Task HandleAsync(AccountClosed @event)
        {
            throw new NotImplementedException();
        }
    }
}