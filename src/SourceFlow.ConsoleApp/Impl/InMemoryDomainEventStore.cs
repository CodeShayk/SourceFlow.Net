using SourceFlow.Core;

namespace SourceFlow.ConsoleApp.Impl
{
    public class InMemoryDomainEventStore : IDomainEventStore
    {
        private readonly Dictionary<Guid, List<IDomainEvent>> _store = new();

        public void Append(Guid aggregateId, IDomainEvent @event)
        {
            if (!_store.ContainsKey(aggregateId))
                _store[aggregateId] = new List<IDomainEvent>();

            _store[aggregateId].Add(@event);
        }

        public IEnumerable<IDomainEvent> GetEvents(Guid aggregateId)
        {
            return _store.TryGetValue(aggregateId, out var events)
                ? events
                : Enumerable.Empty<IDomainEvent>();
        }
    }
}