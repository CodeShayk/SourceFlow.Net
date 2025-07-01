using System.Collections.Concurrent;
using System.Linq;

namespace SourceFlow.ConsoleApp.Impl
{
    public class InMemoryEventStore : IEventStore
    {
        private readonly ConcurrentDictionary<Guid, List<IEvent>> _store = new();

        public Task AppendAsync(IEvent @event)
        {
            if (!_store.ContainsKey(@event.AggregateId))
                _store[@event.AggregateId] = new List<IEvent>();

            _store[@event.AggregateId].Add(@event);

            return Task.CompletedTask;
        }

        public async Task<IEnumerable<IEvent>> LoadAsync(Guid aggregateId)
        {
            return await Task.FromResult(_store.TryGetValue(aggregateId, out var events)
               ? events
               : Enumerable.Empty<IEvent>());
        }

        public Task<int> GetNextSequenceNo(Guid aggregateId)
        {
            if (_store.TryGetValue(aggregateId, out var events))
            {
                return Task.FromResult(events.Max<IEvent, int>(c => c.SequenceNo) + 1);
            }
            return Task.FromResult(1);
        }
    }
}