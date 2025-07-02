using System;
using System.Threading.Tasks;

namespace SourceFlow
{
    public abstract class BaseAggregate<TAggregate> : IAggregateRoot
        where TAggregate : class, IIdentity, new()
    {
        public TAggregate State { get; protected set; }
        IIdentity IAggregateRoot.State { get { return State; } set { State = (TAggregate)value; } }

        protected IBusPublisher busPublisher;
        protected IEventReplayer eventReplayer;

        protected BaseAggregate()
        {
            State = new TAggregate();
        }

        public abstract Task ApplyAsync(IEvent @event);

        public Task ReplayEvents()
        {
            return eventReplayer.ReplayEventsAsync(State.Id);
        }

        protected Task PublishAsync(IEvent @event)
        {
            if (@event == null)
                throw new ArgumentNullException(nameof(@event));

            return busPublisher.PublishAsync(@event);
        }
    }
}