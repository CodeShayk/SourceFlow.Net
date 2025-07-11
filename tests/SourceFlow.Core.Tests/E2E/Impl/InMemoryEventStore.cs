using System.Collections.Concurrent;

namespace SourceFlow.Core.Tests.E2E.Impl
{
    public class InMemoryEventStore : IEventStore
    {
        private readonly ConcurrentDictionary<int, List<IEvent>> _store = new();

        public Task AppendAsync(IEvent @event)
        {
            if (!_store.ContainsKey(@event.Entity.Id))
                _store[@event.Entity.Id] = new List<IEvent>();

            _store[@event.Entity.Id].Add(@event);

            return Task.CompletedTask;
        }

        public async Task<IEnumerable<IEvent>> LoadAsync(int aggregateId)
        {
            return await Task.FromResult(_store.TryGetValue(aggregateId, out var events)
               ? events
               : Enumerable.Empty<IEvent>());
        }

        public Task<int> GetNextSequenceNo(int aggregateId)
        {
            if (_store.TryGetValue(aggregateId, out var events))
                return Task.FromResult(events.Max<IEvent, int>(c => c.SequenceNo) + 1);
            return Task.FromResult(1);
        }
    }
}