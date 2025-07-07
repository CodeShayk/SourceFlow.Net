// ===============================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System.Collections.Generic;

namespace SourceFlow.Core
{
    public abstract class AggregateRoot<TAggregate>
        where TAggregate : IAggregate, new()
    {
        private readonly List<IEvent> _uncommittedEvents = new List<IEvent>();
        public IReadOnlyList<IEvent> UncommittedEvents => _uncommittedEvents.AsReadOnly();
        protected TAggregate Aggregate { get; private set; }

        protected AggregateRoot()
        {
            Aggregate = new TAggregate();
        }

        public void MarkEventsAsCommitted()
        {
            _uncommittedEvents.Clear();
        }

        public void LoadFromHistory(IEnumerable<IEvent> events)
        {
            foreach (var @event in events)
            {
                ApplyEvent(@event);
                Aggregate.Version++;
            }
        }

        protected abstract void ApplyEvent(IEvent @event);
    }
}