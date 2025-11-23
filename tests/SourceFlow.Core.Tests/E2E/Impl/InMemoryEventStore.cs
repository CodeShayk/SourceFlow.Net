using System.Collections.Concurrent;
using SourceFlow.Messaging;
using SourceFlow.Messaging.Commands;

namespace SourceFlow.Core.Tests.E2E.Impl
{
    public class InMemoryEventStore : ICommandStore
    {
        private readonly ConcurrentDictionary<int, List<ICommand>> _store = new();

        public Task Append(ICommand command)
        {
            if (!_store.ContainsKey(command.Entity.Id))
                _store[command.Entity.Id] = new List<ICommand>();

            _store[command.Entity.Id].Add(command);

            return Task.CompletedTask;
        }

        public async Task<IEnumerable<ICommand>> Load(int entityId)
        {
            return await Task.FromResult(_store.TryGetValue(entityId, out var events)
               ? events
               : Enumerable.Empty<ICommand>());
        }

        public Task<int> GetNextSequenceNo(int entityId)
        {
            if (_store.TryGetValue(entityId, out var events))
                return Task.FromResult(events.Max<ICommand, int>(c => ((IMetadata)c).Metadata.SequenceNo) + 1);

            return Task.FromResult(1);
        }
    }
}