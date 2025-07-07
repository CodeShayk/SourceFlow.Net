// ====================================================================================
// CORE EVENT SOURCING ABSTRACTIONS
// ====================================================================================

using System.Linq;
using System.Threading.Tasks;

namespace SourceFlow.Core.Impl
{
    public class EventSourcedRepository<T> : IRepository<T> where T : AggregateRoot, new()
    {
        private readonly IEventStore _eventStore;

        public EventSourcedRepository(IEventStore eventStore)
        {
            _eventStore = eventStore;
        }

        public async Task<T> GetByIdAsync(string id)
        {
            var events = await _eventStore.GetEventsAsync(id);

            if (!events.Any())
                return null;

            var aggregate = new T();
            aggregate.LoadFromHistory(events);

            return aggregate;
        }

        public async Task SaveAsync(T aggregate)
        {
            var uncommittedEvents = aggregate.UncommittedEvents;

            if (uncommittedEvents.Any())
            {
                await _eventStore.SaveEventsAsync(
                    aggregate.Id,
                    uncommittedEvents,
                    aggregate.Version - uncommittedEvents.Count);

                aggregate.MarkEventsAsCommitted();
            }
        }
    }
}