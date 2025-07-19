using System.Collections.Concurrent;
using SourceFlow.Messaging;

namespace SourceFlow.ConsoleApp.Impl
{
    public class InMemoryEventStore : IEventStore
    {
        private readonly ConcurrentDictionary<int, List<ICommand>> _store = new();

        public Task Append(ICommand @event)
        {
            if (!_store.ContainsKey(@event.Payload.Id))
                _store[@event.Payload.Id] = new List<ICommand>();

            _store[@event.Payload.Id].Add(@event);

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
            {
                return Task.FromResult(events.Max<ICommand, int>(c => ((IMetadata)c).Metadata.SequenceNo) + 1);
            }
            return Task.FromResult(1);
        }
    }
}