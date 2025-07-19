using System.Collections.Concurrent;
using SourceFlow.Messaging;

namespace SourceFlow.Core.Tests.E2E.Impl
{
    public class InMemoryEventStore : IEventStore
    {
        private readonly ConcurrentDictionary<int, List<ICommand>> _store = new();

        public Task Append(ICommand @event)
        {
            if (!_store.ContainsKey(@event.Entity.Id))
                _store[@event.Entity.Id] = new List<ICommand>();

            _store[@event.Entity.Id].Add(@event);

            return Task.CompletedTask;
        }

        public async Task<IEnumerable<ICommand>> Load(int aggregateId)
        {
            return await Task.FromResult(_store.TryGetValue(aggregateId, out var events)
               ? events
               : Enumerable.Empty<ICommand>());
        }

        public Task<int> GetNextSequenceNo(int aggregateId)
        {
            if (_store.TryGetValue(aggregateId, out var events))
                return Task.FromResult(events.Max<ICommand, int>(c => c.SequenceNo) + 1);
            return Task.FromResult(1);
        }
    }
}