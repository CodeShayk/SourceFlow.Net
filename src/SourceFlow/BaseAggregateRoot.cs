using System.Threading.Tasks;

namespace SourceFlow
{
    public abstract class BaseAggregateRoot : IAggregateRoot
    {
        public IIdentity State { get; protected set; }
        protected ISagaBus sagaBus { get; }

        protected BaseAggregateRoot(ISagaBus sagaBus, IIdentity state)
        {
            State = state;
            this.sagaBus = sagaBus;
        }

        public int SequenceNo { get; set; }

        public abstract Task ApplyAsync(IDomainEvent @event);

        public Task ReplayAllEvents()
        {
            return sagaBus.Replay(this);
        }

        protected Task PublishAsync(IDomainEvent @event)
        {
            if (@event == null)
                return Task.CompletedTask;

            @event.Source = this;
            return sagaBus.PublishAsync(@event);
        }
    }
}