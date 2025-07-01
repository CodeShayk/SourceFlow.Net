using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public abstract class BaseAggregateRoot<TAggregate> : IAggregateRoot
        where TAggregate : class, IIdentity, new()
    {
        public TAggregate State { get; protected set; }
        IIdentity IAggregateRoot.State { get { return State; } set { State = (TAggregate)value; } }

        protected ICommandBus MessageBus { get; }

        protected BaseAggregateRoot(ICommandBus messageBus)
        {
            State = new TAggregate();
            this.MessageBus = messageBus;
        }

        public abstract Task ApplyAsync(IEvent @event);

        public Task ReplayAllEvents()
        {
            return MessageBus.Replay(State.Id);
        }

        protected Task PublishAsync(IEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            return MessageBus.PublishAsync(@event);
        }
    }
}