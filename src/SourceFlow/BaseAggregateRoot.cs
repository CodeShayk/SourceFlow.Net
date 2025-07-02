using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public abstract class BaseAggregateRoot<TAggregate> : IAggregateRoot
        where TAggregate : class, IIdentity, new()
    {
        public TAggregate State { get; protected set; }
        IIdentity IAggregateRoot.State { get { return State; } set { State = (TAggregate)value; } }

        protected IBusPublisher busPublisher;
        protected IBusReplayer busReplayer;

        protected BaseAggregateRoot()
        {
            State = new TAggregate();
        }

        public abstract Task ApplyAsync(IEvent @event);

        public Task ReplayAllEvents()
        {
            return busReplayer.ReplayEventsAsync(State.Id);
        }

        protected Task PublishAsync(IEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            return busPublisher.PublishAsync(@event);
        }
    }
}